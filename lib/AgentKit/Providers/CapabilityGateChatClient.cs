using AgentKit.Catalog;
using Microsoft.Extensions.AI;

namespace AgentKit.Providers;

/// <summary>Per-model gate that reconciles the caller's request with the model's card so failover
/// candidates with different capabilities all receive a request they can serve: hosted web-search tools
/// are silently dropped for models without <c>Search</c>, and a requested temperature is dropped for
/// models that only accept their default. Sits OUTSIDE the logging decorator, so records show what was
/// actually sent.</summary>
public sealed class CapabilityGateChatClient(IChatClient inner, ModelCard card) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, Gate(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, Gate(options), cancellationToken);

    private ChatOptions? Gate(ChatOptions? options)
    {
        if (options is null)
        {
            return null;
        }
        var dropSearch = !card.Search && options.Tools?.Any(t => t is HostedWebSearchTool) == true;
        var dropTemperature = card.Quirks.FixedTemperature && options.Temperature is not null;
        if (!dropSearch && !dropTemperature)
        {
            return options;
        }
        var gated = options.Clone();
        if (dropSearch)
        {
            gated.Tools = [.. options.Tools!.Where(t => t is not HostedWebSearchTool)];
        }
        if (dropTemperature)
        {
            gated.Temperature = null;
        }
        return gated;
    }
}
