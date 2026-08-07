using AgentKit.Schema;

namespace AgentKit.Agents;

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// The non-blocking question-card DTOs (the vacadock mechanism via aws, field shapes kept 1:1 —
// they ARE the wire contract the kit question card renders). A question is an ordinary tool domain
// event: the stream ends normally and the user's answer arrives as their next message — there is
// no server-side "waiting" state.
// ─────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One pickable option in an <see cref="AgentQuestion"/>.</summary>
public sealed record QuestionOption
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    /// <summary>Optional helper line under the label.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// A bounded question the assistant raises for the user to answer with a tap (and/or free text).
/// Streamed as a <c>question</c> frame and rendered as an interactive card; the answer comes back
/// as the next user turn. Hosts persist the question durably so a reload shows the (locked) card.
/// </summary>
public sealed record AgentQuestion
{
    /// <summary>Stable id, echoed back in history so the model never re-asks.</summary>
    public required string Id { get; init; }
    public required string Prompt { get; init; }
    public IReadOnlyList<QuestionOption> Options { get; init; } = [];
    /// <summary>Checkboxes (true) vs radios (false).</summary>
    public bool MultiSelect { get; init; }
    /// <summary>Show an "Other…" free-text row.</summary>
    public bool AllowOther { get; init; } = true;
    public string? OtherPlaceholder { get; init; }
}

/// <summary>
/// A batch of questions asked together as one form (one Submit) — collapses a multi-fact intake
/// into a single turn. A single clarification is just a 1-question form.
/// </summary>
public sealed record AgentQuestionForm
{
    public required string Id { get; init; }
    /// <summary>Optional lead-in shown above the fields.</summary>
    public string? Intro { get; init; }
    public IReadOnlyList<AgentQuestion> Questions { get; init; } = [];
}

/// <summary>DTO-derived JSON schemas for the question tools (the vacadock ToolSchemas pattern).</summary>
public static class QuestionSchemas
{
    public static readonly string Question = DtoJsonSchema.For<AgentQuestion>(new DtoSchemaOptions
    {
        Description = "The question card: prompt, selectable options, multiSelect/allowOther flags.",
    });

    public static readonly string QuestionForm = DtoJsonSchema.For<AgentQuestionForm>(new DtoSchemaOptions
    {
        Description = "One batched form: an intro plus the questions the user answers together.",
    });
}

/// <summary>One durable interaction record collected from a turn — what the host persists (and
/// later feeds back through <see cref="AgentQuestionHistory.Render"/> so the model never re-asks).</summary>
public abstract record AgentInteraction;

public sealed record AgentQuestionInteraction(AgentQuestion Question) : AgentInteraction;

public sealed record AgentQuestionFormInteraction(AgentQuestionForm Form) : AgentInteraction;
