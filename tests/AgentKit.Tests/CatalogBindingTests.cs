using AgentKit.Catalog;
using AgentKit.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentKit.Tests;

public sealed class CatalogBindingTests
{
    private static IConfiguration Config(params (string Key, string Value)[] extra)
    {
        var values = new Dictionary<string, string?>
        {
            ["Llm:Providers:foundry:Kind"] = "azure-openai",
            ["Llm:Providers:foundry:Endpoint"] = "https://example.openai.azure.com/",
            ["Llm:Models:0:Id"] = "gpt-chat-latest",
            ["Llm:Models:0:Provider"] = "foundry",
            ["Llm:Models:0:Tier"] = "High",
            ["Llm:Models:0:Vision"] = "true",
            ["Llm:Models:0:Search"] = "true",
            ["Llm:Models:0:Quirks:FixedTemperature"] = "true",
            ["Llm:Models:1:Id"] = "qwen3.6-35b",
            ["Llm:Models:1:Provider"] = "neuralwatt",
            ["Llm:Models:1:Tier"] = "Low",
            ["Llm:Models:1:PriceInPerMtok"] = "0.29",
            ["Llm:Models:1:PriceOutPerMtok"] = "1.15",
            ["Llm:Routing:Prefer:high:0"] = "gpt-chat-latest",
            ["Llm:Logging:Sink"] = "memory",
        };
        foreach (var (key, value) in extra)
        {
            values[key] = value;
        }
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static ServiceProvider Services(IConfiguration config) =>
        new ServiceCollection().AddLogging().AddAgentKit(config).BuildServiceProvider();

    [Fact]
    public void CatalogBindsCards_WithTiersPricesAndQuirks()
    {
        using var services = Services(Config());
        var catalog = services.GetRequiredService<IModelCatalog>();

        var gpt = catalog.Get("GPT-CHAT-LATEST"); // case-insensitive
        Assert.Equal(ModelTier.High, gpt.Tier);
        Assert.True(gpt.Vision);
        Assert.True(gpt.Search);
        Assert.True(gpt.Quirks.FixedTemperature);
        Assert.Null(gpt.PriceInPerMtok);

        var qwen = catalog.Get("qwen3.6-35b");
        Assert.Equal(0.29m, qwen.PriceInPerMtok);
        Assert.Equal(1.15m, qwen.PriceOutPerMtok);
    }

    [Fact]
    public void UnknownModelId_ThrowsListingCatalog()
    {
        using var services = Services(Config());
        var catalog = services.GetRequiredService<IModelCatalog>();

        var ex = Assert.Throws<InvalidOperationException>(() => catalog.Get("gpt-unknown"));
        Assert.Contains("gpt-chat-latest", ex.Message);
    }

    [Fact]
    public void SinkRegistration_FollowsLoggingConfig()
    {
        using var memoryServices = Services(Config());
        Assert.IsType<InMemorySink>(memoryServices.GetRequiredService<ICompletionSink>());

        using var defaultServices = Services(Config(("Llm:Logging:Sink", "none")));
        Assert.IsType<NullCompletionSink>(defaultServices.GetRequiredService<ICompletionSink>());
    }

    [Fact]
    public void AddAgentKit_ResolvesTheFullPipeline()
    {
        using var services = Services(Config());
        Assert.NotNull(services.GetRequiredService<ILlmClient>());
        Assert.NotNull(services.GetRequiredService<AgentKit.Routing.IModelRouter>());
        Assert.NotNull(services.GetRequiredService<AgentKit.Providers.IChatClientFactory>());
    }
}
