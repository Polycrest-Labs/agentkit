using AgentKit.Catalog;
using Microsoft.Extensions.Options;

namespace AgentKit.Routing;

/// <summary>Default router: filter the catalog by the way's required capabilities (and by provider
/// configuration — a model whose provider has no endpoint/key can't serve), order by the configured
/// per-tier preference list, then any remaining capable models cheapest-first. The first candidate is
/// the primary; the rest are the failover chain.</summary>
public sealed class CapabilityTierRouter(IModelCatalog catalog, IOptions<LlmOptions> options) : IModelRouter
{
    public ModelResolution Resolve(LlmWay way, string? modelPin = null)
    {
        if (!string.IsNullOrWhiteSpace(modelPin))
        {
            var pinned = catalog.Get(modelPin);
            return new ModelResolution(way, pinned,
                $"pinned to '{pinned.Id}' — capability filter bypassed, no failover", []);
        }

        var opts = options.Value;
        var capable = catalog.Models
            .Where(m => (!way.Vision || m.Vision) && (!way.Search || m.Search))
            .Where(m => opts.Providers.GetValueOrDefault(m.Provider)?.IsConfigured == true)
            .ToList();
        if (capable.Count == 0)
        {
            throw new InvalidOperationException(
                $"No configured model in the catalog supports way '{way}'. " +
                $"Catalog: {string.Join(", ", catalog.Models.Select(Describe))}.");
        }

        var tierKey = way.Tier == ModelTier.High ? "high" : "low";
        var prefer = opts.Routing.Prefer.GetValueOrDefault(tierKey) ?? [];
        var preferred = prefer
            .Select(id => capable.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)))
            .OfType<ModelCard>()
            .Distinct()
            .ToList();
        var rest = capable.Except(preferred)
            .OrderBy(m => m.PriceInPerMtok + m.PriceOutPerMtok ?? decimal.MaxValue)
            .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase);
        var ordered = preferred.Concat(rest).ToList();

        var primary = ordered[0];
        return new ModelResolution(way, primary,
            $"way={way} → prefer[{tierKey}] → {primary.Id}", ordered.Skip(1).ToList());
    }

    private static string Describe(ModelCard m)
    {
        var caps = new List<string> { m.Tier == ModelTier.High ? "high" : "low" };
        if (m.Vision)
        {
            caps.Add("vision");
        }
        if (m.Search)
        {
            caps.Add("search");
        }
        return $"{m.Id} ({string.Join("+", caps)})";
    }
}
