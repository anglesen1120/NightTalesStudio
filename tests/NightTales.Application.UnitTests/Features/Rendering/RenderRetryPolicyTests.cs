using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Rendering;
using NightTales.Application.Rendering;
using NightTales.Application.Telemetry;
using NightTales.Domain.Rendering;
using NightTales.Domain.Rendering.Subtitles;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Usage;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Rendering;

/// <summary>
/// <see cref="RenderRetryPolicy"/> (09.6.3-T1 step 4, BD7, 03 D7): the retry table of 05 D5 (six transient
/// codes, three never-retried codes, <c>image_generation_failed</c> following the 03 classifier) and the
/// creation of the next attempt against a real <c>AppDbContext</c> — its own reservation, the failed row's
/// episode/story version/params, and the budget re-check that can still refuse it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RenderRetryPolicyTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("render_timeout")]
    [InlineData("render_lost")]
    [InlineData("render_node_error")]
    [InlineData("tts_failed")]
    [InlineData("render_assembly_failed")]
    [InlineData("render_output_invalid")]
    public void Transient_Codes_RetriedUntilAttempt2(string code)
    {
        using var db = Database.CreateAppDbContext();
        var policy = Policy(db);

        policy.ShouldRetry(code, attempt: 1).ShouldBeTrue("05 D5's transient codes are retried");

        policy.ShouldRetry(code, attempt: RenderRetryPolicy.MaxAttempts)
            .ShouldBeFalse("one automatic retry: attempt 2 has no budget left");
    }

    [Fact]
    public void ImageModerated_NeverRetried()
    {
        using var db = Database.CreateAppDbContext();
        var policy = Policy(db);

        policy.ShouldRetry("image_moderated", attempt: 1).ShouldBeFalse();
        policy.ShouldRetry("story_moderated", attempt: 1).ShouldBeFalse();
        policy.ShouldRetry("render_quality_failed", attempt: 1).ShouldBeFalse("03 D7's quality failure is never retried");
    }

    [Fact]
    public void FundsExhausted_NeverRetried()
    {
        using var db = Database.CreateAppDbContext();
        var policy = Policy(db);

        // provider_funds_exhausted is not one of 05 D5's transient codes, and the 03 classifier marks it
        // non-retryable, so neither the static table nor the per-outcome verdict queues another attempt.
        policy.ShouldRetry("provider_funds_exhausted", attempt: 1).ShouldBeFalse();
        policy.ShouldRetry("provider_funds_exhausted", 1, outcomeRetryable: false).ShouldBeFalse();

        // contract_violation is not one of the ten D5 outcome codes at all.
        policy.ShouldRetry("contract_violation", attempt: 1).ShouldBeFalse();
    }

    [Fact]
    public void ImageGenerationFailed_RetriesPerClassifierOutcome()
    {
        using var db = Database.CreateAppDbContext();
        var policy = Policy(db);

        // 03 D7: rows 5 and 8 are retryable, provider_funds_exhausted and a moderated row are not. BD7's blanket
        // "never image_generation_failed" is overridden by the classifier's per-outcome verdict.
        policy.ShouldRetry("image_generation_failed", 1, outcomeRetryable: true).ShouldBeTrue();
        policy.ShouldRetry("image_generation_failed", 1, outcomeRetryable: false).ShouldBeFalse();
        policy.ShouldRetry("image_generation_failed", 1).ShouldBeFalse("without a verdict the static tables decide");

        // The verdict does not outrank the attempt budget either.
        policy.ShouldRetry("image_generation_failed", RenderRetryPolicy.MaxAttempts, outcomeRetryable: true)
            .ShouldBeFalse();
    }

    [Fact]
    public async Task Retry_RechecksBudget_AndReservesAgain()
    {
        var world = await SeedFailedAsync("render_timeout");
        var money = new RecordingMoney(Start);
        using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }, new FakeClock(Start));
        var failed = await db.RenderJobs.SingleAsync(job => job.Id == world.RenderJobId, Ct);
        var policy = Policy(db, money);

        var created = await policy.CreateRetryAsync(failed, Ct);
        await db.SaveChangesAsync(Ct);

        created.IsError.ShouldBeFalse();
        money.Calls.ShouldBe(["priceCatalog", "imageCostEstimator", "budgetGuard", "creditLedger"]);
        money.ExcludedRunIds.ShouldBe([world.RenderJobId], "the spend cap is re-checked without the failed job's own bound");

        var retry = created.Value;
        retry.Id.ShouldNotBe(world.RenderJobId);
        retry.Attempt.ShouldBe(2);
        retry.Status.ShouldBe(RenderJobStatus.Queued);
        retry.EpisodeId.ShouldBe(world.EpisodeId);
        retry.StoryVersionId.ShouldBe(world.StoryVersionId);
        retry.ReservationId.ShouldNotBe(world.ReservationId, "the retry row owns its own reservation (WT50)");
        retry.WorstCaseUsd.ShouldBeGreaterThan(0m);

        // The retry's own reservation comes from the ledger, for the new row.
        var reserve = money.Reserves.ShouldHaveSingleItem();
        reserve.Operation.ShouldBe(CreditOperation.Render);
        reserve.ReferenceId.ShouldBe(retry.Id);
        reserve.UserId.ShouldBe(world.UserId);

        (await ScalarAsync($"SELECT count(*)::text FROM render_jobs WHERE episode_id = '{world.EpisodeId}'", Ct))
            .ShouldBe("2", "the failed row stays and the retry is a second row");
        (await ScalarAsync($"SELECT attempt::text || '|' || status FROM render_jobs WHERE id = '{retry.Id}'", Ct))
            .ShouldBe("2|queued");
        (await ScalarAsync(
                "SELECT ((SELECT params->'image_plan' FROM render_jobs WHERE id = "
                + $"'{world.RenderJobId}') = (SELECT params->'image_plan' FROM render_jobs WHERE id = '{retry.Id}'))::text",
                Ct))
            .ShouldBe("true", "the retry reuses the failed attempt's image plan (AC7)");

        // RenderJob.Create raises RenderRequested, whose outbox row is what queues SubmitRenderJob in this same
        // transaction (D11/D12) — never a direct enqueue.
        (await ScalarAsync($"SELECT count(*)::text FROM outbox_messages WHERE user_id = '{world.UserId}'", Ct))
            .ShouldBe("1");
    }

    [Fact]
    public async Task BudgetRefusal_IsNoRetry_AndNoNewRow()
    {
        var world = await SeedFailedAsync("render_node_error");
        var money = new RecordingMoney(Start)
        {
            Budget = Error.Failure("budget_spend_cap", "The platform spend cap is reached."),
        };

        using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }, new FakeClock(Start));
        var failed = await db.RenderJobs.SingleAsync(job => job.Id == world.RenderJobId, Ct);
        var policy = Policy(db, money);

        var created = await policy.CreateRetryAsync(failed, Ct);

        // A refusal is not an error of the failure transition: it means the attempt is final (WT50).
        created.IsError.ShouldBeTrue();
        created.FirstError.Code.ShouldBe("budget_spend_cap");
        money.Calls.ShouldBe(["priceCatalog", "imageCostEstimator", "budgetGuard"]);
        money.Reserves.ShouldBeEmpty("nothing is reserved before the cap answered yes");
        (await ScalarAsync($"SELECT count(*)::text FROM render_jobs WHERE episode_id = '{world.EpisodeId}'", Ct))
            .ShouldBe("1", "no retry row exists");
        (await ScalarAsync($"SELECT count(*)::text FROM outbox_messages WHERE user_id = '{world.UserId}'", Ct))
            .ShouldBe("0", "and nothing is queued");
    }

    [Fact]
    public async Task Retry_OfANonFailedRow_IsAConflict()
    {
        var world = await SeedFailedAsync("render_timeout");
        using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }, new FakeClock(Start));
        var job = await db.RenderJobs.SingleAsync(candidate => candidate.Id == world.RenderJobId, Ct);

        // A queued row has no verdict to retry from.
        await Database.ExecuteAsync(
            $"UPDATE render_jobs SET status = 'queued', error_code = NULL WHERE id = '{world.RenderJobId}'",
            Ct);
        await db.Entry(job).ReloadAsync(Ct);

        var created = await Policy(db).CreateRetryAsync(job, Ct);

        created.IsError.ShouldBeTrue();
        created.FirstError.Code.ShouldBe(RenderJob.InvalidStateCode);
    }

    /// <summary>
    /// The policy over the ports a test drives: the pure table tests never reach the write model or the money
    /// ports, so they only have to exist.
    /// </summary>
    private static RenderRetryPolicy Policy(IAppDbContext db, RecordingMoney? money = null)
    {
        var ports = money ?? new RecordingMoney(Start);

        return new RenderRetryPolicy(
            db,
            new FakeClock(Start),
            ports,
            ports,
            ports,
            ports,
            new ImagesOptions(),
            new RenderMetrics(new NightTalesMetrics()));
    }

    private async Task<string> ScalarAsync(string sql, CancellationToken ct) =>
        (await Database.QueryStringsAsync(sql, ct)).Single();

    /// <summary>One owner with an episode, its story version and a <c>failed</c> render row of attempt 1.</summary>
    private async Task<World> SeedFailedAsync(string code)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor, new FakeClock(Start))
            .CreateAsync(user.Id, series.Id, ct: Ct);

        var draft = Draft();
        var version = StoryVersion.Create(
            user.Id,
            episode.Id,
            1,
            StoryVersionSource.Generated,
            draft,
            StoryContentJson.Write(draft, new JsonArray()),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            Start);

        await using (var seedWriter = Database.CreateAppDbContext(actor, new FakeClock(Start)))
        {
            seedWriter.StoryVersions.Add(version);
            await seedWriter.SaveChangesAsync(Ct);
        }

        var jobId = Guid.CreateVersion7();
        var reservationId = Guid.CreateVersion7();

        await Database.ExecuteAsync(
            "INSERT INTO render_jobs (id, user_id, episode_id, story_version_id, reservation_id, status, stage, "
            + "percent, params, attempt, error_code, next_poll_at) "
            + $"VALUES ('{jobId}', '{user.Id}', '{episode.Id}', '{version.Id}', '{reservationId}', 'failed', "
            + $"'{RenderJob.QueuedStage}', 0, '{ParamsJson(version.Id)}'::jsonb, 1, '{code}', NULL)",
            Ct);

        return new World(user.Id, episode.Id, version.Id, jobId, reservationId);
    }

    /// <summary>The ids one seeded render world carries.</summary>
    private sealed record World(Guid UserId, Guid EpisodeId, Guid StoryVersionId, Guid RenderJobId, Guid ReservationId);

    /// <summary>
    /// A complete <c>render-params.v1</c> document (05 D11): every member is <c>[JsonRequired]</c>, so the row
    /// could not be loaded without one.
    /// </summary>
    private static string ParamsJson(Guid storyVersionId) =>
        JsonSerializer.Serialize(new RenderParamsV1(
            RenderParamsV1.CurrentMapperVersion,
            storyVersionId,
            "landscape",
            "vi",
            "kokoro:test",
            Guid.CreateVersion7(),
            "style=test@0123abcd;neg=v1",
            new ImagePlanSummary(3, 1, 2, 1, 0, [], ["paragraph:1"]),
            new SubtitlesParams(true, "landscape@1"),
            null,
            3));

    /// <summary>The smallest clean story document the version factory accepts; its text is never rendered here.</summary>
    private static StoryContentV1 Draft() => new(
        "The pier at dawn",
        new StorySegmentV1("Grey mist drifted over the riverside.", ["quiet riverside morning"], []),
        [new StorySegmentV1("A wooden pier stood empty at dusk.", ["empty wooden pier at dusk"], [], "setup")],
        new StorySegmentV1("Cold morning light returned.", ["cold morning light"], []),
        "A quiet story about an empty pier.",
        ["mist", "pier"],
        ["atmosphere"],
        new StoryStructureV1("ghost", "riverside", "a misty dawn", "the narrator", "first_person"));

    /// <summary>
    /// The four money ports of one retry, recorded in one place: the order they are asked proves the 03 D8
    /// sequence, and the reserve/refund lists prove which reservation the retry owns.
    /// </summary>
    private sealed class RecordingMoney(DateTimeOffset now) :
        IPriceCatalog,
        IImageCostEstimator,
        IBudgetGuard,
        ICreditLedger
    {
        public List<string> Calls { get; } = [];

        public List<Guid?> ExcludedRunIds { get; } = [];

        public List<(Guid UserId, CreditOperation Operation, Guid ReferenceId)> Reserves { get; } = [];

        public ErrorOr<Success> Budget { get; init; } = Result.Success;

        public Task<ErrorOr<PriceVersion>> GetActiveAsync(DateTimeOffset queuedAt, CancellationToken ct)
        {
            Calls.Add("priceCatalog");

            return Task.FromResult<ErrorOr<PriceVersion>>(
                PriceVersion.Create(1, now.AddDays(-1), "{}", "{}", "hash", now.AddDays(-1)));
        }

        public Task<PriceVersion> GetAsync(int version, CancellationToken ct) => throw new NotSupportedException();

        public Task<CostBound> WorstCaseAsync(Aspect aspect, int maxTerms, int priceVersion, CancellationToken ct)
        {
            Calls.Add("imageCostEstimator");

            return Task.FromResult(new CostBound(1.23m));
        }

        public ValueTask<ErrorOr<Success>> CheckAsync(CostBound worstCaseUsd, CancellationToken ct, Guid? excludingRunId = null)
        {
            Calls.Add("budgetGuard");
            ExcludedRunIds.Add(excludingRunId);

            return ValueTask.FromResult(Budget);
        }

        public ValueTask<ErrorOr<ReservationId>> ReserveAsync(
            Guid userId,
            CreditOperation op,
            Guid referenceId,
            UsageBound bound,
            CancellationToken ct)
        {
            Calls.Add("creditLedger");
            Reserves.Add((userId, op, referenceId));

            return ValueTask.FromResult<ErrorOr<ReservationId>>(new ReservationId(Guid.CreateVersion7()));
        }

        public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
