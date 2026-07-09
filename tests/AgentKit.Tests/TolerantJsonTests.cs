namespace AgentKit.Tests;

/// <summary>AgentJson tolerates the malformed-but-common JSON that extraction models emit — a trailing
/// comma, a stray // comment, a quoted number, or a numeric literal that overflows the target type —
/// instead of failing the whole completion. Restores the leniency of the deleted
/// <c>core.AI.ForgivingJsonParsing</c> path the receipt pipeline used to run behind.</summary>
public sealed class TolerantJsonTests
{
    private sealed record Money(decimal? TotalPrice);
    private sealed record Item(string Name, decimal? Price, int? Quantity);

    [Fact]
    public void TrailingComma_IsTolerated()
    {
        var m = AgentJson.Deserialize<Money>("{\"totalPrice\": 12.50,}");
        Assert.Equal(12.50m, m.TotalPrice);
    }

    [Fact]
    public void LineComment_IsTolerated()
    {
        var m = AgentJson.Deserialize<Money>("{\n  // the receipt total\n  \"totalPrice\": 12.50\n}");
        Assert.Equal(12.50m, m.TotalPrice);
    }

    [Fact]
    public void OutOfRangeNumericLiteral_CoercesToNull_NeverThrows()
    {
        // A 60-digit OCR'd "total" overflows decimal; the strict post-cutover path threw JsonException here
        // and failed the entire extraction.
        var big = new string('9', 60);
        var m = AgentJson.Deserialize<Money>($"{{\"totalPrice\": {big}}}");
        Assert.Null(m.TotalPrice);
    }

    [Fact]
    public void QuotedNumbers_AreParsed()
    {
        var i = AgentJson.Deserialize<Item>("{\"name\":\"Widget\",\"price\":\"6.50\",\"quantity\":\"2\"}");
        Assert.Equal(6.50m, i.Price);
        Assert.Equal(2, i.Quantity);
    }

    [Fact]
    public void UnparseableNumericString_CoercesToNull()
    {
        var i = AgentJson.Deserialize<Item>("{\"name\":\"Widget\",\"price\":\"n/a\",\"quantity\":null}");
        Assert.Null(i.Price);
        Assert.Null(i.Quantity);
    }
}
