using System.Text.Json;
using AgentKit.Agents;

namespace AgentKit.Tests;

public sealed class AgentQuestionToolsTests
{
    private static async Task<ToolOutcome> Run(AgentTool tool, string argsJson) =>
        await tool.Handler(argsJson, CancellationToken.None);

    // ── ask_question ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AskQuestion_NormalizesMissingIds_EmitsTypedEvent()
    {
        var outcome = await Run(AgentQuestionTools.AskQuestion(),
            """{"question":{"id":"","prompt":"Which rights?","options":[{"id":"","label":"Fee simple"},{"id":"custom","label":"Leased fee"}]}}""");

        Assert.False(outcome.IsError);
        var ev = Assert.IsType<QuestionEvent>(Assert.Single(outcome.DomainEvents));
        Assert.Equal("q1", ev.Question.Id);
        Assert.Equal(["o1", "custom"], ev.Question.Options.Select(o => o.Id));
        Assert.Contains("never re-ask", outcome.OutputForModel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AskQuestion_MissingPrompt_IsRecoverableError()
    {
        var outcome = await Run(AgentQuestionTools.AskQuestion(), """{"question":{"id":"q1","prompt":""}}""");
        Assert.True(outcome.IsError);
        Assert.Empty(outcome.DomainEvents);

        var malformed = await Run(AgentQuestionTools.AskQuestion(), "not json at all");
        Assert.True(malformed.IsError);
    }

    // ── ask_questions ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AskQuestions_NormalizesFormAndQuestionIds()
    {
        var outcome = await Run(AgentQuestionTools.AskQuestions(),
            """{"form":{"id":"","questions":[{"id":"","prompt":"County?"},{"id":"","prompt":"Acres?"}]}}""");

        Assert.False(outcome.IsError);
        var ev = Assert.IsType<QuestionFormEvent>(Assert.Single(outcome.DomainEvents));
        Assert.Equal("form-1", ev.Form.Id);
        Assert.Equal(["q1", "q2"], ev.Form.Questions.Select(q => q.Id));
    }

    [Fact]
    public async Task AskQuestions_EmptyForm_IsRecoverableError()
    {
        var outcome = await Run(AgentQuestionTools.AskQuestions(), """{"form":{"id":"f","questions":[]}}""");
        Assert.True(outcome.IsError);
    }

    // ── suggest_followups ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SuggestFollowups_TrimsDedupesAndCapsAtFour()
    {
        var outcome = await Run(AgentQuestionTools.SuggestFollowups(),
            """{"prompts":["  Find comps  ","find comps","Run the workup","Draft the report","Map the parcels","One more"]}""");

        Assert.False(outcome.IsError);
        var ev = Assert.IsType<FollowupsEvent>(Assert.Single(outcome.DomainEvents));
        Assert.Equal(["Find comps", "Run the workup", "Draft the report", "Map the parcels"], ev.Prompts);
    }

    [Fact]
    public async Task SuggestFollowups_MalformedOrEmpty_ErrorsWithoutFaulting()
    {
        Assert.True((await Run(AgentQuestionTools.SuggestFollowups(), "{ not json")).IsError);
        Assert.True((await Run(AgentQuestionTools.SuggestFollowups(), """{"prompts":[]}""")).IsError);
        Assert.True((await Run(AgentQuestionTools.SuggestFollowups(), """{"prompts":["  ","  "]}""")).IsError);
        Assert.True((await Run(AgentQuestionTools.SuggestFollowups(), "[1,2]")).IsError);
    }

    // ── schemas + labels ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Tools_CarrySchemasAndDisplayLabels()
    {
        foreach (var tool in new[]
        {
            AgentQuestionTools.AskQuestion(),
            AgentQuestionTools.AskQuestions(),
            AgentQuestionTools.SuggestFollowups(),
        })
        {
            using var schema = JsonDocument.Parse(tool.ParametersSchema);
            Assert.Equal(JsonValueKind.Object, schema.RootElement.ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(tool.DisplayLabel));
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
        }
        Assert.Contains("\"question\"", AgentQuestionTools.AskQuestion().ParametersSchema);
        Assert.Contains("\"form\"", AgentQuestionTools.AskQuestions().ParametersSchema);
        Assert.Contains("never re-ask", AgentQuestionTools.AskQuestion().Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do NOT auto-send", AgentQuestionTools.SuggestFollowups().Description);
        Assert.Contains("SKIP it when you asked a question", AgentQuestionTools.SuggestFollowups().Description);
    }

    // ── anti-re-ask history ───────────────────────────────────────────────────────────────────

    [Fact]
    public void QuestionHistory_RendersBracketNotes()
    {
        var question = new AgentQuestion
        {
            Id = "q1",
            Prompt = "Which rights are appraised?",
            Options = [new QuestionOption { Id = "o1", Label = "Fee simple" }, new QuestionOption { Id = "o2", Label = "Leased fee" }],
        };
        var form = new AgentQuestionForm
        {
            Id = "form-1",
            Questions = [new AgentQuestion { Id = "q2", Prompt = "County?" }],
        };

        var rendered = AgentQuestionHistory.Render(
        [
            new AskedInteraction(new AgentQuestionInteraction(question), "Fee simple"),
            new AskedInteraction(new AgentQuestionFormInteraction(form)),
        ]);

        Assert.Contains("[QUESTION(q1): Which rights are appraised? | options: Fee simple, Leased fee", rendered);
        Assert.Contains("already asked and answered: \"Fee simple\". Never re-ask.]", rendered);
        Assert.Contains("[FORM(form-1):", rendered);
        Assert.Contains("[QUESTION(q2): County?", rendered);
        Assert.Contains("the user's next message answers it. Never re-ask.]", rendered);
    }

    [Fact]
    public void QuestionHistory_EmptyInput_RendersNothing()
    {
        Assert.Equal(string.Empty, AgentQuestionHistory.Render([]));
    }
}
