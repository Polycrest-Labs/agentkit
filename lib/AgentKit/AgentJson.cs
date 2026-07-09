using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentKit;

/// <summary>Shared JSON settings for agent tool I/O and proposal request capture/replay — camelCase +
/// string enums, matching the API's controller serialization so a replayed proposal is identical to a
/// website write. Enum reads are tolerant (see <see cref="TolerantEnumConverterFactory"/>) because the
/// model authors these payloads and invents values the strict converter would reject.</summary>
public static class AgentJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new TolerantEnumConverterFactory());
        o.Converters.Add(new TolerantStringConverter());
        return o;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;

    /// <summary>Overlays the model's partial <paramref name="patchJson"/> onto a full <paramref name="baselineJson"/>
    /// (top-level shallow merge), so an agent "update" only changes the fields it actually supplied and never
    /// blanks the rest. <em>Null</em> patch values are skipped — the assistant can never clear a field this way
    /// (clearing is a website-form action). Returns the baseline unchanged if the patch isn't a JSON object.</summary>
    public static string Merge(string baselineJson, string patchJson)
    {
        var baseline = JsonNode.Parse(baselineJson)?.AsObject() ?? new JsonObject();
        if (JsonNode.Parse(string.IsNullOrWhiteSpace(patchJson) ? "{}" : patchJson) is not JsonObject patch)
        {
            return baselineJson;
        }

        foreach (var (key, value) in patch)
        {
            if (value is null)
            {
                continue; // omitted or explicit null → keep the current value
            }
            baseline[key] = value.DeepClone();
        }

        return baseline.ToJsonString(Options);
    }

    /// <summary>The camelCase field names of <typeparamref name="TSave"/> — for actionable tool errors.</summary>
    public static IReadOnlyList<string> KnownPatchKeys<TSave>() => PatchShape<TSave>.Keys;

    /// <summary>The patchable field names of <typeparamref name="TSave"/> for tool descriptions and error
    /// text: every serialized property minus the system-managed <paramref name="immutableKeys"/>.</summary>
    public static string PatchFieldList<TSave>(params string[] immutableKeys) =>
        string.Join(", ", PatchShape<TSave>.Keys.Where(k => !immutableKeys.Contains(k, StringComparer.OrdinalIgnoreCase)));

    /// <summary>
    /// Validate a model-authored patch against <typeparamref name="TSave"/> BEFORE any merge or store read,
    /// and fail loudly on everything the pipeline would otherwise mishandle silently:
    /// unknown keys (the serializer would drop them), system-managed <paramref name="immutableKeys"/>
    /// (the tool would force them back or they must not be model-writable), and invented enum values
    /// (the tolerant converter would coerce them to the enum default and apply an unintended change).
    /// The thrown message names the valid fields/values so the model can correct and retry in-turn.
    /// </summary>
    public static void ValidatePatch<TSave>(string what, string patchJson, string[]? immutableKeys = null, string? hint = null)
    {
        if (string.IsNullOrWhiteSpace(patchJson) || JsonNode.Parse(patchJson) is not JsonObject patch)
        {
            return;
        }

        immutableKeys ??= [];
        var suffix = $" Recognized fields: {PatchFieldList<TSave>(immutableKeys)}.{(hint is null ? "" : $" {hint}")}";

        var unknown = patch.Select(kv => kv.Key).Where(k => !PatchShape<TSave>.Set.Contains(k)).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"Unrecognized field(s) on the {what} patch: {string.Join(", ", unknown)} — they would be dropped, so nothing was proposed.{suffix}");
        }

        var immutable = patch.Select(kv => kv.Key).Where(k => immutableKeys.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        if (immutable.Count > 0)
        {
            throw new InvalidOperationException(
                $"System-managed field(s) on the {what} patch: {string.Join(", ", immutable)} — they are set automatically and cannot be changed here. Remove them and retry.{suffix}");
        }

        foreach (var (key, value) in patch)
        {
            if (value is not JsonValue jv || !jv.TryGetValue<string>(out var text)
                || PatchShape<TSave>.EnumTypes.GetValueOrDefault(key) is not { } enumType)
            {
                continue;
            }
            if (!Enum.TryParse(enumType, text, ignoreCase: true, out _))
            {
                throw new InvalidOperationException(
                    $"'{text}' is not a valid {what} {key} — valid values: {string.Join(", ", Enum.GetNames(enumType))}. Nothing was proposed.");
            }
        }
    }

    /// <summary>Top-level keys whose values differ between two serialized requests of the same DTO type
    /// (both sides must come from <see cref="Serialize{T}"/> so formatting is normalized). This is the
    /// change detector the no-op guard trusts — the human-readable diff rows are cosmetic on top.</summary>
    public static IReadOnlyList<string> ChangedTopLevelKeys(string beforeJson, string afterJson)
    {
        var before = JsonNode.Parse(beforeJson)?.AsObject() ?? new JsonObject();
        var after = JsonNode.Parse(afterJson)?.AsObject() ?? new JsonObject();
        return before.Select(kv => kv.Key).Union(after.Select(kv => kv.Key), StringComparer.Ordinal)
            .Where(k => !JsonNode.DeepEquals(before[k], after[k]))
            .ToList();
    }

    /// <summary>A compact display value for a top-level JSON field (generic diff rows).</summary>
    public static string DisplayValue(string requestJson, string key)
    {
        var node = JsonNode.Parse(requestJson)?.AsObject()?[key];
        var text = node switch
        {
            null => "-",
            JsonValue v => v.ToString(),
            _ => node.ToJsonString(Options),
        };
        return Truncate(string.IsNullOrWhiteSpace(text) ? "-" : text, 80);
    }

    /// <summary>Cap at <paramref name="max"/> chars with a trailing ellipsis, never splitting a surrogate
    /// pair (same semantics as web's <c>AgentText.Truncate</c>, inlined so the library stays standalone).</summary>
    private static string Truncate(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }
        var cut = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..cut].TrimEnd() + "…";
    }

    /// <summary>Per-DTO reflection cache: serialized key names, a case-insensitive set (matching the Web
    /// serializer's tolerant property binding), and enum-typed properties for strict patch validation.</summary>
    private static class PatchShape<TSave>
    {
        internal static readonly IReadOnlyList<string> Keys =
            typeof(TSave).GetProperties()
                .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
                .ToList();

        internal static readonly HashSet<string> Set = Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        internal static readonly IReadOnlyDictionary<string, Type> EnumTypes =
            typeof(TSave).GetProperties()
                .Select(p => (Name: JsonNamingPolicy.CamelCase.ConvertName(p.Name), Type: Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType))
                .Where(p => p.Type.IsEnum)
                .ToDictionary(p => p.Name, p => p.Type, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Writes enums as their string names (so a captured proposal replays identically to a website write), but
/// on read tolerates a value the model invented — an unrecognized string/number falls back to the enum's
/// default rather than throwing. Without this, a model-authored itinerary using e.g. <c>"kind":"Sightseeing"</c>
/// (not a valid <c>ItemKind</c>) would fail the entire tool call.
/// </summary>
internal sealed class TolerantEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(TolerantEnumConverter<>).MakeGenericType(typeToConvert))!;

    private sealed class TolerantEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    return Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var parsed) ? parsed : default;
                case JsonTokenType.Number when reader.TryGetInt64(out var n):
                    var value = (T)Enum.ToObject(typeof(T), n);
                    return Enum.IsDefined(value) ? value : default;
                default:
                    reader.Skip();
                    return default;
            }
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}

