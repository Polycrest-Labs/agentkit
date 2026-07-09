using System.Security.Cryptography;
using AgentKit.Catalog;
using AgentKit.Logging;
using Microsoft.Extensions.AI;

namespace AgentKit.Tests;

/// <summary>The image-persistence hook: bytes reach the store with the exact hash the record carries,
/// the default is a no-op, and a throwing store can never fault the completion.</summary>
public sealed class ImageStoreTests
{
    private static readonly ModelCard Card = new() { Id = "m", Provider = "p" };
    private static readonly byte[] Png = [137, 80, 78, 71, 1, 2, 3, 4];

    private sealed class RecordingImageStore : IImageStore
    {
        public List<(string Sha256, string? MediaType, byte[] Bytes)> Persisted { get; } = [];
        public void Persist(string sha256, string? mediaType, ReadOnlyMemory<byte> bytes) =>
            Persisted.Add((sha256, mediaType, bytes.ToArray()));
    }

    private sealed class ThrowingImageStore : IImageStore
    {
        public void Persist(string sha256, string? mediaType, ReadOnlyMemory<byte> bytes) =>
            throw new InvalidOperationException("storage is down");
    }

    private static ChatMessage VisionMessage() =>
        new(ChatRole.User, [new TextContent("what is this?"), new DataContent(Png, "image/png")]);

    [Fact]
    public async Task Hook_ReceivesBytesAndTheHashTheRecordCarries()
    {
        var sink = new InMemorySink();
        var store = new RecordingImageStore();
        var client = new LoggingChatClient(new FakeChatClient().EnqueueText("red"), Card, sink, imageStore: store);

        await client.GetResponseAsync([VisionMessage()]);

        var persisted = Assert.Single(store.Persisted);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Png)), persisted.Sha256);
        Assert.Equal("image/png", persisted.MediaType);
        Assert.Equal(Png, persisted.Bytes);
        // The record references the same hash — the sha256 join the replay source depends on.
        var record = Assert.Single(sink.Records);
        Assert.Contains($"sha256:{persisted.Sha256}", record.Request.Messages.Single(m => m.Role == "user").Text);
    }

    [Fact]
    public async Task NoStoreConfigured_LogsHashesExactlyAsBefore()
    {
        var sink = new InMemorySink();
        var client = new LoggingChatClient(new FakeChatClient().EnqueueText("red"), Card, sink);

        await client.GetResponseAsync([VisionMessage()]);

        Assert.Contains("sha256:", Assert.Single(sink.Records).Request.Messages.Single(m => m.Role == "user").Text);
    }

    [Fact]
    public async Task ThrowingStore_NeverFaultsTheTurn_AndTheRecordStillLands()
    {
        var sink = new InMemorySink();
        var client = new LoggingChatClient(new FakeChatClient().EnqueueText("red"), Card, sink, imageStore: new ThrowingImageStore());

        var response = await client.GetResponseAsync([VisionMessage()]);

        Assert.Equal("red", response.Text);
        Assert.Single(sink.Records);
    }

    [Fact]
    public void NullImageStore_IsANoOp()
    {
        NullImageStore.Instance.Persist("abc", "image/png", Png); // must not throw
    }
}
