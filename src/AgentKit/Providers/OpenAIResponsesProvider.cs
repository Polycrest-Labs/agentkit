using System.ClientModel;
using AgentKit.Catalog;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentKit.Providers;

/// <summary>Builds an <see cref="IChatClient"/> over the OpenAI platform's <b>Responses</b> API
/// (<c>https://api.openai.com/v1</c>) — the sibling of <see cref="AzureOpenAIProvider"/> for accounts
/// that talk to OpenAI directly rather than through an Azure deployment.
///
/// <para>Why this exists rather than routing everything through <see cref="OpenAICompatProvider"/>:
/// OpenAI refuses the combination of <b>function tools and reasoning</b> on chat completions for its
/// newer models — <i>"Function tools with reasoning_effort are not supported for gpt-5.6-luna in
/// /v1/chat/completions. To use function tools, use /v1/responses or set reasoning_effort to
/// 'none'."</i> An agent loop is function tools by definition, so on the compat path a reasoning model
/// has to run with its reasoning switched off. This path is how a host gets both. It also carries the
/// hosted <c>web_search</c> tool and URL citations, exactly as the Azure Responses path does.</para></summary>
public static class OpenAIResponsesProvider
{
    public static IChatClient Create(ModelCard card, LlmProviderOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException(
                $"Provider '{card.Provider}' (openai-responses) has no API key — set Llm:Providers:{card.Provider}:ApiKey.");
        }
        // Endpoint is optional here, unlike the compat path: the SDK already knows where OpenAI is,
        // and a host that sets one (a gateway, a regional host) still gets it honoured.
        var clientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(options.Endpoint))
        {
            clientOptions.Endpoint = new Uri(options.Endpoint);
        }
        var client = new OpenAIClient(new ApiKeyCredential(options.ApiKey), clientOptions);
        // As on Azure, the ResponsesClient carries no model of its own — it is named per request.
        return client.GetResponsesClient().AsIChatClient(card.ApiModel);
    }
}
