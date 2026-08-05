using AgentKit.Agents;
using AgentKit.Catalog;
using AgentKit.Providers;
using AgentKit.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace AgentKit.Tests;

public sealed class AgentRunnerTests
{
    private sealed class SingleClientFactory(IChatClient client) : IChatClientFactory
    {
        public IChatClient GetClient(ModelCard model) => client;
        public IChatClient GetClient(ModelResolution resolution) => client;
    }

    private static AgentRunner Runner(FakeChatClient fake)
    {
        var options = TestCatalog.Options();
        var router = new CapabilityTierRouter(TestCatalog.Catalog(options), Options.Create(options));
        return new AgentRunner(router, new SingleClientFactory(fake));
    }

    private static AgentTurnRequest Request(params AgentTool[] tools) => new()
    {
        Instructions = "You are a test agent.",
        UserText = "hello",
        Tools = new AgentToolCatalog(tools),
        Feature = "test-agent",
    };

    private static ChatResponseUpdate Call(string callId, string name, Dictionary<string, object?> args) =>
        new(ChatRole.Assistant, [new FunctionCallContent(callId, name, args)]);

    private static async Task<List<AgentEvent>> Collect(AgentRunner runner, AgentTurnRequest request, AgentRunnerOptions? options = null)
    {
        var events = new List<AgentEvent>();
        await foreach (var ev in runner.RunAsync(request, options))
        {
            events.Add(ev);
        }
        return events;
    }

    [Fact]
    public async Task HappyPath_StreamsTokensAndCompletes()
    {
        var fake = new FakeChatClient().EnqueueStream("Hel", "lo");

        var events = await Collect(Runner(fake), Request());

        Assert.Equal(["Hel", "lo"], events.OfType<TokenDelta>().Select(t => t.Delta));
        Assert.Equal("Hello", Assert.Single(events.OfType<Completed>()).Text);
    }

    [Fact]
    public async Task ToolCall_DispatchesAndFeedsResultBack()
    {
        string? receivedArgs = null;
        var tool = new AgentTool
        {
            Name = "get_day",
            Description = "d",
            ParametersSchema = """{"type":"object","properties":{"date":{"type":"string"}}}""",
            Handler = (args, _) =>
            {
                receivedArgs = args;
                return Task.FromResult(new ToolOutcome("day contents"));
            },
        };
        var fake = new FakeChatClient()
            .EnqueueStream(Call("c1", "get_day", new Dictionary<string, object?> { ["date"] = "2026-08-03" }))
            .EnqueueStream("All done.");

        var events = await Collect(Runner(fake), Request(tool));

        Assert.Contains("2026-08-03", receivedArgs);
        var finished = Assert.Single(events.OfType<ToolCallFinished>());
        Assert.Equal(("get_day", "day contents", false), (finished.Name, finished.Output, finished.IsError));
        // Second hop received the tool result in the resent history.
        var secondCall = fake.Calls[1];
        var toolMessage = secondCall.Messages.Single(m => m.Role == ChatRole.Tool);
        var result = Assert.Single(toolMessage.Contents.OfType<FunctionResultContent>());
        Assert.Equal("c1", result.CallId);
        Assert.Equal("All done.", Assert.Single(events.OfType<Completed>()).Text);
    }

    [Fact]
    public async Task Completed_CarriesOnlyFinalHopText_WhenToolHopAlsoStreamsText()
    {
        var tool = new AgentTool
        {
            Name = "get_context",
            Description = "d",
            ParametersSchema = """{"type":"object","properties":{}}""",
            Handler = (_, _) => Task.FromResult(new ToolOutcome("context")),
        };
        var fake = new FakeChatClient()
            .EnqueueStream(new ChatResponseUpdate(ChatRole.Assistant,
            [
                new TextContent("I'll check that."),
                new FunctionCallContent("c1", "get_context", new Dictionary<string, object?>()),
            ]))
            .EnqueueStream("The client is First National Bank.");

        var events = await Collect(Runner(fake), Request(tool));

        Assert.Equal(
            ["I'll check that.", "The client is First National Bank."],
            events.OfType<TokenDelta>().Select(t => t.Delta));
        Assert.Equal(
            "The client is First National Bank.",
            Assert.Single(events.OfType<Completed>()).Text);
    }

