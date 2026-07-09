namespace AgentKit.Logging;

/// <summary>Ambient (AsyncLocal) context for the completions of one logical turn: the feature tag,
/// correlation ids, and — stamped per attempt by the failover pipeline — the route and attempt info.
/// <c>LoggingChatClient</c> reads this when building each <see cref="CompletionRecord"/>, so per-call
/// metadata never has to thread through <c>IChatClient</c> signatures.</summary>
public sealed class CompletionScope : IDisposable
{
    private static readonly AsyncLocal<CompletionScope?> _current = new();

    private readonly CompletionScope? _parent;
    private int _hop;

    private CompletionScope(string feature)
    {
        Feature = feature;
        _parent = _current.Value;
    }

    public static CompletionScope? Current => _current.Value;

    public string Feature { get; }
    public string TurnId { get; private init; } = "";
    public string? ConversationId { get; private init; }
    public string? UserId { get; private init; }

    // ── Stamped by FailoverChatClient just before each candidate call ──
    public string? Way { get; set; }
    public string? RouteReason { get; set; }
    public int Attempt { get; set; } = 1;
    public string? FailedOverFrom { get; set; }

    public static CompletionScope Begin(string feature, string? turnId = null, string? conversationId = null, string? userId = null)
    {
        var scope = new CompletionScope(feature)
        {
            TurnId = turnId ?? Guid.NewGuid().ToString("n"),
            ConversationId = conversationId,
            UserId = userId,
        };
        _current.Value = scope;
        return scope;
    }

    /// <summary>The next 1-based hop number within this turn.</summary>
    public int NextHop() => Interlocked.Increment(ref _hop);

    public void Dispose() => _current.Value = _parent;
}
