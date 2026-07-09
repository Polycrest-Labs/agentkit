using System.ClientModel;
using AgentKit.Catalog;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentKit.Providers;

/// <summary>Builds an <see cref="IChatClient"/> over any OpenAI-compatible chat-completions endpoint —
/// NeuralWatt (<c>https://api.neuralwatt.com/v1</c>) and Gemini
/// (<c>https://generativelanguage.googleapis.com/v1beta/openai/</c>).</summary>
public static class OpenAICompatProvider
{
    public static IChatClient Create(ModelCard card, LlmProviderOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            throw new InvalidOperationException($"Provider '{card.Provider}' (openai-compat) has no endpoint configured.");
        }
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException(
                $"Provider '{card.Provider}' (openai-compat) has no API key — set Llm:Providers:{card.Provider}:ApiKey.");
        }
        var client = new OpenAIClient(new ApiKeyCredential(options.ApiKey), new OpenAIClientOptions
        {
            Endpoint = new Uri(options.Endpoint),
        });
        return client.GetChatClient(card.Id).AsIChatClient();
    }
}
