using AgentKit.Catalog;
using AgentKit.Diagnostics;
using Microsoft.Extensions.Options;

namespace AgentKit.Tests;

/// <summary>Shared in-code catalog/options for router + pipeline tests (no config binding involved).</summary>
public static class TestCatalog
{
    public static LlmOptions Options(bool configureNeuralwatt = true)
    {
        var options = new LlmOptions();
        options.Providers["foundry"] = new LlmProviderOptions { Kind = "azure-openai", Endpoint = "https://example.openai.azure.com/" };
        if (configureNeuralwatt)
        {
            options.Providers["neuralwatt"] = new LlmProviderOptions { Kind = "openai-compat", Endpoint = "https://api.neuralwatt.com/v1", ApiKey = "test-key" };
        }
        else
        {
            options.Providers["neuralwatt"] = new LlmProviderOptions { Kind = "openai-compat", Endpoint = "https://api.neuralwatt.com/v1" };
        }
        options.Models.AddRange(
        [
            new ModelCard { Id = "gpt-chat-latest", Provider = "foundry", Tier = ModelTier.High, Vision = true, Search = true, Quirks = new ModelQuirks { FixedTemperature = true } },
            new ModelCard { Id = "gpt-5-mini", Provider = "foundry", Tier = ModelTier.Low, Vision = true, Search = true },
            new ModelCard { Id = "kimi-k2.6", Provider = "neuralwatt", Tier = ModelTier.High, Vision = true, PriceInPerMtok = 0.69m, PriceOutPerMtok = 3.22m },
            new ModelCard { Id = "qwen3.6-35b", Provider = "neuralwatt", Tier = ModelTier.Low, Vision = true, PriceInPerMtok = 0.29m, PriceOutPerMtok = 1.15m },
            new ModelCard { Id = "glm-5.2", Provider = "neuralwatt", Tier = ModelTier.High, PriceInPerMtok = 1.45m, PriceOutPerMtok = 4.50m },
        ]);
        options.Routing.Prefer["high"] = ["gpt-chat-latest", "gpt-5-mini"];
        options.Routing.Prefer["low"] = ["gpt-chat-latest", "gpt-5-mini"];
        return options;
    }

    public static ModelCatalog Catalog(LlmOptions? options = null) =>
        new(Microsoft.Extensions.Options.Options.Create(options ?? Options()));
}

/// <summary>Test double capturing diagnostics notifications.</summary>
public sealed class RecordingDiagnostics : ILlmDiagnostics
{
    public List<string> Failures { get; } = [];
    public List<string> Failovers { get; } = [];
    public List<string> Exhausted { get; } = [];

    public void ModelFailure(string model, string provider, string feature, Exception exception) =>
        Failures.Add(model);

    public void Failover(string fromModel, string toModel, string provider, string feature) =>
        Failovers.Add($"{fromModel}->{toModel}");

    public void FailoverExhausted(string feature, string way, string lastModel, Exception exception) =>
        Exhausted.Add(lastModel);
}
