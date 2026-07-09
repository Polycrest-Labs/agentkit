# AgentKit

A reusable .NET library (net10.0) for building LLM features on
[Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai): a
config-driven **model catalog + router**, **providers** (Azure OpenAI Responses, any OpenAI-compatible
endpoint, Gemini native), **completion logging** (tokens, cost, latency, route — every hop, with
replayable vision images), **failover + alerting**, one-shot **JSON completions** with a tolerant
deserialization boundary, and a provider-agnostic **streaming agent loop** with function tools.

```bash
dotnet add package Polycrest.AgentKit
```

Repo layout:

| Path | What it is |
| --- | --- |
| `src/AgentKit/` | The library — ships as the `Polycrest.AgentKit` package (namespace `AgentKit`). |
| `smoke/AgentKit.Smoke/` | Console for live provider checks (`chat` / `tool` / `search` / `vision` / `models`). |
| `tests/AgentKit.Tests/` | Unit tests. |

## Getting started

Register the pipeline from your `Llm` config section:

```csharp
builder.Services.AddAgentKit(builder.Configuration);      // binds the "Llm" section
```

Every registration is `TryAdd`, so a host can pre-register its own `ICompletionSink`, `IImageStore`,
`IModelRouter`, or `ILlmDiagnostics` before calling `AddAgentKit` (that's how a host plugs in a blob
sink for completion logs). An optional `configure` callback post-processes the bound options — e.g. to
pick the Arc-safe dev credential:

```csharp
builder.Services.AddAgentKit(builder.Configuration, o =>
{
    if (builder.Environment.IsDevelopment() && o.Providers.TryGetValue("foundry", out var f))
        f.CredentialMode = CredentialMode.DevSafe;   // az/azd sign-in without the Arc IMDS probe
});
```

### The `Llm` config section

```jsonc
"Llm": {
  "Providers": {
    // kind: "azure-openai"  (Responses API; ApiKey optional — token credential otherwise)
    //       "openai-compat" (any OpenAI-compatible chat endpoint; ApiKey required)
    //       "gemini-native" (Gemini's generateContent API; ApiKey required — the path that
    //                        supports Search grounding, unlike Gemini's openai-compat endpoint)
    "foundry":    { "Kind": "azure-openai",  "Endpoint": "https://<acct>.openai.azure.com/" },
    "gemini":     { "Kind": "gemini-native", "Endpoint": "https://generativelanguage.googleapis.com/v1beta/" },
    "neuralwatt": { "Kind": "openai-compat", "Endpoint": "https://api.neuralwatt.com/v1" }
  },
  "Models": [
    { "Id": "gpt-chat-latest", "Provider": "foundry", "Tier": "High", "Vision": true, "Search": true,
      "Quirks": { "FixedTemperature": true } },
    { "Id": "qwen3.6-35b", "Provider": "neuralwatt", "Tier": "Low", "Vision": true,
      "ContextK": 131, "PriceInPerMtok": 0.29, "PriceOutPerMtok": 1.15 },
    // Two catalog entries can share one upstream model (a search-on variant, an A/B key/region):
    // Id stays the unique catalog key, UpstreamModel is what goes on the wire.
    { "Id": "gemini-3.5-flash-search", "UpstreamModel": "gemini-3.5-flash", "Provider": "gemini",
      "Tier": "Low", "Vision": true, "Search": true }
  ],
  "Routing": {
    // Per tier: FIRST capable entry = primary, the rest (then remaining capable models,
    // cheapest first) = the failover chain.
    "Prefer": { "high": [ "gpt-chat-latest", "gpt-5-mini" ], "low": [ "gpt-chat-latest", "gpt-5-mini" ] }
  },
  "Logging": { "Sink": "none" }   // none | jsonl (local files) | memory — or register your own sink
}
```

API keys never go in appsettings — use user-secrets / app settings
(`Llm:Providers:<name>:ApiKey`). A model whose provider isn't configured (no endpoint, or a
key-requiring kind without a key) is skipped by routing; pinning it fails with a clear error.

## Ways: how callers pick a model

Call sites never name models. They declare a **way** — tier + required capabilities — and the router
resolves it against the catalog + preference order:

```csharp
LlmWay.Low            // cheap text
LlmWay.LowVision      // cheap + image input required
LlmWay.High           // the good stuff
new LlmWay(ModelTier.High, Vision: true, Search: true)
```

