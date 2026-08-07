using System.Threading.Channels;
using AgentKit.Agents;
using Microsoft.Extensions.Logging;

namespace AgentKit.Wire;

/// <summary>The host-resolved identity of one turn: the minted turn id and the routed
/// provider/model (hosts pre-resolve via their router, as Groundsworth's runtimes do).</summary>
public sealed record AgentTurnDescriptor(string TurnId, string Provider, string Model);

public sealed record AgentCitation(string? Title, string? Url);

public sealed record AgentTurnUsage(long? InputTokens, long? OutputTokens);

/// <summary>
/// How one turn actually ended — awaited by the host AFTER the nonterminal frames finish, and fed
/// to the host finalizer BEFORE any terminal frame is written. The session never emits
/// success/error terminals itself: <c>message + done</c> (or a safe <c>error</c>) belong to the
/// SSE writer, after host finalization.
/// </summary>
public sealed record AgentTurnOutcome
{
    /// <summary>The final model hop's text (empty when the turn faulted before completing).</summary>
    public required string FinalText { get; init; }

    /// <summary>The runner fault, when the turn failed. Full detail belongs in server logs only.</summary>
    public Exception? Exception { get; init; }

    public bool WasCanceled { get; init; }

    /// <summary>Ordered durable question/form records asked during the turn (followup hints are
    /// ephemeral and deliberately absent). Hosts persist these before terminal success.</summary>
    public IReadOnlyList<AgentInteraction> Interactions { get; init; } = [];

    public IReadOnlyList<AgentCitation> Citations { get; init; } = [];

    public AgentTurnUsage? Usage { get; init; }

    public bool Succeeded => Exception is null && !WasCanceled;
}

/// <summary>
/// The dependency-free turn session: consumes the runner's <see cref="AgentEvent"/> stream and
/// exposes (1) the ordered NONTERMINAL frame sequence — <c>loaded</c> first, token/tool/citation/
/// usage/question/questionForm/followups/domain frames in event order, <c>heartbeat</c> merged in
/// on a cadence — and (2) the awaited <see cref="AgentTurnOutcome"/>. Completion and faults
/// complete the outcome; they do NOT emit success/error terminal frames — terminal finality is the
/// SSE writer's job, gated on the host finalizer.
/// </summary>
public sealed class AgentTurnSession
{
    private readonly Channel<AgentFrame> _channel;
    private readonly TaskCompletionSource<AgentTurnOutcome> _outcome =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AgentWireOptions _options;
    private readonly ILogger? _logger;

    /// <summary>Starts consuming <paramref name="events"/> immediately. <paramref name="turnCt"/> is
    /// the TURN-execution token (not the request-write token — the SSE writer keeps those distinct);
    /// cancellation completes the outcome with <see cref="AgentTurnOutcome.WasCanceled"/>.</summary>
    public static AgentTurnSession Start(
        IAsyncEnumerable<AgentEvent> events,
        AgentTurnDescriptor descriptor,
        AgentWireOptions? options = null,
        ILogger? logger = null,
        CancellationToken turnCt = default)
        => new(events, descriptor, options ?? new AgentWireOptions(), logger, turnCt);

