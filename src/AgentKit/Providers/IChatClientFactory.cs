using AgentKit.Catalog;
using AgentKit.Routing;
using Microsoft.Extensions.AI;

namespace AgentKit.Providers;

/// <summary>Builds (and memoizes) the decorated <see cref="IChatClient"/> pipeline per model, and the
/// failover chain for a routed call.</summary>
public interface IChatClientFactory
{
    /// <summary>The single-model pipeline: provider client → logging → capability gate.</summary>
    IChatClient GetClient(ModelCard model);

    /// <summary>The failover pipeline over a resolution's candidates (single-candidate when pinned).</summary>
    IChatClient GetClient(ModelResolution resolution);
}
