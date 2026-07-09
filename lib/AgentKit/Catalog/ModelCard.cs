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
    /// <summary>The model/deployment id as the provider knows it (e.g. <c>gpt-chat-latest</c>,
    /// <c>kimi-k2.6</c>). For Azure OpenAI this is the deployment name. When two catalog entries
    /// target the same upstream model (e.g. through different providers/keys), give each a unique
    /// Id and set <see cref="UpstreamModel"/> to the real name.</summary>
    public string Id { get; init; } = "";

    /// <summary>The model name actually sent to the provider, when it differs from <see cref="Id"/>.
    /// Null (the default) means <see cref="Id"/> is the upstream name.</summary>
    public string? UpstreamModel { get; init; }

    /// <summary>What providers should request from the API.</summary>
    public string ApiModel => string.IsNullOrWhiteSpace(UpstreamModel) ? Id : UpstreamModel;

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

    /// <summary>Web grounding is enabled via the OpenAI-compat <c>extra_body</c> escape hatch
    /// (Gemini's <c>google.tools: [{ google_search: {} }]</c>), not the standard tools array.</summary>
    public bool GroundingViaExtraBody { get; init; }
}
