namespace AgentKit.Logging;

/// <summary>Receives one <see cref="CompletionRecord"/> per model hop. Implementations must be fast and
/// non-blocking (buffer internally if the backing store is slow) — <c>LoggingChatClient</c> calls this on
/// the request path and swallows any exception it throws, so a bad sink can degrade logs but never a turn.</summary>
public interface ICompletionSink
{
    void Record(CompletionRecord record);
}

/// <summary>The default no-op sink (logging off).</summary>
public sealed class NullCompletionSink : ICompletionSink
{
    public static NullCompletionSink Instance { get; } = new();

    public void Record(CompletionRecord record)
    {
    }
}
