using System.Text;
using System.Text.Json;

namespace AgentKit.Agents;

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// The prebuilt question/follow-up tools (ported from the aws appraisal tools, descriptions kept —
// the prose encodes the guardrails: never re-ask, prefill-never-auto-send, skip followups after a
// question). Handlers emit the typed domain events below via ToolOutcome.DomainEvents; the
// AgentTurnSession recognizes them natively and maps them onto question/questionForm/followups
// frames while collecting the durable interaction records into the turn outcome.
// ─────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>A question card was asked this turn (rides <see cref="ToolOutcome.DomainEvents"/>).</summary>
public sealed record QuestionEvent(AgentQuestion Question);

/// <summary>A question form was asked this turn (rides <see cref="ToolOutcome.DomainEvents"/>).</summary>
public sealed record QuestionFormEvent(AgentQuestionForm Form);

/// <summary>Next-step suggestion chips (rides <see cref="ToolOutcome.DomainEvents"/>). Ephemeral —
/// chips prefill the composer and are never persisted.</summary>
public sealed record FollowupsEvent(IReadOnlyList<string> Prompts);

/// <summary>Factories for the kit's prebuilt conversation tools.</summary>
public static class AgentQuestionTools
{
    /// <summary>Ask ONE bounded question; renders as a card, the answer arrives as the next turn.</summary>
    public static AgentTool AskQuestion(string? description = null, string? displayLabel = null) => AgentTool.Raw(
        "ask_question",
        description ??
        "Ask the user ONE bounded question with selectable options and/or free text. The card " +
        "renders in the transcript; the user's answer arrives as their next message — the turn " +
        "ends normally after asking. Never re-ask an answered question (history shows " +
        "[QUESTION(id): …] markers for everything already asked).",
        $"{{\"type\":\"object\",\"properties\":{{\"question\":{QuestionSchemas.Question}}},\"required\":[\"question\"]}}",
        AskQuestionAsync,
        displayLabel ?? "Asking a question");

    /// <summary>Ask SEVERAL questions as one form (one Submit, one combined answer turn).</summary>
    public static AgentTool AskQuestions(string? description = null, string? displayLabel = null) => AgentTool.Raw(
        "ask_questions",
        description ??
        "Ask SEVERAL bounded questions at once as ONE form the user answers together (preferred " +
        "over many ask_question calls). The combined answer arrives as the next user message, one " +
        "line per question in form order. Never re-ask an answered form.",
        $"{{\"type\":\"object\",\"properties\":{{\"form\":{QuestionSchemas.QuestionForm}}},\"required\":[\"form\"]}}",
        AskQuestionsAsync,
        displayLabel ?? "Asking questions");

    /// <summary>Offer 2–4 short next-step chips. They prefill the composer; they never auto-send.</summary>
    public static AgentTool SuggestFollowups(string? description = null, string? displayLabel = null) => AgentTool.Raw(
        "suggest_followups",
        description ??
        "Offer 2-4 SHORT next-step suggestions (≤ ~6 words each) the user is likely to want next. " +
        "They render as tappable chips that prefill the composer (they do NOT auto-send). ONLY " +
        "suggest steps you can actually perform with your tools. Call this LAST, once per turn, " +
        "after your other tools — but SKIP it when you asked a question card this turn.",
        """{"type":"object","properties":{"prompts":{"type":"array","items":{"type":"string"},"description":"2-4 short next-step suggestions."}},"required":["prompts"],"additionalProperties":false}""",
        SuggestFollowupsAsync,
        displayLabel ?? "Suggesting next steps");

    // ── handlers ──────────────────────────────────────────────────────────────────────────────

    private static Task<ToolOutcome> AskQuestionAsync(string argsJson, CancellationToken ct)
    {
        var question = ParseWrapped<AgentQuestion>(argsJson, "question");
        if (question is null || string.IsNullOrWhiteSpace(question.Prompt))
        {
            return Task.FromResult(new ToolOutcome(
                "ask_question needs a 'question' object with a prompt.", [], IsError: true));
        }
        question = Normalize(question, index: 0);
        return Task.FromResult(new ToolOutcome(
            "Asked the user — their answer arrives as their next message. End your reply now; never re-ask an answered question.",
            [new QuestionEvent(question)]));
    }

