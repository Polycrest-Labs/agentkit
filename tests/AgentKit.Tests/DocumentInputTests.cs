using AgentKit.Agents;
using AgentKit.Catalog;
using AgentKit.Providers;
using AgentKit.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentKit.Tests;

/// <summary>The 0.2.0 document-input surface: request assembly (one-shot + agent), routing by the
/// Documents way, the gate's loud refusal, and catalog binding.</summary>
public sealed class DocumentInputTests
{
    private static readonly byte[] PdfBytes = [0x25, 0x50, 0x44, 0x46]; // "%PDF"

    private sealed class SingleClientFactory(IChatClient client) : IChatClientFactory
    {
        public IChatClient GetClient(ModelCard model) => client;
        public IChatClient GetClient(ModelResolution resolution) => client;
    }

    private static (LlmClient Client, FakeChatClient Fake) Client()
    {
        var fake = new FakeChatClient();
        var options = TestCatalog.Options();
        var router = new CapabilityTierRouter(TestCatalog.Catalog(options), Options.Create(options));
        return (new LlmClient(router, new SingleClientFactory(fake)), fake);
    }

    private static AgentRunner Runner(FakeChatClient fake)
    {
        var options = TestCatalog.Options();
        var router = new CapabilityTierRouter(TestCatalog.Catalog(options), Options.Create(options));
        return new AgentRunner(router, new SingleClientFactory(fake));
    }

    // ---- one-shot assembly -------------------------------------------------------------------

    [Fact]
    public async Task CompleteAsync_DocumentsRideAsDataContent_WithMediaTypeAndName()
    {
        var (client, fake) = Client();
        fake.EnqueueText("ok");

        await client.CompleteAsync(LlmWay.Low, "test", "sys", "read this",
            documents: [new LlmDocument("application/pdf", PdfBytes, "lease.pdf")]);

        var message = Assert.Single(fake.Calls[0].Messages);
        Assert.Equal("read this", Assert.Single(message.Contents.OfType<TextContent>()).Text);
        var data = Assert.Single(message.Contents.OfType<DataContent>());
        Assert.Equal("application/pdf", data.MediaType);
        Assert.Equal("lease.pdf", data.Name);
        Assert.Equal(PdfBytes, data.Data.ToArray());
    }

    [Fact]
    public async Task CompleteAsync_ImagesAndDocumentsTogether_BothArriveTyped()
    {
        var (client, fake) = Client();
        fake.EnqueueText("ok");

        await client.CompleteAsync(LlmWay.Low, "test", "sys", "both",
            images: [new LlmImage("image/png", [1, 2, 3])],
            documents: [new LlmDocument("application/pdf", PdfBytes)]);

        var contents = Assert.Single(fake.Calls[0].Messages).Contents;
        Assert.Equal(["image/png", "application/pdf"],
            contents.OfType<DataContent>().Select(d => d.MediaType));
    }

    [Fact]
    public async Task CompleteAsync_EmptyDocumentsList_AddsNoContentParts()
    {
        var (client, fake) = Client();
        fake.EnqueueText("ok");

        await client.CompleteAsync(LlmWay.Low, "test", "sys", "plain", documents: []);

        var message = Assert.Single(fake.Calls[0].Messages);
        Assert.Empty(message.Contents.OfType<DataContent>());
    }

    // ---- agent-turn assembly -----------------------------------------------------------------

    [Fact]
    public async Task AgentTurnRequest_Documents_AssembleOnTheUserMessage()
    {
        var fake = new FakeChatClient().EnqueueStream("done");

        var request = new AgentTurnRequest
        {
            Instructions = "sys",
            UserText = "classify",
            Tools = new AgentToolCatalog([]),
            Documents = [new LlmDocument("application/pdf", PdfBytes, "deed.pdf")],
        };
        await foreach (var _ in Runner(fake).RunAsync(request))
        {
        }

        var message = Assert.Single(fake.Calls[0].Messages);
        var data = Assert.Single(message.Contents.OfType<DataContent>());
        Assert.Equal("application/pdf", data.MediaType);
        Assert.Equal("deed.pdf", data.Name);
    }

    // ---- routing -----------------------------------------------------------------------------

