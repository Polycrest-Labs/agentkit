import { computed, signal } from '@angular/core';
import { AgentFrame, AgentQuestion, AgentQuestionForm, DoneFrame, ErrorFrame, LoadedFrame, MessageFrame } from './frames';
import { AgentAttachmentCapable, AgentTransport, AgentTurnBody, AgentTurnRefusal, UploadedAgentAsset } from './tokens';

/** A rendered transcript bubble — the flat COMPAT projection of the timeline (user/assistant text
 * only). New consumers read {@link AgentTurnState.timeline}. */
export interface AgentMessage {
  role: 'user' | 'assistant' | string;
  text: string;
  createdAtUtc: string;
}

/** Attachment metadata carried on a user entry (optimistic from the sent chips; replaced by the
 * host's durable truth on hydration). */
export interface AgentEntryAttachment {
  assetId: string;
  originalName?: string;
  contentType?: string;
  kind?: string;
  byteSize?: number;
  /** Host-resolved preview/object URL. The HOST owns its lifecycle (revocation). */
  previewUrl?: string | null;
}

export interface AgentCitationInfo {
  title?: string | null;
  url?: string | null;
}

export interface AgentUsageInfo {
  inputTokens?: number | null;
  outputTokens?: number | null;
}

export interface AgentUserEntry {
  kind: 'user';
  id?: string;
  text: string;
  createdAtUtc?: string;
  attachments?: AgentEntryAttachment[];
}

export interface AgentAssistantEntry {
  kind: 'assistant';
  id?: string;
  text: string;
  createdAtUtc?: string;
  citations?: AgentCitationInfo[];
  usage?: AgentUsageInfo | null;
  /** Locally-generated status notes (refusals, dead-air, stop) — styled as a notice, not an answer. */
  notice?: boolean;
}

export interface AgentQuestionEntry {
  kind: 'question';
  id?: string;
  question: AgentQuestion;
  createdAtUtc?: string;
  /** Locks the card (a later user message exists / the durable answer is parent-linked). */
  answered?: boolean;
  /** The durable answer text, when the host hydrated it — shown on the locked card. */
  answerText?: string | null;
}

export interface AgentQuestionFormEntry {
  kind: 'questionForm';
  id?: string;
  form: AgentQuestionForm;
  createdAtUtc?: string;
  answered?: boolean;
  answerText?: string | null;
}

/** A host-domain record (e.g. a Groundsworth suggestion) the host appended or hydrated. The
 * transcript renders it through the host's `*agentCard="frameType"` template projection. */
export interface AgentDomainEntry {
  kind: 'domain';
  id?: string;
  frameType: string;
  data: unknown;
  createdAtUtc?: string;
}

/** One ordered timeline entry — the durable transcript unit. */
export type AgentTranscriptEntry =
  | AgentUserEntry
  | AgentAssistantEntry
  | AgentQuestionEntry
  | AgentQuestionFormEntry
  | AgentDomainEntry;

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
  /** The `working` liveness window: the turn reads as actively working while ANY frame arrived
   * within this long. Default 30 s (2× the server's 15 s heartbeat cadence). */
  workingWindowMs?: number;
  /** Composer text substituted when only attachments were sent. */
  attachmentOnlyText?: string;
  /** Most attachments one turn may carry (pending chips + uploads in flight). Files past the cap
   * are refused with a transcript notice instead of uploading — per-file uploads would otherwise
   * sidestep any server per-batch limit chip by chip. Default 4. */
  maxPendingAttachments?: number;
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
  /** The user stopped the turn and the server confirmed the cancellation. */
  stopped: string;
}

const DEFAULT_TEXT: AgentTurnStateText = {
  busy: 'One turn at a time — another turn is already running.',
  failed: 'The turn failed — try again.',
  turnFailed: 'The turn failed — try again.',
  turnFailedDetail: 'The turn failed — {detail}',
  endedEarly: 'The turn ended before it finished — try again.',
  nothingToAdd: 'The turn finished with nothing to add.',
  stopped: 'Stopped.',
};

export interface SendAgentTurnOptions<TDomain extends { type: string } = { type: string }> {
  /** The stream opened (turn id, routed provider/model). */
  onLoaded?(frame: LoadedFrame): void;
  /** The authoritative final text arrived (already recorded for the transcript). */
  onMessage?(frame: MessageFrame): void;
  /** The turn's terminal success frame (the final text is already in the timeline). */
  onDone?(frame: DoneFrame): void;
  /** The turn's terminal error frame (its message is already in the timeline). */
  onError?(frame: ErrorFrame): void;
  /** Anything outside the core union — host domain frames fall through here, never error. The
   * host decides what becomes a durable timeline entry (via {@link AgentTurnState.append}). */
  onDomainFrame?(frame: TDomain): void;
  /** Extra turn-body members merged into the body handed to the transport. */
  extraBody?: Record<string, unknown>;
}

