using Microsoft.Extensions.Options;

namespace AgentKit.Catalog;

/// <summary>Lookup over the configured <see cref="ModelCard"/>s (case-insensitive by id).</summary>
public interface IModelCatalog
{
    IReadOnlyList<ModelCard> Models { get; }

    /// <summary>The card for <paramref name="id"/>, or null when the catalog doesn't know it.</summary>
    ModelCard? Find(string id);

    /// <summary>The card for <paramref name="id"/>; throws a catalog-listing error for unknown ids.</summary>
    ModelCard Get(string id);
}

public sealed class ModelCatalog : IModelCatalog
{
    private readonly Dictionary<string, ModelCard> _byId;

    public ModelCatalog(IOptions<LlmOptions> options)
    {
        Models = options.Value.Models;
        _byId = new Dictionary<string, ModelCard>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in Models)
        {
            _byId[model.Id] = model; // last one wins on duplicates
        }
    }

    public IReadOnlyList<ModelCard> Models { get; }

    public ModelCard? Find(string id) => _byId.GetValueOrDefault(id);

    public ModelCard Get(string id) =>
        Find(id) ?? throw new InvalidOperationException(
            $"Unknown model '{id}' — the Llm:Models catalog has: {string.Join(", ", Models.Select(m => m.Id))}.");
}
