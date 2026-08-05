using AgentKit.Catalog;
using AgentKit.Diagnostics;
using AgentKit.Logging;
using AgentKit.Providers;
using Microsoft.Extensions.Options;

namespace AgentKit.Tests;

/// <summary>The <c>openai-responses</c> kind: the OpenAI platform's Responses API, which is the only
/// path where OpenAI permits function tools TOGETHER WITH reasoning (chat completions refuses the
/// combination on its newer models).</summary>
public sealed class OpenAIResponsesProviderTests
{
    private static LlmOptions Options(string? apiKey, string? endpoint = null)
    {
        var options = new LlmOptions();
        options.Providers["openai"] = new LlmProviderOptions
        {
            Kind = "openai-responses",
            ApiKey = apiKey,
            Endpoint = endpoint,
        };
        options.Models.Add(new ModelCard { Id = "gpt-5.6-luna", Provider = "openai", Tier = ModelTier.High });
        return options;
    }

    private static ChatClientFactory Factory(LlmOptions options) =>
        new(Microsoft.Extensions.Options.Options.Create(options),
            NullCompletionSink.Instance, new RecordingDiagnostics());

    [Fact]
    public void BuildsAClient_WithNoEndpoint_BecauseTheSdkKnowsWhereOpenAILives()
    {
        var client = Factory(Options("sk-test")).GetClient(new ModelCard { Id = "gpt-5.6-luna", Provider = "openai" });

        Assert.NotNull(client);
    }

    [Fact]
    public void BuildsAClient_WhenAnEndpointIsSupplied()
    {
        var client = Factory(Options("sk-test", "https://gateway.example.com/v1"))
            .GetClient(new ModelCard { Id = "gpt-5.6-luna", Provider = "openai" });

        Assert.NotNull(client);
    }

    /// <summary>Endpoint-less is CONFIGURED for this kind alone. Getting this wrong is silent: routing
    /// skips unconfigured providers, so the model would simply vanish from the catalog rather than
    /// raise anything a host could read.</summary>
    [Fact]
    public void IsConfigured_WithKeyAlone()
    {
        Assert.True(Options("sk-test").Providers["openai"].IsConfigured);
        Assert.False(Options(apiKey: null).Providers["openai"].IsConfigured);
    }

    [Fact]
    public void OtherKinds_StillRequireAnEndpoint()
    {
        var compat = new LlmProviderOptions { Kind = "openai-compat", ApiKey = "sk-test" };
        Assert.False(compat.IsConfigured);

        compat.Endpoint = "https://api.neuralwatt.com/v1";
        Assert.True(compat.IsConfigured);
    }

    [Fact]
    public void MissingKey_FailsWithTheSettingToSet()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            OpenAIResponsesProvider.Create(
                new ModelCard { Id = "gpt-5.6-luna", Provider = "openai" },
                new LlmProviderOptions { Kind = "openai-responses" }));

        Assert.Contains("Llm:Providers:openai:ApiKey", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownKind_NamesEveryKindItAccepts()
    {
        var options = new LlmOptions();
        options.Providers["weird"] = new LlmProviderOptions { Kind = "not-a-kind", Endpoint = "https://x/", ApiKey = "k" };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            Factory(options).GetClient(new ModelCard { Id = "m", Provider = "weird" }));

        Assert.Contains("openai-responses", exception.Message, StringComparison.Ordinal);
        Assert.Contains("azure-openai", exception.Message, StringComparison.Ordinal);
        Assert.Contains("openai-compat", exception.Message, StringComparison.Ordinal);
        Assert.Contains("gemini-native", exception.Message, StringComparison.Ordinal);
    }
}
