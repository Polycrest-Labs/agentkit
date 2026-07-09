using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using AgentKit.Catalog;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentKit.Logging;

/// <summary>Per-model decorator that turns every completion (streaming or not) into one
/// <see cref="CompletionRecord"/>: route, request, response, usage, cost (from the ModelCard's prices),
/// latency, and error. Sink failures are logged and swallowed — logging can never fault a turn.
/// An optional <see cref="IImageStore"/> receives each image's bytes at hash time so hosts can keep
/// vision logs replayable (bytes never reach sinks — only the <c>sha256:…</c> reference does).</summary>
public sealed class LoggingChatClient(IChatClient inner, ModelCard card, ICompletionSink sink, ILogger? logger = null, IImageStore? imageStore = null)
    : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var snapshot = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await base.GetResponseAsync(snapshot, options, cancellationToken);
            Emit(snapshot, options, response, sw.ElapsedMilliseconds, error: null);
            return response;
        }
        catch (Exception ex)
        {
            // A caller-cancelled call (user abort, SSE disconnect) is not a model failure — recording
            // it as an errored completion would pollute the error rate the logs exist to track.
            if (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Emit(snapshot, options, response: null, sw.ElapsedMilliseconds, error: ex);
            }
            throw;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var snapshot = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        var updates = new List<ChatResponseUpdate>();
        var sw = Stopwatch.StartNew();
        Exception? error = null;
        var enumerator = base.GetStreamingResponseAsync(snapshot, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        break;
                    }
                    update = enumerator.Current;
                }
                catch (Exception ex)
                {
                    error = ex;
                    throw;
                }
                updates.Add(update);
                yield return update;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
            // A caller-cancelled stream is not a model failure — skip the record (same convention as
            // FailoverChatClient's cancellation filter). A provider timeout (TaskCanceledException with
            // an untripped token) still logs as a real error.
            if (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                ChatResponse? response = null;
                if (updates.Count > 0)
                {
                    try
                    {
                        response = updates.ToChatResponse();
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Could not materialize streamed updates for logging (model {Model})", card.Id);
                    }
                }
                Emit(snapshot, options, response, sw.ElapsedMilliseconds, error);
            }
        }
    }

    private void Emit(IReadOnlyList<ChatMessage> messages, ChatOptions? options, ChatResponse? response, long latencyMs, Exception? error)
    {
        try
        {
            var scope = CompletionScope.Current;
            var usage = response?.Usage is { } u
                ? new CompletionUsage { Input = u.InputTokenCount, Output = u.OutputTokenCount, Cached = CachedTokens(u) }
                : null;
            sink.Record(new CompletionRecord
            {
                Feature = scope?.Feature ?? "",
                TurnId = scope?.TurnId,
                Hop = scope?.NextHop() ?? 1,
                ConversationId = scope?.ConversationId,
                UserId = scope?.UserId,
                Route = new CompletionRoute
                {
                    Way = scope?.Way,
                    Model = card.Id,
                    Provider = card.Provider,
                    Reason = scope?.RouteReason,
                },
                Request = new CompletionRequest
                {
                    System = options?.Instructions,
                    Messages = [.. messages.Select(m => new LoggedMessage(m.Role.Value, Render(m)))],
                    ToolNames = options?.Tools is { Count: > 0 } tools ? [.. tools.Select(t => t.Name)] : [],
                    Temperature = options?.Temperature,
                },
                Response = new CompletionResponse
                {
                    Text = response?.Text,
                    ToolCalls = response is null ? [] : [.. ToolCalls(response)],
                    Finish = response?.FinishReason?.ToString(),
                },
                Usage = usage,
                CostUsd = Cost(usage),
                LatencyMs = latencyMs,
                Error = error is null ? null : $"{error.GetType().Name}: {error.Message}",
                Attempt = scope?.Attempt ?? 1,
                FailedOverFrom = scope?.FailedOverFrom,
            });
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Completion sink failed for model {Model}; the turn continues unlogged", card.Id);
        }
    }

    /// <summary>Estimated cost from the card's per-Mtok prices; null (never 0) when a price is unknown.</summary>
    private decimal? Cost(CompletionUsage? usage)
    {
        if (usage is null || card.PriceInPerMtok is not { } priceIn || card.PriceOutPerMtok is not { } priceOut)
        {
            return null;
        }
        return ((usage.Input ?? 0) * priceIn + (usage.Output ?? 0) * priceOut) / 1_000_000m;
    }

    private static long? CachedTokens(UsageDetails usage)
    {
        if (usage.AdditionalCounts is null)
        {
            return null;
        }
        foreach (var (key, value) in usage.AdditionalCounts)
        {
            if (key.Contains("cached", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }
        return null;
    }

    private static IEnumerable<LoggedToolCall> ToolCalls(ChatResponse response) =>
        response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Select(c => new LoggedToolCall(c.Name, SerializeArgs(c.Arguments)));

    private static string SerializeArgs(IDictionary<string, object?>? arguments)
    {
        try
        {
            return arguments is null ? "{}" : JsonSerializer.Serialize(arguments, JsonlFileSink.Json);
        }
        catch (Exception)
        {
            return "{}";
        }
    }

    /// <summary>Flattens one message for the log — text as-is, images replaced by content hashes
    /// (with the bytes offered to the optional <see cref="IImageStore"/> at that moment).</summary>
    private string Render(ChatMessage message)
    {
        if (message.Contents.Count == 1 && message.Contents[0] is TextContent only)
        {
            return only.Text;
        }
        var parts = new List<string>();
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text:
                    parts.Add(text.Text);
                    break;
                case DataContent data:
                    var sha256 = Convert.ToHexStringLower(SHA256.HashData(data.Data.Span));
                    try
                    {
                        imageStore?.Persist(sha256, data.MediaType, data.Data);
                    }
                    catch (Exception ex)
                    {
                        // Same posture as the sink: persistence may degrade, never fault a turn.
                        logger?.LogWarning(ex, "Image store failed for sha256:{Sha256}; the turn continues", sha256);
                    }
                    parts.Add($"[{data.MediaType} sha256:{sha256}]");
                    break;
                case UriContent uri:
                    parts.Add($"[{uri.MediaType} {uri.Uri}]");
                    break;
                case FunctionCallContent call:
                    parts.Add($"[tool-call {call.Name} {SerializeArgs(call.Arguments)}]");
                    break;
                case FunctionResultContent result:
                    parts.Add($"[tool-result {result.CallId}: {result.Result}]");
                    break;
            }
        }
        return string.Join("\n", parts);
    }
}
