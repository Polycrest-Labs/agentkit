/**
 * The v2 frame vocabulary — the kit-owned wire protocol, mirrored 1:1 from the C# records in
 * `src/AgentKit/Wire/AgentFrames.cs` and conformance-tested against the shared fixtures in
 * `protocol/fixtures/` (see `wire-fixtures.spec.ts`). The SSE `event:` name is the SOLE
 * discriminant: `parseFrame` reassembles `{...payload, type: eventName}`, so a hostile payload
 * `type` can never override it, and payloads must be non-null, non-array JSON objects.
 *
 * Terminal convention: a healthy turn always closes with `message` (authoritative final text) +
 * `done`, or a safe `error` — and the server emits those only AFTER host finalization commits.
 * A stream that merely stops is indistinguishable from success on the wire, so the turn state
 * machine treats silence as failure (dead-air timeout) and a clean close without a terminal as an
 * error. `heartbeat` proves liveness in between and is never rendered.
 *
 * Hosts layer domain frames on top: any event type outside this union falls through the state
 * machine's `onDomainFrame` hook (e.g. Groundsworth's `suggestion` and `filters`), so a host can
 * extend the wire without forking the kit. Domain event names match `^[a-z][a-z0-9-]*$` and never
 * shadow a core name.
 */

/** Opens the stream: the turn id and the routed provider/model serving it. Informational — a
 * "working" affordance is usually already up; hosts rarely render this. */
export interface LoadedFrame {
  type: 'loaded';
  turnId: string;
  provider: string;
  model: string;
}

/** One chunk of streamed assistant text. */
export interface TokenFrame {
  type: 'token';
  delta: string;
}

/** A tool call started or finished. `label` is the server-supplied display label
 * (`AgentTool.DisplayLabel`); absent → hosts show the raw name. */
export interface ToolFrame {
  type: 'tool';
  name: string;
  phase: 'start' | 'end';
  label?: string | null;
}

/** One pickable option in an {@link AgentQuestion}. */
export interface QuestionOption {
  id: string;
  label: string;
  /** Optional helper line under the label. */
  detail?: string | null;
}

/** A bounded question the assistant raises; rendered as an interactive card, answered as the next
 * user turn (non-blocking — the stream ends normally after asking). */
export interface AgentQuestion {
  id: string;
  prompt: string;
  options?: QuestionOption[];
  /** Checkboxes (true) vs radios (false). */
  multiSelect?: boolean;
  /** Show an "Other…" free-text row. */
  allowOther?: boolean;
  otherPlaceholder?: string | null;
}

/** A batch of questions asked together as one form (one Submit). */
export interface AgentQuestionForm {
  id: string;
  intro?: string | null;
  questions?: AgentQuestion[];
}

/** One question card. */
export interface QuestionFrame {
  type: 'question';
  question: AgentQuestion;
}

/** One batched question form. */
export interface QuestionFormFrame {
  type: 'questionForm';
  form: AgentQuestionForm;
}

/** Tappable next-step chips. Ephemeral — never persisted; chips prefill the composer, never
 * auto-send. */
export interface FollowupsFrame {
  type: 'followups';
  prompts: string[];
}

/** A web citation surfaced while streaming (rendered as a deduped footer). */
export interface CitationFrame {
  type: 'citation';
  title?: string | null;
  url?: string | null;
}

/** Aggregated token usage for the turn. Optional display; hosts may suppress server-side. */
export interface UsageFrame {
  type: 'usage';
  inputTokens?: number | null;
  outputTokens?: number | null;
}

/** Liveness only — a monotone `seq` that feeds the dead-air timer; never rendered. */
export interface HeartbeatFrame {
  type: 'heartbeat';
  seq: number;
}

/** The authoritative final assistant text. Streamed tokens are the fallback only when this frame
 * is absent. Arrives only after the host's durable finalization committed. */
export interface MessageFrame {
  type: 'message';
  text: string;
}

/** Terminal success — never emitted before host finalization commits. */
export interface DoneFrame {
  type: 'done';
  turnId: string;
  title?: string | null;
}

/** Terminal failure after the SSE headers went out. `code` is a stable machine-readable code;
 * `detail` is present only when the server marked the text safe/domain-facing (infrastructure
 * exception detail never crosses the wire). */
export interface ErrorFrame {
  type: 'error';
  code: string;
  detail?: string | null;
  turnId?: string | null;
}

/** The core union — what the state machine understands natively. */
export type AgentFrame =
  | LoadedFrame
  | TokenFrame
  | ToolFrame
  | QuestionFrame
  | QuestionFormFrame
  | FollowupsFrame
  | CitationFrame
  | UsageFrame
  | HeartbeatFrame
  | MessageFrame
  | DoneFrame
  | ErrorFrame;

/** A core frame or a host domain frame (unknown types fall through to `onDomainFrame`). */
export type AgentFrameOf<TDomain extends { type: string } = never> = AgentFrame | TDomain;
