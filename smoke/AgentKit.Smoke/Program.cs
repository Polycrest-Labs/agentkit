// ─────────────────────────────────────────────────────────────────────────────────────────────────
// AgentKit live smoke — tiny console for provider bring-up checks against real endpoints:
//   chat    stream text through the routed pipeline
//   tool    declared function tool: verify streamed tool-call arguments arrive + a result round-trips
//   search  HostedWebSearchTool: verify server-side web_search runs and URL citations surface
//   vision  send a small image and ask what it is (provider vision check)
//   models  print the catalog + provider configuration state
// Reads the "agentkit-smoke" user-secrets (Foundry:Endpoint/ApiKey, Llm:Providers:*:ApiKey) — never CI-run.
//   dotnet run --project smoke/AgentKit.Smoke -- search [--model gpt-chat-latest]
// ─────────────────────────────────────────────────────────────────────────────────────────────────
using System.Text.Json;
using AgentKit;
using AgentKit.Agents;
using AgentKit.Catalog;
using AgentKit.Providers;
using AgentKit.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
string? Arg(string name)
{
    var i = Array.IndexOf(args, "--" + name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var secrets = new ConfigurationBuilder()
    .AddUserSecrets("agentkit-smoke")
    .AddEnvironmentVariables()
    .Build();

var foundryEndpoint = secrets["Llm:Providers:foundry:Endpoint"] ?? secrets["Foundry:Endpoint"] ?? "";
var foundryKey = secrets["Llm:Providers:foundry:ApiKey"] ?? secrets["Foundry:ApiKey"];
var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Llm:Providers:foundry:Kind"] = "azure-openai",
    ["Llm:Providers:foundry:Endpoint"] = foundryEndpoint,
    ["Llm:Providers:foundry:ApiKey"] = foundryKey,
    ["Llm:Providers:foundry:CredentialMode"] = "DevSafe",
    ["Llm:Providers:gemini:Kind"] = "gemini-native",
    ["Llm:Providers:gemini:Endpoint"] = "https://generativelanguage.googleapis.com/v1beta/",
    ["Llm:Providers:gemini:ApiKey"] = secrets["Llm:Providers:gemini:ApiKey"],
    ["Llm:Providers:neuralwatt:Kind"] = "openai-compat",
    ["Llm:Providers:neuralwatt:Endpoint"] = "https://api.neuralwatt.com/v1",
    ["Llm:Providers:neuralwatt:ApiKey"] = secrets["Llm:Providers:neuralwatt:ApiKey"],
    ["Llm:Models:0:Id"] = "gpt-chat-latest",
    ["Llm:Models:0:Provider"] = "foundry",
    ["Llm:Models:0:Tier"] = "High",
    ["Llm:Models:0:Vision"] = "true",
    ["Llm:Models:0:Search"] = "true",
    ["Llm:Models:0:Quirks:FixedTemperature"] = "true",
    ["Llm:Models:1:Id"] = "gpt-5-mini",
    ["Llm:Models:1:Provider"] = "foundry",
    ["Llm:Models:1:Tier"] = "Low",
    ["Llm:Models:1:Vision"] = "true",
    ["Llm:Models:1:Search"] = "true",
    ["Llm:Models:2:Id"] = "gemini-3.5-flash",
    ["Llm:Models:2:Provider"] = "gemini",
    ["Llm:Models:2:Tier"] = "Low",
    ["Llm:Models:2:Vision"] = "true",
    ["Llm:Models:3:Id"] = "gemini-3.5-flash-search",
    ["Llm:Models:3:Provider"] = "gemini",
    ["Llm:Models:3:Tier"] = "Low",
    ["Llm:Models:3:Vision"] = "true",
    ["Llm:Models:3:Search"] = "true",
    ["Llm:Models:3:UpstreamModel"] = "gemini-3.5-flash",
    ["Llm:Models:4:Id"] = "glm-5.2",
    ["Llm:Models:4:Provider"] = "neuralwatt",
    ["Llm:Models:4:Tier"] = "High",
    ["Llm:Models:5:Id"] = "kimi-k2.6",
    ["Llm:Models:5:Provider"] = "neuralwatt",
    ["Llm:Models:5:Tier"] = "High",
    ["Llm:Models:5:Vision"] = "true",
    ["Llm:Models:6:Id"] = "qwen3.6-35b",
    ["Llm:Models:6:Provider"] = "neuralwatt",
    ["Llm:Models:6:Tier"] = "Low",
    ["Llm:Models:6:Vision"] = "true",
    ["Llm:Routing:Prefer:high:0"] = "gpt-chat-latest",
    ["Llm:Routing:Prefer:high:1"] = "gpt-5-mini",
    ["Llm:Routing:Prefer:low:0"] = "gpt-chat-latest",
    ["Llm:Routing:Prefer:low:1"] = "gpt-5-mini",
    ["Llm:Logging:Sink"] = "none",
}).Build();

var services = new ServiceCollection()
    .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
    .AddAgentKit(config)
    .BuildServiceProvider();

var catalog = services.GetRequiredService<IModelCatalog>();
var router = services.GetRequiredService<IModelRouter>();
var factory = services.GetRequiredService<IChatClientFactory>();
var modelId = Arg("model") ?? "gpt-chat-latest";

return command switch
{
    "models" => Models(),
    "chat" => await Chat(),
    "tool" => await Tool(),
    "search" => await Search(),
    "vision" => await Vision(),
    _ => Help(),
};

int Models()
{
    var options = services.GetRequiredService<IOptions<LlmOptions>>().Value;
    foreach (var m in catalog.Models)
    {
        var provider = options.Providers.GetValueOrDefault(m.Provider);
        Console.WriteLine($"{m.Id,-26} {m.Provider,-11} {m.Tier,-5} vision={m.Vision,-5} search={m.Search,-5} configured={provider?.IsConfigured == true}");
    }
    return 0;
}

