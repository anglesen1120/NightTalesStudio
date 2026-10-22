using NightTales.Application.Features.Publishing.Upload;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Publishing;

/// <summary>
/// <c>PtDay</c> (06.2.3-T2 plan §7): the retry instant of a quota reschedule is the next Pacific local midnight — the one
/// rule <c>PacificQuotaDay</c> owns, never re-derived here — plus a jitter of 0–10 minutes, and it stays inside that
/// window across both DST transitions.
/// </summary>
public sealed class PtDayTests
{
    /// <summary>The instant the integration harness drives the upload job with (2026-10-02T09:00Z).</summary>
    private static readonly DateTimeOffset HarnessNow = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    /// <summary>What the harness expects: the next Pacific midnight for that instant, in UTC.</summary>
    private static readonly DateTimeOffset HarnessMidnight = new(2026, 10, 3, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NextPtMidnight_MatchesTheHarnessClock()
    {
        // 2026-10-02T09:00Z is 02:00 PDT, so the day resets at 2026-10-03T00:00 local = 07:00Z.
        PtDay.NextPtMidnight(HarnessNow).ShouldBe(HarnessMidnight);
    }

    [Fact]
    public void NextPtMidnight_IsLosAngeles_AcrossSpringForward()
    {
        // 2026-03-08 is the spring-forward day: 12:00Z is 05:00 PDT, and the next local midnight is 07:00Z — an hour
        // earlier in UTC than a winter midnight, which is exactly what a fixed -08:00 would get wrong.
        var midnight = PtDay.NextPtMidnight(new DateTimeOffset(2026, 3, 8, 12, 0, 0, TimeSpan.Zero));

        midnight.ShouldBe(new DateTimeOffset(2026, 3, 9, 7, 0, 0, TimeSpan.Zero));
        midnight.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void NextPtMidnight_IsLosAngeles_AcrossFallBack()
    {
        // 2026-11-01 is the fall-back day and its midnight is ambiguous: the earlier instant (still PDT) is the one the
        // quota day starts at, so 2026-10-31T20:00Z (13:00 PDT) resets at 07:00Z, not 08:00Z.
        var midnight = PtDay.NextPtMidnight(new DateTimeOffset(2026, 10, 31, 20, 0, 0, TimeSpan.Zero));

        midnight.ShouldBe(new DateTimeOffset(2026, 11, 1, 7, 0, 0, TimeSpan.Zero));
        midnight.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(2026, 1, 1, 0, 0)]
    [InlineData(2026, 3, 8, 9, 59)]
    [InlineData(2026, 7, 4, 23, 59)]
    [InlineData(2026, 11, 1, 8, 0)]
    [InlineData(2026, 12, 31, 23, 59)]
    public void NextPtMidnight_IsAlwaysStrictlyAfterNow(int year, int month, int day, int hour, int minute)
    {
        var now = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero);

        PtDay.NextPtMidnight(now).ShouldBeGreaterThan(now);
    }

    [Fact]
    public void NextRetryAt_StoresTheGivenJitter()
    {
        PtDay.NextRetryAt(HarnessNow, 7).ShouldBe(HarnessMidnight.AddMinutes(7));
        PtDay.NextRetryAt(HarnessNow, 0).ShouldBe(HarnessMidnight);
        PtDay.NextRetryAt(HarnessNow, PtDay.MaxJitterMinutes).ShouldBe(HarnessMidnight.AddMinutes(10));
    }

    [Fact]
    public void NextRetryAt_JitterIsBounded()
    {
        // Both ends of the window are allowed, and a fresh draw never leaves it: the row cannot wake before the quota
        // resets, and it cannot drift minutes past the ten the plan allows.
        for (var attempt = 0; attempt < 500; attempt++)
        {
            var retryAt = PtDay.NextRetryAt(HarnessNow);

            retryAt.ShouldBeGreaterThanOrEqualTo(HarnessMidnight);
            retryAt.ShouldBeLessThanOrEqualTo(HarnessMidnight.AddMinutes(PtDay.MaxJitterMinutes));
            PtDay.IsInRetryWindow(HarnessNow, retryAt).ShouldBeTrue();
        }

        PtDay.NextJitterMinutes().ShouldBeInRange(0, PtDay.MaxJitterMinutes);
    }

    [Fact]
    public void IsInRetryWindow_RejectsAnInstantOutsideTheWindow()
    {
        PtDay.IsInRetryWindow(HarnessNow, HarnessMidnight.AddTicks(-1)).ShouldBeFalse();
        PtDay.IsInRetryWindow(HarnessNow, HarnessMidnight.AddMinutes(11)).ShouldBeFalse();
        PtDay.IsInRetryWindow(HarnessNow, HarnessNow).ShouldBeFalse();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    [InlineData(int.MaxValue)]
    public void NextRetryAt_RejectsAnOutOfRangeJitter(int jitterMinutes)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => PtDay.NextRetryAt(HarnessNow, jitterMinutes));
    }
}
