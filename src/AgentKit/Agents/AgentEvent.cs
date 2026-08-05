namespace AgentKit.Agents;

/// <summary>A streamed event from one agent turn. Deliberately generic — a host's domain events
/// (proposals, questions, offer cards, …) ride through <see cref="CustomEvent"/> untyped, so the
/// library never needs to know about them. Failures are NOT an event: <c>AgentRunner.RunAsync</c>
/// throws, matching runtimes whose orchestrators translate exceptions into their own error events.</summary>
public abstract record AgentEvent;

/// <summary>A chunk of assistant text.</summary>
public sealed record TokenDelta(string Delta) : AgentEvent;

/// <summary>The model requested a tool call; dispatch is about to run.</summary>
public sealed record ToolCallStarted(string Name) : AgentEvent;

/// <summary>A tool call finished (possibly with a recoverable error fed back to the model).</summary>
public sealed record ToolCallFinished(string Name, string Output, bool IsError) : AgentEvent;

/// <summary>A web citation annotation surfaced while streaming.</summary>
public sealed record CitationFound(string? Title, string? Url) : AgentEvent;

/// <summary>Aggregated token usage for the whole turn (all hops), reported once before <see cref="Completed"/>.</summary>
public sealed record UsageReport(long? InputTokens, long? OutputTokens) : AgentEvent;

/// <summary>An opaque host domain event a tool handler emitted (see <see cref="ToolOutcome.DomainEvents"/>).</summary>
public sealed record CustomEvent(object Payload) : AgentEvent;

/// <summary>The turn finished; carries the full assistant text from the final model hop. Text from
/// earlier tool-call hops is still emitted as <see cref="TokenDelta"/> and retained in history.</summary>
public sealed record Completed(string Text) : AgentEvent;
