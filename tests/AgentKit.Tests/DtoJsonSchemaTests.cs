using System.Text.Json;
using AgentKit.Agents;
using AgentKit.Schema;

namespace AgentKit.Tests;

public sealed class DtoJsonSchemaTests
{
    private enum SampleKind
    {
        General,
        Recommendation,
        Place,
    }

    private sealed record SampleArgs(string Title, string? Notes, SampleKind Kind, decimal? Cost, List<string> Tags);

    [Fact]
    public void GeneratesCamelCaseProperties_StringEnums_AndAClosedObject()
    {
        var schema = JsonDocument.Parse(DtoJsonSchema.For<SampleArgs>()).RootElement;

        var properties = schema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("title", out _));      // camelCase
        Assert.True(properties.TryGetProperty("cost", out _));
        var kind = properties.GetProperty("kind");
        var names = kind.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["General", "Recommendation", "Place"], names); // enum as names, not numbers
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void PatchMode_MakesEveryPropertyOptional()
    {
        var schema = JsonDocument.Parse(DtoJsonSchema.For<SampleArgs>(new DtoSchemaOptions { PatchMode = true })).RootElement;

        Assert.False(schema.TryGetProperty("required", out _));
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void ExcludedSystemFields_DisappearFromTheSchema()
    {
        var schema = JsonDocument.Parse(DtoJsonSchema.For<SampleArgs>(new DtoSchemaOptions
        {
            ExcludeFields = ["tags"],
        })).RootElement;

        Assert.False(schema.GetProperty("properties").TryGetProperty("tags", out _));
        if (schema.TryGetProperty("required", out var required))
        {
            Assert.DoesNotContain("tags", required.EnumerateArray().Select(e => e.GetString()));
        }
    }

    private sealed record NestedItem(string Title, string? PlaceId);

    private sealed record NestedArgs(string? PlaceId, List<NestedItem> Items);

    [Fact]
    public void ExcludedSystemFields_DisappearAtEveryDepth()
    {
        var schema = JsonDocument.Parse(DtoJsonSchema.For<NestedArgs>(new DtoSchemaOptions
        {
            ExcludeFields = ["placeId"],
        })).RootElement;

        Assert.False(schema.GetProperty("properties").TryGetProperty("placeId", out _));
        var itemSchema = schema.GetProperty("properties").GetProperty("items").GetProperty("items");
        Assert.False(itemSchema.GetProperty("properties").TryGetProperty("placeId", out _)); // nested too
        Assert.True(itemSchema.GetProperty("properties").TryGetProperty("title", out _));
    }

    [Fact]
    public void FieldDescriptions_OverrideOntoProperties()
    {
        var schema = JsonDocument.Parse(DtoJsonSchema.For<SampleArgs>(new DtoSchemaOptions
        {
            Description = "The sample payload.",
            FieldDescriptions = new Dictionary<string, string> { ["title"] = "Short human title." },
        })).RootElement;

        Assert.Equal("The sample payload.", schema.GetProperty("description").GetString());
        Assert.Equal("Short human title.", schema.GetProperty("properties").GetProperty("title").GetProperty("description").GetString());
    }

    [Fact]
    public async Task TypedTool_BindsArgsTolerantly()
    {
        SampleArgs? received = null;
        var tool = AgentTool.Typed<SampleArgs>("sample", "d", (args, _) =>
        {
            received = args;
            return Task.FromResult(new ToolOutcome("ok"));
        });

        // A number where the DTO wants a string, and an invented enum member — both must bind (safety net).
        await tool.Handler("""{"title": 42, "kind": "Sightseeing", "tags": []}""", CancellationToken.None);

        Assert.Equal("42", received!.Title);
        Assert.Equal(SampleKind.General, received.Kind); // tolerant default, not a crash
    }
}
