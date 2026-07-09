namespace AgentKit.Catalog;

/// <summary>Capability tier of a model — the coarse "how smart" axis a way asks for. Low-tier ways may
/// still route to a high-tier model when the routing preference says so (tier selects the preference
/// list, it is not a hard filter).</summary>
public enum ModelTier
{
    Low,
    High,
}

/// <summary>One entry in the model catalog: identity, capabilities, pricing, and quirks. Bound from the
/// <c>Llm:Models</c> config section; everything the router, providers, and cost math need to know about
/// a model lives here — call sites never name a concrete model.</summary>
public sealed record ModelCard
{
    /// <summary>The catalog key — unique per card, and the id the pipeline routes/pins/logs by (e.g.
    /// <c>gpt-chat-latest</c>, <c>kimi-k2.6</c>). Usually also the provider's model/deployment id, unless
    /// <see cref="ModelName"/> overrides it. For Azure OpenAI this is the deployment name.</summary>
    public string Id { get; init; } = "";

    /// <summary>The model id to send to the provider when it differs from the catalog <see cref="Id"/>.
    /// The catalog can list two cards for one underlying model (e.g. a search-on variant
    /// <c>gemini-3.5-flash-search</c> and a plain <c>gemini-3.5-flash</c>) that both map to the provider's
    /// single <c>gemini-3.5-flash</c>. Null ⇒ the wire name is <see cref="Id"/>.</summary>
    public string? ModelName { get; init; }

    /// <summary>The id to send on the wire: <see cref="ModelName"/> when set, else <see cref="Id"/>.</summary>
    public string WireName => string.IsNullOrWhiteSpace(ModelName) ? Id : ModelName!;

    /// <summary>The provider key this model is served by (matches a <c>Llm:Providers</c> entry).</summary>
    public string Provider { get; init; } = "";

    public ModelTier Tier { get; init; } = ModelTier.Low;

    /// <summary>Accepts image input.</summary>
    public bool Vision { get; init; }

    /// <summary>Supports server-side web search (Azure hosted <c>web_search</c>, or Gemini grounding).</summary>
    public bool Search { get; init; }

    /// <summary>Context window in thousands of tokens (informational).</summary>
    public int? ContextK { get; init; }

    /// <summary>USD per million input tokens. Null = unknown (cost is reported as null, never 0).</summary>
    public decimal? PriceInPerMtok { get; init; }

    /// <summary>USD per million output tokens. Null = unknown.</summary>
    public decimal? PriceOutPerMtok { get; init; }

    public ModelQuirks Quirks { get; init; } = new();
}

/// <summary>Model-specific oddities the pipeline must tolerate, encoded as data instead of call-site ifs.</summary>
public sealed record ModelQuirks
{
    /// <summary>The model only accepts its default sampling temperature (e.g. <c>gpt-chat-latest</c>);
    /// any requested temperature is silently dropped for this model.</summary>
    public bool FixedTemperature { get; init; }
}
