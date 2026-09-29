using NightTales.Domain.Common;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Harness;

/// <summary>
/// <see cref="FakeClock"/> (09 README D3, D13) is a pure double, so this is a unit test: integration
/// tests move time through it and never sleep.
/// </summary>
public sealed class FakeClockTests
{
    [Fact]
    public void FakeClock_AdvanceAndSet_ChangeUtcNow()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));

        clock.UtcNow.ShouldBe(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));

        clock.Advance(TimeSpan.FromMinutes(90));
        clock.UtcNow.ShouldBe(new DateTimeOffset(2026, 3, 1, 13, 30, 0, TimeSpan.Zero));

        var pinned = new DateTimeOffset(2027, 1, 2, 3, 4, 5, TimeSpan.Zero);
        clock.Set(pinned);
        clock.UtcNow.ShouldBe(pinned);
    }

    [Fact]
    public void FakeClock_DefaultStart_IsUtc()
    {
        var clock = new FakeClock();

        clock.UtcNow.ShouldBe(FakeClock.DefaultStart);
        FakeClock.DefaultStart.Offset.ShouldBe(TimeSpan.Zero);
    }
}
