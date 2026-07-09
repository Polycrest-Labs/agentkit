namespace AgentKit.Catalog;

/// <summary>What a call needs from a model — the routing request. Callers declare a way
/// (tier + required capabilities); the router picks the concrete model from config, so features never
/// hard-code model ids.</summary>
public readonly record struct LlmWay(ModelTier Tier, bool Vision = false, bool Search = false)
{
    public static LlmWay Low { get; } = new(ModelTier.Low);
    public static LlmWay High { get; } = new(ModelTier.High);
    public static LlmWay LowVision { get; } = new(ModelTier.Low, Vision: true);
    public static LlmWay HighVision { get; } = new(ModelTier.High, Vision: true);
    public static LlmWay HighSearch { get; } = new(ModelTier.High, Search: true);

    /// <summary>Compact label used in logs and completion records, e.g. <c>"low+vision"</c>.</summary>
    public override string ToString()
    {
        var s = Tier == ModelTier.High ? "high" : "low";
        if (Vision)
        {
            s += "+vision";
        }
        if (Search)
        {
            s += "+search";
        }
        return s;
    }
}