/// <summary>
/// Reads any JSON scalar as a string, so a model that emits a number or boolean where the DTO wants a
/// string is coerced instead of failing the whole tool call — a number can always be a string. The most
/// common case is a free-form dictionary value like <c>typeDetails</c> (<see cref="System.Collections.Generic.IReadOnlyDictionary{TKey,TValue}"/>
/// of string→string), where the model writes <c>{"nights":6}</c>. Same tolerance philosophy as
/// <see cref="TolerantEnumConverterFactory"/>. Only applies to <see cref="AgentJson.Options"/> (agent tool
/// I/O), never the API's controller serialization. STJ handles JSON <c>null</c> itself for reference types,
/// so this is only reached for non-null tokens.
/// </summary>
internal sealed class TolerantStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString()!;
            case JsonTokenType.Number:
                // Preserve the exact numeric literal (6, 6.5, 100000) rather than reformatting via a numeric type.
                return reader.HasValueSequence
                    ? Encoding.UTF8.GetString(System.Buffers.BuffersExtensions.ToArray(reader.ValueSequence))
                    : Encoding.UTF8.GetString(reader.ValueSpan);
            case JsonTokenType.True:
                return "true";
            case JsonTokenType.False:
                return "false";
            default:
                // An object/array where a string was expected — keep the raw JSON instead of throwing.
                using (var doc = JsonDocument.ParseValue(ref reader))
                {
                    return doc.RootElement.GetRawText();
                }
        }
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
