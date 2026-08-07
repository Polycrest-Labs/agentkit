import { InjectionToken } from '@angular/core';

/**
 * The two couplings doc #1 ruled cut (2026-07-20) when the kit left its source app:
 * the transport becomes a required injection slot with a minimal core, and the confirm
 * dialog becomes an optional slot with a package-owned default.
 */

/** The composed turn the state machine hands the transport. The TRANSPORT owns the wire shape —
 * it maps this onto its own route's body (Groundsworth's job lane posts `{ message: text }`;
 * the aws surfaces post `{ text, attachmentAssetIds }`). */
export interface AgentTurnBody {
  text: string;
  /** Uploaded asset ids riding this turn (absent when no attachments are pending). Strings —
   * hosts choose their own identity scheme (Groundsworth: an attachment-row uuid). */
  attachmentAssetIds?: string[];
  /** Host extras merged in by the caller (e.g. import's dirty draftJson). */
  [extra: string]: unknown;
}

/** A refusal before any frame arrived (the request itself failed): carries the HTTP status so the
 * state machine can distinguish 409 (turn already running) and 503 (no provider configured) from
 * a generic failure. Transports build the message with `httpErrorMessage`. */
export class AgentTurnRefusal extends Error {
  constructor(message: string, readonly status: number) {
    super(message);
  }
}

/**
 * The minimal REQUIRED transport core: run one turn, invoking `onFrame` per parsed SSE frame,
 * resolving when the stream closes — or QUIETLY when `signal` aborts (a caller hanging up is not
 * a failure). Reject with {@link AgentTurnRefusal} for non-SSE refusals (409/503). Auth headers,
 * routes and the wire body are the transport's own business — the kit never sees them.
 *
 * Chat CRUD and attachment upload are OPTIONAL capabilities a transport may also implement
 * ({@link AgentChatCapable}, {@link AgentAttachmentCapable}) — the aws surfaces have both, the
 * Groundsworth job lane has neither.
 */
export interface AgentTransport<TFrame extends { type: string } = { type: string }> {
  streamTurn(body: AgentTurnBody, onFrame: (frame: TFrame) => void, signal?: AbortSignal): Promise<void>;
}

/** What the chat rail needs to render a chat row. */
export interface AgentChatSummary {
  chatId: number;
  title: string;
  inProgress: boolean;
}

/** Optional capability: multi-chat surfaces (list / create / delete drive the chat rail). */
export interface AgentChatCapable<TSummary extends AgentChatSummary = AgentChatSummary> {
  listChats(): Promise<TSummary[]>;
  createChat(): Promise<TSummary>;
  deleteChat(chatId: number): Promise<unknown>;
}

/** What the attachment-chip flow needs back from an upload (the C# `StoredAgentAsset` mirror). */
export interface UploadedAgentAsset {
  /** String asset id — hosts choose their own identity scheme. */
  assetId: string;
  originalName?: string;
  contentType?: string;
  /** "image" | "document" for kit-stored assets; `source` assets never ride a turn. */
  kind?: string;
  byteSize?: number;
}

/** Optional capability: composer attachment upload (the ids ride the next turn's body). */
export interface AgentAttachmentCapable {
  uploadAttachments(files: File[]): Promise<UploadedAgentAsset[]>;
}

/** The REQUIRED transport slot a host provides at its surface. */
export const AGENT_TRANSPORT = new InjectionToken<AgentTransport>('AGENT_TRANSPORT');

/** The confirm seam surfaces use before destructive acts (chat delete). */
export interface AgentConfirm {
  ask(options: { title: string; message: string }): Promise<boolean>;
}

/** Package-owned default: the browser's own confirm. Hosts with a dialog system override the
 * token; hosts without one still get a working (if plain) confirm. */
export class BuiltInAgentConfirm implements AgentConfirm {
  ask(options: { title: string; message: string }): Promise<boolean> {
    return Promise.resolve(globalThis.confirm ? globalThis.confirm(`${options.title}\n\n${options.message}`) : true);
  }
}

/** The OPTIONAL confirm slot — defaults to {@link BuiltInAgentConfirm}. */
export const AGENT_CONFIRM = new InjectionToken<AgentConfirm>('AGENT_CONFIRM', {
  providedIn: 'root',
  factory: () => new BuiltInAgentConfirm(),
});
