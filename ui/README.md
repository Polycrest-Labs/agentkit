# @polycrestlabs/agentkit-ui

The AgentKit conversation UI kit for Angular — the UI half of the AgentKit library
(one repo, one version, one CHANGELOG with the .NET half). Provenance: the aws chat
kit (`ui/src/app/shared/chat`, commit `28131fd`) reconciled onto the frame vocabulary
the Groundsworth job-turn channel already emits.

## What's in the box

| Module | What it is |
| --- | --- |
| `frames` | The core event union: `loaded · suggestion · changeset · heartbeat · error`, plus `AgentFrameOf<TDomain>` for host domain frames |
| `sse` | `parseFrame` / `readSseStream` / `httpErrorMessage` — the fetch+SSE plumbing |
| `turn-state` | `AgentTurnState` — the agent-turn state machine (transcript, liveness, dead-air timeout, refusals, attachment upload-then-chip flow) |
| `tokens` | `AGENT_TRANSPORT` (required slot) and `AGENT_CONFIRM` (optional slot, package-owned default), plus the optional `AgentChatCapable` / `AgentAttachmentCapable` capabilities |
| `chat-rail` | `<agent-chat-rail>` — the multi-chat list rail (list / new / delete-with-confirm / in-progress pulse) |
| `markdown.pipe` | `agentMarkdown` — sanitizer-friendly markdown-to-HTML for assistant text |

## The transport contract

The kit never sees routes, auth headers, or wire bodies. A host implements the minimal
required core against its own `/api/*` routes:

```ts
interface AgentTransport<TFrame extends { type: string }> {
  streamTurn(body: AgentTurnBody, onFrame: (frame: TFrame) => void, signal?: AbortSignal): Promise<void>;
}
```

Rules a transport must follow:

- **The transport owns the wire body.** The kit hands it the composed turn
  (`{ text, attachmentAssetIds?, ...extras }`); the transport maps that onto its route's
  shape (Groundsworth's job lane posts `{ message: text }`).
- **Resolve when the stream closes; resolve QUIETLY when `signal` aborts** — a caller
  hanging up is not a failure.
- **Reject with `AgentTurnRefusal(message, status)`** for non-SSE refusals — the state
  machine maps 409 to the busy line and 503 to the latched `unavailable` signal. Build
  the message with `httpErrorMessage(response, fallback)`.

Chat CRUD (`AgentChatCapable`) and attachment upload (`AgentAttachmentCapable`) are
optional capabilities a transport may also implement — multi-chat surfaces have both,
a single-thread job lane has neither.

## The frame vocabulary

The kit types what the server emits rather than inventing a second protocol:

| Frame | Payload | Semantics |
| --- | --- | --- |
| `loaded` | `turnId, provider, model` | Stream opened; informational |
| `suggestion` | `section, path, value, note?` | A draft card — the HOST renders and applies it (drafts, never concludes) |
| `changeset` | `turnId, text?, suggestions` | Terminal success: closing text + suggestion count |
| `heartbeat` | `seq, round?` | Liveness only — consumed by the state machine, never rendered |
| `error` | `turnId, error, detail?` | Terminal failure after headers went out; `detail` null for infrastructure failures |

A healthy turn always ends with `changeset` or `error`. The state machine treats
silence as failure: any frame resets the dead-air timer (default 90 s, configurable via
`AgentTurnStateOptions.deadAirMs`); starvation transitions to the error state exactly
once and hangs up. Unknown frame types fall through to `onDomainFrame` — never an error.

## Build & test

```bash
npm ci
npm run build   # ng-packagr → dist/
npm test        # vitest + jsdom
```

Angular (`@angular/core`, `@angular/common` ^22) and rxjs are peerDependencies — the kit
never pins a host's Angular version.