Swapping which model serves a way is a config edit, not a code change.

## One-shot completions: `ILlmClient`

For extract/adjudicate/generate features (no tool loop):

```csharp
public sealed class MyExtractor(ILlmClient llm)
{
    public async Task<Candidate[]> ExtractAsync(string text, CancellationToken ct)
    {
        // feature tag ("booking-extract") labels every CompletionRecord for this call
        var result = await llm.CompleteJsonAsync<Candidate[]>(
            LlmWay.LowVision, "booking-extract", SystemPrompt, text, images: null, ct);
        return result;
    }
}
```

- `CompleteAsync` → raw reply text.
- `CompleteJsonAsync` → the extracted JSON payload (code fences / surrounding prose stripped; `"{}"` when none).
- `CompleteJsonAsync<T>` → tolerant-deserialized via `AgentJson` (string enums coerced, scalars-as-strings
  tolerated, human/OCR date formats parsed, out-of-range numerics coerced to null instead of faulting).
  Throws `LlmNoJsonException` when the reply carries no JSON at all (refusal, content filter, prose) so
  retry policies engage instead of persisting a blank DTO.
- Pass images as `LlmImage(mediaType, bytes)` — remember to use a `Vision: true` way.

## The agent loop: `AgentRunner`

The runner streams a completion, dispatches function calls through your tool catalog, feeds results
back, and loops until the model stops calling tools (or `MaxHops`). Failures throw; tool handler
exceptions become recoverable error outputs fed back to the model (never a crashed turn).

```csharp
var tools = new AgentToolCatalog(
[
    // Raw JSON schema (ported/hand-authored):
    new AgentTool
    {
        Name = "get_day", Description = "Get one day of the itinerary.",
        ParametersSchema = """{"type":"object","properties":{"date":{"type":"string"}},"required":["date"]}""",
        Handler = async (argsJson, ct) => new ToolOutcome(await LoadDayAsync(argsJson, ct)),
    },
    // Or typed — schema generated from a DTO (see DtoJsonSchema below):
    AgentTool.Typed<SaveNoteArgs>("save_note", "Save a note.",
        async (args, ct) => new ToolOutcome(await SaveAsync(args, ct))),
]);

var request = new AgentTurnRequest
{
    Instructions = systemPrompt + snapshot,
    History = renderedTurns,                 // AgentHistoryMessage("user"|"assistant", text)
    UserText = userMessage,
    Images = attachedImages,
    Tools = tools,
    Way = new LlmWay(ModelTier.High, Vision: attachedImages.Count > 0),
    Feature = "trip-agent",                  // completion-log label
};

await foreach (var ev in runner.RunAsync(request, new AgentRunnerOptions { /* eval knobs */ }, ct))
{
    switch (ev)
    {
        case TokenDelta d:        /* stream text */            break;
        case CitationFound c:     /* web-search citation */    break;
        case ToolCallFinished f:  /* name, output, isError */  break;
        case CustomEvent e:       /* YOUR domain event */      break;
        case UsageReport u:       /* whole-turn tokens */      break;
        case Completed done:      /* full text */              break;
    }
}
```