/**
 * The agent-turn state machine v2 — the aws `ChatTurnState` mechanics (the monotonic stale-stream
 * guard, optimistic user append, upload-then-chip attachment flow, end-of-turn finalization)
 * grown into an ordered TIMELINE of rich transcript entries on the v2 frame union:
 *
 * - The {@link timeline} is the transcript: user/assistant messages, durable question/form cards,
 *   and host-domain entries, in order. {@link hydrate} seeds it from host persistence;
 *   live frames append after.
 * - `message` is the authoritative final text; accumulated `token` deltas (exposed live via
 *   {@link streamingText}) are the fallback only when no `message` arrived before `done`.
 * - `heartbeat` is consumed INTERNALLY as liveness — it feeds {@link working} (frame recency
 *   within ~2× the heartbeat cadence) and resets the dead-air timer; it is never rendered.
 * - Silence is failure: no frame at all for `deadAirMs` mid-stream transitions to the error state
 *   (exactly once) and hangs up.
 * - Refusals before any frame (409 busy, 503 unconfigured) arrive as {@link AgentTurnRefusal}
 *   from the transport: 503 latches the `unavailable` signal; 409 gets the busy line.
 * - `abort()` is a quiet hang-up: no message, no error state. {@link stop} is different — it is
 *   rendered only when the transport advertises `serverCancel`, invokes the transport's explicit
 *   server cancellation, and appends "Stopped." only after the server confirmed.
 * - Followup chips belong to the turn/thread they were emitted in: cleared on every send and on
 *   reset/hydrate — they never leak across turns or threads.
 *
 * Panel components own their templates and feature chrome; this class owns the async state.
 */
export class AgentTurnState<TDomain extends { type: string } = { type: string }> {
  /** The ordered transcript. */
  readonly timeline = signal<AgentTranscriptEntry[]>([]);
  /** Flat user/assistant text projection (compat surface for simple hosts). */
  readonly messages = computed<AgentMessage[]>(() => this.timeline()
    .filter((e): e is AgentUserEntry | AgentAssistantEntry => e.kind === 'user' || e.kind === 'assistant')
    .map((e) => ({ role: e.kind, text: e.text, createdAtUtc: e.createdAtUtc ?? '' })));

  readonly phase = signal<AgentTurnPhase>('idle');
  readonly streaming = computed(() => this.phase() === 'streaming');
  /** Live streamed text for the in-flight assistant bubble ('' when idle or before first token). */
  readonly streamingText = signal('');
  /** The running tool ({name, label}) while a `tool` start frame is unclosed; null otherwise. */
  readonly activeTool = signal<{ name: string; label?: string | null } | null>(null);
  /** Ephemeral next-step chips for the latest turn (cleared on send/reset/hydrate). */
  readonly followups = signal<string[]>([]);
  /** Deduped citations streamed during the CURRENT turn (folded into the assistant entry at finish). */
  readonly citations = signal<AgentCitationInfo[]>([]);
  /** The current/most recent turn's usage report (folded into the assistant entry at finish). */
  readonly usage = signal<AgentUsageInfo | null>(null);
  /** True while a turn is running AND frames are still arriving (liveness-fresh — any frame
   * within ~2× the heartbeat cadence). */
  readonly working = computed(() => this.phase() === 'streaming' && this.frameFresh());
  /** Latched by a 503 refusal: no completion provider is configured server-side. */
  readonly unavailable = signal(false);
  /** The last `loaded` frame — the turn id and the routed provider/model serving it. */
  readonly loaded = signal<LoadedFrame | null>(null);
  readonly pendingAttachments = signal<PendingChip[]>([]);
  /** Attachment uploads still in flight. Send is refused while > 0 — a send racing an upload
   * would post the turn without the asset ids (the chip only lands on upload response). */
  readonly uploadsInFlight = signal(0);
  readonly composerDisabled = computed(() => this.phase() === 'streaming' || this.uploadsInFlight() > 0);
  /** Whether the visible Stop control may render: a turn is running AND the transport genuinely
   * cancels server work. */
  readonly canStop = computed(() => this.phase() === 'streaming' && !!this.transport.capabilities?.serverCancel);

  /** Monotonic turn id — bumped on each send, abort, reset, stop, and dead-air failure. */
  private streamSeq = 0;
  private attachmentAssetIds: string[] = [];
  private readonly previewUrls = new Set<string>();
  private inflight: AbortController | null = null;
  private deadAirTimer: ReturnType<typeof setTimeout> | null = null;
  private readonly frameFresh = signal(false);
  private workingTimer: ReturnType<typeof setTimeout> | null = null;
  private readonly deadAirMs: number;
  private readonly workingWindowMs: number;
  private readonly attachmentOnlyText: string;
  private readonly maxPendingAttachments: number;
  private readonly text: AgentTurnStateText;

