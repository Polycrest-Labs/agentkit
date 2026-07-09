using System.Text.Json;
using System.Text.Json.Nodes;
using AgentKit.Schema;

namespace AgentKit.Tests;

/// <summary>The sanitizer is the load-bearing part of the native Gemini provider: Gemini 400s the whole
/// request on any schema construct it doesn't know, so these lock in the two transforms that matter and
/// the pass-through of everything else.</summary>
public sealed class GeminiSchemaTests
{
    [Fact]
    public void StripsAdditionalProperties_AtEveryDepth()
    {
        var raw = """
        {"type":"object","additionalProperties":false,"properties":{
          "items":{"type":"array","items":{
            "type":"object","additionalProperties":false,"properties":{"title":{"type":"string"}}}}}}
        """;
        var node = GeminiSchema.Sanitize(raw)!.AsObject();

        Assert.False(node.ContainsKey("additionalProperties"));
        var item = node["properties"]!["items"]!["items"]!.AsObject();
        Assert.False(item.ContainsKey("additionalProperties"));
        Assert.NotNull(item["properties"]!["title"]); // real schema preserved
    }

    [Fact]
    public void CollapsesNullableUnion_ToSingleTypePlusNullable()
    {
        var raw = """{"type":"object","properties":{"summary":{"type":["string","null"]}}}""";

        var summary = GeminiSchema.Sanitize(raw)!["properties"]!["summary"]!.AsObject();
        Assert.Equal("string", summary["type"]!.GetValue<string>());
        Assert.True(summary["nullable"]!.GetValue<bool>());
    }

    [Fact]
    public void PreservesEnumTypeAndRequired()
    {
        var raw = """{"type":"object","required":["kind"],"properties":{"kind":{"type":"string","enum":["Activity","Meal"]}}}""";

        var node = GeminiSchema.Sanitize(raw)!.AsObject();
        Assert.Equal("kind", node["required"]!.AsArray()[0]!.GetValue<string>());
        var kind = node["properties"]!["kind"]!.AsObject();
        Assert.Equal("string", kind["type"]!.GetValue<string>());
        Assert.Equal(["Activity", "Meal"], kind["enum"]!.AsArray().Select(e => e!.GetValue<string>()));
    }

    [Fact]
    public void RealDtoSchema_BecomesGeminiSafe_NoUnknownFieldsOrTypeArrays()
    {
        // A DtoJsonSchema-shaped schema (closed object, nullable unions, nested list) → fully sanitized.
        var raw = """
        {"type":"object","additionalProperties":false,"properties":{
          "location":{"type":["string","null"]},
          "items":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{
            "title":{"type":"string"},
            "notes":{"type":["string","null"]},
            "order":{"type":"integer"}}}}}}
        """;
        var node = GeminiSchema.Sanitize(raw)!;

        Assert.Empty(FindKeys(node, "additionalProperties"));
        Assert.Empty(TypeArrays(node));
    }

    private static IEnumerable<JsonNode> FindKeys(JsonNode? node, string key) => node switch
    {
        JsonObject obj => obj.SelectMany(kv =>
            (string.Equals(kv.Key, key, StringComparison.Ordinal) ? [kv.Value!] : Enumerable.Empty<JsonNode>())
            .Concat(FindKeys(kv.Value, key))),
        JsonArray arr => arr.SelectMany(n => FindKeys(n, key)),
        _ => [],
    };

    private static IEnumerable<JsonNode> TypeArrays(JsonNode? node) => node switch
    {
        JsonObject obj => obj.SelectMany(kv =>
            (kv is { Key: "type", Value: JsonArray } ? [kv.Value!] : Enumerable.Empty<JsonNode>())
            .Concat(TypeArrays(kv.Value))),
        JsonArray arr => arr.SelectMany(TypeArrays),
        _ => [],
    };
}
