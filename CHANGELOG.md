# AgentKit changelog

AgentKit started life inside the **vacadock** repo (`lib/AgentKit`), was copied into the
**hsa** repo on 2026-07-08 (at vacadock commit `b21897e027cee71633c867a113aa0fcaef357ac4`),
and evolved in both places for two days. This standalone repo is the merge of the two
lines — see entry 9 — and ships as the **`Polycrest.AgentKit`** NuGet package.

Entries 0–8 below are the hsa-side log, written while the fork was live (their file paths
reflect the hsa layout: `lib/AgentKit*`). The vacadock side contributed one change in the
same window: the Gemini native provider (entry 9 describes how the two lines merged).

---

## [0.4.0] — the conversation kit (in progress)

**The one sanctioned pre-1.0 break.** Every release until now honored the "0.x stays additive"
rule; 0.4.0 amends it exactly once, deliberately: the wire protocol both halves speak is redesigned
as a single kit-owned vocabulary (frames v2), and the code that spoke the old dialect — the one
consumer's hand-written SSE controllers and the 0.2.0 frame union — is deleted by the same
coordinated release that ships this version. The break is nearly free now, with exactly one kit
consumer to migrate, and will never be repeated: from here the vocabulary evolves additively.

**Frames v2** (`src/AgentKit/Wire/AgentFrames.cs` ↔ `ui/src/lib/frames.ts`, conformance-tested
against `protocol/fixtures/*.json` from BOTH suites — drift on either side fails that repo's CI):

| Frame | Payload | Produced from |
| --- | --- | --- |
| `loaded` | `{turnId, provider, model}` | host-resolved `AgentTurnDescriptor`; always first |
| `token` | `{delta}` | `TokenDelta` |
| `tool` | `{name, phase: start\|end, label?}` | `ToolCallStarted/Finished` + `AgentTool.DisplayLabel` |
| `question` / `questionForm` | `{question}` / `{form}` | the kit question tools |
| `followups` | `{prompts[]}` | `suggest_followups`; ephemeral, prefill-never-auto-send |
| `citation` | `{title?, url?}` | `CitationFound` |
| `usage` | `{inputTokens?, outputTokens?}` | `UsageReport`; suppressible |
| `heartbeat` | `{seq}` | the session's pump (default 15 s); never rendered |
| `message` | `{text}` | successful outcome, AFTER host finalization; authoritative final text |
| `done` | `{turnId, title?}` | terminal success, never before the host commit |
| `error` | `{code, detail?, turnId?}` | stable code; `detail` only when explicitly safe |
| *domain* | host-defined object | `ToolOutcome.DomainEvents` → host `DomainFrameMapper` |

`changeset` and core `suggestion` retire (the sole consumer's dialect — `suggestion`/`filters`
demote to Groundsworth domain frames). The SSE `event:` name is the sole discriminant: payloads
are object-only, TS reassembles `{...payload, type: eventName}` (a hostile payload `type` can
never spoof a terminal), domain names match `^[a-z][a-z0-9-]*$` and can't shadow core names.

Shipped so far (phase 1 of the conversation-kit build):

- **`AgentTurnSession` + `AgentWireOptions`** (dependency-free): nonterminal frame sequence
  (`loaded` first, heartbeat merged through one bounded channel, fake-time testable via
  `TimeProvider`) plus an awaited `AgentTurnOutcome {FinalText, Exception, WasCanceled,
  Interactions, Citations, Usage}`. Completion/fault/cancel complete the OUTCOME — the session
  never emits success/error terminals; that's the SSE writer's job, gated on the host finalizer.
- **Question/follow-up tools promoted from the aws line**: `AgentQuestion`/`QuestionOption`/
  `AgentQuestionForm` DTOs (field shapes 1:1 — they are the wire contract),
  `AgentQuestionTools.AskQuestion/AskQuestions/SuggestFollowups` factories with the guardrail
  prose intact, typed `QuestionEvent`/`QuestionFormEvent`/`FollowupsEvent` domain events the
  session maps natively, and `AgentQuestionHistory.Render` (the anti-re-ask bracket-note idiom,
  fed from durable host records).
- **`AgentTool.DisplayLabel`** (+ `AgentTool.Raw` factory; `Typed` gains the parameter) —
  server-supplied tool labels replace per-app label maps.
- **Attachment/multimodal contracts**: scope-generic `IAgentAttachmentStore<TScope>` with rich
  DTOs (`StoredAgentAsset` — string asset ids), `AgentAttachmentLimits` (4 files / 25 MB /
  exact-MIME allowlist defaults), and `AgentHistoryMessage.Parts` (ordered text/image/document
  parts) so hosts can replay eligible prior attachments as real multimodal history.
- **TS mirror hardening**: `parseFrame` requires a valid event name and a non-null non-array
  object payload; `AgentTurnBody.attachmentAssetIds`/`UploadedAgentAsset.assetId` retype to
  string; the turn state adopts the v2 terminals (`message` authoritative, streamed tokens as
  fallback) ahead of its full v2 rework.
- `DtoJsonSchema.For` is now safe under concurrent first use (schema generation serialized —
  two racing static initializers could previously hit "options instance is read-only").

---

## [0.3.0] — the `openai-responses` provider kind (2026-08-05)

A fourth provider kind, `openai-responses`: the **OpenAI platform's** Responses API, the
sibling of `azure-openai` for accounts that talk to OpenAI directly rather than through an
Azure deployment.

It exists because of a hard refusal, not a preference. OpenAI will not serve function tools
and reasoning together on chat completions for its newer models:

> Function tools with reasoning_effort are not supported for gpt-5.6-luna in
> /v1/chat/completions. To use function tools, use /v1/responses or set reasoning_effort to
> 'none'.

An agent loop *is* function tools, so on the `openai-compat` path a reasoning model has to
run with its reasoning switched off — `AgentRunner` returns HTTP 400 on every turn
otherwise. This kind is how a host gets both. It also carries the hosted `web_search` tool
and URL citations, exactly as the Azure Responses path does.

One behavioural note worth recording, because it was the build's chief risk: `ForHistory`
strips reasoning traces when re-sending an assistant turn, and the Responses API was known
to reject a resubmitted `web_search_call` without its paired reasoning item. **Function
calls do not behave that way** — a live three-hop, two-tool loop against `gpt-5.6-luna` with
`reasoning_effort: medium` completed cleanly with the traces stripped. No change to
`ForHistory` was needed.

`IsConfigured` gains one exception for this kind: an endpoint is optional, because the SDK
already knows where OpenAI lives. Requiring one would have made an otherwise-complete config
*silently* unroutable — routing skips unconfigured providers, so the symptom is a model that
vanishes from the catalog rather than an error anyone can read.

Additive per the two-consumer rule: every existing `azure-openai` / `openai-compat` /
`gemini-native` config is untouched. Tests 111 green.

---

## [0.2.1] — final-hop completion text (2026-08-04)

`AgentRunner`'s terminal `Completed.Text` now contains only the final model
hop's text. Earlier tool-call hops may stream preambles such as “I'll check
that”; those deltas still stream through `TokenDelta` and remain in the model's
conversation history, but they are no longer concatenated into the host's one
closing message. This restores the result semantics of Groundsworth's retired
tool loop and prevents run-together output such as “I'll check that.The client
is …”. A mixed text-plus-tool first-hop regression test pins the contract.

The .NET package advances to `Polycrest.AgentKit` 0.2.1. The UI package is
unchanged and remains `@polycrestlabs/agentkit-ui` 0.2.0.

---

## [0.2.0] — document input + the UI half + a CI lane (2026-08-04)

Entries 10 (document input on the request surface), 11 (the ui/ workspace —
first npm publish of `@polycrestlabs/agentkit-ui`), and the push/PR CI test
workflow (`.github/workflows/ci.yml`: dotnet build+test and ui build+test on
every push/PR — until now tests ran only inside the release workflow).
Additive over 0.1.0 per the two-consumer compat rule (vacadock stays on 0.1.0
untouched; upgrading is a recompile, not a rewrite).

Cut checks, 2026-08-04: `AgentKit.Tests` 104 green · ui vitest 19 green · CI
run 30957107775 green on both jobs. The standard `AgentKit.Smoke` was skipped
on the cutting machine (no `agentkit-smoke` user-secrets; its baked-in catalog
names deployments the available Azure resource does not host) — instead the
NEW surface got a live proof: a one-page PDF through
`ILlmClient.CompleteAsync(documents:)` → `AzureOpenAIProvider` (Responses,
`gpt-4o-mini` deployment) returned the document's embedded marker text
verbatim, confirming the M.E.AI `DataContent(application/pdf)` →
`input_file` mapping end-to-end.

---

## [0.1.0] — first NuGet release (2026-07-09)

Everything below, merged: entries 0–8 (hsa) + the vacadock Gemini native provider,
reconciled per entry 9.

---

## Entries

### 0. Baseline: unmodified copy (2026-07-08)

Unmodified copy of AgentKit at the source commit above, with exactly two structural
fixes required by the new location — no behavior changes:

- `lib/AgentKit.Tests/AgentKit.Tests.csproj`: project reference path
  `..\..\lib\AgentKit\AgentKit.csproj` → `..\AgentKit\AgentKit.csproj`
  (tests moved from `tests/` to `lib/` relative to the library).
- `lib/AgentKit.Smoke/AgentKit.Smoke.csproj`: `<UserSecretsId>` `vacadock-web` → `hsa-web`,
  and `lib/AgentKit.Smoke/Program.cs`: `AddUserSecrets("vacadock-web")` → `"hsa-web"`
  (Smoke reads this repo's dev secrets for live checks).

Upstream intent: none — these are copy mechanics, not library changes.

### 1. Smoke: vision check image 4x4 → 64x64 (2026-07-08)

- `lib/AgentKit.Smoke/Program.cs` (`Vision()`): the embedded solid-red test PNG grew
  from 4x4 to 64x64.
- Why: the Azure OpenAI resource used here (`new2026-resource`, Responses API) rejects
  the 4x4 image with HTTP 400 "The image data you provided does not represent a valid
  image"; 64x64 passes. No library code touched.
- Upstream intent: yes — harmless in vacadock and makes the smoke portable across
  stricter Azure resources.

### 2. Package-graph note: OpenAI must stay ≤ 2.10.0 while Azure.AI.OpenAI is 2.9.0-beta.1 (2026-07-08)

- No AgentKit file changed (a 10.7.0 M.E.AI bump was tried and reverted).
- Why recorded: hsa's `core` pinned `OpenAI 2.11.0`; NuGet unified the whole graph
  upward and `AzureOpenAIClient.GetResponsesClient()` failed at runtime with
  `MissingMethodException: OpenAI.Responses.ResponsesClient..ctor` — Azure.AI.OpenAI
  2.9.0-beta.1 (the latest available) is compiled against OpenAI 2.10.0. Bumping
  M.E.AI(.OpenAI) to 10.7.0 (which targets OpenAI 2.11.0) does NOT help while
  Azure.AI.OpenAI stays 2.9.0-beta.1. Resolution: hsa's core downgraded its direct
  pin to `OpenAI 2.10.0` so the graph unifies where Azure.AI.OpenAI expects it.
- Upstream intent: knowledge only — confirms vacadock's README gotcha ("bump the
  pair together") and adds the concrete failure signature; when a newer
  Azure.AI.OpenAI ships, both repos can move the trio together.

### 3. IImageStore hook — replayable vision logs (2026-07-08)

- New `lib/AgentKit/Logging/IImageStore.cs`: `Persist(sha256, mediaType, bytes)` +
  `NullImageStore` default.
- `lib/AgentKit/Logging/LoggingChatClient.cs`: optional `IImageStore` ctor param; at
  the moment `Render` replaces a `DataContent`'s bytes with the `sha256:…` hash, the
  bytes are offered to the store (per-image swallow-and-log — persistence may degrade,
  never fault a turn, mirroring the `ICompletionSink` contract).
- `lib/AgentKit/Providers/ChatClientFactory.cs`: optional `IImageStore` ctor param,
  passed to every `LoggingChatClient`.
- `lib/AgentKit/AgentKitServiceCollectionExtensions.cs`: `TryAddSingleton<IImageStore>`
  (no-op default) resolved into the factory — hosts pre-register a real store before
  `AddAgentKit` (hsa: `web/Services/Llm/BlobImageStore.cs` → `llm-logs/images/{sha256}`).
- Tests: `lib/AgentKit.Tests/ImageStoreTests.cs` (hash/bytes fidelity, no-op default,
  throwing store never faults, record carries the same hash).
- Why: `LoggingChatClient` hashes image bytes before any sink runs, so a host sink
  alone can never make vision logs replayable — the hook must live inside AgentKit.
- Upstream intent: yes — vacadock wants the same replayability for its vision logs;
  candidate for the shared NuGet package.

### 4. AgentJson: tolerant DateTime reads (2026-07-09)

- `lib/AgentKit/AgentJson.cs`: new `TolerantDateTimeConverterFactory` registered in
  `AgentJson.Options` — ISO 8601 first, then the common human formats extraction
  models actually emit ("05/31/2026", "May 31, 2026", "2026/05/31", …); unparseable
  text coerces to null/default instead of throwing. Writes stay round-trip "o".
- Tests: `lib/AgentKit.Tests/TolerantDateTimeTests.cs` (format matrix, garbage → null,
  never throws).
- Why: hsa's phase-7 cutover routes typed extraction through
  `ILlmClient.CompleteJsonAsync<T>` → `AgentJson`. The legacy pipeline parsed model
  output with a multi-format-tolerant DateTime converter; strict ISO-only reads
  failed live completions (`$.date … not in a supported DateTime format`) that
  production has always accepted. Same tolerance philosophy as the existing
  tolerant-enum and tolerant-string converters.
- Upstream intent: yes — any vacadock DTO with a DateTime field gets the same
  robustness; candidate for the shared NuGet package.

### 5. Routine package bumps (2026-07-09)

- `lib/AgentKit/AgentKit.csproj`: Azure.Identity 1.13.2 → 1.21.0,
  Microsoft.Extensions.Options.ConfigurationExtensions 10.0.1 → 10.0.9.
- `lib/AgentKit.Smoke` / `lib/AgentKit.Tests`: Microsoft.Extensions.* 10.0.1 → 10.0.9,
  Microsoft.NET.Test.Sdk → 18.7.0, xunit.runner.visualstudio → 3.1.5,
  coverlet.collector → 10.0.1.
- Deliberately NOT bumped (entry 2's constraint still holds): Microsoft.Extensions.AI(.OpenAI)
  stay 10.6.0 and Azure.AI.OpenAI stays 2.9.0-beta.1 — 10.7.0 targets OpenAI 2.11.0, which
  Azure.AI.OpenAI 2.9.0-beta.1 (still the latest) breaks against at runtime. Move the trio
  together when a newer Azure.AI.OpenAI ships.
- Upstream intent: routine; vacadock can take the same bumps.

### 6. ModelCard.UpstreamModel — one upstream model behind multiple catalog entries (2026-07-09)

- `lib/AgentKit/Catalog/ModelCard.cs`: new optional `UpstreamModel`; `ApiModel` resolves
  `UpstreamModel ?? Id`. Providers (`AzureOpenAIProvider`, `OpenAICompatProvider`) now request
  `card.ApiModel` instead of `card.Id`.
- Why: hsa compares the same Gemini model through a free-tier key and a paid key — two provider
  entries, two catalog cards ("gemini-3.5-flash" and "gemini-3.5-flash-paid"), one upstream model
  name. Catalog Ids stay unique (router pins, item folders, and reports key on them).
- Tests: `lib/AgentKit.Tests/UpstreamModelTests.cs` (fallback, override, config binding).
- Upstream intent: yes — also useful for A/B-ing endpoints/regions serving the same model.

### 7. AgentJson numeric tolerance + LlmNoJsonException (2026-07-09, by Hank)

- `lib/AgentKit/AgentJson.cs`: `AllowTrailingCommas` + comment skipping; Safe(Nullable)
  Decimal/Int converters — out-of-range or non-numeric scalars in numeric fields coerce
  to null/0 instead of failing the completion (completes the absorption of core's
  deleted ForgivingJsonParsing into AgentJson).
- `lib/AgentKit/LlmClient.cs`: `CompleteJsonAsync<T>` now throws `LlmNoJsonException`
  when the reply carries no JSON (refusal, content filter, prose) instead of silently
  deserializing `"{}"` into a blank T — restores the legacy providers' throw-on-no-tool-call
  contract so retry policies engage and blank receipts are never persisted.
- Upstream intent: yes — both are general robustness; candidates for the shared NuGet.

### 8. AgentJson: restore the full legacy date-format set (2026-07-09)

- `lib/AgentKit/AgentJson.cs`: `TolerantDateTimeConverterFactory` (now `partial`) expands its `Formats`
  set to the full legacy `core.AI.NormalizationDateParser` list — European dotted/day-first
  (`19.08.2025`, `31/05/2025 20:11`), OCR meridiem (`…5:00:21P`), compressed-apostrophe
  (`Feb24'17 03:28PM`), `sept` token, `at`-joined, and `Uhr`/timezone-suffixed forms — plus the matching
  normalization (source-generated regexes for the `Uhr`/`sept`/single-letter-meridiem quirks, apostrophe
  and parenthetical stripping) applied before the format match, then the invariant fallback.
- Why: the AgentKit cutover routes typed extraction through `AgentJson`, whose US/ISO-centric `Formats`
  regressed dates production had always accepted (via the old `NormalizationDateParser` chain): European/OCR
  forms coerced to **null**, and a day-first `06/07/2025 20:11` parsed **month-first** (June 7 instead of
  July 6 — a silently wrong stored receipt date). Day-first slash forms are intentionally biased day-first,
  exactly as the legacy parser was, to match what production accepted. Companion to entry 7 (which restored
  the numeric/trailing-comma leniency of the same deleted parsing path).
- Tests: `lib/AgentKit.Tests/TolerantDateTimeTests.cs` (legacy-format matrix + a day-first-with-time
  regression guard).
- Upstream intent: yes — any vacadock DTO with a DateTime field parsed from model output gets the same
  robustness; candidate for the shared NuGet package.

### 9. The merge: hsa line + vacadock line → this repo (2026-07-09)

Three-way merged from the common baseline (vacadock `b21897e`, the copy point):

- **From hsa** (entries 1–8): 64x64 smoke vision image, `IImageStore` hook,
  `AgentJson` tolerant DateTime/numeric reads + `LlmNoJsonException`, the full legacy
  date-format set, routine package bumps, `ModelCard.UpstreamModel`.
- **From vacadock** (`e4683a3`): **Gemini native provider** — `gemini-native` provider
  kind, `GeminiNativeChatClient`/`GeminiNativeProvider` (Gemini `generateContent` API,
  the path that supports Search grounding), `GeminiSchema` sanitization + tests, and
  `LlmProviderOptions.IsConfigured` now requiring a key for every non-azure kind.
- **Conflict, resolved**: both sides independently built the same catalog-id-vs-wire-name
  feature — hsa as `UpstreamModel`/`ApiModel`, vacadock as `ModelName`/`WireName`.
  Canonical name is **`UpstreamModel`** (config) / **`ApiModel`** (resolved property);
  the Gemini client was renamed to match. Hosts using `"ModelName"` in config must
  rename the key to `"UpstreamModel"`.
- **Removed**: `ModelQuirks.GroundingViaExtraBody` (vacadock's deletion) — Gemini search
  grounding now rides the `gemini-native` provider instead of the openai-compat
  `extra_body` hack. No host code referenced it; the Smoke catalog's search card now
  uses `UpstreamModel` instead.
- **Repo mechanics**: layout moved to `src/AgentKit` + `smoke/AgentKit.Smoke` +
  `tests/AgentKit.Tests`; Smoke reads `agentkit-smoke` user-secrets; packaging metadata
  added (`Polycrest.AgentKit`, MIT); publish via GitHub Actions Trusted Publishing,
  mirroring polyauth.

### 10. Document (PDF) input on the request surface (2026-08-04)

The first consumer-driven 0.2.0 change: Groundsworth's seam retirement moves its
base64-PDF classification/extraction flows onto the library, which had `Images` only.
Everything is additive; the plumbing is the exact path images already ride
(`DataContent` is media-type-agnostic, and Microsoft.Extensions.AI.OpenAI 10.6.0 maps
`application/pdf` parts to Responses `input_file` items with the part's `Name` as the
filename).

- `LlmDocument(MediaType, Bytes, Name?)` beside `LlmImage`; `AgentTurnRequest.Documents`
  beside `Images`; both assemble as `DataContent` parts on the user message (documents
  carry `DataContent.Name` for the provider-side filename).
- **Interface note for external `ILlmClient` implementors**: the three `ILlmClient`
  methods gain an optional `IReadOnlyList<LlmDocument>? documents = null` parameter
  before `ct`. Source-compatible for callers (named `ct:` arguments unaffected);
  external *implementations* must add the parameter when they recompile against 0.2.0.
- `ModelCard.Documents` (default false), config-bound from `Llm:Models` like
  `Vision`/`Search`; `LlmWay` gains a `Documents` requirement flag (+ `LowDocuments`/
  `HighDocuments` presets, `"+documents"` in the way label) and the router filters to
  documents-capable cards exactly as Vision does.
- **Gate decision — documents refuse loudly, deliberately diverging from the silent
  reconcile idiom**: `CapabilityGateChatClient` throws a typed
  `DocumentsNotSupportedException` (naming the card and media type) when a non-image
  `DataContent` reaches a card without `Documents`. Search tools and temperature still
  drop silently — a dropped search degrades an answer; a dropped document turns an
  extraction into confident hallucination over no input.
- Completion logging needed no change: the `DataContent` branch already logs
  `[{mediaType} sha256:…]` stubs (bytes only to the host's `IImageStore`), so document
  bytes never reach sinks.
- Upstream intent: n/a — this IS upstream now.

### 11. The ui/ workspace — @polycrestlabs/agentkit-ui (2026-08-04)

The chat kit becomes the library's UI half (one repo, one version, one CHANGELOG),
per the 2026-08-04 rulings: an ng-packagr Angular library at `ui/`, published as
`@polycrestlabs/agentkit-ui`, Angular ^22 + rxjs as peerDependencies (the kit never
pins a host's Angular), `marked`/`tslib` as its only hard deps.

- **Provenance**: the aws chat kit at tip `28131fd` — `chat-types.ts` (→ `src/lib/sse.ts`,
  near-verbatim parseFrame/readSseStream/httpErrorMessage), `chat-turn.ts` (→
  `src/lib/turn-state.ts`, the state machine incl. the tip's attachment-upload state),
  `chat-rail.ts` (→ `src/lib/chat-rail.ts`), `markdown.pipe.ts` (→ `agentMarkdown`).
  `chat-transport.ts` dissolved into `src/lib/tokens.ts`.
- **The two couplings, cut** (doc #1's 2026-07-20 ruling): `AGENT_TRANSPORT` is a
  required injection slot with a minimal core — `streamTurn(body, onFrame, signal?)`;
  chat CRUD (`AgentChatCapable`) and attachment upload (`AgentAttachmentCapable`) are
  optional capabilities — and `AGENT_CONFIRM` is an optional slot with a package-owned
  `BuiltInAgentConfirm` default.
- **The core event union IS the Groundsworth frame vocabulary** —
  `loaded · suggestion · changeset · heartbeat · error` (`src/lib/frames.ts`), typed
  from what the server already emits rather than a second protocol; unknown types fall
  through `onDomainFrame`. `heartbeat` is first-class and consumed internally as a
  liveness signal (dead-air timeout, default 90 s, → error state exactly once — silence
  is failure, matching the server's terminal-error convention); it is never rendered.
  Refusals: `AgentTurnRefusal{status}` maps 409 → busy line, 503 → latched
  `unavailable`. Abort is a quiet teardown.
- Tests: vitest + jsdom (`npm test`) — frame round-trips, malformed-block tolerance,
  chunk reassembly, turn-state transitions (done / refusals / dead-air-exactly-once /
  abort-quietly / domain fall-through), attachment gating (send refused while uploads
  are in flight, `source` assets never ride, joined-chip removal).
- Deliberately NOT shipped at 0.2.0: the change-review window (no consuming surface
  until Report Studio) and token/tool stream frames (the consuming lane does not
  stream tokens; behavior-identical is its bar).
