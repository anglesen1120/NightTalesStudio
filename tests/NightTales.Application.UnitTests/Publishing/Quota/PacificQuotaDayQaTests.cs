using System.Globalization;
using NightTales.Application.Publishing.Quota;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Publishing.Quota;

/// <summary>QA: the exact Pacific-midnight instants on both sides of each DST offset.</summary>
public sealed class PacificQuotaDayQaTests
{
    [Theory]
    [InlineData("2026-02-19T07:59:59.999Z", "2026-02-18")]
    [InlineData("2026-02-19T08:00:00Z", "2026-02-19")]
    [InlineData("2026-06-15T06:59:59.999Z", "2026-06-14")]
    [InlineData("2026-06-15T07:00:00Z", "2026-06-15")]
    public void PacificQuotaDay_Day_AtExactMidnightBoundary(string utc, string expectedDay)
    {
        PacificQuotaDay.Day(Parse(utc)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture).ShouldBe(expectedDay);
    }

    [Theory]
    [InlineData("2026-02-19T07:59:59.999Z", "2026-02-19T08:00:00Z")]
    [InlineData("2026-02-19T08:00:00Z", "2026-02-20T08:00:00Z")]
    [InlineData("2026-06-15T06:59:59.999Z", "2026-06-15T07:00:00Z")]
    [InlineData("2026-06-15T07:00:00Z", "2026-06-16T07:00:00Z")]
    public void PacificQuotaDay_NextResetAt_IsStrictlyAfterNow_AndAtNextPacificMidnight(string utc, string expectedReset)
    {
        var now = Parse(utc);
        var reset = PacificQuotaDay.NextResetAt(now);

        reset.ShouldBe(Parse(expectedReset));
        (reset > now).ShouldBeTrue();
        (reset - now).TotalSeconds.ShouldBeLessThanOrEqualTo(86400);
    }

    private static DateTimeOffset Parse(string utc) =>
        DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}
