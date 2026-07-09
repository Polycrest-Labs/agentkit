using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentKit.Logging;

/// <summary>Appends records as JSON lines to <c>{dir}/{yyyy-MM-dd}/{feature}.jsonl</c> — the local-dev
/// mirror of the deployed blob sink's layout.</summary>
public sealed class JsonlFileSink(string directory) : ICompletionSink
{
    /// <summary>The JSONL record shape (camelCase, string enums, nulls omitted) — shared by every
    /// line-oriented sink, including hosts' own (e.g. the web app's blob sink).</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Lock _gate = new();

    public void Record(CompletionRecord record)
    {
        var day = record.Ts.UtcDateTime.ToString("yyyy-MM-dd");
        var feature = Sanitize(string.IsNullOrWhiteSpace(record.Feature) ? "unknown" : record.Feature);
        var dir = Path.Combine(directory, day);
        var line = JsonSerializer.Serialize(record, Json);
        lock (_gate)
        {
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, feature + ".jsonl"), line + Environment.NewLine);
        }
    }

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
}
