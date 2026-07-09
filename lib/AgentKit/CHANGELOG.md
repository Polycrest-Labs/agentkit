# AgentKit — changes in this repo (hsa)

**Provenance**
- Source repo: `C:\projects\vacadock` (vacadock)
- Source commit: `b21897e027cee71633c867a113aa0fcaef357ac4`
- Copied: 2026-07-08
- Copied trees: `lib/AgentKit`, `lib/AgentKit.Smoke`, `tests/AgentKit.Tests` → `lib/AgentKit.Tests` (bin/obj excluded)

**Rule:** every subsequent change to any file under `lib/AgentKit*` requires an entry
here — what changed, why, and the upstream intent. These changes will be incorporated
back into vacadock immediately after this effort; this file is that PR's description,
pre-written. The authoritative diff is `git diff <baseline-commit> -- lib/AgentKit*`.

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
