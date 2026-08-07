import { computed, signal } from '@angular/core';
import { AgentFrame, DoneFrame, ErrorFrame, LoadedFrame, MessageFrame } from './frames';
import { AgentAttachmentCapable, AgentTransport, AgentTurnBody, AgentTurnRefusal, UploadedAgentAsset } from './tokens';

/** A rendered transcript bubble (optimistic entries carry an empty timestamp). */
export interface AgentMessage {
  role: 'user' | 'assistant' | string;
  text: string;
  createdAtUtc: string;
}

/** A pending composer attachment chip. A PDF is one chip covering every page asset —
 * `id` joins the asset ids with `+`, so removing the chip drops all pages. */
export interface PendingChip {
  id: string;
  fileName: string;
  previewUrl?: string | null;
  contentType?: string;
}

/** Where the turn lifecycle currently stands. `error` covers a failed turn AND a stream that
 * ended without a terminal frame (silence is failure — the server always closes a healthy turn
 * with `message` + `done` or `error`). */
export type AgentTurnPhase = 'idle' | 'streaming' | 'done' | 'error';

export interface AgentTurnStateOptions {
  /** Dead-air ceiling: with no frame (heartbeat included) for this long mid-stream, the turn
   * fails locally and the stream is hung up. Default 90 s — comfortably past the server
   * heartbeat cadence, short enough that a dead proxy hop doesn't hold the composer forever. */
  deadAirMs?: number;
  /** Composer text substituted when only attachments were sent. */
  attachmentOnlyText?: string;
  /** Transcript wording, overridable per host. */
  text?: Partial<AgentTurnStateText>;
}

export interface AgentTurnStateText {
  /** Another turn holds the server's single-flight gate (HTTP 409). */
  busy: string;
  /** The request itself failed (network, auth) with no frame received. */
  failed: string;
  /** The turn ended with a server `error` frame carrying no detail. */
  turnFailed: string;
  /** The turn ended with a server `error` frame carrying a detail sentence — `{detail}` interpolates. */
  turnFailedDetail: string;
  /** The stream closed with no terminal frame (proxy timeout, server recycle, dead air). */
  endedEarly: string;
  /** The turn closed cleanly but produced nothing visible. */
  nothingToAdd: string;
}

const DEFAULT_TEXT: AgentTurnStateText = {
  busy: 'One turn at a time — another turn is already running.',
  failed: 'The turn failed — try again.',
  turnFailed: 'The turn failed — try again.',
  turnFailedDetail: 'The turn failed — {detail}',
  endedEarly: 'The turn ended before it finished — try again.',
  nothingToAdd: 'The turn finished with nothing to add.',
};

export interface SendAgentTurnOptions<TDomain extends { type: string } = { type: string }> {
  /** The stream opened (turn id, routed provider/model). */
  onLoaded?(frame: LoadedFrame): void;
  /** The authoritative final text arrived (already recorded for the transcript). */
  onMessage?(frame: MessageFrame): void;
  /** The turn's terminal success frame (the message text is already in the transcript). */
  onDone?(frame: DoneFrame): void;
  /** The turn's terminal error frame (its message is already in the transcript). */
  onError?(frame: ErrorFrame): void;
  /** Anything outside the core union — host domain frames fall through here, never error. */
  onDomainFrame?(frame: TDomain): void;
  /** Extra turn-body members merged into the body handed to the transport. */
  extraBody?: Record<string, unknown>;
}

/**
 * The agent-turn state machine — the aws `ChatTurnState` mechanics (commit 28131fd: the
 * monotonic stale-stream guard, optimistic user append, upload-then-chip attachment flow,
 * end-of-turn finalization) on the v2 frame union (`loaded · token · tool · question ·
 * questionForm · followups · citation · usage · heartbeat · message · done · error`):
 *
 * - `message` is the authoritative final text; accumulated `token` deltas are the fallback only
 *   when no `message` arrived before `done`.
 * - `heartbeat` is consumed INTERNALLY as a liveness signal — it resets the dead-air timer and
 *   feeds the `working` signal; it is never rendered.
 * - Silence is failure: no frame at all for `deadAirMs` mid-stream transitions to the error
 *   state (exactly once) and hangs up — matching the server's terminal-error convention, where
 *   a healthy turn always closes with `message` + `done` or `error`.
 * - Refusals before any frame (409 busy, 503 unconfigured) arrive as {@link AgentTurnRefusal}
 *   from the transport: 503 latches the `unavailable` signal; 409 gets the busy line.
 * - `abort()` is a quiet hang-up: no message, no error state (the aws detach semantics; the
 *   Groundsworth turn-client resolves quietly on abort for the same reason).
 *
 * Panel components own their templates and feature chrome; this class owns the async state.
 */
