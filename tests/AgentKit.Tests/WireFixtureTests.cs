using System.Text.Json;
using System.Text.Json.Nodes;
using AgentKit.Wire;

namespace AgentKit.Tests;

/// <summary>
/// The C# half of the protocol conformance bargain: every fixture in <c>protocol/fixtures/</c>
/// either round-trips through <see cref="AgentWireJson"/> or is rejected, exactly as declared.
/// The TS suite (<c>ui/src/lib/wire-fixtures.spec.ts</c>) parses the same files — drift on either
/// side of the mirror fails that repo's CI.
/// </summary>
public sealed class WireFixtureTests
{
    private sealed record Fixture(
        string File, string Event, JsonElement? Payload, string? RawData, bool Valid, string Kind, bool RoundTrip)
    {
        public string Data => RawData ?? Payload!.Value.GetRawText();
    }

    private static readonly IReadOnlyList<Fixture> Fixtures = Load();

    private static IReadOnlyList<Fixture> Load()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "protocol-fixtures");
        var fixtures = new List<Fixture>();
        foreach (var path in Directory.GetFiles(dir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            fixtures.Add(new Fixture(
                File: Path.GetFileName(path),
                Event: root.GetProperty("event").GetString()!,
                Payload: root.TryGetProperty("payload", out var payload) ? payload.Clone() : null,
                RawData: root.TryGetProperty("rawData", out var raw) ? raw.GetString() : null,
                Valid: root.GetProperty("valid").GetBoolean(),
                Kind: root.GetProperty("kind").GetString()!,
                RoundTrip: root.TryGetProperty("roundTrip", out var rt) && rt.GetBoolean()));
        }
        Assert.NotEmpty(fixtures);
        return fixtures;
    }

    public static TheoryData<string> ValidCoreFixtures() =>
        [.. Fixtures.Where(f => f is { Valid: true, Kind: "core" }).Select(f => f.File)];

    public static TheoryData<string> InvalidFixtures() =>
        [.. Fixtures.Where(f => !f.Valid).Select(f => f.File)];

    private static Fixture Get(string file) => Fixtures.Single(f => f.File == file);

    [Fact]
    public void EveryCoreFrameTypeHasAFixture()
    {
        var covered = Fixtures.Where(f => f is { Valid: true, Kind: "core" }).Select(f => f.Event).ToHashSet(StringComparer.Ordinal);
        foreach (var eventName in AgentWireJson.CoreFrameTypes.Keys)
        {
            Assert.Contains(eventName, covered);
        }
    }

    [Theory]
    [MemberData(nameof(ValidCoreFixtures))]
    public void ValidCoreFixture_DeserializesAndRoundTrips(string file)
    {
        var fixture = Get(file);
        var type = AgentWireJson.CoreFrameTypes[fixture.Event];

        var frame = (AgentFrame?)JsonSerializer.Deserialize(fixture.Data, type, AgentWireJson.Options);

        Assert.NotNull(frame);
        Assert.Equal(fixture.Event, frame.Event);
        var serialized = AgentWireJson.Serialize(frame);
        Assert.DoesNotContain('\n', serialized); // single-line: one SSE data: line per frame
        if (fixture.RoundTrip)
        {
            Assert.True(
                JsonNode.DeepEquals(JsonNode.Parse(serialized), JsonNode.Parse(fixture.Data)),
                $"{file}: round-trip drifted.\n  fixture: {fixture.Data}\n  re-serialized: {serialized}");
        }
    }

    [Fact]
    public void DomainFixture_RoundTripsThroughDomainFrame()
    {
        var fixture = Get("domain-suggestion.json");

        var frame = new DomainFrame(fixture.Event, fixture.Payload!.Value);

        Assert.Equal("suggestion", frame.Event);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(AgentWireJson.Serialize(frame)), JsonNode.Parse(fixture.Data)));
    }

    [Theory]
    [MemberData(nameof(InvalidFixtures))]
    public void InvalidFixture_IsRejected(string file)
    {
        var fixture = Get(file);
        if (fixture.File == "invalid-event-name.json")
        {
            // The invalid EVENT NAME is refused at DomainFrame construction, before any wire write.
            Assert.Throws<ArgumentException>(() => new DomainFrame(fixture.Event, new { }));
            return;
        }

        // Malformed / primitive / array / null payloads must never deserialize into a core frame.
        var type = AgentWireJson.CoreFrameTypes[fixture.Event];
        AgentFrame? frame = null;
        try
        {
            frame = (AgentFrame?)JsonSerializer.Deserialize(fixture.Data, type, AgentWireJson.Options);
        }
        catch (JsonException)
        {
            return; // rejected — exactly right
        }
        Assert.Null(frame); // "null" deserializes without throwing; the result must still be no frame
    }

    [Fact]
    public void HostileTypeMember_CannotOverrideTheEventDiscriminant()
    {
        var fixture = Get("hostile-type.json");
        var frame = (AgentFrame?)JsonSerializer.Deserialize(fixture.Data, AgentWireJson.CoreFrameTypes[fixture.Event], AgentWireJson.Options);

        Assert.NotNull(frame);
        Assert.Equal("token", frame.Event); // the frame's identity comes from its TYPE, never the payload
        var reserialized = JsonNode.Parse(AgentWireJson.Serialize(frame))!.AsObject();
        Assert.False(reserialized.ContainsKey("type"), "payloads must never carry a type member");
    }

    [Fact]
    public void DomainFrame_RefusesCoreNamesAndInvalidNames()
    {
        Assert.Throws<ArgumentException>(() => new DomainFrame("done", new { ok = true }));
        Assert.Throws<ArgumentException>(() => new DomainFrame("message", new { ok = true }));
        Assert.Throws<ArgumentException>(() => new DomainFrame("Suggestion", new { ok = true }));
        Assert.Throws<ArgumentException>(() => new DomainFrame("has space", new { ok = true }));
        Assert.Throws<ArgumentException>(() => new DomainFrame("", new { ok = true }));
        _ = new DomainFrame("suggestion", new { ok = true });
        _ = new DomainFrame("filters-v2", new { ok = true });
    }

    [Fact]
    public void DomainFrame_NonObjectPayloadRefusesToSerialize()
    {
        Assert.Throws<AgentWireException>(() => AgentWireJson.Serialize(new DomainFrame("filters", 42)));
        Assert.Throws<AgentWireException>(() => AgentWireJson.Serialize(new DomainFrame("filters", new[] { 1, 2 })));
        Assert.Throws<AgentWireException>(() => AgentWireJson.Serialize(new DomainFrame("filters", "text")));
    }

    [Fact]
    public void Serializer_OmitsNullsAndCamelCases()
    {
        Assert.Equal("""{"name":"rank_comps","phase":"end"}""", AgentWireJson.Serialize(new ToolFrame("rank_comps", AgentToolPhase.End)));
        Assert.Equal("""{"code":"turn_failed"}""", AgentWireJson.Serialize(new ErrorFrame("turn_failed")));
    }
}
