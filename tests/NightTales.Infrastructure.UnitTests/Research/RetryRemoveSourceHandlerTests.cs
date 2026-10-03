using Microsoft.EntityFrameworkCore;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Research.Sources;
using NightTales.Application.Features.Research.Sources.Commands.AddSource;
using NightTales.Application.Features.Research.Sources.Commands.RemoveSource;
using NightTales.Application.Features.Research.Sources.Commands.RetrySource;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;
using static NightTales.Infrastructure.UnitTests.Research.SourceHandlerHarness;

namespace NightTales.Infrastructure.UnitTests.Research;

/// <summary>The retry and remove handlers (02.5.1 T1 Design) against real PostgreSQL.</summary>
[Trait("Category", "Integration")]
public sealed class RetryRemoveSourceHandlerTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ErrorOr.ErrorOr<SourceDto>> RetryAsync(Scope scope, Guid sourceId)
    {
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = scope.UserId });
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        var handler = new RetrySourceHandler(db, new TestCurrentUser { Id = scope.UserId }, new FakeClock(Now), new SourcesOptions());

        var result = await handler.Handle(new RetrySourceCommand(scope.EpisodeId, sourceId), Ct);
        if (!result.IsError)
        {
            await db.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
        }

        return result;
    }

    private async Task<ErrorOr.ErrorOr<ErrorOr.Deleted>> RemoveAsync(Scope scope, Guid sourceId)
    {
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = scope.UserId });
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);

        var result = await new RemoveSourceHandler(db).Handle(new RemoveSourceCommand(scope.EpisodeId, sourceId), Ct);
        if (!result.IsError)
        {
            await db.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
        }

        return result;
    }

    [Fact]
    public async Task Retry_FailedSource_BecomesPendingWithANewQueuedTask()
    {
        var scope = await NewEpisodeAsync(Database, ct: Ct);
        var source = await SeedUrlAsync(Database, scope.UserId, scope.EpisodeId, "https://example.com/a", Now.AddHours(-1), failed: true, ct: Ct);

        var result = await RetryAsync(scope, source.Id);

        result.IsError.ShouldBeFalse();
        result.Value.Status.ShouldBe(StorySourceStatus.Pending);
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = scope.UserId });
        var tasks = await db.FetchTasks.Where(task => task.SourceId == source.Id).OrderBy(task => task.CreatedAt).ToListAsync(Ct);
        tasks.Count.ShouldBe(2);
        tasks.Count(task => task.Status == FetchTaskStatus.Queued && task.Attempts == 0).ShouldBe(1);
    }

    [Fact]
    public async Task Retry_FailedSource_AppendsACreationRecord_AndRemoveFreesNoQuota()
    {
        var scope = await NewEpisodeAsync(Database, ct: Ct);
        var source = await SeedUrlAsync(Database, scope.UserId, scope.EpisodeId, "https://example.com/a", Now.AddHours(-1), failed: true, ct: Ct);

        (await RetryAsync(scope, source.Id)).IsError.ShouldBeFalse();
        (await RemoveAsync(scope, source.Id)).IsError.ShouldBeFalse();

        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = scope.UserId });
        (await db.SourceCreations.CountAsync(creation => creation.UserId == scope.UserId, Ct)).ShouldBe(2, "the seed and the retry; the delete removes none");
    }

    [Fact]
    public async Task Retry_FailedSource_Reapplies24hCap()
    {
        var scope = await NewEpisodeAsync(Database, ct: Ct);
        var others = new[] { await NewEpisodeOfAsync(Database, scope, Ct), await NewEpisodeOfAsync(Database, scope, Ct), await NewEpisodeOfAsync(Database, scope, Ct) };
        for (var index = 0; index < 30; index++)
        {
            await SeedUrlAsync(Database, scope.UserId, others[index / 10], $"https://example.com/{index}", Now.AddHours(-2), ct: Ct);
        }

        var failed = await SeedUrlAsync(Database, scope.UserId, scope.EpisodeId, "https://example.com/failed", Now.AddHours(-30), failed: true, ct: Ct);

        var result = await RetryAsync(scope, failed.Id);

        result.FirstError.Code.ShouldBe(ErrorCodes.SourceFetchLimitReached);
    }

    [Fact]
    public async Task Retry_QueuedSource_Returns409()
    {
        var scope = await NewEpisodeAsync(Database, ct: Ct);
        var source = await SeedUrlAsync(Database, scope.UserId, scope.EpisodeId, "https://example.com/a", Now.AddHours(-1), ct: Ct);

        (await RetryAsync(scope, source.Id)).FirstError.Code.ShouldBe(ErrorCodes.EpisodeInvalidState);
    }

    [Fact]
    public async Task Retry_OnApprovedEpisode_Returns409_AndUnknownSource404()
    {
        var approved = await NewEpisodeAsync(Database, EpisodeStatus.StoryApproved, Ct);
        var source = await SeedUrlAsync(Database, approved.UserId, approved.EpisodeId, "https://example.com/a", Now.AddHours(-1), failed: true, ct: Ct);

        (await RetryAsync(approved, source.Id)).FirstError.Code.ShouldBe(ErrorCodes.EpisodeInvalidState);
        (await RetryAsync(approved, Guid.NewGuid())).FirstError.Code.ShouldBe(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task Remove_DeletesSourceAndItsTaskByCascade_ThenSecondRemoveIs404()
    {
        var scope = await NewEpisodeAsync(Database, ct: Ct);
        var source = await SeedUrlAsync(Database, scope.UserId, scope.EpisodeId, "https://example.com/a", Now.AddHours(-1), ct: Ct);

        (await RemoveAsync(scope, source.Id)).IsError.ShouldBeFalse();

        (await CountSourcesAsync(Database, scope, Ct)).ShouldBe(0);
        (await CountTasksAsync(Database, scope, Ct)).ShouldBe(0);
        (await RemoveAsync(scope, source.Id)).FirstError.Code.ShouldBe(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task Remove_OnApprovedEpisode_Returns409_AndKeepsTheRow()
    {
        var scope = await NewEpisodeAsync(Database, EpisodeStatus.StoryApproved, Ct);
        var source = await SeedUrlAsync(Database, scope.UserId, scope.EpisodeId, "https://example.com/a", Now.AddHours(-1), ct: Ct);

        (await RemoveAsync(scope, source.Id)).FirstError.Code.ShouldBe(ErrorCodes.EpisodeInvalidState);
        (await CountSourcesAsync(Database, scope, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Remove_OtherUsersSource_Returns404()
    {
        var owner = await NewEpisodeAsync(Database, ct: Ct);
        var stranger = await NewEpisodeAsync(Database, ct: Ct);
        var source = await SeedUrlAsync(Database, owner.UserId, owner.EpisodeId, "https://example.com/a", Now.AddHours(-1), ct: Ct);

        // The stranger names the owner's episode and source in the route: the tenant filter hides both.
        var result = await RemoveAsync(stranger with { EpisodeId = owner.EpisodeId }, source.Id);

        result.FirstError.Code.ShouldBe(ErrorCodes.NotFound);
        (await CountSourcesAsync(Database, owner, Ct)).ShouldBe(1);
    }
}
