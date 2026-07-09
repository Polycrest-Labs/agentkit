using AgentKit.Catalog;
using AgentKit.Logging;
using AgentKit.Resilience;
using AgentKit.Routing;
using Microsoft.Extensions.AI;

namespace AgentKit.Tests;

public sealed class FailoverTests
{
    private static readonly ModelCard Primary = new() { Id = "gpt-chat-latest", Provider = "foundry" };
    private static readonly ModelCard Fallback = new() { Id = "gpt-5-mini", Provider = "foundry" };

    private static FailoverChatClient Client(
        RecordingDiagnostics diagnostics, IChatClient primary, IChatClient? fallback = null, InMemorySink? sink = null)
    {
        var resolution = fallback is null
            ? new ModelResolution(LlmWay.High, Primary, "test", [])
            : new ModelResolution(LlmWay.High, Primary, "test", [Fallback]);
        IChatClient Wrap(ModelCard card, IChatClient client) =>
            sink is null ? client : new LoggingChatClient(client, card, sink);
        return new FailoverChatClient(resolution,
            card => Wrap(card, card.Id == Primary.Id ? primary : fallback!), diagnostics);
    }

    [Fact]
    public async Task EligibleFailure_FailsOverToNextCandidate_ExactlyOneFailoverEvent()
    {
        var diagnostics = new RecordingDiagnostics();
        var sink = new InMemorySink();
        var primary = new FakeChatClient().EnqueueError(new HttpRequestException("429ish", null, System.Net.HttpStatusCode.TooManyRequests));
        var fallback = new FakeChatClient().EnqueueText("rescued");

        using var scope = CompletionScope.Begin("failover-test");
        var response = await Client(diagnostics, primary, fallback, sink)
            .GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal("rescued", response.Text);
        Assert.Equal(["gpt-chat-latest->gpt-5-mini"], diagnostics.Failovers);
        Assert.Empty(diagnostics.Exhausted);

        // The logging layer stamped the failover attempt: primary error record, then attempt-2 success.
        Assert.Equal(2, sink.Records.Count);
        var success = sink.Records.Single(r => r.Error is null);
        Assert.Equal(2, success.Attempt);
        Assert.Equal("gpt-chat-latest", success.FailedOverFrom);
        Assert.Equal("gpt-5-mini", success.Route.Model);
    }

    [Fact]
    public async Task IneligibleFailure_DoesNotFailOver()
    {
        var diagnostics = new RecordingDiagnostics();
        var primary = new FakeChatClient().EnqueueError(new ArgumentException("bad request shape"));
        var fallback = new FakeChatClient().EnqueueText("never");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Client(diagnostics, primary, fallback).GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        Assert.Empty(diagnostics.Failovers);
        Assert.Equal(["gpt-chat-latest"], diagnostics.Failures);
    }

    [Fact]
    public async Task PinnedSingleCandidate_ExhaustsWithoutFallback()
    {
        var diagnostics = new RecordingDiagnostics();
        var primary = new FakeChatClient().EnqueueError(new HttpRequestException("down"));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Client(diagnostics, primary).GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        Assert.Empty(diagnostics.Failovers);
        Assert.Equal(["gpt-chat-latest"], diagnostics.Exhausted);
    }

    [Fact]
    public async Task AllCandidatesFail_EmitsExhausted()
    {
        var diagnostics = new RecordingDiagnostics();
        var primary = new FakeChatClient().EnqueueError(new HttpRequestException("down"));
        var fallback = new FakeChatClient().EnqueueError(new HttpRequestException("also down"));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Client(diagnostics, primary, fallback).GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        Assert.Equal(["gpt-chat-latest->gpt-5-mini"], diagnostics.Failovers);
        Assert.Equal(["gpt-5-mini"], diagnostics.Exhausted);
    }

    [Fact]
    public async Task StreamFailure_BeforeFirstUpdate_FailsOver()
    {
        var diagnostics = new RecordingDiagnostics();
        var primary = new FakeChatClient().EnqueueStream(new HttpRequestException("cold start failure"));
        var fallback = new FakeChatClient().EnqueueStream("res", "cued");

        var text = "";
        await foreach (var update in Client(diagnostics, primary, fallback)
            .GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            text += update.Text;
        }

        Assert.Equal("rescued", text);
        Assert.Single(diagnostics.Failovers);
    }

    [Fact]
    public async Task MidStreamFailure_DoesNotRetry_ButStillAlerts()
    {
        var diagnostics = new RecordingDiagnostics();
        var primary = new FakeChatClient().EnqueueStream("partial ", new HttpRequestException("died mid-stream"));
        var fallback = new FakeChatClient().EnqueueStream("never");

        var text = "";
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var update in Client(diagnostics, primary, fallback)
                .GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            {
                text += update.Text;
            }
        });

        Assert.Equal("partial ", text);
        Assert.Empty(diagnostics.Failovers);
        Assert.Equal(["gpt-chat-latest"], diagnostics.Failures); // alert still emitted
    }
}
