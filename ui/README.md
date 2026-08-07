# @polycrestlabs/agentkit-ui

The AgentKit conversation UI kit for Angular — the UI half of the AgentKit library
(one repo, one version, one CHANGELOG with the .NET half). 0.4.0 completes the
extraction the 0.2.0 intake started: the full conversation surface (transcript,
composer, question cards, proposal presentation, followup chips, streaming) ported
from its most evolved sibling copies (the aws chat kit and vacadock's Ask lane) and
restyled onto `--agentkit-*` tokens.

## What's in the box

| Module | What it is |
| --- | --- |
| `frames` | The v2 core frame union: `loaded · token · tool · question · questionForm · followups · citation · usage · heartbeat · message · done · error`, plus `AgentFrameOf<TDomain>` for host domain frames — mirrored 1:1 from the C# `Wire/AgentFrames.cs` and conformance-tested against `protocol/fixtures/` |
| `sse` | `parseFrame` / `readSseStream` / `httpErrorMessage` — the fetch+SSE plumbing (hardened: object-only payloads, event-name guard, the event name is the sole discriminant) |
| `turn-state` | `AgentTurnState` v2 — the ordered rich `timeline` (messages, durable question/form cards, domain entries), `streamingText`/`activeTool`/`followups`/`citations`/`usage`, `hydrate()` from host persistence, capability-gated `stop()`, liveness (`working`, dead-air), refusals, and the attachment upload-then-chip flow |
| `tokens` | `AGENT_TRANSPORT` (required slot) and `AGENT_CONFIRM` (optional slot, package-owned default), plus the optional `AgentChatCapable` / `AgentAttachmentCapable` capabilities and `AgentTransportCapabilities` (`serverCancel` gates the visible Stop) |
| `components/` | `<agent-chat-panel>` (the assembled surface), `<agent-transcript>`, `<agent-composer>`, `<agent-question-card>`, `<agent-proposal-card>` + `<agent-proposal-list>` (presentation-only — proposals are host-domain data), `<agent-followup-chips>`, `<agent-thinking>`, and the `*agentCard` host-domain template directive |
| `testing/` | `scriptedTransport(frames[][])` — the fixture-replaying stub transport; demos, specs and host prototyping run key-less against the real frame contract |
| `chat-rail` | `<agent-chat-rail>` — the multi-chat list rail (list / new / delete-with-confirm / in-progress pulse); optional |
| `markdown.pipe` | `agentMarkdown` — sanitizer-friendly markdown-to-HTML for assistant text |

## Styling: one import, tokens only

The kit ships a single stylesheet driven entirely by `--agentkit-*` custom properties.
Import it once in the host:

```css
/* styles.css or a vendor layer */
@import '@polycrestlabs/agentkit-ui/styles.css';
```

Then map the tokens to your design system in one block (all have working light
defaults):

```css
:root {
  --agentkit-accent: var(--brand-primary);
  --agentkit-accent-strong: var(--brand-primary-strong);
  /* … */
}
```

Dark mode is **opt-in only**: set `data-agentkit-theme="dark"` on an ancestor. The kit
never activates dark from `prefers-color-scheme`, so a light host cannot acquire an
untested half-dark surface. Styling hooks are the stable `agentkit-*` classes and
`data-agentkit-part` attributes — `data-testid` values are test API, never styling API.

## The assembled surface

```html
<agent-chat-panel [starters]="['What can you do?']">
  <!-- host domain cards project by frame type -->
  <ng-template agentCard="suggestion" let-data let-entry="entry">
    <my-suggestion-card [suggestion]="data" />
  </ng-template>
</agent-chat-panel>
```

`agent-chat-panel` injects `AGENT_TRANSPORT`, builds the turn state, and assembles
transcript / chips / composer in a `min-h-0` flex column (the transcript owns the
scroll region; the composer is flex-pinned — never a `calc(100vh-…)` magic number).
Hosts wanting different chrome compose the pieces directly and pass their own
`AgentTurnState`.

Behavioral contracts baked in:

- **Followup chips prefill the composer and focus it — never auto-send.**
- **`message` is the authoritative final text**; streamed tokens are the fallback only
  when no `message` arrived before `done`.
- **Question cards are non-blocking**: the stream ends normally; the answer is the next
  user turn; a later message locks earlier cards; hosts hydrate durable answers.
- **Stop renders only on a `serverCancel` transport** and appends "Stopped." only after
  the server confirmed the cancellation. Quiet `abort()` remains teardown.
- **Scroll sticks to the bottom — never while the user scrolled up to read**; a
  jump-to-latest button brings them back.

## The transport contract

The kit never sees routes, auth headers, or wire bodies. A host implements the minimal
required core against its own `/api/*` routes:

```ts
interface AgentTransport<TFrame extends { type: string }> {
  streamTurn(body: AgentTurnBody, onFrame: (frame: TFrame) => void, signal?: AbortSignal): Promise<void>;
  capabilities?: { serverCancel?: boolean };
  cancelTurn?(): Promise<void>; // required when serverCancel — must actually cancel server work
}
```

Rules a transport must follow:

- **The transport owns the wire body.** The kit hands it the composed turn
  (`{ text, attachmentAssetIds?, ...extras }`); the transport maps that onto its
  route's shape.
- **Resolve when the stream closes; resolve QUIETLY when `signal` aborts** — a caller
  hanging up is not a failure.
- **Reject with `AgentTurnRefusal(message, status)`** for non-SSE refusals — the state
  machine maps 409 to the busy line and 503 to the latched `unavailable` signal. Build
  the message with `httpErrorMessage(response, fallback)`.
- **Advertise `serverCancel` only when abort genuinely cancels server-side turn
  execution** (and implement `cancelTurn`). Without it no Stop control ever renders.

Chat CRUD (`AgentChatCapable`) and attachment upload (`AgentAttachmentCapable`) are
optional capabilities a transport may also implement. Asset ids are **strings** —
hosts choose their own identity scheme.

## The frame vocabulary (v2)

One kit-owned wire protocol, defined in this repo and spoken by both halves. The SSE
`event:` name is the sole discriminant; payloads are single-line JSON objects that
never repeat it. The canonical fixtures live in `../protocol/fixtures/` and are
conformance-tested from both C# (round-trip) and TS (parse) — drift on either side
fails CI.

| Frame | Payload | Semantics |
| --- | --- | --- |
| `loaded` | `turnId, provider, model` | Always first; informational |
| `token` | `delta` | Streamed assistant text (live `streamingText`) |
| `tool` | `name, phase, label?` | Tool activity pill; `label` is server-supplied |
| `question` / `questionForm` | `question` / `form` | Non-blocking question cards; durable |
| `followups` | `prompts[]` | Ephemeral chips; prefill, never auto-send |
| `citation` | `title?, url?` | Deduped citations footer |
| `usage` | `inputTokens?, outputTokens?` | Optional usage line |
| `heartbeat` | `seq` | Liveness only — never rendered |
| `message` | `text` | Authoritative final text, after host finalization |
| `done` | `turnId, title?` | Terminal success — never before the host commit |
| `error` | `code, detail?, turnId?` | Stable code; `detail` only when explicitly safe |

A healthy turn always ends with `message` + `done` or `error`. The state machine treats
silence as failure: any frame resets the dead-air timer (default 90 s, configurable via
`AgentTurnStateOptions.deadAirMs`); starvation transitions to the error state exactly
once and hangs up. Unknown frame types fall through to `onDomainFrame` — never an error.

## Build & test

```bash
npm ci
npm run build   # ng-packagr → dist/ (styles.css ships as a package asset)
npm test        # vitest + jsdom (+ the Angular compiler plugin for component specs)
```

Angular (`@angular/core`, `@angular/common` ^22) and rxjs are peerDependencies — the kit
never pins a host's Angular version.