    [Fact]
    public async Task ToolError_BecomesRecoverableFeedback_NeverKillsTheTurn()
    {
        var tool = new AgentTool
        {
            Name = "explodes",
            Description = "d",
            ParametersSchema = """{"type":"object","properties":{}}""",
            Handler = (_, _) => throw new InvalidOperationException("unknown field 'phone'"),
        };
        var fake = new FakeChatClient()
            .EnqueueStream(Call("c1", "explodes", []))
            .EnqueueStream("Recovered.");

        var events = await Collect(Runner(fake), Request(tool));

        var finished = Assert.Single(events.OfType<ToolCallFinished>());
        Assert.True(finished.IsError);
        Assert.Contains("unknown field 'phone'", finished.Output);
        Assert.Contains("Re-check the JSON arguments", finished.Output);
        Assert.Equal("Recovered.", Assert.Single(events.OfType<Completed>()).Text);
    }

    [Fact]
    public async Task DomainEvents_EmitBeforeToolFinished_AndObserverSeesRawArgs()
    {
        var observed = new List<(string Name, string Args, bool IsError)>();
        var tool = new AgentTool
        {
            Name = "propose",
            Description = "d",
            ParametersSchema = """{"type":"object","properties":{}}""",
            Handler = (_, _) => Task.FromResult(new ToolOutcome("Proposed.", ["domain-payload"])),
        };
        var fake = new FakeChatClient()
            .EnqueueStream(Call("c1", "propose", new Dictionary<string, object?> { ["x"] = 1 }))
            .EnqueueStream("Done.");

        var events = await Collect(Runner(fake), Request(tool),
            new AgentRunnerOptions { OnToolCall = (n, a, _, e) => observed.Add((n, a, e)) });

        var custom = events.FindIndex(e => e is CustomEvent { Payload: "domain-payload" });
        var finished = events.FindIndex(e => e is ToolCallFinished);
        Assert.True(custom >= 0 && custom < finished, "domain event must precede tool-finished");
        var call = Assert.Single(observed);
        Assert.Equal("propose", call.Name);
        Assert.Contains("\"x\":1", call.Args);
    }

    [Fact]
    public async Task ParallelToolCallsInOneHop_AllDispatch()
    {
        var hits = new List<string>();
        AgentTool Tool(string name) => new()
        {
            Name = name,
            Description = "d",
            ParametersSchema = """{"type":"object","properties":{}}""",
            Handler = (_, _) =>
            {
                hits.Add(name);
                return Task.FromResult(new ToolOutcome(name + " ok"));
            },
        };
        var fake = new FakeChatClient()
            .EnqueueStream(new ChatResponseUpdate(ChatRole.Assistant,
            [
                new FunctionCallContent("c1", "alpha", new Dictionary<string, object?>()),
                new FunctionCallContent("c2", "beta", new Dictionary<string, object?>()),
            ]))
            .EnqueueStream("Done.");

        var events = await Collect(Runner(fake), Request(Tool("alpha"), Tool("beta")));

        Assert.Equal(["alpha", "beta"], hits);
        Assert.Equal(2, events.OfType<ToolCallFinished>().Count());
    }

    [Fact]
    public async Task UnknownTool_FeedsBackNonError()
    {
        var fake = new FakeChatClient()
            .EnqueueStream(Call("c1", "nope", []))
            .EnqueueStream("Done.");

        var events = await Collect(Runner(fake), Request());

        var finished = Assert.Single(events.OfType<ToolCallFinished>());
        Assert.Equal("Unknown tool 'nope'.", finished.Output);
        Assert.False(finished.IsError);
    }