**Domain events**: a handler returns `ToolOutcome(outputForModel, domainEvents)` — the objects in
`DomainEvents` come back out as `CustomEvent(payload)` *before* the tool-finished event, untyped. That's
how a host streams proposals/questions/cards without the library knowing about them. `ToolOutcome.Images`
attaches images a read surfaced as follow-up vision input (they can't ride a function output).

**Web search**: `AgentRunnerOptions.EnableWebSearch` (default true) adds the provider's hosted
web-search tool; models whose card lacks `Search` silently skip it (the capability gate also drops a
requested temperature for `FixedTemperature` models). Citations surface as `CitationFound`.

**Pinning**: `AgentTurnRequest.ModelPin` (or a router `Resolve(way, modelPin)`) targets one exact
catalog model, bypasses the capability filter, and **disables failover** — an eval graded against a
silent substitute model would be worthless.

## Typed tool schemas: `DtoJsonSchema`

Generate a provider-constrainable args schema from the request DTO so the model-visible contract can
never drift from the real shape (enums as names, nullable unions, `additionalProperties: false`):

```csharp
DtoJsonSchema.For<SaveBookingRequest>(new DtoSchemaOptions
{
    Description = "The booking to add.",
    ExcludeFields = ["source"],                       // system-managed — hidden from the model
    FieldDescriptions = new() { ["startUtc"] = "ISO-8601 UTC start, or null" },
});
DtoJsonSchema.For<SaveBookingRequest>(new DtoSchemaOptions { PatchMode = true });  // all-optional patch
```

Pin generated schemas with snapshot tests in your host so an accidental DTO change fails a test instead
of drifting silently.

## Completion logging

Every model hop produces one `CompletionRecord` — feature, turn/conversation ids, route
(way/model/provider/reason), request (system, messages with image bytes replaced by sha256 hashes, tool
names), response (text, tool calls, finish), usage, estimated cost (from the card's `$/Mtok`; **null**
when prices are unknown, never 0), latency, error, attempt/failedOverFrom. Sinks:

- `none` (default), `jsonl` (`{dir}/{yyyy-MM-dd}/{feature}.jsonl`), `memory` (`InMemorySink`, useful for
  eval token/cost correlation) — or register your own `ICompletionSink` before `AddAgentKit`.
- Register an `IImageStore` to persist the original image bytes keyed by the same sha256 hash the log
  records — that's what makes vision logs replayable (the default store is a no-op).
- Sink and image-store failures are swallowed and logged — logging can degrade but never fault a turn.

## Failover + alerting

The router's candidate order doubles as the failover chain. `FailoverChatClient` retries the next
candidate on transport/provider errors (network, 408/429/5xx, auth, model-missing) — never on content
quality. Streams fail over **only before the first yielded update**; once content has streamed, tool
calls may have dispatched, so mid-stream failures surface as errors (still alerted) rather than silently
duplicating side effects. Each hop is a fresh completion, so hop-level failover mid-turn is safe.

`ILlmDiagnostics` (default `LoggerLlmDiagnostics`) emits structured `ILogger` errors plus
`Meter("AgentKit")` counters — `llm.model.failures`, `llm.failovers`, `llm.failover.exhausted`, tagged
model/provider/feature. Make them alertable by adding the meter to your OpenTelemetry pipeline:

```csharp
.WithMetrics(m => m.AddMeter(AgentKitMetrics.MeterName))
```

## Live smoke checks

```bash
dotnet run --project smoke/AgentKit.Smoke -- models                    # catalog + configured state
dotnet run --project smoke/AgentKit.Smoke -- chat   [--model <id>]    # streaming text
dotnet run --project smoke/AgentKit.Smoke -- tool   [--model <id>]    # function-call round-trip
dotnet run --project smoke/AgentKit.Smoke -- search [--model <id>]    # hosted web_search + citations
dotnet run --project smoke/AgentKit.Smoke -- vision [--model <id>]    # image input
```

It reads the `agentkit-smoke` user-secrets (`Llm:Providers:*:ApiKey`, legacy `Foundry:*`) — set them
with `dotnet user-secrets -p smoke/AgentKit.Smoke set "Llm:Providers:gemini:ApiKey" "<key>"`. Use it to
bring up a new provider before pointing an eval at it.

## Gotchas the design bakes in

- **Version pairing**: `Microsoft.Extensions.AI(.OpenAI)` and `Azure.AI.OpenAI` must agree on the
  transitive `OpenAI` package. Currently 10.6.0 + 2.9.0-beta.1 (the Responses client is
  `GetResponsesClient().AsIChatClient(deploymentId)` since OpenAI 2.10). Bump them together — a
  consumer pinning `OpenAI` 2.11+ while `Azure.AI.OpenAI` is 2.9.0-beta.1 fails at runtime with
  `MissingMethodException: OpenAI.Responses.ResponsesClient..ctor`.
- **History resend is sanitized**: the runner re-sends full history each hop but keeps only the durable
  parts of an assistant turn (text + function calls). Hosted-tool artifacts (`web_search_call`) must not
  be resubmitted — the Responses API rejects them without their paired reasoning item, which the adapter
  can't round-trip.
- **The runner produces through a channel**, not a plain async iterator: the ambient `CompletionScope`
  (feature/route/attempt for logging + alerting) is an AsyncLocal, and async iterators resume in the
  consumer's execution context — a naive iterator loses the scope after the first yielded event.
- **`AgentJson` is the tolerant boundary**: models invent enum members, pass numbers where strings
  belong, and emit every date format OCR has ever seen; the typed schemas constrain generation
  provider-side and `AgentJson` stays as the app-side safety net.
