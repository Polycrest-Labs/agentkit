using AgentKit.Catalog;

namespace AgentKit.Agents;

/// <summary>One rendered prior turn ("user" or "assistant" + text).</summary>
public sealed record AgentHistoryMessage(string Role, string Text);

/// <summary>Everything the runner needs for one turn. The host renders its own history/instructions;
/// the library stays domain-free.</summary>
public sealed record AgentTurnRequest
{
    /// <summary>The full system instructions for the turn (prompt + any grounding snapshot).</summary>
    public required string Instructions { get; init; }

    /// <summary>Prior turns, oldest→newest, already rendered to plain text.</summary>
    public IReadOnlyList<AgentHistoryMessage> History { get; init; } = [];

    /// <summary>The current user message text (attachment listings already included by the host).</summary>
    public required string UserText { get; init; }

    /// <summary>Images attached to the current user message (vision input).</summary>
    public IReadOnlyList<LlmImage> Images { get; init; } = [];

    /// <summary>Documents (typically PDFs) attached to the current user message. They ride the same
    /// content channel as <see cref="Images"/>; route with a Documents way (see
    /// <see cref="LlmWay.Documents"/>) so only documents-capable cards resolve — an incapable card
    /// refuses loudly at the capability gate.</summary>
    public IReadOnlyList<LlmDocument> Documents { get; init; } = [];

    public required AgentToolCatalog Tools { get; init; }

    /// <summary>Routing requirements. Search is expressed via <c>AgentRunnerOptions.EnableWebSearch</c>
    /// (a desire, not a requirement — incapable models just skip the hosted tool).</summary>
    public LlmWay Way { get; init; } = LlmWay.High;

    /// <summary>Explicit model pin (eval); bypasses routing and disables failover.</summary>
    public string? ModelPin { get; init; }

    /// <summary>Feature tag for completion records (e.g. <c>trip-agent</c>).</summary>
    public string Feature { get; init; } = "agent";

    public string? ConversationId { get; init; }
    public string? UserId { get; init; }
}
