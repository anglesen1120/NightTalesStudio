using NightTales.Api.IntegrationTests.Harness;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Communication;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Email;

/// <summary>
/// <c>IEmailDeliveryStatus</c> (09 README BD9/D10, 09.2.6-T3): the state a caller that must know the
/// outcome polls, and the 404 another user's message answers.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Hangfire server")]
public sealed class EmailDeliveryStatusTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task GetAsync_OtherUsersMessage_Is404()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var owner = await Database.CreateUserBuilder().CreateAsync(ct: ct);
        var other = await Database.CreateUserBuilder().CreateAsync(ct: ct);
        await using var harness = await EmailHarness.StartAsync(Database, clock, ct);

        var id = await harness.QueueAsync(
            owner.Id,
            "password_changed",
            Language.Vi,
            EmailHarness.Values(("displayName", "A"), ("occurredAt", "2026-01-01 00:00")),
            ct);

        var mine = await harness.ReadStatusAsync(owner.Id, id, ct);
        mine.IsError.ShouldBeFalse();
        mine.Value.Id.ShouldBe(id);
        mine.Value.Status.ShouldBe(EmailStatus.Queued);
        mine.Value.Attempts.ShouldBe(0);
        mine.Value.LastErrorCode.ShouldBeNull();
        mine.Value.SentAt.ShouldBeNull();
        mine.Value.CreatedAt.ShouldBe(clock.UtcNow);

        // 09 README D10: the tenant filter answers "another user's row" and "no such row" identically.
        var theirs = await harness.ReadStatusAsync(other.Id, id, ct);
        theirs.IsError.ShouldBeTrue();
        theirs.FirstError.Code.ShouldBe(ErrorCodes.NotFound);

        var unknown = await harness.ReadStatusAsync(owner.Id, Guid.CreateVersion7(), ct);
        unknown.IsError.ShouldBeTrue();
        unknown.FirstError.Code.ShouldBe(ErrorCodes.NotFound);

        // A class shares one database across its facts (09 README D13) and DeliverAsync claims every due
        // outbox row in it, so the message queued above must be delivered before this fact ends: it is the
        // sibling fact that asserts delivery counts, and xunit v3 runs the facts in the order its
        // DefaultTestCaseOrderer derives from the test cases (unpredictable, stable per test assembly), so
        // which fact runs first is not ours to choose.
        await harness.DeliverAsync(ct);
    }

    [Fact]
    public async Task GetAsync_AfterTheSendAttempt_ReflectsTheCommittedRow()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var owner = await Database.CreateUserBuilder().CreateAsync(ct: ct);
        await using var harness = await EmailHarness.StartAsync(Database, clock, ct);

        var id = await harness.QueueAsync(
            owner.Id,
            "password_changed",
            Language.Vi,
            EmailHarness.Values(("displayName", "A"), ("occurredAt", "2026-01-01 00:00")),
            ct);

        (await harness.DeliverAsync(ct)).ShouldBe(1);

        var delivery = await harness.ReadStatusAsync(owner.Id, id, ct);
        delivery.IsError.ShouldBeFalse();
        delivery.Value.Status.ShouldBe(EmailStatus.Sent);
        delivery.Value.Attempts.ShouldBe(1);
        delivery.Value.SentAt.ShouldBe(clock.UtcNow);
        delivery.Value.LastErrorCode.ShouldBeNull();
    }
}
