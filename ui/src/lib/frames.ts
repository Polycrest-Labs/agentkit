/**
 * The kit's core event union IS the frame vocabulary the Groundsworth job-turn channel already
 * emits — `loaded · suggestion · changeset · heartbeat · error` — typed here rather than invented
 * as a second protocol. A stream that merely stops is indistinguishable from success on the wire,
 * so the server's convention is: a terminal frame (`changeset` or `error`) always closes a healthy
 * turn, and `heartbeat` proves liveness in between; the turn state machine treats silence as
 * failure (dead-air timeout) and never renders a heartbeat.
 *
 * Hosts layer domain frames on top: any event type outside this union falls through the state
 * machine's `onDomainFrame` hook (the aws kit's pattern), so a host can add e.g. `dataset` or
 * `question` frames without forking the kit.
 */

/** Opens the stream: the turn id and the routed provider/model serving it. Informational — a
 * "working" affordance is usually already up; hosts rarely render this. */
export interface LoadedFrame {
  type: 'loaded';
  turnId: string;
  provider: string;
  model: string;
}

/** One suggestion card. The card body is host-domain (Groundsworth: a field-edit draft); the kit
 * hands it to the host untouched — drafting is the kit's job, applying is the user's. */
export interface SuggestionFrame {
  type: 'suggestion';
  section: string;
  path: string;
  value?: unknown;
  note?: string | null;
}

/** The turn's terminal success frame: the assistant's closing text and the count of suggestion
 * frames that preceded it. */
export interface ChangesetFrame {
  type: 'changeset';
  turnId: string;
  text?: string | null;
  suggestions?: number;
}

/** Liveness only — a monotone `seq` that keeps idle-timeout proxies from killing a long model
 * round, with an optional `round` stamped at tool-round boundaries. Consumed internally by the
 * turn state machine (liveness timer); never rendered. */
export interface HeartbeatFrame {
  type: 'heartbeat';
  seq: number;
  round?: number;
}

/** The turn failed AFTER the SSE headers went out, so the refusal arrives as the terminal frame
 * rather than an HTTP status. `detail` is null for infrastructure failures (their text is
 * audit-only) and carries the sentence for a domain refusal. */
export interface ErrorFrame {
  type: 'error';
  turnId?: string;
  error: string;
  detail?: string | null;
}

/** The core union — what the state machine understands natively. */
export type AgentFrame = LoadedFrame | SuggestionFrame | ChangesetFrame | HeartbeatFrame | ErrorFrame;

/** A core frame or a host domain frame (unknown types fall through to `onDomainFrame`). */
export type AgentFrameOf<TDomain extends { type: string } = never> = AgentFrame | TDomain;
