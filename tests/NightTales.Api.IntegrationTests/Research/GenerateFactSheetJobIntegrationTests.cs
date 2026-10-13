using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Research.Facts;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Moderation;
using NightTales.TestKit.Research;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Research;

/// <summary>
/// The whole fact-sheet chain on a real PostgreSQL 17 (02.5.3-T2, plan §5/§7): <c>POST
/// /api/v1/episodes/{id}/fact-sheet/generate</c> on the real Api host writes the run and the
/// <c>fact_sheet_generation_requested.v1</c> outbox row; the Worker host delivers that row (the handler enqueues
/// <c>GenerateFactSheetJob</c>), the inline scheduler runs the job through the real
/// <c>GenerateFactSheetPipeline</c>, and the follow-up outbox rows reach the realtime handlers. The extractor and
/// the D12 moderator are scripted; every other collaborator — persistence, the price catalog, the validator, the
/// usage meter, the outbox — is the production graph. No sleeps: the inline scheduler runs each enqueued job as it
/// arrives and <c>RunOutboxOnceAsync</c> drains a batch once.
/// </summary>
public sealed class GenerateFactSheetJobIntegrationTests(TestDatabase database) : IntegrationTest(database)
{
    private const string NoteProse =
        "Người gác bến kể rằng đêm ấy mưa rất to và không ai trong xóm ra ngoài.";

    private static readonly DateTimeOffset Start = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The D1 refund reason of a failed fact-sheet run (<c>GenerateFactSheetPipeline.RefundFailed</c>).</summary>
    private const string RefundFailed = "fact_sheet_generation_failed";

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task PostFactSheetGenerate_RunsJob_WritesDraftV1_AndEmitsEpisodeUpdated()
    {
        var world = await SeedAsync();
        var extractor = new FakeFactExtractor().EnqueueOk(new FactsV1(
        [
            new FactDraftV1("Người gác bến giữ ngọn đèn dầu bên cổng suốt đêm mưa.", ["s1"], FactConfidence.High),
        ]));
        var notifier = new RecordingNotifier();
        await using var worker = await StartWorkerAsync(extractor, new FakeContentModerator(), notifier);

        var runId = await GenerateAsync(world);
        await DeliverAndRunAsync(worker);

        extractor.Calls.ShouldBe(1);
        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '/' || stage || '/' || (cost_usd > 0)::text || '/' || counts_toward_limit::text "
                + $"|| '/' || (result_fact_sheet_id IS NOT NULL)::text FROM generation_runs WHERE id = '{runId}'")
            .ShouldBe("succeeded/done/true/true/true");

        var sheetId = SqlProbe.ScalarText(Database, $"SELECT result_fact_sheet_id::text FROM generation_runs WHERE id = '{runId}'");
        SqlProbe.ScalarText(
                Database,
                $"SELECT version::text || '/' || status || '/' || max_fact_id::text || '/' || input_truncated::text "
                + $"|| '/' || generation_run_id::text FROM fact_sheets WHERE id = '{sheetId}'")
            .ShouldBe($"1/{(int)FactSheetStatus.Draft}/1/false/{runId}");
        SqlProbe.Count(Database, $"SELECT count(*) FROM fact_sheets WHERE episode_id = '{world.EpisodeId}'").ShouldBe(1);
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = 'fact_sheet_generated.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(1);