    private AgentTurnSession(
        IAsyncEnumerable<AgentEvent> events, AgentTurnDescriptor descriptor,
        AgentWireOptions options, ILogger? logger, CancellationToken turnCt)
    {
        _options = options;
        _logger = logger;
        _channel = Channel.CreateBounded<AgentFrame>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = false, // the event pump and the heartbeat pump both write
            FullMode = BoundedChannelFullMode.Wait,
        });
        _ = PumpAsync(events, descriptor, turnCt);
    }

    /// <summary>The ordered nonterminal frames. Enumerate exactly once; the sequence ends when the
    /// turn completes, faults, or is canceled — then await <see cref="Outcome"/>.</summary>
    public IAsyncEnumerable<AgentFrame> Frames => _channel.Reader.ReadAllAsync();

    /// <summary>Completes when the event stream ends (successfully, faulted, or canceled). Never faults.</summary>
    public Task<AgentTurnOutcome> Outcome => _outcome.Task;

    private async Task PumpAsync(IAsyncEnumerable<AgentEvent> events, AgentTurnDescriptor descriptor, CancellationToken ct)
    {
        var interactions = new List<AgentInteraction>();
        var citations = new List<AgentCitation>();
        AgentTurnUsage? usage = null;
        var finalText = string.Empty;
        Exception? failure = null;
        var canceled = false;
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = Task.CompletedTask;
        try
        {
            await _channel.Writer.WriteAsync(new LoadedFrame(descriptor.TurnId, descriptor.Provider, descriptor.Model), ct);
            heartbeat = PumpHeartbeatAsync(heartbeatCts.Token);

            await foreach (var ev in events.WithCancellation(ct))
            {
                switch (ev)
                {
                    case TokenDelta token:
                        await _channel.Writer.WriteAsync(new TokenFrame(token.Delta), ct);
                        break;
                    case ToolCallStarted started:
                        await _channel.Writer.WriteAsync(new ToolFrame(started.Name, AgentToolPhase.Start, LabelFor(started.Name)), ct);
                        break;
                    case ToolCallFinished finished:
                        await _channel.Writer.WriteAsync(new ToolFrame(finished.Name, AgentToolPhase.End, LabelFor(finished.Name)), ct);
                        break;
                    case CitationFound citation:
                        citations.Add(new AgentCitation(citation.Title, citation.Url));
                        await _channel.Writer.WriteAsync(new CitationFrame(citation.Title, citation.Url), ct);
                        break;
                    case UsageReport report:
                        usage = new AgentTurnUsage(report.InputTokens, report.OutputTokens);
                        if (_options.EmitUsage)
                        {
                            await _channel.Writer.WriteAsync(new UsageFrame(report.InputTokens, report.OutputTokens), ct);
                        }
                        break;
                    case CustomEvent { Payload: QuestionEvent question }:
                        interactions.Add(new AgentQuestionInteraction(question.Question));
                        await _channel.Writer.WriteAsync(new QuestionFrame(question.Question), ct);
                        break;
                    case CustomEvent { Payload: QuestionFormEvent form }:
                        interactions.Add(new AgentQuestionFormInteraction(form.Form));
                        await _channel.Writer.WriteAsync(new QuestionFormFrame(form.Form), ct);
                        break;
                    case CustomEvent { Payload: FollowupsEvent followups }:
                        await _channel.Writer.WriteAsync(new FollowupsFrame(followups.Prompts), ct);
                        break;
                    case CustomEvent custom:
                        if (MapDomain(custom.Payload) is { } domain)
                        {
                            await _channel.Writer.WriteAsync(domain, ct);
                        }
                        break;
                    case Completed completed:
                        finalText = completed.Text;
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            heartbeatCts.Cancel();
            try
            {
                await heartbeat;
            }
            catch (OperationCanceledException)
            {
                // the heartbeat pump's normal exit
            }
            _channel.Writer.TryComplete();
            _outcome.TrySetResult(new AgentTurnOutcome
            {
                FinalText = finalText,
                Exception = failure,
                WasCanceled = canceled,
                Interactions = interactions,
                Citations = citations,
                Usage = usage,
            });
        }
    }

    private async Task PumpHeartbeatAsync(CancellationToken ct)
    {
        if (_options.HeartbeatInterval <= TimeSpan.Zero)
        {
            return;
        }
        long seq = 0;
        while (true)
        {
            await Task.Delay(_options.HeartbeatInterval, _options.TimeProvider, ct);
            await _channel.Writer.WriteAsync(new HeartbeatFrame(++seq), ct);
        }
    }

    private string? LabelFor(string toolName) => _options.Tools?.Find(toolName)?.DisplayLabel;

    /// <summary>Domain mapping discipline: unmapped payloads drop with a diagnostic (never streamed
    /// untyped); a mapped frame whose payload cannot serialize as a JSON object is refused BEFORE it
    /// reaches the writer. An invalid domain NAME throws in the DomainFrame constructor — a host bug
    /// that faults the turn rather than corrupting the wire.</summary>
    private DomainFrame? MapDomain(object payload)
    {
        var frame = _options.DomainFrameMapper?.Invoke(payload);
        if (frame is null)
        {
            _logger?.LogDebug("Unmapped domain event {PayloadType} dropped from the turn stream", payload.GetType().Name);
            return null;
        }
        try
        {
            AgentWireJson.Serialize(frame);
        }
        catch (AgentWireException ex)
        {
            _logger?.LogWarning(ex, "Domain frame '{Name}' dropped — its payload does not serialize as a JSON object", frame.Name);
            return null;
        }
        return frame;
    }
}
