using AgentKit.Catalog;
using AgentKit.Logging;
using Microsoft.Extensions.AI;

namespace AgentKit.Tests;

public sealed class LoggingChatClientTests
{
    private static readonly ModelCard Card = new()
    {
        Id = "qwen3.6-35b",
        Provider = "neuralwatt",
        PriceInPerMtok = 0.29m,
        PriceOutPerMtok = 1.15m,
    };

    private static readonly ModelCard UnpricedCard = new() { Id = "gpt-chat-latest", Provider = "foundry" };

    [Fact]
    public async Task NonStreaming_EmitsOneRecordWithRouteUsageAndCost()
    {
        var sink = new InMemorySink();
        var fake = new FakeChatClient().EnqueueText("hello",
            new UsageDetails { InputTokenCount = 1_000_000, OutputTokenCount = 2_000_000 });
        var client = new LoggingChatClient(fake, Card, sink);

        using var scope = CompletionScope.Begin("test-feature", conversationId: "conv-1", userId: "user-1");
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions { Instructions = "sys" });

        var record = Assert.Single(sink.Records);
        Assert.Equal("test-feature", record.Feature);
        Assert.Equal("qwen3.6-35b", record.Route.Model);
        Assert.Equal("neuralwatt", record.Route.Provider);
        Assert.Equal("conv-1", record.ConversationId);
        Assert.Equal("sys", record.Request.System);
        Assert.Equal("hello", record.Response.Text);
        Assert.Equal(1_000_000, record.Usage!.Input);
        // 1M in @ $0.29 + 2M out @ $1.15 = $2.59
        Assert.Equal(2.59m, record.CostUsd);
        Assert.Equal(1, record.Hop);
    }

    [Fact]
    public async Task MissingPrices_ReportNullCost_NotZero()
    {
        var sink = new InMemorySink();
        var fake = new FakeChatClient().EnqueueText("hello", new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 });
        var client = new LoggingChatClient(fake, UnpricedCard, sink);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Null(Assert.Single(sink.Records).CostUsd);
        Assert.NotNull(Assert.Single(sink.Records).Usage);
    }

    [Fact]
    public async Task Streaming_AccumulatesUpdatesIntoOneRecord()
    {
        var sink = new InMemorySink();
        var fake = new FakeChatClient().EnqueueStream("Hel", "lo",
            new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 3, OutputTokenCount = 2 })]));
        var client = new LoggingChatClient(fake, Card, sink);

        using var scope = CompletionScope.Begin("stream-feature");
        var text = "";
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            text += update.Text;
        }

        Assert.Equal("Hello", text);
        var record = Assert.Single(sink.Records);
        Assert.Equal("Hello", record.Response.Text);
        Assert.Equal(3, record.Usage!.Input);
        Assert.Null(record.Error);
    }

    [Fact]
    public async Task StreamFailure_RecordsErrorAndRethrows()
    {
        var sink = new InMemorySink();
        var fake = new FakeChatClient().EnqueueStream("partial", new InvalidOperationException("boom"));
        var client = new LoggingChatClient(fake, Card, sink);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            {
            }
        });

        var record = Assert.Single(sink.Records);
        Assert.Contains("boom", record.Error);
    }

    [Fact]
    public async Task SinkFailure_NeverFaultsTheCall()
    {
        var fake = new FakeChatClient().EnqueueText("hello");
        var client = new LoggingChatClient(fake, Card, new ThrowingSink());

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal("hello", response.Text);
    }

    [Fact]
    public async Task ImageBytes_AreLoggedAsHashes_NeverRaw()
    {
        var sink = new InMemorySink();
        var fake = new FakeChatClient().EnqueueText("ok");
        var client = new LoggingChatClient(fake, Card, sink);

        var message = new ChatMessage(ChatRole.User, [new TextContent("look"), new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);
        await client.GetResponseAsync([message]);

        var logged = Assert.Single(sink.Records).Request.Messages.Single().Text;
        Assert.Contains("sha256:", logged);
        Assert.Contains("image/png", logged);
    }

    private sealed class ThrowingSink : ICompletionSink
    {
        public void Record(CompletionRecord record) => throw new IOException("sink down");
    }
}
