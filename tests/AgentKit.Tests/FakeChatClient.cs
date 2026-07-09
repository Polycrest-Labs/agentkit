using Microsoft.Extensions.AI;

namespace AgentKit.Tests;

/// <summary>Scripted <see cref="IChatClient"/>: enqueue responses, errors, or streamed item sequences
/// (strings stream as text updates; an <see cref="Exception"/> mid-sequence faults the stream there).</summary>
public sealed class FakeChatClient : IChatClient
{
    private readonly Queue<Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse>> _responses = new();
    private readonly Queue<IReadOnlyList<object>> _streams = new();

    public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];

    public FakeChatClient Enqueue(Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse> script)
    {
        _responses.Enqueue(script);
        return this;
    }

    public FakeChatClient EnqueueText(string text, UsageDetails? usage = null) =>
        Enqueue((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { Usage = usage });

    public FakeChatClient EnqueueError(Exception exception) =>
        Enqueue((_, _) => throw exception);

    /// <summary>Streamed script: each string yields one text update; an Exception faults the stream at
    /// that point; a <see cref="ChatResponseUpdate"/> passes through as-is.</summary>
    public FakeChatClient EnqueueStream(params object[] items)
    {
        _streams.Enqueue(items);
        return this;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var snapshot = messages.ToList();
        Calls.Add((snapshot, options));
        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("FakeChatClient: no scripted response left.");
        }
        return Task.FromResult(_responses.Dequeue()(snapshot, options));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var snapshot = messages.ToList();
        if (_streams.Count > 0)
        {
            Calls.Add((snapshot, options));
            foreach (var item in _streams.Dequeue())
            {
                await Task.Yield();
                yield return item switch
                {
                    string text => new ChatResponseUpdate(ChatRole.Assistant, text),
                    ChatResponseUpdate update => update,
                    Exception exception => throw exception,
                    _ => throw new InvalidOperationException($"FakeChatClient: unsupported stream item {item.GetType()}"),
                };
            }
            yield break;
        }

        // Fall back to a scripted response, streamed as one text update (+ usage).
        var response = await GetResponseAsync(snapshot, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        if (response.Usage is { } usage)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(usage)]);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
