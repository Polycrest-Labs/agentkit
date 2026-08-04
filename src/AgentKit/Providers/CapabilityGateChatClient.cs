using AgentKit.Catalog;
using Microsoft.Extensions.AI;

namespace AgentKit.Providers;

/// <summary>Per-model gate that reconciles the caller's request with the model's card so failover
/// candidates with different capabilities all receive a request they can serve: hosted web-search tools
/// are silently dropped for models without <c>Search</c>, and a requested temperature is dropped for
/// models that only accept their default. Document input is the deliberate exception to the
/// silent-reconcile idiom: a request carrying a document part against a card without <c>Documents</c>
/// throws <see cref="DocumentsNotSupportedException"/> — a silently dropped search degrades an answer,
/// but a silently dropped document turns an extraction into confident hallucination over no input.
/// Sits OUTSIDE the logging decorator, so records show what was actually sent.</summary>
public sealed class CapabilityGateChatClient(IChatClient inner, ModelCard card) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(GateMessages(messages), Gate(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(GateMessages(messages), Gate(options), cancellationToken);

    private IEnumerable<ChatMessage> GateMessages(IEnumerable<ChatMessage> messages)
    {
        if (card.Documents)
        {
            return messages;
        }
        var snapshot = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        var offending = snapshot
            .SelectMany(m => m.Contents)
            .OfType<DataContent>()
            .FirstOrDefault(d => !d.HasTopLevelMediaType("image"));
        if (offending is not null)
        {
            throw new DocumentsNotSupportedException(card.Id, offending.MediaType);
        }
        return snapshot;
    }

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

/// <summary>A request carrying document input reached a model whose card lacks <c>Documents</c> —
/// refused loudly, never silently dropped (the attachment IS the input for extraction/classification
/// features). Reach a capable model by routing with a Documents way (e.g.
/// <see cref="LlmWay.HighDocuments"/>) so the router only resolves documents-capable cards, or mark
/// the intended card <c>Documents: true</c> in <c>Llm:Models</c> where its deployment really accepts
/// file input.</summary>
public sealed class DocumentsNotSupportedException(string modelId, string mediaType)
    : InvalidOperationException(
        $"Model '{modelId}' does not accept document input, but the request carries a '{mediaType}' part. " +
        "Route with a Documents way (e.g. LlmWay.HighDocuments) so only documents-capable cards resolve, " +
        "or set 'Documents: true' on a card whose deployment accepts file input.")
{
    /// <summary>The catalog id of the model that refused.</summary>
    public string ModelId { get; } = modelId;

    /// <summary>The media type of the first offending (non-image) data part.</summary>
    public string MediaType { get; } = mediaType;
}