    private static Task<ToolOutcome> AskQuestionsAsync(string argsJson, CancellationToken ct)
    {
        var form = ParseWrapped<AgentQuestionForm>(argsJson, "form");
        if (form is null || form.Questions.Count == 0)
        {
            return Task.FromResult(new ToolOutcome(
                "ask_questions needs a 'form' object with at least one question.", [], IsError: true));
        }
        form = form with
        {
            Id = string.IsNullOrWhiteSpace(form.Id) ? "form-1" : form.Id,
            Questions = form.Questions.Select(Normalize).ToList(),
        };
        return Task.FromResult(new ToolOutcome(
            "Asked the user a short form — their combined answer arrives as their next message (one line per question). End your reply now; never re-ask it.",
            [new QuestionFormEvent(form)]));
    }

    private sealed record SuggestFollowupsArgs(string[]? Prompts);

    private static Task<ToolOutcome> SuggestFollowupsAsync(string argsJson, CancellationToken ct)
    {
        var prompts = ParseFollowups(argsJson);
        if (prompts.Count == 0)
        {
            return Task.FromResult(new ToolOutcome("No follow-up suggestions provided.", [], IsError: true));
        }
        return Task.FromResult(new ToolOutcome(
            "Offered the user next-step suggestion chips.",
            [new FollowupsEvent(prompts)]));
    }

    /// <summary>Parse discipline (the vacadock ParseFollowups behavior): trim, de-dupe, cap at 4;
    /// malformed args yield an empty list rather than faulting the tool.</summary>
    internal static IReadOnlyList<string> ParseFollowups(string argsJson)
    {
        SuggestFollowupsArgs? args;
        try
        {
            args = AgentJson.Deserialize<SuggestFollowupsArgs>(argsJson);
        }
        catch (Exception)
        {
            return [];
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return (args?.Prompts ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Where(seen.Add)
            .Take(4)
            .ToList();
    }

    /// <summary>Backfill missing ids so answers/history can reference them (models often omit ids).</summary>
    private static AgentQuestion Normalize(AgentQuestion question, int index) => question with
    {
        Id = string.IsNullOrWhiteSpace(question.Id) ? $"q{index + 1}" : question.Id,
        Options = question.Options
            .Select((o, i) => string.IsNullOrWhiteSpace(o.Id) ? o with { Id = $"o{i + 1}" } : o)
            .ToList(),
    };

    private static T? ParseWrapped<T>(string argsJson, string property) where T : class
    {
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty(property, out var wrapped) ||
                wrapped.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            return AgentJson.Deserialize<T>(wrapped.GetRawText());
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>One asked interaction plus its durable answer (if any) — sourced by hosts from their
/// persisted timeline entries and parent-linked answers, never from transient frames.</summary>
public sealed record AskedInteraction(AgentInteraction Interaction, string? AnswerText = null);

/// <summary>
/// Renders already-asked questions/forms into model history as bracket notes (the aws
/// AppraisalHistoryRenderer idiom) so the model never re-asks: append the result to the turn's
/// rendered history/instructions.
/// </summary>
public static class AgentQuestionHistory
{
    public static string Render(IEnumerable<AskedInteraction> asked)
    {
        var sb = new StringBuilder();
        foreach (var item in asked)
        {
            switch (item.Interaction)
            {
                case AgentQuestionInteraction { Question: var question }:
                    AppendQuestion(sb, question, item.AnswerText);
                    break;
                case AgentQuestionFormInteraction { Form: var form }:
                    sb.AppendLine().Append($"[FORM({form.Id}): asked as one form — already asked. Never re-ask.]");
                    foreach (var question in form.Questions)
                    {
                        AppendQuestion(sb, question, answer: null);
                    }
                    if (!string.IsNullOrWhiteSpace(item.AnswerText))
                    {
                        sb.AppendLine().Append($"[ANSWERED({form.Id}): {item.AnswerText.Trim()}]");
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    private static void AppendQuestion(StringBuilder sb, AgentQuestion question, string? answer)
    {
        sb.AppendLine().Append($"[QUESTION({question.Id}): {question.Prompt}");
        var labels = question.Options.Select(o => o.Label).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (labels.Count > 0)
        {
            sb.Append(" | options: ").Append(string.Join(", ", labels));
        }
        sb.Append(string.IsNullOrWhiteSpace(answer)
            ? " — already asked; the user's next message answers it. Never re-ask.]"
            : $" — already asked and answered: \"{answer.Trim()}\". Never re-ask.]");
    }
}
