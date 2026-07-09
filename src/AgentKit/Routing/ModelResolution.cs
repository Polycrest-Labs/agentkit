using AgentKit.Catalog;

namespace AgentKit.Routing;

/// <summary>The router's answer for one call: the chosen model, why, and the ordered failover chain.
/// A pinned resolution has an empty <see cref="Fallbacks"/> list — pinned calls never fail over
/// (an eval graded against a silent substitute model would be worthless).</summary>
public sealed record ModelResolution(
    LlmWay Way,
    ModelCard Model,
    string Reason,
    IReadOnlyList<ModelCard> Fallbacks)
{
    public IEnumerable<ModelCard> Candidates => Fallbacks.Count == 0 ? [Model] : [Model, .. Fallbacks];
}
