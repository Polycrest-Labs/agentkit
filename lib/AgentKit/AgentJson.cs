using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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
        // Model-authored JSON routinely carries a trailing comma or a stray // comment; tolerate both
        // rather than fail the whole extraction (Web defaults already allow reading numbers from strings).
        o.AllowTrailingCommas = true;
        o.ReadCommentHandling = JsonCommentHandling.Skip;
        o.Converters.Add(new TolerantEnumConverterFactory());
        o.Converters.Add(new TolerantStringConverter());
        o.Converters.Add(new TolerantDateTimeConverterFactory());
        // Out-of-range / non-numeric scalars in a numeric field coerce to null/0 instead of throwing —
        // an OCR'd 50-digit "total" or a "$" -prefixed price no longer fails the completion.
        o.Converters.Add(new SafeNullableDecimalConverter());
        o.Converters.Add(new SafeDecimalConverter());
        o.Converters.Add(new SafeNullableIntConverter());
        o.Converters.Add(new SafeIntConverter());
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

/// <summary>
/// Reads model-authored dates in whatever format the model chose — ISO 8601 first, then the broad set of
/// human/OCR formats receipt extractions actually emit: US ("05/31/2026", "May 31, 2026"), European
/// dotted/day-first ("19.08.2025", "31/05/2025 20:11"), localized ("… Uhr"), "at"-joined, and
/// timezone-suffixed forms — instead of failing the whole completion on a strict-ISO mismatch. This
/// restores the multi-format tolerance of the pre-AgentKit <c>core.AI.NormalizationDateParser</c> chain
/// the receipt pipeline used to run behind. Unparseable text coerces to null (nullable) or default
/// (non-nullable), matching the swallow-don't-throw philosophy of <see cref="TolerantEnumConverterFactory"/>.
/// Writes round-trip "o" format.
/// </summary>
internal sealed partial class TolerantDateTimeConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert == typeof(DateTime) || typeToConvert == typeof(DateTime?);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        typeToConvert == typeof(DateTime)
            ? new TolerantDateTimeConverter()
            : new TolerantNullableDateTimeConverter();

    // ISO is tried first (via TryGetDateTime); this explicit set follows. The lower block restores the
    // legacy NormalizationDateParser formats — a strict US/ISO-only read silently dropped these (→ null
    // Date) or mis-parsed a day-first date month-first ("06/07/2025 20:11" → June 7 instead of July 6),
    // corrupting the stored receipt date. Day-first slash forms are intentionally biased day-first here,
    // exactly as the legacy parser was, to match what production has always accepted.
    private static readonly string[] Formats =
    [
        // Date-only, US/ISO
        "yyyy-MM-dd", "MM/dd/yyyy", "M/d/yyyy", "MM-dd-yyyy", "yyyy/MM/dd", "yyyyMMdd",
        "MMMM d, yyyy", "MMM d, yyyy", "d MMMM yyyy", "d MMM yyyy", "MM/dd/yy", "M/d/yy",
        // Legacy multi-format (day-first slashes/dots, OCR meridiem, "at", timezone literal)
        "MMMddyy hh:mmtt",                     // Feb24'17 03:28PM (apostrophes stripped first)
        "dd-MMM-yyyy h:mm:sstt",               // 02-Dec-2024 5:00:21P → …PM
        "dd.MM.yyyy HH:mm",                    // 31.05.2023 20:50
        "dd/MM/yyyy HH:mm",                    // 31/05/2025 20:11
        "dd/MM/yyyy HH:mm:ss",                 // 18/09/2025 11:50:22
        "dd/MM/yyyy HH.mm",                    // 01/06/2025 18.41
        "dd/MMM/yyyy HH:mm:ss",                // 12/sep/2025 16:43:02 (after sept→sep)
        "dd.MM.yyyy HH:mm:ss",                 // 27.08.2025 12:38:32
        "dd/MM/yy HH:mm",                      // 17/03/23 08:10
        "yyyy-MM-dd 'at' h.mm.ss tt",          // 2025-08-20 at 8.55.19 AM
        "yyyy-MM-dd 'at' h.mm tt",             // 2025-08-20 at 8.55 AM
        "dd-MM-yyyy HH:mm",                    // 20-08-2025 16:01
        "dd.MM.yyyy",                          // 19.08.2025
        "MMMM d, yyyy 'at' hh:mm:ss tt 'PDT'", // July 3, 2024 at 11:00:14 AM PDT
        "MMMM d, yyyy 'at' hh:mm:ss tt",       // …without the tz literal
    ];

    internal static DateTime? ParseTolerant(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            reader.Skip();
            return null;
        }
        if (reader.TryGetDateTime(out var iso))
        {
            return iso;
        }
        var text = reader.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : ParseText(text);
    }

    // Normalize the OCR/localized quirks the legacy parser handled, then match the explicit formats, then a
    // last-resort invariant parse. Self-contained so AgentKit keeps no dependency on core.
    private static DateTime? ParseText(string raw)
    {
        var text = UhrSuffix().Replace(raw.Trim(), string.Empty)  // strip a trailing " Uhr" (German receipts)
            .Replace("'", string.Empty);                          // Feb24'17 → Feb2417
        text = SeptToken().Replace(text, "sep");                  // 12/sept/2025 → 12/sep/2025
        var paren = text.IndexOf('(');
        if (paren > 0)
        {
            text = text[..paren].Trim();                          // "19.08.2025 (See note)" → "19.08.2025"
        }
        if (SingleLetterMeridiem().IsMatch(text))
        {
            text += "M";                                          // "5:00:21P" → "5:00:21PM"
        }

        if (DateTime.TryParseExact(text, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
        {
            return exact;
        }
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var loose) ? loose : null;
    }

    [GeneratedRegex(@"\s+Uhr$", RegexOptions.IgnoreCase)]
    private static partial Regex UhrSuffix();

    [GeneratedRegex(@"\bsept\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeptToken();

    [GeneratedRegex("[0-9][APap]$")]
    private static partial Regex SingleLetterMeridiem();

    private sealed class TolerantNullableDateTimeConverter : JsonConverter<DateTime?>
    {
        public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            ParseTolerant(ref reader);

        public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStringValue(value.Value.ToString("o"));
            }
        }
    }

    private sealed class TolerantDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            ParseTolerant(ref reader) ?? default;

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString("o"));
    }
}

