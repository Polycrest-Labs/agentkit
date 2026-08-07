using AgentKit.Catalog;

namespace AgentKit.Agents;

/// <summary>One rendered prior turn ("user" or "assistant" + text). Simple hosts use the text-only
/// constructor; hosts replaying prior attachments as real multimodal history populate
/// <see cref="Parts"/> — when non-empty, the ordered parts ARE the message content and
/// <see cref="Text"/> is ignored (include an <see cref="AgentHistoryTextPart"/> for the text).
/// Replay policy (which prior assets, under what budget) is the HOST's job; the library passes
/// parts through exactly like the current message's images/documents.</summary>
public sealed record AgentHistoryMessage(string Role, string Text)
{
    public IReadOnlyList<AgentHistoryPart> Parts { get; init; } = [];
}

/// <summary>One ordered piece of a multimodal history message.</summary>
public abstract record AgentHistoryPart;

public sealed record AgentHistoryTextPart(string Text) : AgentHistoryPart;

public sealed record AgentHistoryImagePart(LlmImage Image) : AgentHistoryPart;

public sealed record AgentHistoryDocumentPart(LlmDocument Document) : AgentHistoryPart;

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
