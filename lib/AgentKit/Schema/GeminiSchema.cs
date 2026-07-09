using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentKit.Schema;

/// <summary>Rewrites a tool's JSON-Schema (as produced by <see cref="DtoJsonSchema"/> — standard
/// JSON-Schema draft) into the narrow OpenAPI-3.0 subset Gemini's <c>functionDeclarations.parameters</c>
/// accepts. Gemini rejects the request outright (400) on constructs it doesn't know, so we must:
/// <list type="bullet">
///   <item>drop <c>additionalProperties</c> (unknown field → 400) and <c>$schema</c>/<c>$id</c>;</item>
///   <item>collapse a nullable union <c>"type": ["string","null"]</c> to a single <c>type</c> plus
///   <c>"nullable": true</c> (Gemini's <c>type</c> is a single string, never an array);</item>
///   <item>recurse through <c>properties</c> and array <c>items</c> so nested DTOs are cleaned too.</item>
/// </list>
/// Everything Gemini does understand — <c>type</c>, <c>description</c>, <c>enum</c>, <c>properties</c>,
/// <c>required</c>, <c>items</c>, numeric bounds — is passed through untouched.</summary>
public static class GeminiSchema
{
    /// <summary>Sanitize a schema given as raw JSON text; returns a Gemini-safe <see cref="JsonNode"/>
    /// (null when the input is not a JSON object, e.g. a bare <c>true</c>).</summary>
    public static JsonNode? Sanitize(string schemaJson)
    {
        var node = JsonNode.Parse(schemaJson);
        Clean(node);
        return node;
    }

    /// <summary>Sanitize a schema given as a <see cref="JsonElement"/> (an <see cref="AIFunction"/>'s
    /// <c>JsonSchema</c>).</summary>
    public static JsonNode? Sanitize(JsonElement schema) => Sanitize(schema.GetRawText());

    private static void Clean(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                CleanObject(obj);
                break;
            case JsonArray arr:
                foreach (var item in arr)
                {
                    Clean(item);
                }
                break;
        }
    }

    private static void CleanObject(JsonObject obj)
    {
        // Fields Gemini's schema validator does not know — their mere presence is a 400.
        obj.Remove("additionalProperties");
        obj.Remove("$schema");
        obj.Remove("$id");

        // A nullable union ("type": ["string","null"]) → single type + nullable:true.
        if (obj["type"] is JsonArray types)
        {
            string? primitive = null;
            var nullable = false;
            foreach (var t in types)
            {
                var name = t?.GetValue<string>();
                if (string.Equals(name, "null", StringComparison.Ordinal))
                {
                    nullable = true;
                }
                else
                {
                    primitive ??= name;
                }
            }
            if (primitive is not null)
            {
                obj["type"] = primitive;
            }
            else
            {
                obj.Remove("type");
            }
            if (nullable)
            {
                obj["nullable"] = true;
            }
        }

        // Recurse: property schemas, array item schemas, and any nested schema-bearing nodes.
        if (obj["properties"] is JsonObject properties)
        {
            foreach (var (_, value) in properties.ToList())
            {
                Clean(value);
            }
        }
        if (obj["items"] is { } items)
        {
            Clean(items);
        }
        foreach (var key in new[] { "anyOf", "allOf", "oneOf" })
        {
            if (obj[key] is JsonArray branch)
            {
                foreach (var b in branch)
                {
                    Clean(b);
                }
            }
        }
    }
}