export class AgentTurnState<TDomain extends { type: string } = { type: string }> {
  readonly messages = signal<AgentMessage[]>([]);
  readonly phase = signal<AgentTurnPhase>('idle');
  readonly streaming = computed(() => this.phase() === 'streaming');
  /** True while a turn is running AND frames are still arriving (liveness-fresh). */
  readonly working = computed(() => this.phase() === 'streaming');
  /** Latched by a 503 refusal: no completion provider is configured server-side. */
  readonly unavailable = signal(false);
  /** The last `loaded` frame — the turn id and the routed provider/model serving it. */
  readonly loaded = signal<LoadedFrame | null>(null);
  readonly pendingAttachments = signal<PendingChip[]>([]);
  /** Attachment uploads still in flight. Send is refused while > 0 — a send racing an upload
   * would post the turn without the asset ids (the chip only lands on upload response). */
  readonly uploadsInFlight = signal(0);
  readonly composerDisabled = computed(() => this.phase() === 'streaming' || this.uploadsInFlight() > 0);

  /** Monotonic turn id — bumped on each send, abort, reset, and dead-air failure. */
  private streamSeq = 0;
  private attachmentAssetIds: string[] = [];
  private readonly previewUrls = new Set<string>();
  private inflight: AbortController | null = null;
  private deadAirTimer: ReturnType<typeof setTimeout> | null = null;
  private readonly deadAirMs: number;
  private readonly attachmentOnlyText: string;
  private readonly text: AgentTurnStateText;

  constructor(
    private readonly transport: AgentTransport<AgentFrame | TDomain> & Partial<AgentAttachmentCapable>,
    options: AgentTurnStateOptions = {},
  ) {
    this.deadAirMs = options.deadAirMs ?? 90_000;
    this.attachmentOnlyText = options.attachmentOnlyText ?? '(attachments)';
    this.text = { ...DEFAULT_TEXT, ...options.text };
  }

  /** Quiet hang-up: abort any in-flight stream and return to idle — no message, no error state.
   * (An abandoned stream can hold a server-side single-flight gate until the model round-trip
   * returns, so surfaces abort on teardown.) */
  abort(): void {
    this.streamSeq++;
    this.clearDeadAir();
    this.inflight?.abort();
    this.inflight = null;
    if (this.phase() === 'streaming') {
      this.phase.set('idle');
    }
  }

  /** Reset for a fresh surface/thread: aborts, clears the transcript and pending chips. */
  reset(): void {
    this.abort();
    this.messages.set([]);
    this.loaded.set(null);
    this.phase.set('idle');
    this.clearPending();
  }

