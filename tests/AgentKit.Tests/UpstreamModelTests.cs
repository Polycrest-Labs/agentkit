using AgentKit.Catalog;
using Microsoft.Extensions.Configuration;

namespace AgentKit.Tests;

/// <summary>Two catalog entries can target the same upstream model (e.g. free-tier vs paid-tier keys
/// via different providers): unique catalog <c>Id</c>s, shared <c>UpstreamModel</c> sent to the API.</summary>
public sealed class UpstreamModelTests
{
    [Fact]
    public void ApiModel_FallsBackToId_WhenNoUpstreamModel()
    {
        Assert.Equal("kimi-k2.6", new ModelCard { Id = "kimi-k2.6" }.ApiModel);
    }

    [Fact]
    public void ApiModel_UsesUpstreamModel_WhenSet()
    {
        var card = new ModelCard { Id = "gemini-3.5-flash-paid", UpstreamModel = "gemini-3.5-flash" };
        Assert.Equal("gemini-3.5-flash", card.ApiModel);
    }

    [Fact]
    public void UpstreamModel_BindsFromConfiguration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Llm:Models:0:Id"] = "gemini-3.5-flash-paid",
            ["Llm:Models:0:Provider"] = "gemini-paid",
            ["Llm:Models:0:UpstreamModel"] = "gemini-3.5-flash",
        }).Build();

        var options = new LlmOptions();
        config.GetSection("Llm").Bind(options);

        var card = Assert.Single(options.Models);
        Assert.Equal("gemini-3.5-flash-paid", card.Id);
        Assert.Equal("gemini-3.5-flash", card.ApiModel);
    }
}
