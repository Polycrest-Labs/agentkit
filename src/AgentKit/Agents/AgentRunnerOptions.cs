namespace AgentKit.Agents;

/// <summary>Per-run knobs (the eval CLI's A/B surface; hosts usually pass none of these).</summary>
public sealed record AgentRunnerOptions
{
    public static AgentRunnerOptions Default { get; } = new();

    /// <summary>Add the provider's hosted web-search tool (true in the app). Models whose card lacks
    /// <c>Search</c> silently skip it via the capability gate.</summary>
    public bool EnableWebSearch { get; init; } = true;

    /// <summary>Sampling temperature; dropped per-model when the card pins the default.</summary>
    public float? Temperature { get; init; }

    /// <summary>Tool-hop ceiling per turn — a runaway tool loop ends the turn instead of spinning.</summary>
    public int MaxHops { get; init; } = 12;

    /// <summary>Reasoning effort for reasoning models ("minimal"|"low"|"medium"|"high") — gpt-5-series
    /// web_search requires at least "low".</summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>Output-token cap (reasoning + web-search turns need room to finish).</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>Out-of-band observer for every tool call: (name, raw args JSON, output, isError).
    /// The eval CLI records these; the app passes none.</summary>
    public Action<string, string, string, bool>? OnToolCall { get; init; }
}
