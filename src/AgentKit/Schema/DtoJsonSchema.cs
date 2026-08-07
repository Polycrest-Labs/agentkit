using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace AgentKit.Schema;

/// <summary>Knobs for one generated tool-args schema.</summary>
public sealed record DtoSchemaOptions
{
    /// <summary>Top-level schema description.</summary>
    public string? Description { get; init; }

    /// <summary>Per-field description overrides, keyed by the serialized (camelCase) property name.
    /// Nested paths use dots (e.g. <c>"items.title"</c> is not supported — keep guidance top-level).</summary>
    public IReadOnlyDictionary<string, string>? FieldDescriptions { get; init; }

    /// <summary>Serialized property names to REMOVE from the schema — system-managed fields the model
    /// must never author (they'd fail app-side validation anyway; hiding them prevents the attempt).
    /// Applied at EVERY depth, since nested DTOs carry them too (e.g. <c>items[].placeId</c>).</summary>
    public IReadOnlyList<string>? ExcludeFields { get; init; }

    /// <summary>Patch mode: every property optional (<c>required</c> emptied) — for tools that send a
    /// partial patch merged over the current entity.</summary>
    public bool PatchMode { get; init; }
}

/// <summary>
/// Generates a provider-constrainable JSON schema for a tool's arguments from the request DTO type
/// itself, so the schema can never drift from the real shape. Enums render as their string names and
/// <c>additionalProperties</c> is always <c>false</c> — the provider rejects invented fields instead of
/// the app having to. Generation uses plain Web + string-enum serializer options (the wire shape);
/// runtime binding still goes through <see cref="AgentJson"/>'s tolerant converters as the safety net.
/// </summary>
public static class DtoJsonSchema
{
    // The exporter must see the standard string-enum converter (which it understands and turns into
    // "enum": [names]); AgentJson's tolerant converter factory is custom and would erase enum schemas.
    private static readonly JsonSerializerOptions SchemaOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // CreateJsonSchema mutates the shared options' TypeInfoResolver on first use; two concurrent
    // first calls race one thread into setting it after the other froze the instance
    // (InvalidOperationException: read-only). Generation happens at static-init time and is cheap —
    // serialize it.
    private static readonly Lock SchemaGate = new();

    public static string For<T>(DtoSchemaOptions? options = null) => For(typeof(T), options);

    public static string For(Type type, DtoSchemaOptions? options = null)
    {
        options ??= new DtoSchemaOptions();
        JsonElement schema;
        lock (SchemaGate)
        {
            schema = AIJsonUtilities.CreateJsonSchema(
                type,
                description: options.Description,
                serializerOptions: SchemaOptions);
        }
        var node = JsonSerializer.SerializeToNode(schema)?.AsObject()
            ?? throw new InvalidOperationException($"Schema generation for {type.Name} produced no object.");
        Shape(node, options, isRoot: true);
        return node.ToJsonString();
    }

    private static void Shape(JsonObject schema, DtoSchemaOptions options, bool isRoot)
    {
        if (schema["properties"] is JsonObject properties)
        {
            // System-managed fields are stripped at every depth — a nested DTO carries them too
            // (e.g. items[].placeId), and the model must never author them anywhere.
            foreach (var field in options.ExcludeFields ?? [])
            {
                properties.Remove(field);
            }
            if (isRoot)
            {
                foreach (var (field, description) in options.FieldDescriptions ?? new Dictionary<string, string>())
                {
                    if (properties[field] is JsonObject property)
                    {
                        property["description"] = description;
                    }
                }
            }

            // Constrain generation everywhere: invented keys are a provider-side error, not a silent drop.
            schema["additionalProperties"] = false;

            if (options.PatchMode && isRoot)
            {
                PruneRequired(schema, removeOnly: null);
            }
            else if (options.ExcludeFields is { Count: > 0 })
            {
                PruneRequired(schema, [.. options.ExcludeFields]);
            }

            // Recurse into nested object schemas (items of arrays, nested DTOs).
            foreach (var (_, value) in properties)
            {
                Recurse(value, options);
            }
        }

        if (schema["items"] is JsonObject items)
        {
            Shape(items, options, isRoot: false);
        }
    }

    private static void Recurse(JsonNode? node, DtoSchemaOptions options)
    {
        if (node is not JsonObject obj)
        {
            return;
        }
        if (obj["properties"] is not null || obj["items"] is not null)
        {
            Shape(obj, options, isRoot: false);
        }
    }

    /// <summary>Empty (patch mode) or filter (exclusions) the root <c>required</c> list.</summary>
    private static void PruneRequired(JsonObject schema, HashSet<string>? removeOnly)
    {
        if (schema["required"] is not JsonArray required)
        {
            return;
        }
        if (removeOnly is null)
        {
            schema.Remove("required");
            return;
        }
        var kept = required.Where(r => r is not null && !removeOnly.Contains(r.GetValue<string>())).Select(r => r!.DeepClone()).ToList();
        schema["required"] = new JsonArray([.. kept]);
    }
}