/// <summary>
/// Numeric fields coerce instead of throwing: a value that overflows the target type (an OCR'd 50-digit
/// "total") or an unparseable numeric string reads as null (nullable) or 0/default rather than faulting the
/// whole completion; a quoted number is accepted too. Ports the deleted <c>core.AI.ForgivingJsonParsing</c>
/// "safe" converters into AgentKit, with the same swallow-don't-throw posture as the tolerant
/// enum/string/date converters. Only applies to <see cref="AgentJson.Options"/> (model-authored payloads).
/// </summary>
internal sealed class SafeNullableDecimalConverter : JsonConverter<decimal?>
{
    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number:
                return reader.TryGetDecimal(out var n) ? n : null;
            case JsonTokenType.String:
                return decimal.TryParse(reader.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var s) ? s : null;
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value.HasValue) { writer.WriteNumberValue(value.Value); }
        else { writer.WriteNullValue(); }
    }
}

internal sealed class SafeDecimalConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetDecimal(out var n) ? n : 0m;
            case JsonTokenType.String:
                return decimal.TryParse(reader.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var s) ? s : 0m;
            default:
                reader.Skip();
                return 0m;
        }
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}

internal sealed class SafeNullableIntConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var n) ? n : null;
            case JsonTokenType.String:
                return int.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null;
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value.HasValue) { writer.WriteNumberValue(value.Value); }
        else { writer.WriteNullValue(); }
    }
}

internal sealed class SafeIntConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var n) ? n : 0;
            case JsonTokenType.String:
                return int.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
            default:
                reader.Skip();
                return 0;
        }
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}
