using AgentKit.Catalog;
using Microsoft.Extensions.AI;

namespace AgentKit.Providers;

/// <summary>Builds a <see cref="GeminiNativeChatClient"/> over Gemini's native <c>generateContent</c> API
/// (base <c>https://generativelanguage.googleapis.com/v1beta/</c>) — the provider kind (<c>gemini-native</c>)
/// used instead of <c>openai-compat</c> for Gemini so Google Search grounding actually works. One
/// <see cref="HttpClient"/> per model, owned by the memoizing <see cref="ChatClientFactory"/>.</summary>
public static class GeminiNativeProvider
{
    public static IChatClient Create(ModelCard card, LlmProviderOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            throw new InvalidOperationException($"Provider '{card.Provider}' (gemini-native) has no endpoint configured.");
        }
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException(
                $"Provider '{card.Provider}' (gemini-native) has no API key — set Llm:Providers:{card.Provider}:ApiKey.");
        }
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        return new GeminiNativeChatClient(http, card, options.Endpoint, options.ApiKey);
    }
}