        // The realtime consumer told the owner's page the episode's fact sheet changed: ids only.
        var updates = notifier.Events.Where(item => item.Event.Name == RealtimeEventName.EpisodeUpdated).ToList();
        updates.ShouldContain(item => item.UserId == world.UserId && item.Event.Ids.EpisodeId == world.EpisodeId);
        updates.ShouldAllBe(item => item.Event.Data == null, "ids only, never a payload (BD11/WT54)");
    }

    [Fact]
    public async Task GenerateFactSheetJob_ZeroFactsSurvive_SavesEmptyDraft()
    {
        var world = await SeedAsync();
        // Every fact cites an alias the plan does not have: the validator drops them all and the job still saves an
        // empty draft version, which is a valid run outcome (F13).
        var extractor = new FakeFactExtractor().EnqueueOk(new FactsV1(
        [
            new FactDraftV1("Một sự việc không nguồn nào kể lại.", ["s9"], FactConfidence.High),
        ]));
        await using var worker = await StartWorkerAsync(extractor, new FakeContentModerator(), new RecordingNotifier());

        var runId = await GenerateAsync(world);
        await DeliverAndRunAsync(worker);

        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '/' || (result_fact_sheet_id IS NOT NULL)::text FROM generation_runs WHERE id = '{runId}'")
            .ShouldBe("succeeded/true");
        SqlProbe.ScalarText(
                Database,
                $"SELECT version::text || '/' || status || '/' || max_fact_id::text || '/' || jsonb_array_length(facts)::text "
                + $"FROM fact_sheets WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe($"1/{(int)FactSheetStatus.Draft}/0/0");
        SqlProbe.Count(Database, $"SELECT count(*) FROM fact_sheets WHERE episode_id = '{world.EpisodeId}'").ShouldBe(1);
    }

    /// <summary>
    /// The D9 version race on the real chain: while this run sits between the <c>max(version) + 1</c> read and
    /// its own insert, a competing run claims version 1 for the same episode, so the insert meets
    /// <c>ux_fact_sheets_episode_version</c> (PostgreSQL <c>23505</c>). The pipeline maps that to a typed
    /// failure — never a 500 at the job's log — refunds the reservation, and leaves exactly the competitor's row.
    /// </summary>
    [Fact]
    public async Task GenerateFactSheetJob_VersionConflict_MapsToFailedRun()
    {
        var world = await SeedAsync();
        var ledger = new RecordingLedger();
        var competing = new CompetingVersionInterceptor(Database, world, new FakeClock(Start));
        var extractor = new FakeFactExtractor().EnqueueOk(new FactsV1(
        [
            new FactDraftV1("Người gác bến giữ ngọn đèn dầu bên cổng.", ["s1"], FactConfidence.High),
        ]));
        await using var worker = await StartWorkerAsync(extractor, new FakeContentModerator(), new RecordingNotifier(), services =>
        {
            services.RemoveAll<ICreditLedger>();
            services.AddSingleton<ICreditLedger>(ledger);
            // One more options configuration on the production registration (the AppDbContext precedent of the
            // episode-detail tests): the host keeps its connection string, its naming convention and its auditable
            // and outbox interceptors, and gains the competing write of the race.
            services.AddDbContext<AppDbContext>((_, options) => options.AddInterceptors(competing));
        });

        var runId = await GenerateAsync(world);
        await DeliverAndRunAsync(worker);

        competing.RowsInserted.ShouldBe(1, "the competing run must claim version 1 before the pipeline's own insert");

        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '/' || error_code || '/' || error_detail || '/' || (result_fact_sheet_id IS NULL)::text "
                + $"FROM generation_runs WHERE id = '{runId}'")
            .ShouldBe($"failed/{ErrorCodes.FactSheetGenerationFailed}/version_conflict/true");

        // The reservation of this run was released with the D1 reason of a failed fact-sheet run, and nothing settled.
        var reservation = new ReservationId(Guid.Parse(
            SqlProbe.ScalarText(Database, $"SELECT reservation_id::text FROM generation_runs WHERE id = '{runId}'")));
        ledger.Refunds.ShouldBe(new[] { (RefundFailed, reservation) });
        ledger.Settled.ShouldBeEmpty();

        // Exactly one sheet row survives: the competitor's version 1, not a draft of this run.
        SqlProbe.Count(Database, $"SELECT count(*) FROM fact_sheets WHERE episode_id = '{world.EpisodeId}'").ShouldBe(1);
        SqlProbe.Count(Database, $"SELECT count(*) FROM fact_sheets WHERE generation_run_id = '{runId}'").ShouldBe(0);
    }

    /// <summary>
    /// The F12 watermark race on the real chain: a competing version hands out fact ids 1..7 for the same episode
    /// between this run's validation (which read <c>max(max_fact_id) = 0</c>) and its save, so the ids this draft
    /// numbered are already taken. The save's own re-read under the episode's row lock must fail the run typed
    /// (<c>version_conflict</c>) instead of writing a second sheet that reuses them, and refund the reservation.
    /// </summary>
    [Fact]
    public async Task GenerateFactSheetJob_WatermarkMovedBeforeSave_FailsTypedVersionConflict()
    {
        var world = await SeedAsync();
        var ledger = new RecordingLedger();
        var competing = new CompetingWatermarkInterceptor(Database, world, new FakeClock(Start));
        var extractor = new FakeFactExtractor().EnqueueOk(new FactsV1(
        [
            new FactDraftV1("Người gác bến giữ ngọn đèn dầu bên cổng.", ["s1"], FactConfidence.High),
        ]));
        await using var worker = await StartWorkerAsync(extractor, new FakeContentModerator(), new RecordingNotifier(), services =>
        {
            services.RemoveAll<ICreditLedger>();
            services.AddSingleton<ICreditLedger>(ledger);
            services.AddDbContext<AppDbContext>((_, options) => options.AddInterceptors(competing));
        });

        var runId = await GenerateAsync(world);
        await DeliverAndRunAsync(worker);

        competing.RowsInserted.ShouldBe(1, "the competing version must land between the validation and the save");

        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '/' || error_code || '/' || error_detail || '/' || (result_fact_sheet_id IS NULL)::text "
                + $"FROM generation_runs WHERE id = '{runId}'")
            .ShouldBe($"failed/{ErrorCodes.FactSheetGenerationFailed}/version_conflict/true");

        var reservation = new ReservationId(Guid.Parse(
            SqlProbe.ScalarText(Database, $"SELECT reservation_id::text FROM generation_runs WHERE id = '{runId}'")));
        ledger.Refunds.ShouldBe(new[] { (RefundFailed, reservation) });
        ledger.Settled.ShouldBeEmpty();

        // Only the competitor's row survives: this run wrote nothing that could reuse the handed-out ids.
        SqlProbe.Count(Database, $"SELECT count(*) FROM fact_sheets WHERE episode_id = '{world.EpisodeId}'").ShouldBe(1);
        SqlProbe.Count(Database, $"SELECT count(*) FROM fact_sheets WHERE generation_run_id = '{runId}'").ShouldBe(0);
    }

    /// <summary>
    /// The episode is deleted while its run is in flight (task failure handling: "Episode deleted mid-run -> job
    /// no-ops (FK cascade), run failed, no orphan sheet"). The delete lands inside the extraction call, so the run's
    /// own saving batch meets a foreign key whose episode no longer exists. Nothing escapes the job: the pipeline
    /// maps the lost row to its terminal path, which finds no run left to fail.
    /// </summary>
    [Fact]
    public async Task Pipeline_EpisodeDeletedMidRun_NoOrphanSheet_AndTheJobDoesNotThrow()
    {
        var world = await SeedAsync();
        var ledger = new RecordingLedger();
        var extractor = new EpisodeDeletingExtractor(
            Database,
            world,
            new FakeFactExtractor().EnqueueOk(new FactsV1(
            [
                new FactDraftV1("Người gác bến giữ ngọn đèn dầu bên cổng.", ["s1"], FactConfidence.High),
            ])));
        await using var worker = await StartWorkerAsync(extractor, new FakeContentModerator(), new RecordingNotifier(), services =>
        {
            services.RemoveAll<ICreditLedger>();
            services.AddSingleton<ICreditLedger>(ledger);
        });

        var runId = await GenerateAsync(world);

        // DeliverAndRunAsync asserts `Jobs.Failures` is empty: no exception leaves the pipeline.
        await DeliverAndRunAsync(worker);

        SqlProbe.Count(Database, $"SELECT count(*) FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM fact_sheets WHERE episode_id = '{world.EpisodeId}'").ShouldBe(
            0,
            "the cascade takes the drafts and the pipeline's own insert rolls back");
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE id = '{runId}'").ShouldBe(
            0,
            "the run row cascades away with its episode (GenerationRunConfiguration: DeleteBehavior.Cascade)");

        // The real behaviour of the cascade: with no run row left there is nothing for the terminal write to fail,
        // so this path never reaches the refund (see the yield's finding — the reservation is stranded).
        ledger.Refunds.ShouldBeEmpty();
        ledger.Settled.ShouldBeEmpty();
    }

    /// <summary>Seeds a user, a true-crime <c>vi</c> draft episode, one fetched note source and the active price version.</summary>
    private async Task<World> SeedAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, topic: Topic.TrueCrime, language: Language.Vi, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, status: EpisodeStatus.Draft, ct: Ct);

        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = user.Id }, new FakeClock(Start));
        db.StorySources.Add(StorySource.CreateNote(Guid.CreateVersion7(), user.Id, episode.Id, NoteProse, Start).Value);

        if (!db.PriceVersions.Any(version => version.Version == 1))
        {
            var rates = new PriceVersionRates(
                new Dictionary<string, PriceRates> { ["gpt-6-luna"] = new(0.10m, 0.01m, 0.125m, 0.50m) },
                new Dictionary<string, ImagePriceRate>(),
                null,
                TextCostCalculator.DefaultSurchargeThresholdTokens).ToJson();
            db.PriceVersions.Add(PriceVersion.Create(
                1,
                Start.AddDays(-1),
                rates,
                PriceVersionJson.WriteLimits(PriceLimits.D4Defaults),
                "e2e-price-version",
                Start.AddDays(-1)));
        }

        await db.SaveChangesAsync(Ct);

        return new World(user.Id, series.Id, episode.Id);
    }

    /// <summary><c>POST /api/v1/episodes/{id}/fact-sheet/generate</c> on the real Api host; returns the queued run id.</summary>
    private async Task<Guid> GenerateAsync(World world)
    {
        using var factory = new ApiFactory(Database, new FakeClock(Start))
            .WithSetting("Budget:MaxPlatformCostUsdPerDay", "1000")
            .WithSetting("Budget:MaxPlatformCostUsdPerMonth", "100000")
            .WithServices(services =>
            {
                services.RemoveAll<IFactSheetCostEstimator>();
                services.AddSingleton<IFactSheetCostEstimator, FixedEstimator>();
            });
        using var client = factory.ClientFor(world.UserId);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/episodes/{world.EpisodeId}/fact-sheet/generate");
        request.Headers.Add("Idempotency-Key", $"e2e-facts-{Guid.NewGuid():N}");

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        return body.GetProperty("runId").GetGuid();
    }

    /// <summary>
    /// Delivers the request row (enqueues the job), runs the job through the real pipeline, then delivers what the
    /// job wrote (the outcome row) to the realtime handlers. No sleeps, no polling.
    /// </summary>
    private static async Task DeliverAndRunAsync(WorkerHost worker)
    {
        (await worker.RunOutboxOnceAsync(Ct)).ShouldBeGreaterThan(0);
        worker.Jobs.Enqueued.ShouldContain(job => job.JobType == "GenerateFactSheetJob");

        await worker.Jobs.RunPendingAsync(Ct);
        worker.Jobs.Failures.ShouldBeEmpty();

        await worker.RunOutboxOnceAsync(Ct);
    }

    private async Task<WorkerHost> StartWorkerAsync(
        IFactExtractor extractor,
        FakeContentModerator moderator,
        RecordingNotifier notifier,
        Action<IServiceCollection>? configure = null)
    {
        var host = new WorkerHost(Database, new FakeClock(Start))
            .ConfigureServices(services =>
            {
                services.RemoveAll<IFactExtractor>();
                services.AddSingleton(extractor);
                services.RemoveAll<IContentModerator>();
                services.AddSingleton<IContentModerator>(moderator);
                services.RemoveAll<IRealtimeNotifier>();
                services.AddSingleton<IRealtimeNotifier>(notifier);
            });

        if (configure is not null)
        {
            host.ConfigureServices(configure);
        }

        await host.StartAsync(Ct);

        return host;
    }

    /// <summary>The version-1 draft the competing run claims for the episode (the winner of the D9 race).</summary>
    private static FactSheet CompetingDraft(World world, Guid competingRunId, int maxFactId = 0) =>
        FactSheet.CreateDraft(
            world.UserId,
            world.EpisodeId,
            1,
            [],
            new Dictionary<string, Guid>(StringComparer.Ordinal),
            inputTruncated: false,
            maxFactId: maxFactId,
            generationRunId: competingRunId,
            promptVersion: "pv-competing",
            createdAt: Start);

    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId);

    /// <summary>
    /// The deterministic seam of the D9 version race. EF calls <see cref="SavingChangesAsync"/> after the pipeline
    /// read <c>max(version) + 1</c> and built its own draft, and before the batch reaches PostgreSQL: the hook
    /// writes another run's version-1 row for the same episode on a second connection, so the pipeline's own insert
    /// meets <c>ux_fact_sheets_episode_version</c> (<c>23505</c>).
    /// </summary>
    /// <remarks>
    /// An insert made from inside the extractor cannot collide: it lands before <c>SaveAsync</c> reads the version
    /// watermark, so the pipeline would simply take the next version. The competing run is stored <c>failed</c>
    /// because <c>ux_generation_runs_active_episode</c> admits only one <c>queued</c>/<c>running</c> fact-sheet run
    /// per episode — the status is set before the row is added, because a queued insert followed by an update would
    /// trip that same index first.
    /// </remarks>
    private sealed class CompetingVersionInterceptor(
        TestDatabase database,
        World world,
        FakeClock clock) : SaveChangesInterceptor
    {
        /// <summary>How many competing rows the hook wrote; exactly one per test.</summary>
        public int RowsInserted { get; private set; }

        /// <inheritdoc />
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(eventData);

            var claimsTheVersion = eventData.Context?.ChangeTracker
                .Entries<FactSheet>()
                .Any(entry => entry.State == EntityState.Added && entry.Entity.EpisodeId == world.EpisodeId) == true;

            if (RowsInserted > 0 || !claimsTheVersion)
            {
                return result;
            }

            RowsInserted++;

            await using var competing = database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }, clock);
            var run = GenerationRun.Queue(
                    world.UserId,
                    world.SeriesId,
                    world.EpisodeId,
                    GenerationKind.FactSheet,
                    GenerationTrigger.User,
                    null,
                    EpisodeStatus.Draft,
                    null,
                    Guid.CreateVersion7(),
                    0.5m,
                    Start)
                .Value;
            run.Fail(ErrorCodes.FactSheetGenerationFailed, "version_conflict", countsTowardLimit: false, Start.AddSeconds(1));

            competing.GenerationRuns.Add(run);
            competing.FactSheets.Add(CompetingDraft(world, run.Id));
            await competing.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return result;
        }
    }

    /// <summary>
    /// The deterministic seam of the F12 watermark race. The pipeline reads the episode's <c>max(max_fact_id)</c>
    /// during its validation; this hook writes a competing version for the same episode when the run enters its
    /// <c>saving</c> stage — after that read, before the save's own reads. The competitor has handed out fact ids
    /// 1..7, so the draft the pipeline numbered would reuse them.
    /// </summary>
    private sealed class CompetingWatermarkInterceptor(
        TestDatabase database,
        World world,
        FakeClock clock) : SaveChangesInterceptor
    {
        /// <summary>How many competing rows the hook wrote; exactly one per test.</summary>
        public int RowsInserted { get; private set; }

        /// <inheritdoc />
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(eventData);

            var enteringSave = eventData.Context?.ChangeTracker
                .Entries<GenerationRun>()
                .Any(entry => entry.State == EntityState.Modified
                    && entry.Entity.EpisodeId == world.EpisodeId
                    && entry.Entity.Stage == GenerationStage.Saving) == true;

            if (RowsInserted > 0 || !enteringSave)
            {
                return result;
            }

            RowsInserted++;

            await using var competing = database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }, clock);
            var run = GenerationRun.Queue(
                    world.UserId,
                    world.SeriesId,
                    world.EpisodeId,
                    GenerationKind.FactSheet,
                    GenerationTrigger.User,
                    null,
                    EpisodeStatus.Draft,
                    null,
                    Guid.CreateVersion7(),
                    0.5m,
                    Start)
                .Value;
            run.Fail(ErrorCodes.FactSheetGenerationFailed, "version_conflict", countsTowardLimit: false, Start.AddSeconds(1));

            competing.GenerationRuns.Add(run);
            competing.FactSheets.Add(CompetingDraft(world, run.Id, maxFactId: 7));
            await competing.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return result;
        }
    }

    /// <summary>
    /// Deletes the episode and its cascade just before the scripted extraction outcome is returned, so the rest of
    /// the run executes against a world the episode no longer exists in.
    /// </summary>
    private sealed class EpisodeDeletingExtractor(
        TestDatabase database,
        World world,
        IFactExtractor inner) : IFactExtractor
    {
        /// <inheritdoc />
        public async ValueTask<TextCallResult<FactsV1>> ExtractAsync(FactExtractionRequest request, CancellationToken ct)
        {
            await using var admin = database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }, new FakeClock(Start));

            var deleted = await admin.Episodes
                .Where(episode => episode.Id == world.EpisodeId)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
            deleted.ShouldBe(1, "the episode of the in-flight run is the one this test deletes");

            return await inner.ExtractAsync(request, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The ledger port of the two race cases: the P implementation is a no-op, so the refund contract is proven by
    /// capturing the call the pipeline makes.
    /// </summary>
    private sealed class RecordingLedger : ICreditLedger
    {
        /// <summary>Every refund, in order: the D1 reason and the reservation it released.</summary>
        public List<(string Reason, ReservationId Reservation)> Refunds { get; } = [];

        /// <summary>Every settle; a failed run never settles.</summary>
        public List<ReservationId> Settled { get; } = [];

        /// <inheritdoc />
        public ValueTask<ErrorOr<ReservationId>> ReserveAsync(
            Guid userId,
            CreditOperation op,
            Guid referenceId,
            UsageBound bound,
            CancellationToken ct) =>
            throw new NotSupportedException("These runs are queued by the Api host; the job never reserves.");

        /// <inheritdoc />
        public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct)
        {
            Settled.Add(reservationId);

            return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
        }

        /// <inheritdoc />
        public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct)
        {
            Refunds.Add((reason, reservationId));

            return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
        }
    }

    private sealed class RecordingNotifier : IRealtimeNotifier
    {
        private readonly List<(Guid UserId, RealtimeEvent Event)> _events = [];

        public IReadOnlyList<(Guid UserId, RealtimeEvent Event)> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public Task NotifyAsync(Guid userId, RealtimeEvent realtimeEvent, CancellationToken cancellationToken)
        {
            lock (_events)
            {
                _events.Add((userId, realtimeEvent));
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FixedEstimator : IFactSheetCostEstimator
    {
        public Task<ErrorOr<UsageBound>> WorstCaseAsync(CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(new UsageBound(0.5m, 1000, 500));
    }
}
