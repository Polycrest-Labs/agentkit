using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AgentKit.Catalog;
using AgentKit.Logging;
using AgentKit.Providers;
using AgentKit.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentKit.Agents;

/// <summary>
/// The provider-agnostic streaming agent loop: stream a completion with the full message history, yield
/// text deltas and citations as they arrive, dispatch function calls through the turn's
/// <see cref="AgentToolCatalog"/> (tool exceptions become recoverable error outputs fed back to the
/// model — never a crashed turn), append the results, and loop until the model stops calling tools or
/// the hop ceiling hits. Each hop is a fresh completion, so hop-level failover is safe; usage is
/// aggregated across hops and reported before <see cref="Completed"/>.
/// </summary>
/// <remarks>
/// The loop runs as a channel-fed producer rather than a plain async iterator on purpose: the ambient
/// <see cref="CompletionScope"/> (feature/route/attempt for completion logging + failover alerting) is
/// an AsyncLocal, and an async iterator resumes each MoveNextAsync in the CONSUMER's execution context —
/// the scope would silently vanish after the first yielded event. Inside one producer flow it holds for
/// every hop.
/// </remarks>
public sealed class AgentRunner(IModelRouter router, IChatClientFactory factory, ILogger<AgentRunner>? logger = null)
{
    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AgentTurnRequest request, AgentRunnerOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producer = ProduceAsync(channel.Writer, request, options ?? AgentRunnerOptions.Default, linked.Token);
        try
        {
            await foreach (var ev in channel.Reader.ReadAllAsync(ct))
            {
                yield return ev;
            }
        }
        finally
        {
            // An abandoned consumer (early break, SSE disconnect) stops the model stream too.
            linked.Cancel();
            await producer; // never faults — ProduceAsync funnels errors through the channel
        }
    }

    private async Task ProduceAsync(
        ChannelWriter<AgentEvent> writer, AgentTurnRequest request, AgentRunnerOptions options, CancellationToken ct)
    {
        try
        {
            using var scope = CompletionScope.Begin(request.Feature,
                conversationId: request.ConversationId, userId: request.UserId);
            await RunCoreAsync(writer, request, options, ct);
            writer.Complete();
        }
        catch (Exception ex)
        {
            writer.Complete(ex); // rethrown to the consumer from ReadAllAsync
        }
    }

    private async Task RunCoreAsync(
        ChannelWriter<AgentEvent> writer, AgentTurnRequest request, AgentRunnerOptions options, CancellationToken ct)
    {
        var resolution = router.Resolve(request.Way, request.ModelPin);
        var client = factory.GetClient(resolution);
        var chatOptions = BuildChatOptions(request, options);
        var messages = BuildInitialMessages(request);

        var fullText = new StringBuilder();
        var seenCitations = new HashSet<(string?, string?)>();
        long inputTokens = 0, outputTokens = 0;
        var sawUsage = false;

        async ValueTask Emit(AgentEvent ev) => await writer.WriteAsync(ev, ct);

        for (var hop = 0; hop < options.MaxHops; hop++)
        {
            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in client.GetStreamingResponseAsync(messages, chatOptions, ct))
            {
                ct.ThrowIfCancellationRequested();
                updates.Add(update);
                foreach (var content in update.Contents)
                {
                    if (content is TextContent { Text.Length: > 0 } text)
                    {
                        fullText.Append(text.Text);
                        await Emit(new TokenDelta(text.Text));
                    }
                    if (content is UsageContent usage)
                    {
                        sawUsage = true;
                        inputTokens += usage.Details.InputTokenCount ?? 0;
                        outputTokens += usage.Details.OutputTokenCount ?? 0;
                    }
                    foreach (var citation in (content.Annotations ?? []).OfType<CitationAnnotation>())
                    {
                        if (seenCitations.Add((citation.Title, citation.Url?.ToString())))
                        {
                            await Emit(new CitationFound(citation.Title, citation.Url?.ToString()));
                        }
                    }
                }
            }

            var response = updates.ToChatResponse();
            messages.AddRange(ForHistory(response.Messages));

            var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
            if (calls.Count == 0)
            {
                break;
            }
            if (hop == options.MaxHops - 1)
            {
                // The ceiling hit with tool calls still pending: the turn ends on whatever text streamed,
                // which may be empty/truncated — leave a trace so a blank answer is diagnosable.
                logger?.LogWarning(
                    "Feature {Feature} hit the {MaxHops}-hop ceiling with tool calls still pending; the answer may be truncated",
                    request.Feature, options.MaxHops);
            }

            foreach (var call in calls)
            {
                var argsJson = SerializeArgs(call.Arguments);
                await Emit(new ToolCallStarted(call.Name));
                var outcome = await DispatchAsync(request.Tools, call.Name, argsJson, ct);
                options.OnToolCall?.Invoke(call.Name, argsJson, outcome.OutputForModel, outcome.IsError);
                foreach (var domainEvent in outcome.DomainEvents)
                {
                    await Emit(new CustomEvent(domainEvent));
                }
                await Emit(new ToolCallFinished(call.Name, outcome.OutputForModel, outcome.IsError));
                messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, outcome.OutputForModel)]));
                // Images can't ride a function output — attach them as a follow-up user message (the
                // same vision channel the initial user message uses) for the next hop.
                foreach (var image in outcome.Images)
                {
                    messages.Add(new ChatMessage(ChatRole.User, [new DataContent(image.Bytes, image.MediaType)]));
                }
            }
        }

        if (sawUsage)
        {
            await Emit(new UsageReport(inputTokens, outputTokens));
        }
        await Emit(new Completed(fullText.ToString()));
    }

    /// <summary>Keep only the durable parts of an assistant turn when re-sending history: its text and
    /// its function calls. Hosted-tool artifacts (web_search calls/results) and reasoning traces don't
    /// round-trip — the Responses API rejects a resubmitted <c>web_search_call</c> item without its
    /// paired reasoning item, which the adapter cannot reconstruct — so they live and die on their hop;
    /// the search's contribution survives in the assistant text (with its citations).</summary>
    private static IEnumerable<ChatMessage> ForHistory(IEnumerable<ChatMessage> responseMessages)
    {
        foreach (var message in responseMessages)
        {
            var kept = message.Contents.Where(c => c is TextContent or FunctionCallContent).ToList();
            if (kept.Count == message.Contents.Count)
            {
                yield return message;
            }
            else if (kept.Count > 0)
            {
                yield return new ChatMessage(message.Role, kept);
            }
        }
    }

    /// <summary>Dispatch one call; a failed tool (invalid JSON args, unknown field, domain error) feeds a
    /// recoverable error back to the model so it can correct and retry — it never kills the turn.</summary>
    private async Task<ToolOutcome> DispatchAsync(AgentToolCatalog catalog, string name, string argsJson, CancellationToken ct)
    {
        if (catalog.Find(name) is not { } tool)
        {
            return new ToolOutcome($"Unknown tool '{name}'.");
        }
        try
        {
            return await tool.Handler(argsJson, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Tool {Tool} failed; returning a recoverable error to the model", name);
            return new ToolOutcome(
                $"Tool '{name}' failed: {ex.Message}. Re-check the JSON arguments (valid JSON, correct field names and enum values) and try again.",
                [], IsError: true);
        }
    }

    private static ChatOptions BuildChatOptions(AgentTurnRequest request, AgentRunnerOptions options)
    {
        var tools = new List<AITool>();
        foreach (var tool in request.Tools.Tools)
        {
            tools.Add(RawJsonFunction.FromJson(tool.Name, tool.Description, tool.ParametersSchema));
        }
        if (options.EnableWebSearch)
        {
            tools.Add(new HostedWebSearchTool());
        }
        var chatOptions = new ChatOptions
        {
            Instructions = request.Instructions,
            Temperature = options.Temperature,
            MaxOutputTokens = options.MaxOutputTokens,
            Tools = tools,
        };
        if (MapReasoningEffort(options.ReasoningEffort) is { } effort)
        {
            chatOptions.Reasoning = new ReasoningOptions { Effort = effort };
        }
        return chatOptions;
    }

    private static ReasoningEffort? MapReasoningEffort(string? effort) => effort?.ToLowerInvariant() switch
    {
        "minimal" or "none" => ReasoningEffort.None,
        "low" => ReasoningEffort.Low,
        "medium" => ReasoningEffort.Medium,
        "high" => ReasoningEffort.High,
        "extrahigh" or "xhigh" => ReasoningEffort.ExtraHigh,
        _ => null,
    };

    private static List<ChatMessage> BuildInitialMessages(AgentTurnRequest request)
    {
        var messages = new List<ChatMessage>();
        foreach (var m in request.History)
        {
            if (!string.IsNullOrWhiteSpace(m.Text))
            {
                messages.Add(new ChatMessage(
                    string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? ChatRole.Assistant : ChatRole.User,
                    m.Text));
            }
        }
        if (request.Images.Count == 0 && request.Documents.Count == 0)
        {
            messages.Add(new ChatMessage(ChatRole.User, request.UserText));
        }
        else
        {
            var contents = new List<AIContent> { new TextContent(request.UserText) };
            contents.AddRange(request.Images.Select(i => new DataContent(i.Bytes, i.MediaType)));
            contents.AddRange(request.Documents.Select(d => new DataContent(d.Bytes, d.MediaType) { Name = d.Name }));
            messages.Add(new ChatMessage(ChatRole.User, contents));
        }
        return messages;
    }

    /// <summary>The model's arguments as JSON text. The adapter parses streamed arguments into a
    /// dictionary of <see cref="JsonElement"/>s, so re-serializing reproduces the payload (an inlined
    /// object stays an object, a JSON-string arg stays a string).</summary>
    private static string SerializeArgs(IDictionary<string, object?>? arguments)
    {
        try
        {
            return arguments is null ? "{}" : JsonSerializer.Serialize(arguments);
        }
        catch (Exception)
        {
            return "{}";
        }
    }
}
