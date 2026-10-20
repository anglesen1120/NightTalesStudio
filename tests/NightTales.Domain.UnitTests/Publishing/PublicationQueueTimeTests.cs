using NightTales.Domain.Publishing;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Publishing;

/// <summary>
/// <see cref="PublicationQueueTime.For"/> — the one place 06 README D12's queue time is spelled (YR3): the owner's
/// <c>publishAt</c> minus the lead time, "as soon as possible" when they asked for no particular time, and never an
/// instant in the past.
/// </summary>
/// <remarks>
/// Pure unit tests over a pure function: no clock, no options, no database. The caller
/// (06.2.3-T2's <c>PublicationScheduledForUploadHandler</c>) supplies all three arguments; the consent command of
/// this folder does not call it at all, because the Api never enqueues (YR3).
/// </remarks>
public sealed class PublicationQueueTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    /// <summary><c>YouTubeOptions.PublishLeadMinutes</c> as D11 states it.</summary>
    private const int LeadMinutes = 120;

    [Fact]
    public void For_PublishAtSet_ReturnsPublishAtMinusLead()
    {
        var publishAt = Now.AddHours(6);

        var due = PublicationQueueTime.For(publishAt, LeadMinutes, Now);

        // D12: the upload is queued two hours before the owner wants the video live, not at the live moment.
        due.ShouldBe(publishAt.AddMinutes(-LeadMinutes));
        due.ShouldBe(Now.AddHours(4));
    }

    [Fact]
    public void For_PublishAtMinusLeadExactlyNow_ReturnsNow()
    {
        // The boundary of the clamp: an instant that lies exactly on `now` is neither late nor clamped away.
        var publishAt = Now.AddMinutes(LeadMinutes);

        PublicationQueueTime.For(publishAt, LeadMinutes, Now).ShouldBe(Now);
    }

    [Fact]
    public void For_PublishAtNull_ReturnsNow()
    {
        // D12: no `publishAt` means "as soon as it is processed", so only the clock decides.
        PublicationQueueTime.For(publishAt: null, LeadMinutes, Now).ShouldBe(Now);
    }

    [Fact]
    public void For_PastPublishAt_ClampsToNow()
    {
        // The owner's time is already inside the lead window (or behind the clock): queueing for the past would
        // hand the scheduler an instant that has passed, so the clamp answers `now`.
        var publishAt = Now.AddMinutes(30);

        PublicationQueueTime.For(publishAt, LeadMinutes, Now).ShouldBe(Now);
    }

    [Fact]
    public void For_NonZeroOffsetArguments_ReturnsTheSameInstantAtOffsetZero()
    {
        // Both callers of D12 may hand in a local rendering: the owner's `publishAt` arrives in whatever offset the
        // client sent, and the Worker's clock carries the host's zone. The answer must be one instant expressed in
        // UTC, so every comparison downstream (and the persisted `next_attempt_at`) speaks the same offset.
        var publishAt = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(7));
        var now = new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.FromHours(7));

        var due = PublicationQueueTime.For(publishAt, LeadMinutes, now);

        due.Offset.ShouldBe(TimeSpan.Zero);
        due.ShouldBe(new DateTimeOffset(2026, 10, 20, 3, 0, 0, TimeSpan.Zero));
        due.UtcDateTime.ShouldBe(publishAt.UtcDateTime.AddMinutes(-LeadMinutes));
    }

    [Fact]
    public void For_PublishAtNullWithNonZeroOffsetNow_ReturnsNowAtOffsetZero()
    {
        // The "as soon as it is processed" branch converts too: `now` is the only answer, and it is still UTC.
        var now = new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.FromHours(7));

        var due = PublicationQueueTime.For(publishAt: null, LeadMinutes, now);

        due.Offset.ShouldBe(TimeSpan.Zero);
        due.ShouldBe(now.ToUniversalTime());
    }
}
