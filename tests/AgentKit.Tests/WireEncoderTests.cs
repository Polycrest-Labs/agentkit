using System.Runtime.CompilerServices;
using AgentKit.Agents;
using AgentKit.Wire;

namespace AgentKit.Tests;

/// <summary>
/// The nonterminal turn session: loaded-first ordering, native question/followup mapping, domain
/// mapping discipline, tool label resolution, heartbeat cadence under a hung producer, and the
/// explicit completed/faulted/canceled outcomes. The session must NEVER emit a success or error
/// terminal — terminal finality belongs to the SSE writer, after host finalization.
/// </summary>
public sealed class WireEncoderTests
{
    private static readonly AgentTurnDescriptor Descriptor = new("turn-1", "azure-openai", "gpt-5-mini");

    private static async IAsyncEnumerable<AgentEvent> Events(params AgentEvent[] events)
    {
        foreach (var ev in events)
        {
            await Task.Yield();
            yield return ev;
        }
    }

    private static async IAsyncEnumerable<AgentEvent> FaultingEvents(
        AgentEvent first, Exception failure)
    {
        await Task.Yield();
        yield return first;
        throw failure;
    }

    private static async IAsyncEnumerable<AgentEvent> HungEvents(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    private static async Task<List<AgentFrame>> Collect(AgentTurnSession session)
    {
        var frames = new List<AgentFrame>();
        await foreach (var frame in session.Frames)
        {
            frames.Add(frame);
        }
        return frames;
    }

    private static AgentWireOptions NoHeartbeat(Action<AgentWireOptions>? _ = null) =>
        new() { HeartbeatInterval = TimeSpan.Zero };

    [Fact]
    public async Task LoadedFirst_EventOrderPreserved_NoTerminals()
    {
        var question = new AgentQuestion { Id = "q1", Prompt = "Which rights?" };
        var session = AgentTurnSession.Start(Events(
            new TokenDelta("Hel"),
            new ToolCallStarted("rank_comps"),
            new CustomEvent(new QuestionEvent(question)),
            new ToolCallFinished("rank_comps", "ok", false),
            new TokenDelta("lo"),
            new CitationFound("TxGIO", "https://example.test"),
            new UsageReport(120, 40),
            new Completed("Hello")), Descriptor, NoHeartbeat());

        var frames = await Collect(session);
        var outcome = await session.Outcome;

        Assert.Equal(
            ["loaded", "token", "tool", "question", "tool", "token", "citation", "usage"],
            frames.Select(f => f.Event));
        var loaded = Assert.IsType<LoadedFrame>(frames[0]);
        Assert.Equal(("turn-1", "azure-openai", "gpt-5-mini"), (loaded.TurnId, loaded.Provider, loaded.Model));
        Assert.DoesNotContain(frames, f => f is MessageFrame or DoneFrame or ErrorFrame);

        Assert.True(outcome.Succeeded);
        Assert.Equal("Hello", outcome.FinalText);
        var interaction = Assert.IsType<AgentQuestionInteraction>(Assert.Single(outcome.Interactions));
        Assert.Equal("q1", interaction.Question.Id);
        Assert.Equal(new AgentCitation("TxGIO", "https://example.test"), Assert.Single(outcome.Citations));
        Assert.Equal(new AgentTurnUsage(120, 40), outcome.Usage);
    }

    [Fact]
    public async Task QuestionFormAndFollowups_MapNatively_FollowupsStayEphemeral()
    {
        var form = new AgentQuestionForm { Id = "form-1", Questions = [new AgentQuestion { Id = "q1", Prompt = "County?" }] };
        var session = AgentTurnSession.Start(Events(
            new CustomEvent(new QuestionFormEvent(form)),
            new CustomEvent(new FollowupsEvent(["Find comps", "Run the workup"])),
            new Completed("Asked.")), Descriptor, NoHeartbeat());

        var frames = await Collect(session);
        var outcome = await session.Outcome;

        Assert.Equal(["loaded", "questionForm", "followups"], frames.Select(f => f.Event));
        Assert.Equal(["Find comps", "Run the workup"], Assert.IsType<FollowupsFrame>(frames[2]).Prompts);
        // Followup hints are ephemeral: they never become durable interactions.
        var formInteraction = Assert.IsType<AgentQuestionFormInteraction>(Assert.Single(outcome.Interactions));
        Assert.Equal("form-1", formInteraction.Form.Id);
    }

    [Fact]
    public async Task ToolLabels_ResolveFromCatalog_UnknownToolsNull()
    {
        var labeled = AgentTool.Raw("rank_comps", "d", "{}", (_, _) => Task.FromResult(new ToolOutcome("ok")), "Ranking comparables");
        var session = AgentTurnSession.Start(Events(
            new ToolCallStarted("rank_comps"),
            new ToolCallStarted("mystery_tool"),
            new Completed("done")), Descriptor, new AgentWireOptions
        {
            HeartbeatInterval = TimeSpan.Zero,
            Tools = new AgentToolCatalog([labeled]),
        });

        var frames = await Collect(session);

        Assert.Equal("Ranking comparables", Assert.IsType<ToolFrame>(frames[1]).Label);
        Assert.Null(Assert.IsType<ToolFrame>(frames[2]).Label);
    }

    [Fact]
    public async Task DomainMapping_MappedFramesFlow_UnmappedAndMalformedDrop()
    {
        var session = AgentTurnSession.Start(Events(
            new CustomEvent(new { section = "site", path = "access" }),  // mapped → suggestion
            new CustomEvent(new Uri("https://unmapped.test")),            // mapper returns null → dropped
            new CustomEvent(42),                                          // mapped to a primitive payload → refused before write
            new Completed("done")), Descriptor, new AgentWireOptions
        {
            HeartbeatInterval = TimeSpan.Zero,
            DomainFrameMapper = payload => payload switch
            {
                Uri => null,
                int primitive => new DomainFrame("bad-payload", primitive),
                _ => new DomainFrame("suggestion", payload),
            },
        });

        var frames = await Collect(session);
        var outcome = await session.Outcome;

        Assert.Equal(["loaded", "suggestion"], frames.Select(f => f.Event));
        Assert.True(outcome.Succeeded); // dropped domain frames never fault the turn
    }

    [Fact]
    public async Task EmitUsageFalse_SuppressesTheFrame_OutcomeStillCarriesUsage()
    {
        var session = AgentTurnSession.Start(Events(
            new UsageReport(10, 5),
            new Completed("done")), Descriptor, new AgentWireOptions { HeartbeatInterval = TimeSpan.Zero, EmitUsage = false });

        var frames = await Collect(session);
        var outcome = await session.Outcome;

        Assert.DoesNotContain(frames, f => f is UsageFrame);
        Assert.Equal(new AgentTurnUsage(10, 5), outcome.Usage);
    }

    [Fact]
    public async Task RunnerFault_CompletesOutcomeWithException_NoErrorFrame()
    {
        var failure = new InvalidOperationException("provider exploded: secret connection string");
        var session = AgentTurnSession.Start(
            FaultingEvents(new TokenDelta("Hel"), failure), Descriptor, NoHeartbeat());

        var frames = await Collect(session);
        var outcome = await session.Outcome;

        Assert.Equal(["loaded", "token"], frames.Select(f => f.Event));
        Assert.DoesNotContain(frames, f => f is ErrorFrame); // the writer decides the safe terminal, not the session
        Assert.False(outcome.Succeeded);
        Assert.Same(failure, outcome.Exception);
        Assert.False(outcome.WasCanceled);
    }

    [Fact]
    public async Task Cancellation_CompletesOutcomeAsCanceled()
    {
        using var cts = new CancellationTokenSource();
        var session = AgentTurnSession.Start(HungEvents(), Descriptor, NoHeartbeat(), turnCt: cts.Token);

        var collect = Collect(session);
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));
        var frames = await collect.WaitAsync(TimeSpan.FromSeconds(10));
        var outcome = await session.Outcome.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["loaded"], frames.Select(f => f.Event));
        Assert.True(outcome.WasCanceled);
        Assert.Null(outcome.Exception);
        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public async Task Heartbeats_FireOnCadence_WhileTheProducerHangs()
    {
        var time = new ManualTimeProvider();
        using var cts = new CancellationTokenSource();
        var session = AgentTurnSession.Start(HungEvents(), Descriptor, new AgentWireOptions
        {
            HeartbeatInterval = TimeSpan.FromSeconds(15),
            TimeProvider = time,
        }, turnCt: cts.Token);

        var frames = session.Frames.GetAsyncEnumerator();
        Assert.True(await frames.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsType<LoadedFrame>(frames.Current);

        // Wait until the heartbeat pump has actually parked on its delay, then advance the clock.
        await WaitForTimerAsync(time);
        time.Advance(TimeSpan.FromSeconds(15));
        Assert.True(await frames.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, Assert.IsType<HeartbeatFrame>(frames.Current).Seq);

        await WaitForTimerAsync(time);
        time.Advance(TimeSpan.FromSeconds(15));
        Assert.True(await frames.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, Assert.IsType<HeartbeatFrame>(frames.Current).Seq);

        // Under-cadence advances produce nothing: the next read must still be pending.
        await WaitForTimerAsync(time);
        time.Advance(TimeSpan.FromSeconds(10));
        var pending = frames.MoveNextAsync().AsTask();
        await Task.Delay(100);
        Assert.False(pending.IsCompleted);

        cts.Cancel();
        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(10))); // stream ends, no terminal frame
        var outcome = await session.Outcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(outcome.WasCanceled);
        await frames.DisposeAsync();
    }

    private static async Task WaitForTimerAsync(ManualTimeProvider time)
    {
        for (var i = 0; i < 500 && time.ActiveTimerCount == 0; i++)
        {
            await Task.Delay(10);
        }
        Assert.True(time.ActiveTimerCount > 0, "the heartbeat pump never parked on its delay");
    }
}
