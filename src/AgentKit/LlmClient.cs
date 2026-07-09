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
    /// (string enums coerced, scalars-as-strings tolerated). Throws <see cref="LlmNoJsonException"/> when the
    /// reply carries no JSON at all (a refusal, a content-filter block, or prose over an unreadable input) —
    /// the caller fails loudly (and any retry policy engages) instead of receiving a fabricated blank
    /// <typeparamref name="T"/>.</summary>
    Task<T> CompleteJsonAsync<T>(LlmWay way, string feature, string systemPrompt, string userText,
        IReadOnlyList<LlmImage>? images = null, CancellationToken ct = default);
}

/// <summary>The model returned a reply with no JSON payload — a refusal, a content-filter block, or prose
/// over an unreadable input. Thrown by <see cref="ILlmClient.CompleteJsonAsync{T}"/> so a no-output turn
/// surfaces as a failure (matching the pre-AgentKit provider services, which threw on a missing tool call /
/// empty candidates) rather than deserializing <c>"{}"</c> into a silently blank result that then gets
/// persisted.</summary>
public sealed class LlmNoJsonException(string feature, string reply)
    : InvalidOperationException($"The model returned no JSON for feature '{feature}'. Reply: {Preview(reply)}")
{
    /// <summary>The feature tag of the completion that produced no JSON.</summary>
    public string Feature { get; } = feature;

    /// <summary>The raw model reply, for diagnostics.</summary>
    public string Reply { get; } = reply;

    private static string Preview(string reply) =>
        string.IsNullOrWhiteSpace(reply) ? "(empty)"
        : reply.Length <= 200 ? reply
        : reply[..200] + "…";
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
        var text = await CompleteAsync(way, feature, systemPrompt, userText, images, ct);
        // No JSON at all (prose/refusal) is a failed extraction, not an empty receipt — fail loudly so the
        // caller's error/retry handling runs instead of persisting a fabricated blank. (An explicit empty
        // object "{}" from the model still deserializes normally; only a total absence of JSON throws.)
        var json = JsonExtraction.ExtractJson(text) ?? throw new LlmNoJsonException(feature, text);
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
