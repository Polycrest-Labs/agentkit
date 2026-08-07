using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentKit.Agents;

namespace AgentKit.Wire;

/// <summary>
/// The v2 frame vocabulary — the kit-owned wire protocol both halves speak. Each frame serializes
/// as a single-line JSON object carried in an SSE <c>data:</c> line; the SSE <c>event:</c> name is
/// the SOLE discriminant and never repeats inside the payload (the TS mirror reassembles with
/// <c>{...payload, type: eventName}</c>, so a hostile payload <c>type</c> can never override it).
/// The canonical fixtures live in <c>protocol/fixtures/</c> and are conformance-tested from both
/// C# (round-trip) and TS (parse) — drift on either side fails that repo's CI.
/// </summary>
public abstract record AgentFrame
{
    /// <summary>The SSE event name for this frame (the wire discriminant). Declared once here —
    /// backed by the protected member so no derived type re-declares a public property the wire
    /// serializer could pick up.</summary>
    [JsonIgnore]
    public string Event => EventName;

    protected abstract string EventName { get; }
}

/// <summary>Opens every stream: the turn id and the routed provider/model serving it.</summary>
public sealed record LoadedFrame(string TurnId, string Provider, string Model) : AgentFrame
{
    protected override string EventName => "loaded";
}

/// <summary>One chunk of streamed assistant text.</summary>
public sealed record TokenFrame(string Delta) : AgentFrame
{
    protected override string EventName => "token";
}

public enum AgentToolPhase
{
    Start,
    End,
}

/// <summary>A tool call started or finished. <paramref name="Label"/> is the server-supplied
/// display label (from <see cref="Agents.AgentTool.DisplayLabel"/>); null → hosts show the raw name.</summary>
public sealed record ToolFrame(string Name, AgentToolPhase Phase, string? Label = null) : AgentFrame
{
    protected override string EventName => "tool";
}

/// <summary>One bounded question card. Non-blocking: the stream ends normally; the answer is the
/// next user turn.</summary>
public sealed record QuestionFrame(AgentQuestion Question) : AgentFrame
{
    protected override string EventName => "question";
}

/// <summary>A batched question form (one Submit).</summary>
public sealed record QuestionFormFrame(AgentQuestionForm Form) : AgentFrame
{
    protected override string EventName => "questionForm";
}

/// <summary>Tappable next-step chips. Ephemeral — never persisted; chips prefill, never auto-send.</summary>
public sealed record FollowupsFrame(IReadOnlyList<string> Prompts) : AgentFrame
{
    protected override string EventName => "followups";
}

/// <summary>A web citation surfaced while streaming (rendered as a deduped footer).</summary>
public sealed record CitationFrame(string? Title, string? Url) : AgentFrame
{
    protected override string EventName => "citation";
}

/// <summary>Aggregated token usage for the turn. Optional display; hosts may suppress via
/// <see cref="AgentWireOptions.EmitUsage"/>.</summary>
public sealed record UsageFrame(long? InputTokens, long? OutputTokens) : AgentFrame
{
    protected override string EventName => "usage";
}

/// <summary>Liveness only — a monotone seq that feeds the client's dead-air timer; never rendered.</summary>
public sealed record HeartbeatFrame(long Seq) : AgentFrame
{
    protected override string EventName => "heartbeat";
}

/// <summary>The authoritative final assistant text. Streamed tokens are the fallback only when this
/// frame is absent. Emitted by the SSE writer AFTER successful host finalization, never by the session.</summary>
public sealed record MessageFrame(string Text) : AgentFrame
{
    protected override string EventName => "message";
}

/// <summary>Terminal success. Never emitted before the host's finalizer has committed.</summary>
public sealed record DoneFrame(string TurnId, string? Title = null) : AgentFrame
{
    protected override string EventName => "done";
}

/// <summary>Terminal failure after SSE headers went out. <paramref name="Code"/> is a stable
/// machine-readable code; <paramref name="Detail"/> is included ONLY when the host marks the text
/// safe/domain-facing — infrastructure exception messages and stacks stay in server logs and never
/// cross the wire.</summary>
public sealed record ErrorFrame(string Code, string? Detail = null, string? TurnId = null) : AgentFrame
{
    protected override string EventName => "error";
}

/// <summary>A host domain frame (e.g. Groundsworth's <c>suggestion</c>/<c>filters</c>). The name is
/// the SSE event name and must match <c>^[a-z][a-z0-9-]*$</c>; it may not shadow a core frame name.
/// The payload must serialize as a JSON object (enforced by <see cref="AgentWireJson.Serialize"/>).</summary>
public sealed record DomainFrame : AgentFrame
{
    public DomainFrame(string name, object payload)
    {
        if (!DomainName.IsMatch(name ?? string.Empty))
        {
            throw new ArgumentException(
                $"Domain frame name '{name}' is invalid — names must match ^[a-z][a-z0-9-]*$.", nameof(name));
        }
        if (AgentWireJson.CoreFrameTypes.ContainsKey(name!))
        {
            throw new ArgumentException(
                $"Domain frame name '{name}' shadows a core frame name.", nameof(name));
        }
        Name = name!;
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
    }

    [JsonIgnore]
    public string Name { get; }

    [JsonIgnore]
    public object Payload { get; }

    protected override string EventName => Name;

    private static readonly Regex DomainName = new("^[a-z][a-z0-9-]*$", RegexOptions.Compiled);
}

/// <summary>A frame (or its payload) could not be serialized for the wire.</summary>
public sealed class AgentWireException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The wire serializer: camelCase, nulls omitted, camelCase string enums, single-line
/// output. One place, so the C# side can never drift from the fixtures piecemeal.</summary>
public static class AgentWireJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }

    /// <summary>Every core frame's event name → CLR type (fixture tests and domain-name reservation).</summary>
    public static readonly IReadOnlyDictionary<string, Type> CoreFrameTypes = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        ["loaded"] = typeof(LoadedFrame),
        ["token"] = typeof(TokenFrame),
        ["tool"] = typeof(ToolFrame),
        ["question"] = typeof(QuestionFrame),
        ["questionForm"] = typeof(QuestionFormFrame),
        ["followups"] = typeof(FollowupsFrame),
        ["citation"] = typeof(CitationFrame),
        ["usage"] = typeof(UsageFrame),
        ["heartbeat"] = typeof(HeartbeatFrame),
        ["message"] = typeof(MessageFrame),
        ["done"] = typeof(DoneFrame),
        ["error"] = typeof(ErrorFrame),
    };

    /// <summary>Serializes one frame to its single-line JSON payload (the SSE <c>data:</c> value).
    /// A <see cref="DomainFrame"/> serializes its payload directly and must produce a JSON object —
    /// anything else throws <see cref="AgentWireException"/> rather than putting a spoofable or
    /// unparseable payload on the wire.</summary>
    public static string Serialize(AgentFrame frame)
    {
        try
        {
            if (frame is DomainFrame domain)
            {
                var json = JsonSerializer.Serialize(domain.Payload, domain.Payload.GetType(), Options);
                if (json.Length == 0 || json[0] != '{')
                {
                    throw new AgentWireException(
                        $"Domain frame '{domain.Name}' payload must serialize as a JSON object; got: {Truncate(json)}");
                }
                return json;
            }
            return JsonSerializer.Serialize(frame, frame.GetType(), Options);
        }
        catch (AgentWireException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AgentWireException($"Frame '{frame.Event}' failed to serialize.", ex);
        }
    }

    private static string Truncate(string json) => json.Length <= 80 ? json : json[..80] + "…";
}
