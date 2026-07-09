using AgentKit.Catalog;
using Azure;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.AI;

namespace AgentKit.Providers;

/// <summary>Builds an <see cref="IChatClient"/> over the Azure OpenAI <b>Responses</b> API (which carries
/// the hosted <c>web_search</c> tool + URL citations). Auth: an explicit API key when configured, else a
/// token credential per <see cref="CredentialMode"/> — <c>DevSafe</c> replaces the old
/// <c>FoundryClientFactory</c> <c>IHostEnvironment.IsDevelopment()</c> Arc workaround with explicit config.</summary>
public static class AzureOpenAIProvider
{
    public static IChatClient Create(ModelCard card, LlmProviderOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            throw new InvalidOperationException($"Provider '{card.Provider}' (azure-openai) has no endpoint configured.");
        }
        var endpoint = new Uri(options.Endpoint);
        var azure = string.IsNullOrWhiteSpace(options.ApiKey)
            ? new AzureOpenAIClient(endpoint, CreateCredential(options.CredentialMode))
            : new AzureOpenAIClient(endpoint, new AzureKeyCredential(options.ApiKey));
        // For Azure the Responses "model" is the deployment name; ResponsesClient carries no model itself.
        return azure.GetResponsesClient().AsIChatClient(card.ApiModel);
    }

    private static TokenCredential CreateCredential(CredentialMode mode) => mode switch
    {
        // Drop the cloud-only identity sources so an Azure Arc-enrolled dev box's managed-identity
        // probe can't kill the chain before it reaches the developer's az/azd/VS sign-in.
        CredentialMode.DevSafe => new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ExcludeManagedIdentityCredential = true,
            ExcludeWorkloadIdentityCredential = true,
        }),
        CredentialMode.ManagedIdentity => new ManagedIdentityCredential(),
        _ => new DefaultAzureCredential(),
    };
}
