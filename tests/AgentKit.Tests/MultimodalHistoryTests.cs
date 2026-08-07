using AgentKit.Agents;
using AgentKit.Catalog;
using AgentKit.Providers;
using AgentKit.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace AgentKit.Tests;

/// <summary>History messages with ordered text/image/document parts become real multimodal
/// content on the wire (the host replays eligible prior attachments); text-only history keeps
/// its existing construction path.</summary>
public sealed class MultimodalHistoryTests
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

    private static async Task Drain(AgentRunner runner, AgentTurnRequest request)
    {
        await foreach (var _ in runner.RunAsync(request))
        {
        }
    }

    [Fact]
    public async Task HistoryParts_BecomeOrderedMultimodalContent()
    {
        var fake = new FakeChatClient().EnqueueText("ok");
        var image = new LlmImage("image/png", [1, 2, 3]);
        var document = new LlmDocument("application/pdf", [4, 5, 6], "lease.pdf");
        var request = new AgentTurnRequest
        {
            Instructions = "sys",
            UserText = "and now?",
            Tools = new AgentToolCatalog([]),
            History =
            [
                new AgentHistoryMessage("user", string.Empty)
                {
                    Parts =
                    [
                        new AgentHistoryTextPart("Here is the site photo and the lease."),
                        new AgentHistoryImagePart(image),
                        new AgentHistoryDocumentPart(document),
                    ],
                },
                new AgentHistoryMessage("assistant", "Got both — the photo shows paved access."),
            ],
        };

        await Drain(Runner(fake), request);

        var (messages, _) = Assert.Single(fake.Calls);
        Assert.Equal(3, messages.Count); // multimodal user history + assistant text + current user text

        var multimodal = messages[0];
        Assert.Equal(ChatRole.User, multimodal.Role);
        Assert.Equal(3, multimodal.Contents.Count);
        Assert.Equal("Here is the site photo and the lease.", Assert.IsType<TextContent>(multimodal.Contents[0]).Text);
        var imagePart = Assert.IsType<DataContent>(multimodal.Contents[1]);
        Assert.Equal("image/png", imagePart.MediaType);
        var documentPart = Assert.IsType<DataContent>(multimodal.Contents[2]);
        Assert.Equal("application/pdf", documentPart.MediaType);
        Assert.Equal("lease.pdf", documentPart.Name);

        Assert.Equal(ChatRole.Assistant, messages[1].Role);
        Assert.Equal("Got both — the photo shows paved access.", messages[1].Text);
    }

    [Fact]
    public async Task PartlessHistory_KeepsTheTextOnlyPath_AndBlankMessagesDrop()
    {
        var fake = new FakeChatClient().EnqueueText("ok");
        var request = new AgentTurnRequest
        {
            Instructions = "sys",
            UserText = "hello",
            Tools = new AgentToolCatalog([]),
            History =
            [
                new AgentHistoryMessage("user", "plain text history"),
                new AgentHistoryMessage("assistant", "   "),           // whitespace-only → dropped
                new AgentHistoryMessage("user", string.Empty) { Parts = [new AgentHistoryTextPart("  ")] }, // no usable parts → dropped
            ],
        };

        await Drain(Runner(fake), request);

        var (messages, _) = Assert.Single(fake.Calls);
        Assert.Equal(2, messages.Count);
        Assert.Equal("plain text history", messages[0].Text);
        Assert.Equal("hello", messages[1].Text);
    }
}
