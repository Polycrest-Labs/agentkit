namespace AgentKit;

/// <summary>Pulls the JSON payload out of a model's free-text reply — tolerating code fences and
/// surrounding prose, preferring a bare array but falling back to the outermost object. Moved verbatim
/// from web's <c>FoundryBookingExtractor.ExtractJson</c> so every one-shot JSON consumer shares it.</summary>
public static class JsonExtraction
{
    public static string? ExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = t.IndexOf('\n');
            if (firstNewline >= 0)
            {
                t = t[(firstNewline + 1)..];
            }
            if (t.EndsWith("```", StringComparison.Ordinal))
            {
                t = t[..^3];
            }
            t = t.Trim();
        }

        var lb = t.IndexOf('[');
        var rb = t.LastIndexOf(']');
        var lc = t.IndexOf('{');
        var rc = t.LastIndexOf('}');
        if (lb >= 0 && rb > lb && (lc < 0 || lb < lc))
        {
            return t[lb..(rb + 1)]; // a bare array appears first
        }
        if (lc >= 0 && rc > lc)
        {
            return t[lc..(rc + 1)]; // an object, possibly wrapping the array
        }
        if (lb >= 0 && rb > lb)
        {
            return t[lb..(rb + 1)];
        }
        return null;
    }
}
