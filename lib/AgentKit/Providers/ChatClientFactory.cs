using System.Collections.Concurrent;
using AgentKit.Catalog;
using AgentKit.Diagnostics;
using AgentKit.Logging;
using AgentKit.Resilience;
using AgentKit.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LoggingChatClient = AgentKit.Logging.LoggingChatClient; // M.E.AI ships a type of the same name

namespace AgentKit.Providers;

public sealed class ChatClientFactory(
    IOptions<LlmOptions> options,
    ICompletionSink sink,
    ILlmDiagnostics diagnostics,
    ILoggerFactory? loggerFactory = null,
    IImageStore? imageStore = null) : IChatClientFactory
{
    // Lazy so a first-use race can't run Build twice and orphan an undisposed client stack
    // (GetOrAdd's value factory is not atomic).
    private readonly ConcurrentDictionary<string, Lazy<IChatClient>> _clients = new(StringComparer.OrdinalIgnoreCase);

    public IChatClient GetClient(ModelCard model) =>
        _clients.GetOrAdd(model.Id, _ => new Lazy<IChatClient>(() => Build(model))).Value;

    public IChatClient GetClient(ModelResolution resolution) =>
        new FailoverChatClient(resolution, GetClient, diagnostics);

    private IChatClient Build(ModelCard card)
    {
        var provider = options.Value.Providers.GetValueOrDefault(card.Provider)
            ?? throw new InvalidOperationException(
                $"Model '{card.Id}' names provider '{card.Provider}', which is not in Llm:Providers " +
                $"({string.Join(", ", options.Value.Providers.Keys)}).");
        var inner = provider.Kind.ToLowerInvariant() switch
        {
            "azure-openai" => AzureOpenAIProvider.Create(card, provider),
            "openai-compat" => OpenAICompatProvider.Create(card, provider),
            var kind => throw new InvalidOperationException(
                $"Provider '{card.Provider}' has unknown kind '{kind}' (expected azure-openai or openai-compat)."),
        };
        var logging = new LoggingChatClient(inner, card, sink, loggerFactory?.CreateLogger<LoggingChatClient>(), imageStore);
        return new CapabilityGateChatClient(logging, card);
    }
}