    [Fact]
    public void Router_DocumentsWay_ResolvesOnlyDocumentsCapableCards()
    {
        var options = TestCatalog.Options();
        options.Models.Add(new ModelCard
        {
            Id = "gpt-docs", Provider = "foundry", Tier = ModelTier.High, Vision = true, Documents = true,
        });
        var router = new CapabilityTierRouter(TestCatalog.Catalog(options), Options.Create(options));

        var resolution = router.Resolve(LlmWay.HighDocuments);

        Assert.Equal("gpt-docs", resolution.Model.Id);
        Assert.All(resolution.Candidates, c => Assert.True(c.Documents));
    }

    [Fact]
    public void Router_DocumentsWay_NoCapableCard_ThrowsListingCatalog()
    {
        var options = TestCatalog.Options();
        var router = new CapabilityTierRouter(TestCatalog.Catalog(options), Options.Create(options));

        var ex = Assert.Throws<InvalidOperationException>(() => router.Resolve(LlmWay.HighDocuments));
        Assert.Contains("high+documents", ex.Message);
    }

    // ---- the gate ----------------------------------------------------------------------------

    [Fact]
    public async Task Gate_DocumentAgainstNonDocumentsCard_RefusesLoudlyNamingTheCard()
    {
        var fake = new FakeChatClient().EnqueueText("never reached");
        var card = new ModelCard { Id = "gpt-5-mini", Provider = "foundry", Vision = true };
        var gate = new CapabilityGateChatClient(fake, card);
        var messages = new[]
        {
            new ChatMessage(ChatRole.User,
            [
                new TextContent("classify"),
                new DataContent(PdfBytes, "application/pdf"),
            ]),
        };

        var ex = await Assert.ThrowsAsync<DocumentsNotSupportedException>(() =>
            gate.GetResponseAsync(messages));

        Assert.Equal("gpt-5-mini", ex.ModelId);
        Assert.Equal("application/pdf", ex.MediaType);
        Assert.Empty(fake.Calls); // refused before the provider, never silently dropped
    }

    [Fact]
    public async Task Gate_ImageAgainstNonDocumentsCard_PassesThrough()
    {
        var fake = new FakeChatClient().EnqueueText("ok");
        var card = new ModelCard { Id = "gpt-5-mini", Provider = "foundry", Vision = true };
        var gate = new CapabilityGateChatClient(fake, card);

        var response = await gate.GetResponseAsync(
            [new ChatMessage(ChatRole.User, [new TextContent("look"), new DataContent(new byte[] { 1 }, "image/png")])]);

        Assert.Equal("ok", response.Text);
    }

    [Fact]
    public async Task Gate_DocumentAgainstDocumentsCard_PassesThrough()
    {
        var fake = new FakeChatClient().EnqueueText("ok");
        var card = new ModelCard { Id = "gpt-docs", Provider = "foundry", Vision = true, Documents = true };
        var gate = new CapabilityGateChatClient(fake, card);

        var response = await gate.GetResponseAsync(
            [new ChatMessage(ChatRole.User, [new TextContent("read"), new DataContent(PdfBytes, "application/pdf")])]);

        Assert.Equal("ok", response.Text);
    }

    // ---- catalog binding + labels ------------------------------------------------------------

    [Fact]
    public void CatalogBinding_ReadsDocumentsFlag()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Llm:Providers:foundry:Kind"] = "azure-openai",
            ["Llm:Providers:foundry:Endpoint"] = "https://example.openai.azure.com/",
            ["Llm:Models:0:Id"] = "gpt-docs",
            ["Llm:Models:0:Provider"] = "foundry",
            ["Llm:Models:0:Tier"] = "High",
            ["Llm:Models:0:Vision"] = "true",
            ["Llm:Models:0:Documents"] = "true",
            ["Llm:Logging:Sink"] = "memory",
        }).Build();
        using var services = new ServiceCollection().AddLogging().AddAgentKit(config).BuildServiceProvider();

        var card = services.GetRequiredService<IModelCatalog>().Get("gpt-docs");

        Assert.True(card.Documents);
    }

    [Fact]
    public void LlmWay_DocumentsLabel_ReadsInLogsAndRecords() =>
        Assert.Equal("high+documents", LlmWay.HighDocuments.ToString());
}