  constructor(
    private readonly transport: AgentTransport<AgentFrame | TDomain> & Partial<AgentAttachmentCapable>,
    options: AgentTurnStateOptions = {},
  ) {
    this.deadAirMs = options.deadAirMs ?? 90_000;
    this.workingWindowMs = options.workingWindowMs ?? 30_000;
    this.attachmentOnlyText = options.attachmentOnlyText ?? '(attachments)';
    this.maxPendingAttachments = options.maxPendingAttachments ?? 4;
    this.text = { ...DEFAULT_TEXT, ...options.text };
  }

  /** Appends one entry to the timeline (hosts use this for domain records; live frames use it
   * internally). Stamps `createdAtUtc` when absent. */
  append(entry: AgentTranscriptEntry): void {
    const stamped = entry.createdAtUtc ? entry : { ...entry, createdAtUtc: new Date().toISOString() };
    this.timeline.update((t) => [...t, stamped]);
  }

  /** Seeds the timeline from host persistence (thread open / reload): quiet-aborts any stream,
   * replaces the transcript in order, and clears per-turn ephemera (followups, citations, usage).
   * Pending composer attachments are NOT touched — an unsent upload survives a refresh of the
   * thread view. */
  hydrate(entries: AgentTranscriptEntry[]): void {
    this.abort();
    this.timeline.set([...entries]);
    this.loaded.set(null);
    this.followups.set([]);
    this.citations.set([]);
    this.usage.set(null);
    this.phase.set('idle');
  }

  /** Quiet hang-up: abort any in-flight stream and return to idle — no message, no error state.
   * (An abandoned stream can hold a server-side single-flight gate until the model round-trip
   * returns, so surfaces abort on teardown.) */
  abort(): void {
    this.streamSeq++;
    this.clearDeadAir();
    this.clearWorking();
    this.inflight?.abort();
    this.inflight = null;
    this.streamingText.set('');
    this.activeTool.set(null);
    if (this.phase() === 'streaming') {
      this.phase.set('idle');
    }
  }

  /** Explicit SERVER-side stop — only meaningful when the transport advertises `serverCancel`.
   * Asks the transport to cancel the running turn, and only after the server confirms appends the
   * "Stopped." notice and releases the composer. Returns false when there was nothing to stop,
   * the capability is absent, or cancellation failed (the turn keeps running). */
  async stop(): Promise<boolean> {
    if (!this.canStop() || !this.transport.cancelTurn) {
      return false;
    }
    const myTurn = this.streamSeq;
    try {
      await this.transport.cancelTurn();
    } catch {
      return false; // the server did not confirm — the turn is still running; claim nothing
    }
    if (myTurn !== this.streamSeq || this.phase() !== 'streaming') {
      return true; // confirmed, but the turn already settled some other way
    }
    this.abort();
    this.append({ kind: 'assistant', text: this.text.stopped, notice: true });
    return true;
  }

