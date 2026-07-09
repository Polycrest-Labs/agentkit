using AgentKit.Catalog;
using AgentKit.Logging;
using AgentKit.Providers;
using AgentKit.Routing;
using Microsoft.Extensions.AI;

namespace AgentKit;

/// <summary>An image passed to a completion as direct vision input.</summary>
public sealed record LlmImage(string MediaType, byte[] Bytes);

/// <summary>One-shot completions for non-agent features (extraction, adjudication, creative copy):
/// declare a <see cref="LlmWay"/> and a feature tag, get routing + failover + completion logging for free.</summary>
public interface ILlmClient
{
    /// <summary>Run one completion and return the raw reply text.</summary>
    Task<string> CompleteAsync(LlmWay way, string feature, string systemPrompt, string userText,
        IReadOnlyList<LlmImage>? images = null, CancellationToken ct = default);

    /// <summary>Run one completion and return the extracted JSON payload (code fences and surrounding
    /// prose stripped); <c>"{}"</c> when the reply carries no JSON.</summary>
    Task<string> CompleteJsonAsync(LlmWay way, string feature, string systemPrompt, string userText,
        IReadOnlyList<LlmImage>? images = null, CancellationToken ct = default);

    /// <summary>Run one completion and tolerant-deserialize the extracted JSON via <see cref="AgentJson"/>
    /// (string enums coerced, scalars-as-strings tolerated).</summary>
    Task<T> CompleteJsonAsync<T>(LlmWay way, string feature, string systemPrompt, string userText,
        IReadOnlyList<LlmImage>? images = null, CancellationToken ct = default);
}

public sealed class LlmClient(IModelRouter router, IChatClientFactory factory) : ILlmClient
{
    public async Task<string> CompleteAsync(LlmWay way, string feature, string systemPrompt, string userText,
        IReadOnlyList<LlmImage>? images = null, CancellationToken ct = default)
    {
        var resolution = router.Resolve(way);
        var client = factory.GetClient(resolution);
        using var scope = CompletionScope.Begin(feature);

        var options = new ChatOptions { Instructions = systemPrompt };
        if (way.Search)
        {
            options.Tools = [new HostedWebSearchTool()];
        }
        var response = await client.GetResponseAsync([BuildUserMessage(userText, images)], options, ct);
        return response.Text;
    }

    public async Task<string> CompleteJsonAsync(LlmWay way, string feature, string systemPrompt, string userText,
        IReadOnlyList<LlmImage>? images = null, CancellationToken ct = default)
    {
        var text = await CompleteAsync(way, feature, systemPrompt, userText, images, ct);
        return JsonExtraction.ExtractJson(text) ?? "{}";
    }

    public async Task<T> CompleteJsonAsync<T>(LlmWay way, string feature, string systemPrompt, string userText,
        IReadOnlyList<LlmImage>? images = null, CancellationToken ct = default)
    {
        var json = await CompleteJsonAsync(way, feature, systemPrompt, userText, images, ct);
        return AgentJson.Deserialize<T>(json);
    }

    private static ChatMessage BuildUserMessage(string userText, IReadOnlyList<LlmImage>? images)
    {
        if (images is not { Count: > 0 })
        {
            return new ChatMessage(ChatRole.User, userText);
        }
        var contents = new List<AIContent> { new TextContent(userText) };
        contents.AddRange(images.Select(i => new DataContent(i.Bytes, i.MediaType)));
        return new ChatMessage(ChatRole.User, contents);
    }
}
