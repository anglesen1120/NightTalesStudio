using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories.Generation;
using NightTales.Infrastructure.Persistence.Queries;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Stories;

/// <summary>
/// The FR-02-7 rolling-window guard (02.4.2-T1, 02 README D2) against real <c>generation_runs</c> rows: the counting
/// table, the exclusive window edge, the per-episode scope, <c>Retry-After</c> and the episode row lock the guard
/// relies on. Each test owns a fresh user, series and episode, because the class database is shared.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GenerationLimitGuardTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>One microsecond, the finest step <c>timestamptz</c> stores.</summary>
    private static readonly TimeSpan Microsecond = TimeSpan.FromTicks(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LimitGuard_UnderLimit_ReturnsSuccess()
    {
        var scope = await NewEpisodeAsync();
        for (var index = 0; index < 4; index++)
        {
            await AddRunAsync(scope, Now - TimeSpan.FromHours(index + 1));
        }

        (await CheckAsync(scope)).IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task LimitGuard_NoRuns_ReturnsSuccess()
    {
        var scope = await NewEpisodeAsync();

        (await CheckAsync(scope)).IsError.ShouldBeFalse("an empty window has no oldest run and never computes a Retry-After");
    }

    [Fact]
    public async Task LimitGuard_AtLimit_ReturnsStoryGenerationLimitWithRetryAfter()
    {
        var scope = await NewEpisodeAsync();
        var oldest = Now - TimeSpan.FromHours(20);
        await AddRunAsync(scope, oldest);
        for (var index = 1; index < 5; index++)
        {
            await AddRunAsync(scope, Now - TimeSpan.FromHours(index));
        }

        var result = await CheckAsync(scope);

        result.IsError.ShouldBeTrue("the fifth counted run is accepted; the sixth call is the one rejected");
        result.FirstError.Code.ShouldBe(ErrorCodes.StoryGenerationLimit);
        result.FirstError.Metadata![ErrorMetadata.RetryAfterSeconds].ShouldBe(4 * 3600);
        result.FirstError.Metadata[ErrorMetadata.Limit].ShouldBe(5);
    }

    [Fact]
    public async Task LimitGuard_OldestRunExactlyAtTheWindowEdge_IsNotCounted()
    {
        var scope = await NewEpisodeAsync();
        await AddRunAsync(scope, Now - Window);
        for (var index = 1; index < 5; index++)
        {
            await AddRunAsync(scope, Now - TimeSpan.FromHours(index));
        }

        (await CheckAsync(scope)).IsError.ShouldBeFalse("queued_at = now - 24 h has left the window (strict >)");
    }

    [Fact]
    public async Task LimitGuard_OldestRunJustInsideTheWindow_IsCounted()
    {
        var scope = await NewEpisodeAsync();
        await AddRunAsync(scope, Now - Window + Microsecond);
        for (var index = 1; index < 5; index++)
        {
            await AddRunAsync(scope, Now - TimeSpan.FromHours(index));
        }

        var result = await CheckAsync(scope);

        result.IsError.ShouldBeTrue();
        result.FirstError.Metadata![ErrorMetadata.RetryAfterSeconds].ShouldBe(1, "one microsecond left in the window rounds up to the 1 s floor");
    }

    [Fact]
    public async Task LimitGuard_OldestRunJustOutsideWindow_IsNotCounted()
    {
        var scope = await NewEpisodeAsync();
        await AddRunAsync(scope, Now - Window - Microsecond);
        for (var index = 1; index < 5; index++)
        {
            await AddRunAsync(scope, Now - TimeSpan.FromHours(index));
        }

        (await CheckAsync(scope)).IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task Limit_RunOlderThan24h_DoesNotCount()
    {
        var scope = await NewEpisodeAsync();
        for (var index = 0; index < 5; index++)
        {
            await AddRunAsync(scope, Now - TimeSpan.FromHours(30 + index));
        }

        (await CheckAsync(scope)).IsError.ShouldBeFalse("all counted runs are older than the window: counted = 0, oldest = NULL");
    }

    [Fact]
    public async Task LimitGuard_PlatformFailureRun_IsNotCounted()
    {
        var scope = await NewEpisodeAsync();
        for (var index = 1; index < 5; index++)
        {
            await AddRunAsync(scope, Now - TimeSpan.FromHours(index));
        }

        await AddRunAsync(
            scope,
            Now - TimeSpan.FromMinutes(30),
            run => run.Fail("story_generation_failed", "provider_5xx", countsTowardLimit: false, Now));

        (await CheckAsync(scope)).IsError.ShouldBeFalse("a platform failure does not consume the user's budget");
        (await CountsTowardLimitOfAsync(scope)).ShouldBe([true, true, true, true, false], "true for counted rows, false for the platform failure");
    }

    [Fact]
    public async Task LimitGuard_ModeratedRun_IsCounted()
    {
        var scope = await NewEpisodeAsync();
        await AddRunAsync(
            scope,
            Now - TimeSpan.FromHours(5),
            run => run.Fail("story_moderated", "brief_flagged", countsTowardLimit: true, Now));
        for (var index = 1; index < 5; index++)
        {
            await AddRunAsync(scope, Now - TimeSpan.FromHours(index));
        }

        var result = await CheckAsync(scope);

        result.IsError.ShouldBeTrue("a moderated run counts, and can be the oldest that gates the window");
        result.FirstError.Metadata![ErrorMetadata.RetryAfterSeconds].ShouldBe(19 * 3600);
    }

    [Fact]
    public async Task LimitGuard_AutopilotRun_IsNotCounted()
    {
        var scope = await NewEpisodeAsync();
        for (var index = 1; index < 5; index++)
        {
            await AddRunAsync(scope, Now - TimeSpan.FromHours(index));
        }

        await AddRunAsync(scope, Now - TimeSpan.FromMinutes(10), trigger: GenerationTrigger.Autopilot);

        (await CheckAsync(scope)).IsError.ShouldBeFalse("autopilot runs are capped by D17, not FR-02-7");
    }

    [Fact]
    public async Task LimitGuard_NonStoryKindRun_IsNotCounted()
    {
        var scope = await NewEpisodeAsync();
        for (var index = 1; index < 5; index++)
        {
            await AddRunAsync(scope, Now - TimeSpan.FromHours(index));
        }

        await AddRunAsync(scope, Now - TimeSpan.FromMinutes(10), kind: GenerationKind.FactSheet);

        (await CheckAsync(scope)).IsError.ShouldBeFalse("only kind = story counts");
    }

    [Fact]
    public async Task LimitGuard_CancelledByAdmin_IsCounted()
    {
        var scope = await NewEpisodeAsync();
        await AddRunAsync(scope, Now - TimeSpan.FromHours(2), run => run.Cancel(Now));
        for (var index = 1; index < 5; index++)
        {
            await AddRunAsync(scope, Now - TimeSpan.FromHours(index + 2));
        }

        (await CheckAsync(scope)).IsError.ShouldBeTrue("a user/admin cancel keeps counts_toward_limit = true");
    }

    [Fact]
    public async Task LimitGuard_OtherEpisodeRuns_DoNotCount()
    {
        var scope = await NewEpisodeAsync();
        var other = await NewEpisodeAsync();
        for (var index = 0; index < 6; index++)
        {
            await AddRunAsync(other, Now - TimeSpan.FromHours(index + 1));
        }

        (await CheckAsync(scope)).IsError.ShouldBeFalse("the limit is per episode, not per series or account");
        (await CheckAsync(other)).IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task LimitGuard_OtherUsersRuns_DoNotCount()
    {
        var scope = await NewEpisodeAsync();
        var stranger = await NewEpisodeAsync();
        for (var index = 0; index < 5; index++)
        {
            await AddRunAsync(stranger, Now - TimeSpan.FromHours(index + 1));
        }

        // Asking with the wrong owner for the stranger's episode must not see its runs through the guard.
        var crossTenant = await CheckAsync(stranger with { UserId = scope.UserId });

        crossTenant.IsError.ShouldBeFalse("the count never crosses tenants");
    }

    [Fact]
    public async Task RetryAfter_IsAtLeastOneSecond()
    {
        var scope = await NewEpisodeAsync();
        await AddRunAsync(scope, Now - Window + TimeSpan.FromMilliseconds(100));
        for (var index = 1; index < 5; index++)
        {
            await AddRunAsync(scope, Now - TimeSpan.FromHours(index));
        }

        var result = await CheckAsync(scope);

        result.FirstError.Metadata![ErrorMetadata.RetryAfterSeconds].ShouldBe(1);
    }

    [Fact]
    public async Task RetryAfter_EqualsTimeUntilOldestLeavesWindow()
    {
        var scope = await NewEpisodeAsync();

        // The oldest is queued 20 h 30 min 0.5 s ago: 3 h 29 min 59.5 s remain, rounded up to 12 600 s.
        await AddRunAsync(scope, Now - new TimeSpan(20, 30, 0) - TimeSpan.FromMilliseconds(500));
        for (var index = 1; index < 5; index++)
        {
            await AddRunAsync(scope, Now - TimeSpan.FromHours(index));
        }

        var result = await CheckAsync(scope);

        result.FirstError.Metadata![ErrorMetadata.RetryAfterSeconds].ShouldBe(12_600);
    }

    [Fact]
    public async Task LimitGuard_UsesTheConfiguredLimitAndWindow()
    {
        var scope = await NewEpisodeAsync();
        await AddRunAsync(scope, Now - TimeSpan.FromMinutes(90));
        await AddRunAsync(scope, Now - TimeSpan.FromMinutes(30));
        var options = new GenerationLimitOptions { DailyGenerationLimit = 2, LimitWindowHours = 1 };

        (await CheckAsync(scope, options)).IsError.ShouldBeFalse("the 90-minute-old run left the 1 h window");

        await AddRunAsync(scope, Now - TimeSpan.FromMinutes(10));
        var result = await CheckAsync(scope, options);

        result.IsError.ShouldBeTrue();
        result.FirstError.Metadata![ErrorMetadata.RetryAfterSeconds].ShouldBe(30 * 60);
        result.FirstError.Metadata[ErrorMetadata.Limit].ShouldBe(2);
    }

    [Fact]
    public async Task EpisodeRowLock_TwoConcurrentCheckAndInsert_CreateExactlyOneRun()
    {
        var scope = await NewEpisodeAsync();
        for (var index = 0; index < 4; index++)
        {
            await AddRunAsync(scope, Now - TimeSpan.FromHours(index + 1));
        }

        var firstHoldsLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The winner takes the lock, then dawdles between the count and the insert; without the lock the loser
        // would read four counted runs in that gap and also insert.
        var winner = Task.Run(async () => await CheckAndInsertAsync(scope, firstHoldsLock, holdFor: TimeSpan.FromMilliseconds(400)), Ct);
        await firstHoldsLock.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var loser = Task.Run(async () => await CheckAndInsertAsync(scope, null, holdFor: TimeSpan.Zero), Ct);

        var results = await Task.WhenAll(winner, loser);

        results.Count(accepted => accepted).ShouldBe(1);
        results[0].ShouldBeTrue("the winner holds the row lock first");
        results[1].ShouldBeFalse("the loser counts after the winner committed: five runs");

        await using var read = Database.CreateAppDbContext(new TestCurrentUser { Id = scope.UserId });
        (await read.GenerationRuns.CountAsync(run => run.EpisodeId == scope.EpisodeId, Ct)).ShouldBe(5);
    }

    [Fact]
    public void Options_Validate_RejectsNonPositiveValues()
    {
        GenerationLimitOptions.Validate(new GenerationLimitOptions()).ShouldBeEmpty();
        GenerationLimitOptions.Validate(new GenerationLimitOptions { DailyGenerationLimit = 0 }).Count.ShouldBe(1);
        GenerationLimitOptions.Validate(new GenerationLimitOptions { LimitWindowHours = 0 }).Count.ShouldBe(1);
        GenerationLimitOptions.Validate(new GenerationLimitOptions { DailyGenerationLimit = -1, LimitWindowHours = -1 }).Count.ShouldBe(2);
    }

    private async Task<bool> CheckAndInsertAsync(Scope scope, TaskCompletionSource? lockTaken, TimeSpan holdFor)
    {
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = scope.UserId });
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);

        await db.LockEpisodeForUpdateAsync(scope.EpisodeId, Ct);
        lockTaken?.SetResult();

        var guard = new GenerationLimitGuard(db, Options.Create(new GenerationLimitOptions()), new FakeClock(Now));
        var verdict = await guard.CheckAsync(scope.EpisodeId, scope.UserId, Ct);
        if (verdict.IsError)
        {
            await transaction.RollbackAsync(Ct);
            return false;
        }

        await Task.Delay(holdFor, Ct);

        var run = GenerationRun.Queue(
                scope.UserId,
                scope.SeriesId,
                scope.EpisodeId,
                GenerationKind.Story,
                GenerationTrigger.User,
                null,
                EpisodeStatus.Draft,
                null,
                null,
                0m,
                Now)
            .Value;
        db.GenerationRuns.Add(run);
        await db.SaveChangesAsync(Ct);
        await transaction.CommitAsync(Ct);

        return true;
    }

    private async Task<ErrorOr<Success>> CheckAsync(Scope scope, GenerationLimitOptions? options = null)
    {
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = scope.UserId });
        var guard = new GenerationLimitGuard(db, Options.Create(options ?? new GenerationLimitOptions()), new FakeClock(Now));

        return await guard.CheckAsync(scope.EpisodeId, scope.UserId, Ct);
    }

    private async Task<Scope> NewEpisodeAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, ct: Ct);

        return new Scope(user.Id, series.Id, episode.Id);
    }

    private async Task AddRunAsync(
        Scope scope,
        DateTimeOffset queuedAt,
        Action<GenerationRun>? finish = null,
        GenerationTrigger trigger = GenerationTrigger.User,
        GenerationKind kind = GenerationKind.Story)
    {
        var run = GenerationRun.Queue(
                scope.UserId,
                scope.SeriesId,
                scope.EpisodeId,
                kind,
                trigger,
                null,
                EpisodeStatus.Draft,
                null,
                null,
                0m,
                queuedAt)
            .Value;

        // A queued run is active, and ux_generation_runs_active_episode allows one per episode and kind: every
        // seeded run ends (cancelled keeps counts_toward_limit = true) unless the test finishes it itself.
        if (finish is null)
        {
            run.Cancel(queuedAt);
        }
        else
        {
            finish(run);
        }

        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = scope.UserId });
        db.GenerationRuns.Add(run);
        await db.SaveChangesAsync(Ct);
    }

    private async Task<bool[]> CountsTowardLimitOfAsync(Scope scope)
    {
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = scope.UserId });

        return await db.GenerationRuns
            .Where(run => run.EpisodeId == scope.EpisodeId)
            .OrderBy(run => run.QueuedAt)
            .Select(run => run.CountsTowardLimit)
            .ToArrayAsync(Ct);
    }

    private sealed record Scope(Guid UserId, Guid SeriesId, Guid EpisodeId);
}