  /** Reset for a fresh surface/thread: aborts, clears the transcript, ephemera and pending chips. */
  reset(): void {
    this.abort();
    this.timeline.set([]);
    this.loaded.set(null);
    this.followups.set([]);
    this.citations.set([]);
    this.usage.set(null);
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
    this.append({
      kind: 'user',
      text,
      attachments: sentChips.length
        ? sentChips.map((c) => ({
          assetId: c.id,
          originalName: c.fileName,
          contentType: c.contentType,
          previewUrl: c.previewUrl,
        }))
        : undefined,
    });
    // A later message locks every earlier card ("the answer already went" — the aws rule).
    this.lockOpenCards();

    const myTurn = ++this.streamSeq;
    const inflight = new AbortController();
    this.inflight = inflight;
    this.phase.set('streaming');
    this.streamingText.set('');
    this.activeTool.set(null);
    this.followups.set([]); // this turn's chips arrive with its frames; drop the previous turn's now
    this.citations.set([]);
    this.usage.set(null);
    this.markFrame();

    // Terminal bookkeeping (the shipped rules): `closed` = a terminal frame arrived; `rendered` =
    // anything user-visible came out. A stream that merely stops is indistinguishable from success
    // on the wire, so silence is never an answer — and neither is a half-turn that looks like one.
    let closed = false;
    let rendered = false;
    let deadAirFired = false;
    let messageText: string | undefined;

    const finish = (phase: AgentTurnPhase, transcriptText?: string, notice = false): void => {
      this.clearDeadAir();
      this.clearWorking();
      if (transcriptText) {
        this.append({
          kind: 'assistant',
          text: transcriptText,
          notice: notice || undefined,
          citations: notice ? undefined : (this.citations().length ? this.citations() : undefined),
          usage: notice ? undefined : this.usage(),
        });
      }
      this.streamingText.set('');
      this.activeTool.set(null);
      this.phase.set(phase);
    };

    this.armDeadAir(() => {
      // No frame of any kind for deadAirMs: fail EXACTLY ONCE, then hang up. The abort below
      // settles the transport promise; deadAirFired keeps that settle path quiet.
      if (myTurn !== this.streamSeq || this.phase() !== 'streaming') {
        return;
      }
      deadAirFired = true;
      finish('error', this.text.endedEarly, true);
      inflight.abort();
    });

    const collect = (frame: AgentFrame | TDomain): void => {
      switch (frame.type) {
        case 'loaded':
          this.loaded.set(frame as LoadedFrame);
          opts.onLoaded?.(frame as LoadedFrame);
          break;
        case 'heartbeat':
          // Liveness only — the dead-air/working resets already did the work; never rendered.
          break;
        case 'token':
          this.streamingText.update((t) => t + ((frame as { delta?: string }).delta ?? ''));
          break;
        case 'tool': {
          const tool = frame as { name: string; phase: 'start' | 'end'; label?: string | null };
          this.activeTool.set(tool.phase === 'start' ? { name: tool.name, label: tool.label } : null);
          break;
        }
        case 'question': {
          rendered = true;
          const q = frame as { question: AgentQuestion };
          this.append({ kind: 'question', question: q.question });
          break;
        }
        case 'questionForm': {
          rendered = true;
          const f = frame as { form: AgentQuestionForm };
          this.append({ kind: 'questionForm', form: f.form });
          break;
        }
        case 'followups': {
          const prompts = (frame as { prompts?: string[] }).prompts ?? [];
          this.followups.set(prompts);
          break;
        }
        case 'citation': {
          const citation = frame as AgentCitationInfo;
          this.citations.update((list) =>
            list.some((c) => c.title === citation.title && c.url === citation.url)
              ? list
              : [...list, { title: citation.title, url: citation.url }]);
          break;
        }
        case 'usage': {
          const usage = frame as AgentUsageInfo;
          this.usage.set({ inputTokens: usage.inputTokens, outputTokens: usage.outputTokens });
          break;
        }
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
          const finalText = messageText ?? (this.streamingText() || undefined);
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
            : this.text.turnFailed, true);
          opts.onError?.(error);
          break;
        }
        default:
          if (opts.onDomainFrame) {
            // A HANDLED domain frame is rendered content (0.2.0's suggestion case set this
            // flag; its retirement into domain frames silently dropped the bookkeeping) —
            // a suggestion-only turn must not also announce "nothing to add".
            opts.onDomainFrame(frame as TDomain);
            rendered = true;
          }
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
      this.markFrame();
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
          finish('error', this.text.endedEarly, true);
        } else if (!rendered) {
          this.append({ kind: 'assistant', text: this.text.nothingToAdd, notice: true });
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
          finish('error', this.text.busy, true);
        } else {
          finish('error', this.text.failed, true);
        }
      })
      .finally(() => {
        if (this.inflight === inflight) {
          this.inflight = null;
        }
      });
    return true;
  }

  /** Locks every unanswered question/form card — called when a later user message lands. */
  private lockOpenCards(): void {
    this.timeline.update((t) => t.map((e) =>
      (e.kind === 'question' || e.kind === 'questionForm') && !e.answered
        ? { ...e, answered: true }
        : e));
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
    let accepted = files.filter((f) => f.type.startsWith('image/') || f.type === 'application/pdf');
    // The per-turn cap counts chips already earned plus uploads still in flight — per-file
    // uploads would otherwise walk past any server per-batch limit one file at a time.
    const room = this.maxPendingAttachments - this.pendingAttachments().length - this.uploadsInFlight();
    if (accepted.length > Math.max(0, room)) {
      const refused = accepted.slice(Math.max(0, room));
      accepted = accepted.slice(0, Math.max(0, room));
      this.append({
        kind: 'assistant',
        text: `Attachment limit — a message carries at most ${this.maxPendingAttachments} files; ${refused.length === 1 ? `'${refused[0].name}' was` : `${refused.length} files were`} not added.`,
        notice: true,
      });
    }
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

  // ── liveness plumbing (dead-air failure + working recency) ─────────────────────────────────

  private markFrame(): void {
    this.frameFresh.set(true);
    if (this.workingTimer !== null) {
      clearTimeout(this.workingTimer);
    }
    if (this.workingWindowMs > 0) {
      this.workingTimer = setTimeout(() => {
        this.workingTimer = null;
        this.frameFresh.set(false);
      }, this.workingWindowMs);
    }
  }

  private clearWorking(): void {
    if (this.workingTimer !== null) {
      clearTimeout(this.workingTimer);
      this.workingTimer = null;
    }
    this.frameFresh.set(false);
  }

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
