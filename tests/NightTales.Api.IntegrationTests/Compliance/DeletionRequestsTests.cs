using Microsoft.EntityFrameworkCore;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Compliance.Deletion;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Compliance;

/// <summary>
/// <c>IDeletionRequests.CreateAsync</c> (13.3.1-T1, 13 README D4/YR13) against real PostgreSQL 17: the open-request
/// upsert, per-user keying, parallel inserts, the tenant-scoped re-read and the ownership check.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DeletionRequestsTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreateAsync_SecondOpenRequest_ReturnsExistingRow_DueAtUnchanged()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var user = Guid.CreateVersion7();
        var target = Guid.CreateVersion7();

        var first = await harness.CreateAsync(user, DeletionScope.Channel, target, DeletionReason.InAppRevoke, Ct);
        harness.Clock.Advance(TimeSpan.FromDays(3));
        var second = await harness.CreateAsync(user, DeletionScope.Channel, target, DeletionReason.InAppRevoke, Ct);

        first.IsError.ShouldBeFalse();
        second.IsError.ShouldBeFalse();
        second.Value.Id.ShouldBe(first.Value.Id);
        second.Value.DueAt.ShouldBe(first.Value.DueAt);
        second.Value.RequestedAt.ShouldBe(first.Value.RequestedAt);
        (await Database.QueryStringsAsync($"SELECT count(*)::text FROM data_deletion_requests WHERE user_id = '{user}'", Ct))
            .ShouldBe(["1"]);
    }

    [Fact]
    public async Task CreateAsync_SetsDeadlinePerReason()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var user = Guid.CreateVersion7();

        var google = await harness.CreateAsync(user, DeletionScope.Channel, Guid.CreateVersion7(), DeletionReason.GoogleRevoke, Ct);
        var inApp = await harness.CreateAsync(user, DeletionScope.Channel, Guid.CreateVersion7(), DeletionReason.InAppRevoke, Ct);
        var account = await harness.CreateAsync(user, DeletionScope.Account, null, DeletionReason.AccountDelete, Ct);

        (google.Value.DueAt - google.Value.RequestedAt).ShouldBe(TimeSpan.FromDays(30));
        (inApp.Value.DueAt - inApp.Value.RequestedAt).ShouldBe(TimeSpan.FromDays(7));
        (account.Value.DueAt - account.Value.RequestedAt).ShouldBe(TimeSpan.FromDays(7));
    }

    [Fact]
    public async Task CreateAsync_TwoUsersWithSameAccountScope_ProduceTwoRows()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        var a = await harness.CreateAsync(first, DeletionScope.Account, null, DeletionReason.AccountDelete, Ct);
        var b = await harness.CreateAsync(second, DeletionScope.Account, null, DeletionReason.AccountDelete, Ct);

        a.Value.Id.ShouldNotBe(b.Value.Id);
        a.Value.UserId.ShouldBe(first);
        b.Value.UserId.ShouldBe(second);
    }

    [Fact]
    public async Task CreateAsync_SameUserParallelInserts_ProduceOneRow()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var user = Guid.CreateVersion7();

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => harness.CreateAsync(user, DeletionScope.Account, null, DeletionReason.AccountDelete, Ct), Ct)));

        results.ShouldAllBe(result => !result.IsError);
        results.Select(result => result.Value.Id).Distinct().ShouldHaveSingleItem();
        (await Database.QueryStringsAsync($"SELECT count(*)::text FROM data_deletion_requests WHERE user_id = '{user}'", Ct))
            .ShouldBe(["1"]);
    }

    [Fact]
    public async Task CreateAsync_PostConflictReread_ReturnsOwnRowOnly()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var owner = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var otherRow = await harness.CreateAsync(other, DeletionScope.Account, null, DeletionReason.AccountDelete, Ct);

        var own = await harness.CreateAsync(owner, DeletionScope.Account, null, DeletionReason.AccountDelete, Ct);
        var again = await harness.CreateAsync(owner, DeletionScope.Account, null, DeletionReason.AccountDelete, Ct);

        own.Value.UserId.ShouldBe(owner);
        again.Value.Id.ShouldBe(own.Value.Id);
        again.Value.Id.ShouldNotBe(otherRow.Value.Id);
    }

    [Fact]
    public async Task CreateAsync_CrossTenantTarget_ReturnsNotFound_NoRowCreated()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var owner = await SeedUserAsync();
        var intruder = await SeedUserAsync();
        var episode = await SeedEpisodeAsync(owner);

        var denied = await harness.CreateAsync(intruder, DeletionScope.Episode, episode, DeletionReason.UserRequest, Ct);
        var missing = await harness.CreateAsync(owner, DeletionScope.Episode, Guid.CreateVersion7(), DeletionReason.UserRequest, Ct);
        var allowed = await harness.CreateAsync(owner, DeletionScope.Episode, episode, DeletionReason.UserRequest, Ct);

        denied.IsError.ShouldBeTrue();
        denied.FirstError.Code.ShouldBe(ErrorCodes.NotFound);
        missing.FirstError.Code.ShouldBe(ErrorCodes.NotFound);
        allowed.IsError.ShouldBeFalse();
        (await Database.QueryStringsAsync("SELECT count(*)::text FROM data_deletion_requests", Ct)).ShouldBe(["1"]);
    }

    [Fact]
    public async Task CreateAsync_InvalidCombination_IsRejectedWithValidationFailed()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);

        var result = await harness.CreateAsync(Guid.CreateVersion7(), DeletionScope.Account, Guid.CreateVersion7(), DeletionReason.AccountDelete, Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task CreateAsync_ScopeWithoutOwnershipCheck_FailsLoudly()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, channelHandler: false, ct: Ct);

        await Should.ThrowAsync<InvalidOperationException>(() => harness.CreateAsync(
            Guid.CreateVersion7(), DeletionScope.Channel, Guid.CreateVersion7(), DeletionReason.InAppRevoke, Ct));
    }

    private async Task<Guid> SeedUserAsync() =>
        (await Database.CreateUserBuilder().CreateAsync(ct: Ct)).Id;

    private async Task<Guid> SeedEpisodeAsync(Guid userId)
    {
        var actor = new TestCurrentUser { Id = userId };
        var series = await new SeriesBuilder(Database, actor).CreateAsync(userId, ct: Ct);

        return (await new EpisodeBuilder(Database, actor).CreateAsync(userId, series.Id, ct: Ct)).Id;
    }
}
