using System.Globalization;
using NightTales.Application.Publishing.Quota;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Publishing.Quota;

/// <summary>
/// The Pacific quota day (06.3.1-T1, README D9): the day boundary is local midnight in America/Los_Angeles, so it lands
/// on 07:00Z in PDT and 08:00Z in PST, and it moves across the March and November transitions.
/// </summary>
public sealed class PacificQuotaDayTests
{
    [Theory]
    [InlineData("2026-03-08T07:59:59Z", "2026-03-07")] // 23:59:59 PST on 03-07, the day before spring forward
    [InlineData("2026-03-08T08:00:00Z", "2026-03-08")] // 00:00 PST on 03-08
    [InlineData("2026-11-01T06:59:59Z", "2026-10-31")] // 23:59:59 PDT on 10-31, the day before fall back
    [InlineData("2026-11-01T07:00:00Z", "2026-11-01")] // 00:00 PDT on 11-01
    public void PacificQuotaDay_Day_AcrossDstTransitions(string utc, string expectedDay)
    {
        PacificQuotaDay.Day(Parse(utc)).ShouldBe(DateOnly.Parse(expectedDay, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("2026-03-07T12:00:00Z", "2026-03-08T08:00:00Z")] // PST until spring forward
    [InlineData("2026-03-08T12:00:00Z", "2026-03-09T07:00:00Z")] // PDT after spring forward, never a fixed -07:00 offset
    [InlineData("2026-10-31T12:00:00Z", "2026-11-01T07:00:00Z")] // PDT until fall back
    [InlineData("2026-11-01T12:00:00Z", "2026-11-02T08:00:00Z")] // PST after fall back
    public void PacificQuotaDay_NextResetAt_IsStrictlyFutureAndDstCorrect(string utc, string expectedReset)
    {
        var now = Parse(utc);

        var reset = PacificQuotaDay.NextResetAt(now);

        reset.ShouldBe(Parse(expectedReset));
        reset.ShouldBeGreaterThan(now);
    }

    [Theory]
    [InlineData("2026-03-08T07:59:59Z", "2026-03-08T08:00:00Z")] // one second before the spring-forward day boundary
    [InlineData("2026-03-08T08:00:00Z", "2026-03-09T07:00:00Z")] // exactly on the boundary: the next day's reset
    [InlineData("2026-11-01T06:59:59Z", "2026-11-01T07:00:00Z")] // one second before the fall-back day boundary
    [InlineData("2026-11-01T07:00:00Z", "2026-11-02T08:00:00Z")] // exactly on the boundary: the next day's reset
    public void PacificQuotaDay_NextResetAt_IsStrictlyFutureAndDstCorrect_AroundBoundaries(string utc, string expectedReset)
    {
        var now = Parse(utc);

        PacificQuotaDay.NextResetAt(now).ShouldBe(Parse(expectedReset));
        PacificQuotaDay.NextResetAt(now).ShouldBeGreaterThan(now);
    }

    private static DateTimeOffset Parse(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}
