using AgentKit.Catalog;

namespace AgentKit.Routing;

/// <summary>Resolves a <see cref="LlmWay"/> (what a call needs) to a concrete model + failover chain.
/// Pluggable so a host can swap in smarter routing (cost budgets, A/B splits) without touching callers.</summary>
public interface IModelRouter
{
    /// <param name="way">Required tier + capabilities.</param>
    /// <param name="modelPin">Explicit model id override (the eval CLI's <c>--models</c>): bypasses the
    /// capability filter, resolves that exact card, and disables failover.</param>
    ModelResolution Resolve(LlmWay way, string? modelPin = null);
}