  /** Runs one streamed turn. Returns false when nothing was sent (busy, uploading, or empty). */
  send(text: string, opts: SendAgentTurnOptions<TDomain> = {}): boolean {
    text = (text ?? '').trim();
    const assetIds = this.attachmentAssetIds;
    if ((!text && !assetIds.length) || this.phase() === 'streaming' || this.uploadsInFlight() > 0) {
      return false;
    }
    if (!text) {
      text = this.attachmentOnlyText;
    }

    // The chips leave the composer now (optimistic), but their preview URLs stay alive and the
    // snapshot is kept: a request that fails before any frame arrives restores them — otherwise
    // a 409/401/network blip silently orphans the uploaded assets and a retry would send text only.
    const sentChips = this.pendingAttachments();
    this.attachmentAssetIds = [];
    this.pendingAttachments.set([]);
    const releaseSentPreviews = (): void => {
      for (const chip of sentChips) {
        if (chip.previewUrl && this.previewUrls.delete(chip.previewUrl)) {
          URL.revokeObjectURL(chip.previewUrl);
        }
      }
    };
    this.messages.update((m) => [...m, { role: 'user', text, createdAtUtc: '' }]);

    const myTurn = ++this.streamSeq;
    const inflight = new AbortController();
    this.inflight = inflight;
    this.phase.set('streaming');

    // Terminal bookkeeping (the shipped Groundsworth rail's rules): `closed` = a terminal frame
    // arrived; `rendered` = anything user-visible came out. A stream that merely stops is
    // indistinguishable from success on the wire, so silence is never an answer — and neither is
    // a half-turn that looks like one.
    let closed = false;
    let rendered = false;
    let deadAirFired = false;

    const finish = (phase: AgentTurnPhase, transcriptText?: string): void => {
      this.clearDeadAir();
      if (transcriptText) {
        this.messages.update((m) => [...m, { role: 'assistant', text: transcriptText, createdAtUtc: '' }]);
      }
      this.phase.set(phase);
    };

    this.armDeadAir(() => {
      // No frame of any kind for deadAirMs: fail EXACTLY ONCE, then hang up. The abort below
      // settles the transport promise; deadAirFired keeps that settle path quiet.
      if (myTurn !== this.streamSeq || this.phase() !== 'streaming') {
        return;
      }
      deadAirFired = true;
      finish('error', this.text.endedEarly);
      inflight.abort();
    });

    // The authoritative final text (`message`) and the streamed-token fallback. Phase-1 note:
    // tokens accumulate for the fallback rule; live streaming rendering arrives with state v2.
    let messageText: string | undefined;
    let streamedText = '';

    const collect = (frame: AgentFrame | TDomain): void => {
      switch (frame.type) {
        case 'loaded':
          this.loaded.set(frame as LoadedFrame);
          opts.onLoaded?.(frame as LoadedFrame);
          break;
        case 'heartbeat':
          // Liveness only — the dead-air reset below already did the work; never rendered.
          break;
        case 'token':
          streamedText += (frame as { delta?: string }).delta ?? '';
          break;
        case 'tool':
        case 'citation':
        case 'usage':
        case 'followups':
          // Core frames with no phase-1 rendering — consumed (never onDomainFrame), surfaced by state v2.
          break;
        case 'question':
        case 'questionForm':
          // A question card is user-visible content: a turn that only asked is a complete answer.
          rendered = true;
          break;
        case 'message': {
          const message = frame as MessageFrame;
          messageText = message.text;
          if (message.text) {
            rendered = true;
          }
          opts.onMessage?.(message);
          break;
        }
        case 'done': {
          closed = true;
          const finalText = messageText ?? (streamedText || undefined);
          if (finalText) {
            rendered = true;
          }
          finish('done', finalText);
          opts.onDone?.(frame as DoneFrame);
          break;
        }
        case 'error': {
          closed = true;
          rendered = true;
          const error = frame as ErrorFrame;
          finish('error', error.detail
            ? this.text.turnFailedDetail.replace('{detail}', error.detail)
            : this.text.turnFailed);
          opts.onError?.(error);
          break;
        }
        default:
          opts.onDomainFrame?.(frame as TDomain);
          break;
      }
    };

    let receivedAnyFrame = false;
    const onFrame = (frame: AgentFrame | TDomain): void => {
      receivedAnyFrame = true; // even if superseded — the server consumed the attachments
      if (myTurn !== this.streamSeq) {
        return;
      }
      this.resetDeadAir();
      collect(frame);
    };

    const body: AgentTurnBody = {
      text,
      ...(assetIds.length ? { attachmentAssetIds: assetIds } : {}),
      ...opts.extraBody,
    };
    this.transport
      .streamTurn(body, onFrame, inflight.signal)
      .then(() => {
        releaseSentPreviews();
        if (myTurn !== this.streamSeq || deadAirFired || inflight.signal.aborted) {
          return;
        }
        if (!closed) {
          // A clean close without a terminal frame (proxy idle-timeout, server recycle) must not
          // leave the composer disabled forever — and cards from a half-run turn must not read
          // as a completed answer.
          finish('error', this.text.endedEarly);
        } else if (!rendered) {
          this.messages.update((m) => [...m, { role: 'assistant', text: this.text.nothingToAdd, createdAtUtc: '' }]);
        }
      })
      .catch((error: unknown) => {
        if (myTurn !== this.streamSeq || deadAirFired) {
          releaseSentPreviews();
          return;
        }
        if (inflight.signal.aborted) {
          // The caller hung up — quiet teardown; abort() already restored the phase.
          releaseSentPreviews();
          return;
        }
        if (!receivedAnyFrame) {
          // The request itself failed (409 busy, 401, offline) — the uploaded assets are still
          // valid server-side, so put the chips back for the retry.
          this.attachmentAssetIds = assetIds;
          this.pendingAttachments.set(sentChips);
        } else {
          releaseSentPreviews();
        }
        const status = error instanceof AgentTurnRefusal ? error.status
          : (error as { status?: number } | null)?.status;
        if (status === 503) {
          this.unavailable.set(true);
          finish('error');
        } else if (status === 409) {
          finish('error', this.text.busy);
        } else {
          finish('error', this.text.failed);
        }
      })
      .finally(() => {
        if (this.inflight === inflight) {
          this.inflight = null;
        }
      });
    return true;
  }

