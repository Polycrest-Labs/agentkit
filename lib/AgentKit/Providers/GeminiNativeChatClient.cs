using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentKit.Catalog;
using AgentKit.Schema;
using Microsoft.Extensions.AI;

namespace AgentKit.Providers;

/// <summary>An <see cref="IChatClient"/> over Gemini's <b>native</b> <c>generateContent</c> API — the path
/// that actually supports Google Search grounding (via the <c>google_search</c> tool + returned
/// <c>groundingMetadata</c>), which Gemini's OpenAI-compat endpoint does not. It maps the M.E.AI request
/// (messages, function tools, a <see cref="HostedWebSearchTool"/>) onto a <c>generateContent</c> body and
/// maps the reply back into text, <see cref="FunctionCallContent"/>, <see cref="UsageContent"/>, and
/// <see cref="CitationAnnotation"/>s (from grounding chunks). Responses are buffered — the client satisfies
/// the streaming contract by yielding one terminal update — which is invisible to the agent loop (it
/// aggregates updates via <c>ToChatResponse()</c>) but drops real-time token streaming; acceptable because
/// Gemini is a candidate/eval model, not a routed default. Combining function tools with the built-in
/// search requires <c>toolConfig.includeServerSideToolInvocations</c>, which this sets automatically.</summary>
public sealed class GeminiNativeChatClient : IChatClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string ThoughtSignatureKey = "thoughtSignature";

    private readonly HttpClient _http;
    private readonly ModelCard _card;
    private readonly string _endpoint; // provider base, e.g. https://generativelanguage.googleapis.com/v1beta/
    private readonly string _apiKey;

    public GeminiNativeChatClient(HttpClient http, ModelCard card, string endpoint, string apiKey)
    {
        _http = http;
        _card = card;
        _endpoint = endpoint.EndsWith('/') ? endpoint : endpoint + "/";
        _apiKey = apiKey;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var turn = await CallAsync(messages, options, cancellationToken);
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, turn.Contents))
        {
            ModelId = _card.Id,
            FinishReason = turn.FinishReason,
            Usage = turn.Usage,
        };
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Buffered: one native call, surfaced as a single terminal update. The agent loop collects updates
        // and calls ToChatResponse(), so text, function calls, citations, and usage all arrive intact.
        var turn = await CallAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, turn.Contents)
        {
            ModelId = _card.Id,
            FinishReason = turn.FinishReason,
        };
    }

    private async Task<GeminiTurn> CallAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
    {
        var body = BuildRequest([.. messages], options);
        var url = $"{_endpoint}models/{_card.WireName}:generateContent";
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: Json) };
        req.Headers.Add("x-goog-api-key", _apiKey);

        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var payload = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Gemini generateContent failed ({(int)res.StatusCode}) for '{_card.WireName}': {ErrorMessage(payload)}");
        }
        using var doc = JsonDocument.Parse(payload);
        return ParseResponse(doc.RootElement);
    }

    // ── request ──────────────────────────────────────────────────────────────────────────────────────

    private JsonObject BuildRequest(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var callNames = MapCallIdsToNames(messages);
        var systemParts = new JsonArray();
        var contents = new JsonArray();
        JsonObject? current = null; // coalesce consecutive same-role messages into one content

        void Flush() { if (current is not null) { contents.Add(current); current = null; } }

        void AppendPart(string role, JsonNode part)
        {
            if (current is null || (string)current["role"]! != role)
            {
                Flush();
                current = new JsonObject { ["role"] = role, ["parts"] = new JsonArray() };
            }
            ((JsonArray)current["parts"]!).Add(part);
        }

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                foreach (var text in message.Contents.OfType<TextContent>())
                {
                    systemParts.Add(new JsonObject { ["text"] = text.Text });
                }
                continue;
            }
            var role = message.Role == ChatRole.Assistant ? "model" : "user"; // Tool results ride a user turn
            foreach (var part in TranslateParts(message.Contents, callNames))
            {
                AppendPart(role, part);
            }
        }
        Flush();

        var request = new JsonObject { ["contents"] = contents };
        if (options?.Instructions is { Length: > 0 })
        {
            systemParts.Insert(0, new JsonObject { ["text"] = options.Instructions });
        }
        if (systemParts.Count > 0)
        {
            request["systemInstruction"] = new JsonObject { ["parts"] = systemParts };
        }

        var (tools, wantsSearch) = BuildTools(options);
        if (tools.Count > 0)
        {
            request["tools"] = tools;
        }
        if (wantsSearch)
        {
            // Required whenever the built-in search runs alongside function declarations, else Gemini 400s
            // ("enable tool_config.include_server_side_tool_invocations to use Built-in tools with Function
            // calling"). Harmless when search runs alone.
            request["toolConfig"] = new JsonObject { ["includeServerSideToolInvocations"] = true };
        }

        var generationConfig = new JsonObject();
        if (options?.Temperature is { } temperature)
        {
            generationConfig["temperature"] = temperature;
        }
        if (options?.MaxOutputTokens is { } maxTokens)
        {
            generationConfig["maxOutputTokens"] = maxTokens;
        }
        if (generationConfig.Count > 0)
        {
            request["generationConfig"] = generationConfig;
        }
        return request;
    }

    private static IEnumerable<JsonNode> TranslateParts(IEnumerable<AIContent> contents, IReadOnlyDictionary<string, string> callNames)
    {
        foreach (var content in contents)
        {
            switch (content)
            {
                case TextContent { Text.Length: > 0 } text:
                    yield return new JsonObject { ["text"] = text.Text };
                    break;
                case DataContent data:
                    yield return new JsonObject
                    {
                        ["inlineData"] = new JsonObject
                        {
                            ["mimeType"] = data.MediaType,
                            ["data"] = Convert.ToBase64String(data.Data.Span),
                        },
                    };
                    break;
                case FunctionCallContent call:
                    var callPart = new JsonObject
                    {
                        ["functionCall"] = new JsonObject
                        {
                            ["name"] = call.Name,
                            ["args"] = call.Arguments is null ? new JsonObject() : JsonSerializer.SerializeToNode(call.Arguments, Json),
                        },
                    };
                    // Gemini 3 requires the opaque thought_signature it emitted with the call to be echoed
                    // back on resubmission ("Function call is missing a thought_signature"), else it 400s
                    // the next hop. It was stashed on the content when the call was parsed.
                    if (call.AdditionalProperties?.TryGetValue(ThoughtSignatureKey, out var sig) == true && sig is string s)
                    {
                        callPart["thoughtSignature"] = s;
                    }
                    yield return callPart;
                    break;
                case FunctionResultContent result:
                    // Gemini matches a response to its call by name; recover it from the paired call id.
                    // The response must be a JSON object, so wrap the tool's string output under "result".
                    yield return new JsonObject
                    {
                        ["functionResponse"] = new JsonObject
                        {
                            ["name"] = callNames.GetValueOrDefault(result.CallId, result.CallId),
                            ["response"] = new JsonObject { ["result"] = result.Result?.ToString() ?? "" },
                        },
                    };
                    break;
            }
        }
    }

    /// <summary>Map every function call's id → name across the history, so a later
    /// <see cref="FunctionResultContent"/> (which carries only the id) can be given Gemini's required name.</summary>
    private static Dictionary<string, string> MapCallIdsToNames(IEnumerable<ChatMessage> messages)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var call in messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>())
        {
            if (!string.IsNullOrEmpty(call.CallId))
            {
                map[call.CallId] = call.Name;
            }
        }
        return map;
    }

    private (JsonArray Tools, bool WantsSearch) BuildTools(ChatOptions? options)
    {
        var tools = new JsonArray();
        var declarations = new JsonArray();
        foreach (var fn in (options?.Tools ?? []).OfType<AIFunction>())
        {
            var declaration = new JsonObject
            {
                ["name"] = fn.Name,
                ["description"] = fn.Description,
            };
            if (GeminiSchema.Sanitize(fn.JsonSchema) is JsonObject parameters && parameters.Count > 0)
            {
                declaration["parameters"] = parameters;
            }
            declarations.Add(declaration);
        }
        if (declarations.Count > 0)
        {
            tools.Add(new JsonObject { ["functionDeclarations"] = declarations });
        }

        // The capability gate has already stripped the hosted search tool for non-search models, so its
        // presence here means grounding is wanted and the card supports it.
        var wantsSearch = (options?.Tools ?? []).Any(t => t is HostedWebSearchTool);
        if (wantsSearch)
        {
            tools.Add(new JsonObject { ["google_search"] = new JsonObject() });
        }
        return (tools, wantsSearch);
    }

    // ── response ─────────────────────────────────────────────────────────────────────────────────────

    private GeminiTurn ParseResponse(JsonElement root)
    {
        if (root.TryGetProperty("promptFeedback", out var feedback)
            && feedback.TryGetProperty("blockReason", out var blockReason))
        {
            throw new InvalidOperationException($"Gemini blocked the prompt: {blockReason.GetString()}");
        }

        var contents = new List<AIContent>();
        var textParts = new List<string>();
        var functionCalls = new List<FunctionCallContent>();
        ChatFinishReason? finishReason = null;

        if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
        {
            var candidate = candidates[0];
            if (candidate.TryGetProperty("finishReason", out var reason))
            {
                finishReason = MapFinishReason(reason.GetString());
            }
            if (candidate.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
            {
                var index = 0;
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var text) && text.GetString() is { Length: > 0 } s)
                    {
                        textParts.Add(s);
                    }
                    else if (part.TryGetProperty("functionCall", out var fc))
                    {
                        var name = fc.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var args = ParseArgs(fc);
                        // Gemini calls carry no id; synthesize a stable one so the paired result maps back.
                        var callContent = new FunctionCallContent($"{name}-{index}", name, args);
                        // Preserve the part's thought_signature verbatim; Gemini 3 requires it echoed back
                        // "in the exact part where it was received" or it 400s the next hop. Capturing only
                        // where present is exactly the spec: for PARALLEL calls only the first functionCall
                        // carries a signature; for SEQUENTIAL calls across hops each one does.
                        if (part.TryGetProperty("thoughtSignature", out var ts) && ts.GetString() is { Length: > 0 } signature)
                        {
                            callContent.AdditionalProperties = new AdditionalPropertiesDictionary { [ThoughtSignatureKey] = signature };
                        }
                        functionCalls.Add(callContent);
                    }
                    // Server-side google_search invocations (toolCall/toolResponse), thoughtSignature, and
                    // reasoning parts are Gemini-internal — ignored; their effect survives in text+citations.
                    index++;
                }
            }
        }

        var citations = ExtractCitations(root);
        var textContent = new TextContent(string.Concat(textParts));
        if (citations.Count > 0)
        {
            textContent.Annotations = citations;
        }
        contents.Add(textContent);
        contents.AddRange(functionCalls);
        if (functionCalls.Count > 0)
        {
            finishReason ??= ChatFinishReason.ToolCalls;
        }

        return new GeminiTurn(contents, ParseUsage(root), finishReason);
    }

    private static Dictionary<string, object?> ParseArgs(JsonElement functionCall)
    {
        var args = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (functionCall.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in argsEl.EnumerateObject())
            {
                args[prop.Name] = prop.Value.Clone();
            }
        }
        return args;
    }

    /// <summary>Grounding chunks → citation annotations, which the agent loop surfaces as citation events
    /// and the eval asserts on (<c>minCitations</c>).</summary>
    private static List<AIAnnotation> ExtractCitations(JsonElement root)
    {
        var citations = new List<AIAnnotation>();
        if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0
            && candidates[0].TryGetProperty("groundingMetadata", out var grounding)
            && grounding.TryGetProperty("groundingChunks", out var chunks) && chunks.ValueKind == JsonValueKind.Array)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var chunk in chunks.EnumerateArray())
            {
                if (!chunk.TryGetProperty("web", out var web))
                {
                    continue;
                }
                var uri = web.TryGetProperty("uri", out var u) ? u.GetString() : null;
                var title = web.TryGetProperty("title", out var t) ? t.GetString() : null;
                if (uri is null || !seen.Add(uri))
                {
                    continue;
                }
                citations.Add(new CitationAnnotation
                {
                    Title = title,
                    Url = Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed : null,
                });
            }
        }
        return citations;
    }

    private static UsageDetails? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usageMetadata", out var usage))
        {
            return null;
        }
        long? Count(string name) => usage.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
        return new UsageDetails
        {
            InputTokenCount = Count("promptTokenCount"),
            OutputTokenCount = Count("candidatesTokenCount"),
            TotalTokenCount = Count("totalTokenCount"),
        };
    }

    private static ChatFinishReason? MapFinishReason(string? reason) => reason switch
    {
        "STOP" => ChatFinishReason.Stop,
        "MAX_TOKENS" => ChatFinishReason.Length,
        "SAFETY" or "RECITATION" or "PROHIBITED_CONTENT" or "BLOCKLIST" => ChatFinishReason.ContentFilter,
        _ => null,
    };

    private static string ErrorMessage(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? payload;
            }
        }
        catch (JsonException)
        {
            // fall through to the raw payload
        }
        return payload.Length > 500 ? payload[..500] : payload;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() => _http.Dispose();

    private sealed record GeminiTurn(List<AIContent> Contents, UsageDetails? Usage, ChatFinishReason? FinishReason);
}
