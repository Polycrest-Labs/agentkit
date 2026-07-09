using AgentKit.Catalog;
using AgentKit.Routing;
using Microsoft.Extensions.Options;

namespace AgentKit.Tests;

public sealed class RouterTests
{
    private static CapabilityTierRouter Router(LlmOptions? options = null)
    {
        options ??= TestCatalog.Options();
        return new CapabilityTierRouter(TestCatalog.Catalog(options), Options.Create(options));
    }

    [Fact]
    public void PreferredModelIsPrimary_RestAreFailoverChain()
    {
        var resolution = Router().Resolve(LlmWay.High);

        Assert.Equal("gpt-chat-latest", resolution.Model.Id);
        // Prefer list first (gpt-5-mini), then remaining capable models cheapest-first.
        Assert.Equal(["gpt-5-mini", "qwen3.6-35b", "kimi-k2.6", "glm-5.2"], resolution.Fallbacks.Select(f => f.Id));
    }

    [Fact]
    public void VisionWay_FiltersToVisionCapableModels()
    {
        var resolution = Router().Resolve(LlmWay.LowVision);

        Assert.Equal("gpt-chat-latest", resolution.Model.Id);
        Assert.DoesNotContain(resolution.Fallbacks, f => f.Id == "glm-5.2"); // glm-5.2 has no vision
    }

    [Fact]
    public void SearchWay_FiltersToSearchCapableModels()
    {
        var resolution = Router().Resolve(LlmWay.HighSearch);

        Assert.All(resolution.Candidates, c => Assert.True(c.Search));
    }

    [Fact]
    public void NoCapableModel_ThrowsWithClearMessage()
    {
        var options = TestCatalog.Options();
        options.Models.RemoveAll(m => m.Search);

        var ex = Assert.Throws<InvalidOperationException>(() => Router(options).Resolve(LlmWay.HighSearch));
        Assert.Contains("high+search", ex.Message);
    }

    [Fact]
    public void UnconfiguredProviderModels_AreSkipped()
    {
        var options = TestCatalog.Options(configureNeuralwatt: false); // neuralwatt has no API key

        var resolution = Router(options).Resolve(LlmWay.Low);

        Assert.All(resolution.Candidates, c => Assert.Equal("foundry", c.Provider));
    }

    [Fact]
    public void Pin_BypassesCapabilityFilter_AndDisablesFailover()
    {
        // glm-5.2 has no vision, but a pin resolves it anyway — single candidate, no fallbacks.
        var resolution = Router().Resolve(LlmWay.LowVision, modelPin: "glm-5.2");

        Assert.Equal("glm-5.2", resolution.Model.Id);
        Assert.Empty(resolution.Fallbacks);
        Assert.Contains("pinned", resolution.Reason);
    }

    [Fact]
    public void UnknownPin_ThrowsListingCatalogIds()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Router().Resolve(LlmWay.Low, modelPin: "gpt-nope"));
        Assert.Contains("gpt-chat-latest", ex.Message);
        Assert.Contains("gpt-nope", ex.Message);
    }
}
