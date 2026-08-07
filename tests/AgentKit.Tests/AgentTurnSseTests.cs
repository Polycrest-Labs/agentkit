using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using AgentKit.Agents;
using AgentKit.Wire;
using Microsoft.AspNetCore.Http;

namespace AgentKit.Tests;

/// <summary>
/// The SSE writer's finality contract, driven through an in-memory DefaultHttpContext: the host
/// finalizer runs BEFORE any terminal bytes (for success, fault and cancel alike), finalizer
/// failure can never produce done, exception detail stays server-side, Drain and Cancel keep
/// their write/turn/finalization tokens distinct, and a core serialization failure is never
/// silently swallowed into a false success.
/// </summary>
public sealed class AgentTurnSseTests
{
    private static readonly AgentTurnDescriptor Descriptor = new("turn-9", "azure-openai", "gpt-5-mini");
    private static readonly AgentWireOptions NoHeartbeat = new() { HeartbeatInterval = TimeSpan.Zero };

    private static (HttpContext Context, MemoryStream Body) Http()
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        return (context, body);
    }

    private static string BodyText(MemoryStream body) => Encoding.UTF8.GetString(body.ToArray());

    private static async IAsyncEnumerable<AgentEvent> Events(params AgentEvent[] events)
    {
        foreach (var ev in events)
        {
            await Task.Yield();
            yield return ev;
        }
    }

    /// <summary>An event source the test feeds by hand (a controllable "runner").</summary>
    private static (IAsyncEnumerable<AgentEvent> Source, ChannelWriter<AgentEvent> Feed) ManualEvents()
    {
        var channel = Channel.CreateUnbounded<AgentEvent>();
        return (Read(channel.Reader), channel.Writer);

        static async IAsyncEnumerable<AgentEvent> Read(ChannelReader<AgentEvent> reader, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var ev in reader.ReadAllAsync(ct))
            {
                yield return ev;
            }
        }
    }

    [Fact]
    public async Task Success_FinalizerRunsBeforeTerminalBytes_ThenMessageAndDone()
    {
        var (context, body) = Http();
        var session = AgentTurnSession.Start(Events(new TokenDelta("Hel"), new TokenDelta("lo"), new Completed("Hello")), Descriptor, NoHeartbeat);
        string? bodyAtFinalize = null;

        await AgentTurnSse.RunAsync(context.Response, session, (outcome, _) =>
        {
            bodyAtFinalize = BodyText(body);
            Assert.True(outcome.Succeeded);
            Assert.Equal("Hello", outcome.FinalText);
            return Task.FromResult<AgentTurnFinalization?>(new AgentTurnFinalization { Title = "Ranked" });
        });

        Assert.NotNull(bodyAtFinalize);
        Assert.DoesNotContain("event: message", bodyAtFinalize);
        Assert.DoesNotContain("event: done", bodyAtFinalize); // success bytes cannot precede the commit

        var text = BodyText(body);
        Assert.Equal("text/event-stream", context.Response.Headers.ContentType);
        Assert.Equal("no", context.Response.Headers["X-Accel-Buffering"]);
        Assert.Contains("event: loaded\ndata: {\"turnId\":\"turn-9\"", text);
        Assert.Contains("event: token\ndata: {\"delta\":\"Hel\"}", text);
        Assert.Contains("event: message\ndata: {\"text\":\"Hello\"}", text);
        Assert.Contains("event: done\ndata: {\"turnId\":\"turn-9\",\"title\":\"Ranked\"}", text);
        Assert.True(text.IndexOf("event: message", StringComparison.Ordinal) < text.IndexOf("event: done", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FinalizerFailure_EmitsSafeError_NeverDone()
    {
        var (context, body) = Http();
        var session = AgentTurnSession.Start(Events(new Completed("done text")), Descriptor, NoHeartbeat);

        await AgentTurnSse.RunAsync(context.Response, session,
            (_, _) => throw new InvalidOperationException("pg connection string: Host=secret-db;Password=hunter2"));

        var text = BodyText(body);
        Assert.Contains("event: error", text);
        Assert.Contains("\"code\":\"finalize_failed\"", text);
        Assert.DoesNotContain("event: done", text);
        Assert.DoesNotContain("event: message", text);
        Assert.DoesNotContain("hunter2", text); // exception detail never crosses the wire
    }

    [Fact]
    public async Task RunnerFault_FinalizerStillRuns_SafeErrorWithoutExceptionDetail()
    {
        var (context, body) = Http();
        var failure = new InvalidOperationException("provider exploded: api key sk-secret-123");
        var session = AgentTurnSession.Start(Faulting(new TokenDelta("He"), failure), Descriptor, NoHeartbeat);
        AgentTurnOutcome? finalized = null;

        await AgentTurnSse.RunAsync(context.Response, session, (outcome, _) =>
        {
            finalized = outcome;
            return Task.FromResult<AgentTurnFinalization?>(null);
        });

        Assert.NotNull(finalized); // failed turns persist their provenance too
        Assert.Same(failure, finalized.Exception);
        var text = BodyText(body);
        Assert.Contains("\"code\":\"turn_failed\"", text);
        Assert.DoesNotContain("sk-secret-123", text);
        Assert.DoesNotContain("event: done", text);

        static async IAsyncEnumerable<AgentEvent> Faulting(AgentEvent first, Exception ex)
        {
            await Task.Yield();
            yield return first;
            throw ex;
        }
    }

    [Fact]
    public async Task SafeErrorMapper_SuppliesDomainDetail()
    {
        var (context, body) = Http();
        var session = AgentTurnSession.Start(Faulting(new InvalidOperationException("infra text")), Descriptor, NoHeartbeat);

        await AgentTurnSse.RunAsync(context.Response, session,
            (_, _) => Task.FromResult<AgentTurnFinalization?>(null),
            new AgentSseOptions
            {
                SafeError = outcome => new ErrorFrame("turn_failed", "The job has no subject address yet.", Descriptor.TurnId),
            });

        var text = BodyText(body);
        Assert.Contains("\"detail\":\"The job has no subject address yet.\"", text);
        Assert.DoesNotContain("infra text", text);

        static async IAsyncEnumerable<AgentEvent> Faulting(Exception ex)
        {
            await Task.Yield();
            if (ex is not null)
            {
                throw ex;
            }
            yield break;
        }
    }

    [Fact]
    public async Task Drain_RequestAbortStopsWrites_TurnCompletesAndFinalizes()
    {
        var (context, body) = Http();
        var (source, feed) = ManualEvents();
        using var requestAborted = new CancellationTokenSource();
        using var turnCts = new CancellationTokenSource();
        var session = AgentTurnSession.Start(source, Descriptor, NoHeartbeat, turnCt: turnCts.Token);
        AgentTurnOutcome? finalized = null;

        var run = AgentTurnSse.RunAsync(context.Response, session, (outcome, _) =>
        {
            finalized = outcome;
            return Task.FromResult<AgentTurnFinalization?>(null);
        }, new AgentSseOptions { ClientDisconnect = AgentClientDisconnect.Drain }, requestAborted.Token);

        await feed.WriteAsync(new TokenDelta("before"));
        await WaitForAsync(() => BodyText(body).Contains("before"));
        var lengthAtAbort = body.Length;
        requestAborted.Cancel();

        // The producer keeps running after the disconnect — Drain must consume it to completion.
        await feed.WriteAsync(new TokenDelta("after-disconnect"));
        await feed.WriteAsync(new Completed("landed"));
        feed.Complete();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(finalized);
        Assert.True(finalized.Succeeded); // the turn ran to completion — Drain never cancels it
        Assert.Equal("landed", finalized.FinalText);
        Assert.False(turnCts.IsCancellationRequested); // the turn token was untouched
        Assert.Equal(lengthAtAbort, body.Length); // not one byte after the abort
        Assert.DoesNotContain("after-disconnect", BodyText(body));
    }

    [Fact]
    public async Task Cancel_RequestAbortCancelsTheTurn_FinalizerSeesCanceledOutcome()
    {
        var (context, body) = Http();
        using var requestAborted = new CancellationTokenSource();
        using var turnCts = new CancellationTokenSource();
        var session = AgentTurnSession.Start(Hung(turnCts.Token), Descriptor, NoHeartbeat, turnCt: turnCts.Token);
        AgentTurnOutcome? finalized = null;
        var finalizationCtWasCanceled = true;

        var run = AgentTurnSse.RunAsync(context.Response, session, (outcome, finalizationCt) =>
        {
            finalized = outcome;
            finalizationCtWasCanceled = finalizationCt.IsCancellationRequested;
            return Task.FromResult<AgentTurnFinalization?>(null);
        }, new AgentSseOptions
        {
            ClientDisconnect = AgentClientDisconnect.Cancel,
            TurnCancellation = turnCts,
        }, requestAborted.Token);

        await WaitForAsync(() => BodyText(body).Contains("event: loaded"));
        requestAborted.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(turnCts.IsCancellationRequested); // request abort reached the runner
        Assert.NotNull(finalized);
        Assert.True(finalized.WasCanceled); // canceled provenance still persists
        Assert.False(finalizationCtWasCanceled); // ...on the finalizer's own token
        Assert.DoesNotContain("event: done", BodyText(body));

        static async IAsyncEnumerable<AgentEvent> Hung([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            yield break;
        }
    }

    [Fact]
    public async Task Cancel_WithoutTurnCancellation_IsRefusedUpFront()
    {
        var (context, _) = Http();
        var session = AgentTurnSession.Start(Events(new Completed("x")), Descriptor, NoHeartbeat);

        await Assert.ThrowsAsync<ArgumentException>(() => AgentTurnSse.RunAsync(
            context.Response, session, (_, _) => Task.FromResult<AgentTurnFinalization?>(null),
            new AgentSseOptions { ClientDisconnect = AgentClientDisconnect.Cancel }));
    }

    [Fact]
    public async Task RequestAbortDuringFinalization_FinalizationStillCompletes()
    {
        var (context, _) = Http();
        using var requestAborted = new CancellationTokenSource();
        var session = AgentTurnSession.Start(Events(new Completed("persist me")), Descriptor, NoHeartbeat);
        var finalizeCompleted = false;

        await AgentTurnSse.RunAsync(context.Response, session, async (outcome, finalizationCt) =>
        {
            requestAborted.Cancel(); // the client vanishes exactly while we persist
            await Task.Delay(50, finalizationCt); // must NOT throw — the token is server-owned
            finalizeCompleted = true;
            return null;
        }, requestAborted: requestAborted.Token);

        Assert.True(finalizeCompleted);
    }

    private sealed class SecondSerializeBomb
    {
        private int _calls;
        public int Value => _calls++ == 0 ? 1 : throw new InvalidOperationException("late payload boom");
    }

    [Fact]
    public async Task CoreSerializationFailure_TerminatesWithSafeError_NeverFalseDone()
    {
        var (context, body) = Http();
        // A domain payload that passes the session's pre-write validation (first serialize) and
        // explodes at the writer's serialize — the write path must terminate honestly.
        var session = AgentTurnSession.Start(Events(
            new CustomEvent(new SecondSerializeBomb()),
            new TokenDelta("after"),
            new Completed("looks fine")), Descriptor, new AgentWireOptions
        {
            HeartbeatInterval = TimeSpan.Zero,
            DomainFrameMapper = payload => new DomainFrame("boom", payload),
        });
        AgentTurnOutcome? finalized = null;

        await AgentTurnSse.RunAsync(context.Response, session, (outcome, _) =>
        {
            finalized = outcome;
            return Task.FromResult<AgentTurnFinalization?>(null);
        });

        Assert.NotNull(finalized); // persistence still landed server-side
        var text = BodyText(body);
        Assert.Contains("\"code\":\"turn_failed\"", text);
        Assert.DoesNotContain("event: done", text); // never vouch for a stream with missing frames
        Assert.DoesNotContain("event: message", text);
        Assert.DoesNotContain("late payload boom", text);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }
        Assert.True(condition(), "condition never became true");
    }
}
