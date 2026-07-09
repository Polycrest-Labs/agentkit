namespace AgentKit.Tests;

/// <summary>AgentJson's tolerant date reads: models emit dates in whatever format they like; a strict
/// ISO-only parse would fail whole completions that production has always accepted.</summary>
public sealed class TolerantDateTimeTests
{
    private sealed record Dto(DateTime? Date);
    private sealed record RequiredDto(DateTime Date);

    [Theory]
    [InlineData("\"2026-05-31\"", 2026, 5, 31)]
    [InlineData("\"2026-05-31T00:00:00\"", 2026, 5, 31)]
    [InlineData("\"05/31/2026\"", 2026, 5, 31)]
    [InlineData("\"5/31/2026\"", 2026, 5, 31)]
    [InlineData("\"2026/05/31\"", 2026, 5, 31)]
    [InlineData("\"May 31, 2026\"", 2026, 5, 31)]
    [InlineData("\"31 May 2026\"", 2026, 5, 31)]
    [InlineData("\"05-31-2026\"", 2026, 5, 31)]
    // Legacy multi-format tolerance restored from core.AI.NormalizationDateParser — the pre-AgentKit
    // receipt path accepted all of these; a strict US/ISO-only read regressed them to null.
    [InlineData("\"19.08.2025\"", 2025, 8, 19)]                        // European dotted, day-first
    [InlineData("\"31/05/2025 20:11\"", 2025, 5, 31)]                  // day-first slash + time
    [InlineData("\"27.08.2025 12:38:32\"", 2025, 8, 27)]               // dotted + time
    [InlineData("\"20-08-2025 16:01\"", 2025, 8, 20)]                  // day-first dash + time
    [InlineData("\"02-Dec-2024 5:00:21P\"", 2024, 12, 2)]             // OCR single-letter meridiem
    [InlineData("\"Feb24'17 03:28PM\"", 2017, 2, 24)]                 // compressed w/ apostrophe
    [InlineData("\"12/sept/2025 16:43:02\"", 2025, 9, 12)]            // 'sept' month token
    [InlineData("\"2025-08-20 at 8.55 AM\"", 2025, 8, 20)]            // 'at'-joined
    [InlineData("\"19.08.2025 20:50 Uhr\"", 2025, 8, 19)]             // localized 'Uhr' suffix
    [InlineData("\"July 3, 2024 at 11:00:14 AM PDT\"", 2024, 7, 3)]   // timezone literal
    public void CommonModelDateFormats_Parse(string dateJson, int year, int month, int day)
    {
        var dto = AgentJson.Deserialize<Dto>($"{{\"date\":{dateJson}}}");
        Assert.Equal(new DateTime(year, month, day), dto.Date!.Value.Date);
    }

    [Fact]
    public void DayFirstSlashWithTime_IsReadDayFirst_NotMonthFirst()
    {
        // Regression guard: the AgentKit cutover briefly read this month-first (June 7). The legacy parser
        // read the "dd/MM/yyyy HH:mm" form day-first (July 6); this converter must too.
        var dto = AgentJson.Deserialize<Dto>("{\"date\":\"06/07/2025 20:11\"}");
        Assert.Equal(new DateTime(2025, 7, 6), dto.Date!.Value.Date);
    }

    [Fact]
    public void IsoWithOffset_KeepsItsInstant()
    {
        var dto = AgentJson.Deserialize<Dto>("{\"date\":\"2026-05-31T00:00:00+00:00\"}");
        Assert.Equal(new DateTime(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc), dto.Date!.Value.ToUniversalTime());
    }

    [Theory]
    [InlineData("\"not a date at all\"")]
    [InlineData("\"\"")]
    [InlineData("null")]
    [InlineData("12345")]
    public void UnparseableOrNull_CoercesToNull_NeverThrows(string dateJson)
    {
        var dto = AgentJson.Deserialize<Dto>($"{{\"date\":{dateJson}}}");
        Assert.Null(dto.Date);
    }

    [Fact]
    public void NonNullableDateTime_CoercesToDefault_OnGarbage()
    {
        var dto = AgentJson.Deserialize<RequiredDto>("{\"date\":\"garbage\"}");
        Assert.Equal(default, dto.Date);
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        var json = AgentJson.Serialize(new Dto(new DateTime(2026, 5, 31)));
        Assert.Equal(new DateTime(2026, 5, 31), AgentJson.Deserialize<Dto>(json).Date);
    }
}
