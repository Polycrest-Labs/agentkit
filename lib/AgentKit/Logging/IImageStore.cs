namespace AgentKit.Logging;

/// <summary>Optional image-persistence hook. <c>LoggingChatClient</c> replaces image bytes with
/// <c>sha256:…</c> hashes before any sink sees a record — this hook fires at hash time, the only
/// moment bytes and hash meet, so a host can store the bytes content-addressed and keep vision logs
/// replayable. Same contract as <see cref="ICompletionSink"/>: implementations must never block or
/// fault a turn (the caller additionally swallows and logs any throw). Default is a no-op.</summary>
public interface IImageStore
{
    void Persist(string sha256, string? mediaType, ReadOnlyMemory<byte> bytes);
}

public sealed class NullImageStore : IImageStore
{
    public static readonly NullImageStore Instance = new();
    public void Persist(string sha256, string? mediaType, ReadOnlyMemory<byte> bytes)
    {
    }
}
