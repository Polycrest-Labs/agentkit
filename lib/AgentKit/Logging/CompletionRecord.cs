namespace AgentKit.Logging;

/// <summary>One record per model hop (one completion request/response), the unit every sink receives.
/// Image bytes are never logged — they are replaced by <c>sha256:…</c> hashes in the rendered messages.</summary>
public sealed record CompletionRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("n");
    public DateTimeOffset Ts { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>What feature made the call (e.g. <c>trip-agent</c>, <c>booking-extract</c>).</summary>
    public string Feature { get; init; } = "";

    /// <summary>Correlates all hops of one logical turn (an agent turn, one extraction, …).</summary>
    public string? TurnId { get; init; }

    /// <summary>1-based completion index within the turn.</summary>
    public int Hop { get; init; }

    public string? ConversationId { get; init; }
    public string? UserId { get; init; }

    public CompletionRoute Route { get; init; } = new();
    public CompletionRequest Request { get; init; } = new();
    public CompletionResponse Response { get; init; } = new();
    public CompletionUsage? Usage { get; init; }

    /// <summary>Estimated cost from the ModelCard's prices. Null (not 0) when prices are unknown.</summary>
    public decimal? CostUsd { get; init; }

    public long LatencyMs { get; init; }
    public string? Error { get; init; }

    /// <summary>1-based attempt index (2+ means this hop ran on a failover candidate).</summary>
    public int Attempt { get; init; } = 1;

    /// <summary>The model id that failed and was failed over from, when <see cref="Attempt"/> &gt; 1.</summary>
    public string? FailedOverFrom { get; init; }
}

public sealed record CompletionRoute
{
    public string? Way { get; init; }
    public string Model { get; init; } = "";
    public string Provider { get; init; } = "";
    public string? Reason { get; init; }
}

public sealed record CompletionRequest
{
    /// <summary>The system prompt / instructions for the call.</summary>
    public string? System { get; init; }
    public IReadOnlyList<LoggedMessage> Messages { get; init; } = [];
    public IReadOnlyList<string> ToolNames { get; init; } = [];
    public float? Temperature { get; init; }
}

public sealed record LoggedMessage(string Role, string Text);

public sealed record CompletionResponse
{
    public string? Text { get; init; }
    public IReadOnlyList<LoggedToolCall> ToolCalls { get; init; } = [];
    public string? Finish { get; init; }
}

public sealed record LoggedToolCall(string Name, string ArgsJson);

public sealed record CompletionUsage
{
    public long? Input { get; init; }
    public long? Output { get; init; }
    public long? Cached { get; init; }
}
