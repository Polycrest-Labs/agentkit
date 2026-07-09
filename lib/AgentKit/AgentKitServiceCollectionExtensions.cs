using System.Diagnostics.Metrics;
using AgentKit.Agents;
using AgentKit.Catalog;
using AgentKit.Diagnostics;
using AgentKit.Logging;
using AgentKit.Providers;
using AgentKit.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentKit;

public static class AgentKitServiceCollectionExtensions
{
    /// <summary>Registers the AgentKit pipeline: <c>Llm</c> options, model catalog, router, provider
    /// factory, completion logging, failover diagnostics, and <see cref="ILlmClient"/>. Every
    /// registration is TryAdd, so a host can pre-register its own sink/router/diagnostics.</summary>
    public static IServiceCollection AddAgentKit(
        this IServiceCollection services, IConfiguration configuration, Action<LlmOptions>? configure = null)
    {
        services.AddOptions<LlmOptions>().Bind(configuration.GetSection("Llm"));
        if (configure is not null)
        {
            services.PostConfigure(configure);
        }

        services.TryAddSingleton<IModelCatalog, ModelCatalog>();
        services.TryAddSingleton<IModelRouter, CapabilityTierRouter>();
        services.TryAddSingleton(sp => new AgentKitMetrics(sp.GetService<IMeterFactory>()));
        services.TryAddSingleton<ILlmDiagnostics, LoggerLlmDiagnostics>();
        services.TryAddSingleton<ICompletionSink>(sp =>
        {
            var logging = sp.GetRequiredService<IOptions<LlmOptions>>().Value.Logging;
            return logging.Sink.ToLowerInvariant() switch
            {
                "jsonl" => new JsonlFileSink(logging.Directory ?? "llm-logs"),
                "memory" => new InMemorySink(),
                _ => NullCompletionSink.Instance,
            };
        });
        services.TryAddSingleton<IImageStore>(NullImageStore.Instance);
        services.TryAddSingleton<IChatClientFactory>(sp => new ChatClientFactory(
            sp.GetRequiredService<IOptions<LlmOptions>>(),
            sp.GetRequiredService<ICompletionSink>(),
            sp.GetRequiredService<ILlmDiagnostics>(),
            sp.GetService<ILoggerFactory>(),
            sp.GetService<IImageStore>()));
        services.TryAddSingleton<ILlmClient, LlmClient>();
        services.TryAddSingleton<AgentRunner>(sp => new AgentRunner(
            sp.GetRequiredService<IModelRouter>(),
            sp.GetRequiredService<IChatClientFactory>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger<AgentRunner>()));
        return services;
    }
}
