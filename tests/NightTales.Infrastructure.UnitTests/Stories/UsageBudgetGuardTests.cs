using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Pricing;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Stories;

/// <summary>
/// <c>UsageBudgetGuard</c> + <c>GenerationRunOpenBoundsReader</c> (02.2.2-T2) over real <c>usage_records</c> and
/// <c>generation_runs</c> rows. The class database is shared and the guard sums the whole platform, so every test
/// first reads the platform's current totals and sets the caps relative to them.
/// </summary>
[Trait("Category", "Integration")]
public sealed class UsageBudgetGuardTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Now = new(2036, 5, 15, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UsageBudgetGuard_UnderCaps_ReturnsSuccess()
    {
        var baseline = await BaselineAsync();

        var result = await CheckAsync(Options(baseline, day: 1.0m, month: 10m), 0.5m);

        result.IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task UsageBudgetGuard_OverDailyCap_ReturnsSpendCapReached()
    {
        var baseline = await BaselineAsync();

        var result = await CheckAsync(Options(baseline, day: 0.4m, month: 10m), 0.5m);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.SpendCapReached);
    }

    [Fact]
    public async Task UsageBudgetGuard_OverMonthlyCap_ReturnsSpendCapReached()
    {
        var baseline = await BaselineAsync();

        var result = await CheckAsync(Options(baseline, day: 10m, month: 0.4m), 0.5m);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.SpendCapReached);
    }

    [Fact]
    public async Task UsageBudgetGuard_OverDailyCap_RetryAfterIsSecondsToNextUtcMidnight()
    {
        var baseline = await BaselineAsync();

        var result = await CheckAsync(Options(baseline, day: 0.4m, month: 10m), 0.5m);

        RetryAfterSecondsOf(result).ShouldBe(54000, "09:00 to the next UTC midnight is 15 hours");
    }

    [Fact]
    public async Task UsageBudgetGuard_OverMonthlyCap_RetryAfterIsSecondsToNextUtcMonth()
    {
        var baseline = await BaselineAsync();

        var result = await CheckAsync(Options(baseline, day: 10m, month: 0.4m), 0.5m);

        RetryAfterSecondsOf(result).ShouldBe(1436400, "2036-05-15 09:00 to 2036-06-01 00:00 is 16 days 15 hours");
    }

    [Fact]
    public async Task UsageBudgetGuard_OverBothCaps_RetryAfterWaitsForTheMonthlyReset()
    {
        var baseline = await BaselineAsync();

        var result = await CheckAsync(Options(baseline, day: 0.4m, month: 0.4m), 0.5m);

        RetryAfterSecondsOf(result).ShouldBe(1436400, "one retry only clears both windows at the monthly reset");
    }

    [Fact]
    public async Task UsageBudgetGuard_OverDailyCapJustBeforeMidnight_RetryAfterIsAtLeastOneSecond()
    {
        var baseline = await BaselineAsync();
        var justBeforeMidnight = new DateTimeOffset(2036, 5, 15, 23, 59, 59, TimeSpan.Zero).AddMilliseconds(500);

        var result = await CheckAtAsync(Options(baseline, day: 0.4m, month: 10m), 0.5m, justBeforeMidnight);

        RetryAfterSecondsOf(result).ShouldBe(1, "a sub-second remainder rounds up and never reaches zero");
    }

    [Fact]
    public async Task UsageBudgetGuard_ExactlyAtTheCap_IsAllowed()
    {
        var baseline = await BaselineAsync();

        var result = await CheckAsync(Options(baseline, day: 0.5m, month: 0.5m), 0.5m);

        result.IsError.ShouldBeFalse("the rule denies only when the total is greater than the cap");
    }

    [Fact]
    public async Task UsageBudgetGuard_SpentToday_CountsTowardTheCap()
    {
        var baseline = await BaselineAsync();
        var options = Options(baseline, day: 0.6m, month: 10m);
        var user = await NewUserAsync();
        await AddUsageAsync(user, 0.4m, Now.AddHours(-1));

        (await CheckAsync(options, 0.3m)).FirstError.Code.ShouldBe(ErrorCodes.SpendCapReached);
        (await CheckAsync(options, 0.2m)).IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task UsageBudgetGuard_SpentYesterday_IsNotCountedAgainstToday()
    {
        var baseline = await BaselineAsync();
        var options = Options(baseline, day: 0.6m, month: 10m);
        var user = await NewUserAsync();
        await AddUsageAsync(user, 5m, Now.AddDays(-1));

        (await CheckAsync(options, 0.5m)).IsError.ShouldBeFalse("the daily window is the UTC day; yesterday still counts for the month only");
    }

    [Fact]
    public async Task UsageBudgetGuard_OpenWorstCaseBounds_CountTowardTheCap()
    {
        var baseline = await BaselineAsync();
        var options = Options(baseline, day: 0.7m, month: 10m);
        (await CheckAsync(options, 0.5m)).IsError.ShouldBeFalse("without the open bound the new work fits");

        var user = await NewUserAsync();
        await AddQueuedRunAsync(user, 0.4m);

        var result = await CheckAsync(options, 0.5m);

        result.IsError.ShouldBeTrue("0.4 open + 0.5 new > 0.7");
        result.FirstError.Code.ShouldBe(ErrorCodes.SpendCapReached);
    }

    [Fact]
    public async Task UsageBudgetGuard_ExcludingARun_LeavesOutOnlyThatRunsOpenBound()
    {
        var baseline = await BaselineAsync();
        var options = Options(baseline, day: 0.7m, month: 10m);
        var user = await NewUserAsync();
        var own = await AddQueuedRunAsync(user, 0.4m);
        await AddQueuedRunAsync(user, 0.1m);

        (await CheckAsync(options, 0.5m)).IsError.ShouldBeTrue("0.4 + 0.1 open + 0.5 new > 0.7 at admission");
        (await CheckAsync(options, 0.5m, own)).IsError.ShouldBeFalse("a dispatch of the admitted run does not count its own 0.4: 0.1 + 0.5 <= 0.7");
        (await CheckAsync(options, 0.5m, Guid.CreateVersion7())).IsError.ShouldBeTrue("an unrelated run id excludes nothing");
        (await CheckAsync(options, 0.65m, own)).IsError.ShouldBeTrue("the other run's 0.1 still counts: 0.1 + 0.65 > 0.7");
    }

    [Fact]
    public async Task UsageBudgetGuard_FinishedRuns_NoLongerHoldAnOpenBound()
    {
        var baseline = await BaselineAsync();
        var options = Options(baseline, day: 0.7m, month: 10m);
        var user = await NewUserAsync();
        await AddQueuedRunAsync(user, 0.4m, cancel: true);

        (await CheckAsync(options, 0.5m)).IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task UsageBudgetGuard_Disabled_AllowsEverything()
    {
        var baseline = await BaselineAsync();
        var options = Options(baseline, day: 0.01m, month: 0.01m);
        options.Enabled = false;

        (await CheckAsync(options, 100m)).IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task BudgetGuard_TwoEpisodesConcurrently_SecondSeesFirstOpenBound()
    {
        var baseline = await BaselineAsync();
        var options = Options(baseline, day: 0.8m, month: 10m);
        var first = await NewEpisodeAsync();
        var second = await NewEpisodeAsync();

        await using var firstDb = Database.CreateAppDbContext(new TestCurrentUser { Id = first.UserId });
        await using var secondDb = Database.CreateAppDbContext(new TestCurrentUser { Id = second.UserId });
        await using var firstTransaction = await firstDb.Database.BeginTransactionAsync(Ct);
        await using var secondTransaction = await secondDb.Database.BeginTransactionAsync(Ct);
        await using var firstRead = Database.CreateReadDbContext();
        await using var secondRead = Database.CreateReadDbContext();

        var firstGuard = GuardOver(firstDb, firstRead, options);
        var secondGuard = GuardOver(secondDb, secondRead, options);

        // The first command passes the check and holds the advisory lock until its run row commits.
        (await firstGuard.CheckAsync(new CostBound(0.5m), Ct)).IsError.ShouldBeFalse();
        firstDb.GenerationRuns.Add(
            GenerationRun.Queue(
                    first.UserId,
                    first.SeriesId,
                    first.EpisodeId,
                    GenerationKind.Story,
                    GenerationTrigger.User,
                    null,
                    EpisodeStatus.Draft,
                    null,
                    null,
                    0.5m,
                    Now)
                .Value);
        await firstDb.SaveChangesAsync(Ct);

        // The second command, for a different episode, must wait for that lock.
        var secondCheck = secondGuard.CheckAsync(new CostBound(0.5m), Ct).AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(750), Ct);
        secondCheck.IsCompleted.ShouldBeFalse("the check queues behind the platform-wide advisory lock");

        await firstTransaction.CommitAsync(Ct);
        var verdict = await secondCheck.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        verdict.IsError.ShouldBeTrue("0.5 open from the first command + 0.5 new > 0.8");
        verdict.FirstError.Code.ShouldBe(ErrorCodes.SpendCapReached);
    }

    private static BudgetOptions Options((decimal Day, decimal Month, decimal Open) baseline, decimal day, decimal month) =>
        new()
        {
            Enabled = true,
            MaxPlatformCostUsdPerDay = baseline.Day + baseline.Open + day,
            MaxPlatformCostUsdPerMonth = baseline.Month + baseline.Open + month,
        };

    private static UsageBudgetGuard GuardOver(Infrastructure.Persistence.AppDbContext db, Infrastructure.Persistence.ReadDbContext read, BudgetOptions options) =>
        new(read, new GenerationRunOpenBoundsReader(read), db, options, new FakeClock(Now));

    private async Task<ErrorOr.ErrorOr<ErrorOr.Success>> CheckAsync(BudgetOptions options, decimal worstCase, Guid? excludingRunId = null)
    {
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = Guid.CreateVersion7() });
        await using var read = Database.CreateReadDbContext();

        return await GuardOver(db, read, options).CheckAsync(new CostBound(worstCase), Ct, excludingRunId);
    }

    private async Task<ErrorOr.ErrorOr<ErrorOr.Success>> CheckAtAsync(BudgetOptions options, decimal worstCase, DateTimeOffset at)
    {
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = Guid.CreateVersion7() });
        await using var read = Database.CreateReadDbContext();
        var guard = new UsageBudgetGuard(read, new GenerationRunOpenBoundsReader(read), db, options, new FakeClock(at));

        return await guard.CheckAsync(new CostBound(worstCase), Ct);
    }

    private static int RetryAfterSecondsOf(ErrorOr.ErrorOr<ErrorOr.Success> result) =>
        (int)result.FirstError.Metadata![ErrorMetadata.RetryAfterSeconds];

    /// <summary>The platform's spent-today, spent-this-month and open totals as the guard will read them at <see cref="Now"/>.</summary>
    private async Task<(decimal Day, decimal Month, decimal Open)> BaselineAsync()
    {
        await using var read = Database.CreateReadDbContext();
        var dayStart = new DateTimeOffset(Now.UtcDateTime.Date, TimeSpan.Zero);
        var monthStart = new DateTimeOffset(Now.Year, Now.Month, 1, 0, 0, 0, TimeSpan.Zero);

        var day = await read.Set<UsageRecord>().IgnoreQueryFilters(["tenant"]).Where(r => r.CreatedAt >= dayStart).SumAsync(r => (decimal?)r.CostUsd, Ct) ?? 0m;
        var month = await read.Set<UsageRecord>().IgnoreQueryFilters(["tenant"]).Where(r => r.CreatedAt >= monthStart).SumAsync(r => (decimal?)r.CostUsd, Ct) ?? 0m;
        var open = await new GenerationRunOpenBoundsReader(read).SumOpenWorstCaseUsdAsync(Ct);

        return (day, month, open);
    }

    private async Task<Guid> NewUserAsync()
    {
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: Ct);

        return user.Id;
    }

    private async Task<(Guid UserId, Guid SeriesId, Guid EpisodeId)> NewEpisodeAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, ct: Ct);

        return (user.Id, series.Id, episode.Id);
    }

    private async Task AddUsageAsync(Guid userId, decimal cost, DateTimeOffset at)
    {
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = userId });
        db.UsageRecords.Add(UsageRecord.Record(userId, UsageOperation.Story, "openai", cost, at));
        await db.SaveChangesAsync(Ct);
    }

    private async Task<Guid> AddQueuedRunAsync(Guid userId, decimal worstCase, bool cancel = false)
    {
        var actor = new TestCurrentUser();
        var series = await new SeriesBuilder(Database, actor).CreateAsync(userId, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(userId, series.Id, ct: Ct);
        var run = GenerationRun.Queue(
                userId,
                series.Id,
                episode.Id,
                GenerationKind.Story,
                GenerationTrigger.User,
                null,
                EpisodeStatus.Draft,
                null,
                null,
                worstCase,
                Now)
            .Value;
        if (cancel)
        {
            run.Cancel(Now);
        }

        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = userId });
        db.GenerationRuns.Add(run);
        await db.SaveChangesAsync(Ct);

        return run.Id;
    }
}