async Task<int> Chat()
{
    var client = factory.GetClient(catalog.Get(modelId));
    Console.WriteLine($"── chat @ {modelId} ──");
    var updates = new List<ChatResponseUpdate>();
    await foreach (var update in client.GetStreamingResponseAsync(
        [new ChatMessage(ChatRole.User, "In one short sentence, say hello from the AgentKit smoke test.")],
        new ChatOptions { Instructions = "You are terse." }))
    {
        Console.Write(update.Text);
        updates.Add(update);
    }
    var response = updates.ToChatResponse();
    Console.WriteLine($"\n\nupdates={updates.Count} usage: in={response.Usage?.InputTokenCount} out={response.Usage?.OutputTokenCount}");
    Console.WriteLine("content types seen: " + string.Join(", ",
        updates.SelectMany(u => u.Contents).GroupBy(c => c.GetType().Name).Select(g => $"{g.Key}×{g.Count()}")));
    return response.Text.Length > 0 ? 0 : 1;
}

async Task<int> Tool()
{
    var client = factory.GetClient(catalog.Get(modelId));
    Console.WriteLine($"── tool @ {modelId} ──");
    var weatherTool = RawJsonFunction.FromJson("get_weather", "Get the current weather for a city.",
        """{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""");
    var options = new ChatOptions { Tools = [weatherTool] };
    var history = new List<ChatMessage> { new(ChatRole.User, "What's the weather in Paris right now? Use the get_weather tool.") };

    var streamedCallContents = 0;
    for (var hop = 0; hop < 4; hop++)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(history, options))
        {
            streamedCallContents += update.Contents.Count(c => c is FunctionCallContent);
            Console.Write(update.Text);
            updates.Add(update);
        }
        var response = updates.ToChatResponse();
        history.AddRange(response.Messages);
        var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
        if (calls.Count == 0)
        {
            Console.WriteLine($"\n\nfinal text OK; FunctionCallContent seen in stream: {streamedCallContents}");
            var ok = streamedCallContents > 0 && response.Text.Contains("72", StringComparison.Ordinal);
            Console.WriteLine(ok ? "TOOL ROUND-TRIP: PASS" : "TOOL ROUND-TRIP: CHECK MANUALLY");
            return ok ? 0 : 1;
        }
        foreach (var call in calls)
        {
            Console.WriteLine($"\n[tool call] {call.Name} callId={call.CallId} args={JsonSerializer.Serialize(call.Arguments)} raw={call.RawRepresentation?.GetType().Name}");
            history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, "72F and sunny")]));
        }
    }
    Console.WriteLine("gave up after 4 hops");
    return 1;
}

async Task<int> Search()
{
    var client = factory.GetClient(catalog.Get(modelId));
    Console.WriteLine($"── search @ {modelId} ──");
    var updates = new List<ChatResponseUpdate>();
    await foreach (var update in client.GetStreamingResponseAsync(
        [new ChatMessage(ChatRole.User, "What is one notable technology news story from this week? One sentence, cite the source.")],
        new ChatOptions { Tools = [new HostedWebSearchTool()] }))
    {
        Console.Write(update.Text);
        updates.Add(update);
    }
    var response = updates.ToChatResponse();
    var citations = response.Messages
        .SelectMany(m => m.Contents)
        .SelectMany(c => c.Annotations ?? [])
        .OfType<CitationAnnotation>()
        .ToList();
    Console.WriteLine($"\n\ncontent types: " + string.Join(", ",
        updates.SelectMany(u => u.Contents).GroupBy(c => c.GetType().Name).Select(g => $"{g.Key}×{g.Count()}")));
    Console.WriteLine($"citations: {citations.Count}");
    foreach (var c in citations.Take(5))
    {
        Console.WriteLine($"  - {c.Title} | {c.Url}");
    }
    Console.WriteLine(citations.Count > 0 ? "WEB SEARCH + CITATIONS: PASS" : "WEB SEARCH + CITATIONS: FAIL (no CitationAnnotation surfaced)");
    return citations.Count > 0 ? 0 : 1;
}

async Task<int> Vision()
{
    var client = factory.GetClient(catalog.Get(modelId));
    Console.WriteLine($"── vision @ {modelId} ──");
    // 64x64 solid red PNG (some Azure OpenAI resources reject tiny 4x4 images as invalid).
    var redPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAACHSURBVHhe7dAhAQAADITA719681QAcQbJbjuzMdg0gMGmAQw2DWCwaQCDTQMYbBrAYNMABpsGMNg0gMGmAQw2DWCwaQCDTQMYbBrAYNMABpsGMNg0gMGmAQw2DWCwaQCDTQMYbBrAYNMABpsGMNg0gMGmAQw2DWCwaQCDTQMYbBrAYNMABpsHQ4jh0hEeUY0AAAAASUVORK5CYII=");
    var response = await client.GetResponseAsync(
        [new ChatMessage(ChatRole.User, [new TextContent("What solid color is this image? One word."), new DataContent(redPng, "image/png")])]);
    Console.WriteLine(response.Text);
    var ok = response.Text.Contains("red", StringComparison.OrdinalIgnoreCase);
    Console.WriteLine(ok ? "VISION: PASS" : "VISION: CHECK MANUALLY");
    return ok ? 0 : 1;
}

int Help()
{
    Console.WriteLine("usage: agentkit-smoke <chat|tool|search|vision|models> [--model <catalog-id>]");
    return command == "help" ? 0 : 1;
}