    [Fact]
    public async Task MaxHops_CutsARunawayToolLoop()
    {
        var tool = new AgentTool
        {
            Name = "again",
            Description = "d",
            ParametersSchema = """{"type":"object","properties":{}}""",
            Handler = (_, _) => Task.FromResult(new ToolOutcome("ok")),
        };
        var fake = new FakeChatClient();
        for (var i = 0; i < 10; i++)
        {
            fake.EnqueueStream(Call($"c{i}", "again", []));
        }

        var events = await Collect(Runner(fake), Request(tool), new AgentRunnerOptions { MaxHops = 3 });

        Assert.Equal(3, events.OfType<ToolCallFinished>().Count());
        Assert.Single(events.OfType<Completed>());
    }

    [Fact]
    public async Task Cancellation_PropagatesOut()
    {
        using var cts = new CancellationTokenSource();
        var fake = new FakeChatClient().EnqueueStream("a", "b");
        var runner = Runner(fake);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var ev in runner.RunAsync(Request(), ct: cts.Token))
            {
                cts.Cancel();
            }
        });
    }

    [Fact]
    public async Task HostedToolArtifacts_AreNotResentInHistory_ButTextAndCallsAre()
    {
        // Hop 1: a web-search artifact + text + a function call. The Responses API rejects a resubmitted
        // web_search_call without its (non-round-trippable) reasoning item — so hop 2 must not carry it.
        var tool = new AgentTool
        {
            Name = "noop",
            Description = "d",
            ParametersSchema = """{"type":"object","properties":{}}""",
            Handler = (_, _) => Task.FromResult(new ToolOutcome("ok")),
        };
        var fake = new FakeChatClient()
            .EnqueueStream(new ChatResponseUpdate(ChatRole.Assistant,
            [
                new WebSearchToolCallContent("ws_1"),
                new TextContent("searched. "),
                new FunctionCallContent("c1", "noop", new Dictionary<string, object?>()),
            ]))
            .EnqueueStream("Done.");

        await Collect(Runner(fake), Request(tool));

        var hop2 = fake.Calls[1].Messages;
        Assert.DoesNotContain(hop2, m => m.Contents.Any(c => c is WebSearchToolCallContent));
        Assert.Contains(hop2, m => m.Contents.Any(c => c is FunctionCallContent));
        Assert.Contains(hop2, m => m.Contents.Any(c => c is TextContent t && t.Text.Contains("searched")));
    }

    [Fact]
    public async Task CompletionScope_SurvivesEveryHop_ForLoggingAndAlerting()
    {
        // The runner produces via a channel so the AsyncLocal scope isn't lost after the first yielded
        // event — every hop's completion record must carry the feature tag.
        var tool = new AgentTool
        {
            Name = "noop",
            Description = "d",
            ParametersSchema = """{"type":"object","properties":{}}""",
            Handler = (_, _) => Task.FromResult(new ToolOutcome("ok")),
        };
        var sink = new AgentKit.Logging.InMemorySink();
        var card = new ModelCard { Id = "gpt-chat-latest", Provider = "foundry" };
        var fake = new FakeChatClient()
            .EnqueueStream(Call("c1", "noop", []))
            .EnqueueStream("Done.");
        var logged = new AgentKit.Logging.LoggingChatClient(fake, card, sink);
        var options = TestCatalog.Options();
        var router = new CapabilityTierRouter(TestCatalog.Catalog(options), Options.Create(options));
        var runner = new AgentRunner(router, new SingleClientFactory(logged));

        await Collect(runner, Request(tool));

        Assert.Equal(2, sink.Records.Count);
        Assert.All(sink.Records, r => Assert.Equal("test-agent", r.Feature));
        Assert.Equal([1, 2], sink.Records.Select(r => r.Hop));
    }

    [Fact]
    public async Task WebSearchTool_AddedByDefault_OmittedWhenDisabled()
    {
        var fake = new FakeChatClient().EnqueueStream("hi");
        await Collect(Runner(fake), Request());
        Assert.Contains(fake.Calls[0].Options!.Tools!, t => t is HostedWebSearchTool);

        var fake2 = new FakeChatClient().EnqueueStream("hi");
        await Collect(Runner(fake2), Request(), new AgentRunnerOptions { EnableWebSearch = false });
        Assert.DoesNotContain(fake2.Calls[0].Options!.Tools ?? [], t => t is HostedWebSearchTool);
    }
}
