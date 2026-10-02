using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Infrastructure.Security;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Security;

/// <summary>
/// The bounds of 09.10.4-T1's wrong-code window (WT35): the per-user queue is capped at the threshold and
/// the per-process user dictionary at <see cref="InMemoryMfaGuessWindow.MaxTrackedUsers"/>, without losing
/// an active lockout and without a full scan on every insert.
/// </summary>
public sealed class InMemoryMfaGuessWindowTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Queue_IsBoundedByThreshold_AndTheUserStaysBlocked()
    {
        var window = new InMemoryMfaGuessWindow(NullLogger<InMemoryMfaGuessWindow>.Instance);
        var user = Guid.CreateVersion7();

        for (var index = 0; index < InMemoryMfaGuessWindow.Threshold * 100; index++)
        {
            _ = window.RegisterWrongCode(user, Start + TimeSpan.FromMilliseconds(index));
        }

        window.TrackedCodes.ShouldBe(
            InMemoryMfaGuessWindow.Threshold,
            "only the newest Threshold timestamps can decide 'blocked'; the queue keeps no more");
        window.BlockedFor(user, Start + TimeSpan.FromSeconds(1)).ShouldBe(InMemoryMfaGuessWindow.RetryAfter);
    }

    [Fact]
    public void Trip_IsReportedOncePerWindow_AndAgainAfterTheWindowCloses()
    {
        var window = new InMemoryMfaGuessWindow(NullLogger<InMemoryMfaGuessWindow>.Instance);
        var user = Guid.CreateVersion7();

        for (var index = 0; index < InMemoryMfaGuessWindow.Threshold; index++)
        {
            window.RegisterWrongCode(user, Start).ShouldBe(
                index == InMemoryMfaGuessWindow.Threshold - 1
                    ? GuessWindowCharge.Tripped
                    : GuessWindowCharge.Counted,
                "exactly the crossing call answers Tripped");
        }

        window.TryClaimTripReport(user).ShouldBeTrue();
        window.TryClaimTripReport(user).ShouldBeFalse("one row per window, however many requests see it");

        var later = Start + InMemoryMfaGuessWindow.Window + TimeSpan.FromMinutes(1);
        window.BlockedFor(user, later).ShouldBeNull("the window has closed");

        var trips = 0;
        for (var index = 0; index < InMemoryMfaGuessWindow.Threshold; index++)
        {
            if (window.RegisterWrongCode(user, later) is GuessWindowCharge.Tripped)
            {
                trips++;
            }
        }

        trips.ShouldBe(1, "a new window trips and reports again");
        window.TryClaimTripReport(user).ShouldBeTrue();
    }

    [Fact]
    public void UserCap_IsEnforced_ButAnActiveLockoutSurvives()
    {
        var window = new InMemoryMfaGuessWindow(NullLogger<InMemoryMfaGuessWindow>.Instance);

        var blocked = Guid.CreateVersion7();
        for (var index = 0; index < InMemoryMfaGuessWindow.Threshold; index++)
        {
            _ = window.RegisterWrongCode(blocked, Start);
        }

        window.BlockedFor(blocked, Start + TimeSpan.FromMinutes(1)).ShouldNotBeNull();

        // A later flood of distinct, never-blocked users reaches and passes the cap. Eviction must take
        // them before it ever considers the blocked user.
        var flood = InMemoryMfaGuessWindow.MaxTrackedUsers + (InMemoryMfaGuessWindow.EvictionBatch / 2);
        for (var index = 0; index < flood; index++)
        {
            _ = window.RegisterWrongCode(Guid.CreateVersion7(), Start + TimeSpan.FromMinutes(2));
        }

        window.TrackedUsers.ShouldBeLessThanOrEqualTo(InMemoryMfaGuessWindow.MaxTrackedUsers);
        window.BlockedFor(blocked, Start + TimeSpan.FromMinutes(3)).ShouldNotBeNull(
            "the cap must not silently drop an active lockout");

        // The cap also must not freeze the window: a fresh user is still tracked and can still trip it.
        var fresh = Guid.CreateVersion7();
        for (var index = 0; index < InMemoryMfaGuessWindow.Threshold; index++)
        {
            _ = window.RegisterWrongCode(fresh, Start + TimeSpan.FromMinutes(4));
        }

        window.BlockedFor(fresh, Start + TimeSpan.FromMinutes(5)).ShouldNotBeNull();
    }

    [Fact]
    public void Saturation_HeldByActiveLockouts_RefusesWrongCodes_AndKeepsEveryLockout()
    {
        var window = new InMemoryMfaGuessWindow(NullLogger<InMemoryMfaGuessWindow>.Instance);
        var blocked = new List<Guid>(InMemoryMfaGuessWindow.MaxTrackedUsers);

        for (var index = 0; index < InMemoryMfaGuessWindow.MaxTrackedUsers; index++)
        {
            var user = Guid.CreateVersion7();
            blocked.Add(user);
            Block(window, user);
        }

        window.TrackedUsers.ShouldBe(InMemoryMfaGuessWindow.MaxTrackedUsers);

        // The cap is held entirely by live lockouts: the new key's wrong code is refused (fail closed),
        // not counted and not admitted by evicting a lockout.
        var refused = Guid.CreateVersion7();
        window.RegisterWrongCode(refused, Start).ShouldBe(
            GuessWindowCharge.Refused,
            "an untracked key gets no unlimited guesses while the window is saturated");
        window.TrackedUsers.ShouldBe(
            InMemoryMfaGuessWindow.MaxTrackedUsers,
            "no lockout may be evicted to make room");
        window.BlockedFor(refused, Start).ShouldBeNull(
            "saturation must not block the correct-code path of an untracked key");
        window.TryClaimTripReport(refused).ShouldBeTrue(
            "the refused key never reached the threshold, so it claimed no trip report");

        foreach (var user in blocked)
        {
            window.BlockedFor(user, Start + TimeSpan.FromMinutes(1)).ShouldBe(
                InMemoryMfaGuessWindow.RetryAfter,
                "every active lockout survives saturation");
        }
    }

    [Fact]
    public void Saturation_WithFewerUnblockedThanTheBatch_EvictsOnlyTheUnblocked()
    {
        var window = new InMemoryMfaGuessWindow(NullLogger<InMemoryMfaGuessWindow>.Instance);
        var blocked = new List<Guid>(InMemoryMfaGuessWindow.MaxTrackedUsers);

        for (var index = 0; index < InMemoryMfaGuessWindow.MaxTrackedUsers - 1; index++)
        {
            var user = Guid.CreateVersion7();
            blocked.Add(user);
            Block(window, user);
        }

        // One unblocked user is the only evictable entry, and it is far fewer than the batch.
        var single = Guid.CreateVersion7();
        _ = window.RegisterWrongCode(single, Start);
        window.BlockedFor(single, Start).ShouldBeNull();

        var fresh = Guid.CreateVersion7();
        _ = window.RegisterWrongCode(fresh, Start);

        window.TrackedUsers.ShouldBeLessThanOrEqualTo(InMemoryMfaGuessWindow.MaxTrackedUsers);
        window.BlockedFor(single, Start).ShouldBeNull("the only unblocked entry made room");
        window.BlockedFor(fresh, Start).ShouldBeNull("the new key was admitted");

        foreach (var user in blocked)
        {
            window.BlockedFor(user, Start + TimeSpan.FromMinutes(1)).ShouldBe(
                InMemoryMfaGuessWindow.RetryAfter,
                "no lockout is evicted when the batch cannot be filled without one");
        }
    }

    /// <summary>Trips one user's window at <see cref="Start"/>.</summary>
    private static void Block(InMemoryMfaGuessWindow window, Guid user)
    {
        for (var code = 0; code < InMemoryMfaGuessWindow.Threshold; code++)
        {
            _ = window.RegisterWrongCode(user, Start);
        }
    }
}