  // ── attachments (upload-then-chip; the ids ride the next turn) ─────────────────────────────
  // Images upload as-is; a PDF comes back as one asset per page (plus, on some surfaces, a
  // source asset), but shows as a SINGLE chip ("name (2 pages)") whose id carries every rideable
  // assetId — removing it drops all pages. Requires a transport implementing
  // AgentAttachmentCapable; addFiles is a silent no-op otherwise.

  /** Uploads accepted files one call per file (a PDF returns multiple assets, so a mixed batch
   * can't be index-mapped back to its source files — per-file keeps chips ↔ assets honest). */
  addFiles(files: File[]): void {
    const upload = this.transport.uploadAttachments?.bind(this.transport);
    if (!files.length || !upload) return;
    const mySurface = this.streamSeq;
    const accepted = files.filter((f) => f.type.startsWith('image/') || f.type === 'application/pdf');
    for (const file of accepted) {
      this.uploadsInFlight.update((n) => n + 1);
      upload([file])
        .then((assets: UploadedAgentAsset[]) => {
          // Guard against a reset/abort during the upload — late chips/ids must not land on a
          // fresh thread (which would wire stale assets into the next turn).
          if (this.streamSeq !== mySurface) return;
          // Only page/image assets ride a turn; a `source` asset must not be referenced by the
          // turn body.
          const rideable = assets.filter((a) => a.kind !== 'source');
          if (!rideable.length) return;
          const isPdf = file.type === 'application/pdf';
          let previewUrl: string | null = null;
          if (!isPdf) {
            previewUrl = URL.createObjectURL(file);
            this.previewUrls.add(previewUrl);
          }
          const chip: PendingChip = {
            id: rideable.map((a) => a.assetId).join('+'),
            fileName: isPdf && rideable.length > 1 ? `${file.name} (${rideable.length} pages)` : file.name,
            previewUrl,
            contentType: isPdf ? 'application/pdf' : rideable[0]?.contentType,
          };
          this.attachmentAssetIds = [...this.attachmentAssetIds, ...rideable.map((a) => a.assetId)];
          this.pendingAttachments.update((p) => [...p, chip]);
        })
        .catch(() => { /* upload failed — no chip appears; the user can retry */ })
        .finally(() => this.uploadsInFlight.update((n) => n - 1));
    }
  }

  removePending(chip: { id: string; previewUrl?: string | null }): void {
    const chipIds = new Set(chip.id.split('+'));
    this.attachmentAssetIds = this.attachmentAssetIds.filter((id) => !chipIds.has(id));
    this.pendingAttachments.update((p) => p.filter((c) => c.id !== chip.id));
    if (chip.previewUrl && this.previewUrls.delete(chip.previewUrl)) {
      URL.revokeObjectURL(chip.previewUrl);
    }
  }

  clearPending(revoke = true): void {
    if (revoke) {
      for (const url of this.previewUrls) URL.revokeObjectURL(url);
      this.previewUrls.clear();
      this.attachmentAssetIds = [];
    }
    this.pendingAttachments.set([]);
  }

  // ── dead-air plumbing ──────────────────────────────────────────────────────────────────────

  private armDeadAir(onFire: () => void): void {
    this.clearDeadAir();
    if (this.deadAirMs <= 0) {
      return;
    }
    this.deadAirFire = onFire;
    this.deadAirTimer = setTimeout(onFire, this.deadAirMs);
  }

  private resetDeadAir(): void {
    if (this.deadAirTimer !== null && this.deadAirFire) {
      clearTimeout(this.deadAirTimer);
      this.deadAirTimer = setTimeout(this.deadAirFire, this.deadAirMs);
    }
  }

  private clearDeadAir(): void {
    if (this.deadAirTimer !== null) {
      clearTimeout(this.deadAirTimer);
      this.deadAirTimer = null;
    }
    this.deadAirFire = null;
  }

  private deadAirFire: (() => void) | null = null;
}
