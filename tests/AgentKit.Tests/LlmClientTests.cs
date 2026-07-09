using AgentKit.Catalog;
using AgentKit.Providers;
using AgentKit.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace AgentKit.Tests;

public sealed class LlmClientTests
{
    private sealed class FakeFactory(IChatClient client) : IChatClientFactory
    {
        public IChatClient GetClient(ModelCard model) => client;
        public IChatClient GetClient(ModelResolution resolution) => client;
    }

    private static LlmClient Client(FakeChatClient fake)
    {
        var options = TestCatalog.Options();
        var router = new CapabilityTierRouter(TestCatalog.Catalog(options), Options.Create(options));
        return new LlmClient(router, new FakeFactory(fake));
    }

    [Theory]
    [InlineData("```json\n{\"style\":\"poster\"}\n```", "{\"style\":\"poster\"}")]
    [InlineData("Sure! Here you go: {\"style\":\"poster\"} — enjoy", "{\"style\":\"poster\"}")]
    [InlineData("[{\"a\":1}] trailing", "[{\"a\":1}]")]
    [InlineData("no json at all", null)]
    public void ExtractJson_ToleratesFencesAndProse(string text, string? expected) =>
        Assert.Equal(expected, JsonExtraction.ExtractJson(text));

    [Fact]
    public async Task CompleteJsonAsync_ExtractsAndTolerantlyDeserializes()
    {
        // Fenced reply + a number where the DTO wants a string — both must be tolerated.
        var fake = new FakeChatClient().EnqueueText("```json\n{\"name\": 42}\n```");

        var result = await Client(fake).CompleteJsonAsync<Payload>(LlmWay.Low, "test", "sys", "user");

        Assert.Equal("42", result.Name);
    }

    [Fact]
    public async Task CompleteJsonAsync_NoJsonInReply_YieldsEmptyObject()
    {
        var fake = new FakeChatClient().EnqueueText("I could not produce anything.");

        var json = await Client(fake).CompleteJsonAsync(LlmWay.Low, "test", "sys", "user");

        Assert.Equal("{}", json);
    }

    [Fact]
    public async Task CompleteJsonAsync_Generic_NoJsonInReply_Throws()
    {
        // A refusal / unreadable-input reply carries no JSON. The typed overload must fail loudly (so the
        // caller's retry/error handling runs) rather than deserialize "{}" into a silently blank result.
        var fake = new FakeChatClient().EnqueueText("I could not read this receipt.");

        var ex = await Assert.ThrowsAsync<LlmNoJsonException>(() =>
            Client(fake).CompleteJsonAsync<Payload>(LlmWay.Low, "receipt", "sys", "user"));
        Assert.Equal("receipt", ex.Feature);
    }

    [Fact]
    public async Task CompleteJsonAsync_Generic_ExplicitEmptyObject_DoesNotThrow()
    {
        // A model that genuinely returns {} is a valid (if empty) payload, distinct from no JSON at all.
        var fake = new FakeChatClient().EnqueueText("{}");

        var result = await Client(fake).CompleteJsonAsync<Payload>(LlmWay.Low, "test", "sys", "user");

        Assert.Null(result.Name);
    }

    [Fact]
    public async Task SystemPromptRidesInstructions_AndImagesRideAsDataContent()
    {
        var fake = new FakeChatClient().EnqueueText("ok");

        await Client(fake).CompleteAsync(LlmWay.LowVision, "test", "the-system-prompt", "user text",
            [new LlmImage("image/png", [1, 2, 3])]);

        var (messages, options) = Assert.Single(fake.Calls);
        Assert.Equal("the-system-prompt", options!.Instructions);
        var user = Assert.Single(messages);
        Assert.Contains(user.Contents, c => c is DataContent { MediaType: "image/png" });
    }

    [Fact]
    public async Task SearchWay_AddsHostedWebSearchTool()
    {
        var fake = new FakeChatClient().EnqueueText("ok");

        await Client(fake).CompleteAsync(LlmWay.HighSearch, "test", "sys", "user");

        var (_, options) = Assert.Single(fake.Calls);
        Assert.Contains(options!.Tools!, t => t is HostedWebSearchTool);
    }

    [Fact]
    public async Task CapabilityGate_StripsSearchToolAndFixedTemperature()
    {
        var fake = new FakeChatClient().EnqueueText("ok");
        var card = new ModelCard { Id = "glm-5.2", Provider = "neuralwatt", Quirks = new ModelQuirks { FixedTemperature = true } };
        var gated = new CapabilityGateChatClient(fake, card);

        await gated.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { Tools = [new HostedWebSearchTool()], Temperature = 0.7f });

        var (_, options) = Assert.Single(fake.Calls);
        Assert.DoesNotContain(options!.Tools ?? [], t => t is HostedWebSearchTool);
        Assert.Null(options.Temperature);
    }

    private sealed record Payload(string? Name);
}
