using System.Diagnostics;
using System.Runtime.CompilerServices;
using AgentKit.Catalog;
using AgentKit.Diagnostics;
using AgentKit.Logging;
using AgentKit.Routing;
using Microsoft.Extensions.AI;

namespace AgentKit.Resilience;

/// <summary>Walks a <see cref="ModelResolution"/>'s candidates in order: on an eligible failure the next
/// capable model is tried (each candidate once); ineligible failures surface immediately. Streams fail
/// over ONLY before the first yielded update — once content has streamed, tool calls may already have
/// dispatched, and a silent re-run would duplicate side effects — so mid-stream failures surface as
/// errors (still alerted). Pinned resolutions are single-candidate: they never fail over.</summary>
public sealed class FailoverChatClient : IChatClient
{
    private readonly IReadOnlyList<(ModelCard Card, IChatClient Client)> _candidates;
    private readonly ModelResolution _resolution;
    private readonly FailoverPolicy _policy;
    private readonly ILlmDiagnostics _diagnostics;

    public FailoverChatClient(
        ModelResolution resolution,
        Func<ModelCard, IChatClient> clientFor,
        ILlmDiagnostics diagnostics,
        FailoverPolicy? policy = null)
    {
        _resolution = resolution;
        _candidates = [.. resolution.Candidates.Select(card => (card, clientFor(card)))];
        _diagnostics = diagnostics;
        _policy = policy ?? FailoverPolicy.Default;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var snapshot = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        for (var i = 0; i < _candidates.Count; i++)
        {
            Stamp(i);
            try
            {
                return await _candidates[i].Client.GetResponseAsync(snapshot, options, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (!HandleFailure(i, ex))
                {
                    throw;
                }
            }
        }
        throw new UnreachableException();
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var snapshot = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        for (var i = 0; i < _candidates.Count; i++)
        {
            Stamp(i);
            var yielded = false;
            var enumerator = _candidates[i].Client
                .GetStreamingResponseAsync(snapshot, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
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
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Mid-stream failures never retry — surface (and alert) instead of duplicating
                        // any side effects a partially-streamed hop may have caused.
                        if (yielded || !HandleFailure(i, ex))
                        {
                            if (yielded)
                            {
                                _diagnostics.ModelFailure(_candidates[i].Card.Id, _candidates[i].Card.Provider, Feature, ex);
                            }
                            throw;
                        }
                        goto nextCandidate;
                    }
                    yielded = true;
                    yield return update;
                }
                yield break;
            }
            finally
            {
                await enumerator.DisposeAsync();
            }
        nextCandidate: ;
        }
    }

    /// <summary>Diagnostics for a clean-start failure; true = move on to the next candidate.</summary>
    private bool HandleFailure(int index, Exception exception)
    {
        var (card, _) = _candidates[index];
        _diagnostics.ModelFailure(card.Id, card.Provider, Feature, exception);
        var last = index + 1 >= _candidates.Count;
        if (!last && _policy.IsEligible(exception))
        {
            _diagnostics.Failover(card.Id, _candidates[index + 1].Card.Id, _candidates[index + 1].Card.Provider, Feature);
            return true;
        }
        if (last && (index > 0 || _policy.IsEligible(exception)))
        {
            _diagnostics.FailoverExhausted(Feature, _resolution.Way.ToString(), card.Id, exception);
        }
        return false;
    }

    private string Feature => CompletionScope.Current?.Feature ?? "";

    /// <summary>Route/attempt info for the logging layer, stamped before each candidate call.</summary>
    private void Stamp(int index)
    {
        if (CompletionScope.Current is not { } scope)
        {
            return;
        }
        scope.Way = _resolution.Way.ToString();
        scope.RouteReason = index == 0 ? _resolution.Reason : $"failover from {_candidates[index - 1].Card.Id}";
        scope.Attempt = index + 1;
        scope.FailedOverFrom = index == 0 ? null : _candidates[index - 1].Card.Id;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : _candidates[0].Client.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        // Candidate clients are owned (and memoized) by the factory; nothing to dispose here.
    }
}
