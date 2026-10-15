using System.Globalization;
using System.Net;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Events;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Persistence.Outbox;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Stories;
using NightTales.TestKit.Telemetry;
using NightTales.TestKit.Text;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;

// Aliased: the enclosing namespace is `…Api.IntegrationTests.SeriesBible`, so the simple name `SeriesBible`
// resolves to that namespace and never to the Domain aggregate (the trap the sibling harness documents).
using SeriesBibleEntity = NightTales.Domain.Series.Bible.SeriesBible;

namespace NightTales.Api.IntegrationTests.SeriesBible;

/// <summary>
/// The shared seam of the bible-proposal job suite (02.6.3-T2, plan §7.1): the world the proposal is bought in,
/// the Worker host the delivery and the job run in, and the raw-SQL probes the assertions read.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <b>not</b> replacing <c>IBibleProposalGenerator</c> or <c>IBibleProposalCostEstimator</c>: only
/// <see cref="ITextClient"/> is scripted, so what these tests drive is the production graph — the DataObject the
/// generator builds, the prompt catalogue and the price version it reads, the D14 meter inside it, the D7 outcome
/// mapping of the job, the Domain validator and the real outbox. The paid provider edge is the one thing no test
/// may reach (02 README §tests).
/// </para>
/// <para>
/// The price version is written by the fixture instead of the seeder: the test template is migrated but never
/// seeded, and the estimator reads <c>price_versions.limits.bible_proposal.max_output_tokens</c>, which the D4
/// default document carries.
/// </para>
/// </remarks>
internal static class BibleProposalJobHarness
{
    /// <summary>The clock of every seeded row (<c>FakeClock</c>), so stamps are deterministic.</summary>
    internal static readonly DateTimeOffset Start = SeriesBibleHarness.Start;

    /// <summary>The token xUnit cancels the running test with.</summary>
    internal static CancellationToken Ct => SeriesBibleHarness.Ct;

    /// <summary>The model every scripted outcome names (the fixture's priced model, <c>gpt-6-luna</c>).</summary>
    internal const string Model = FakeTextClient.Model;

    /// <summary>
    /// The description of the approved story of <see cref="SeriesBibleHarness.SeedApprovedEpisodeAsync"/>: the
    /// first source of the deterministic summary fallback (plan §7.1 step 6).
    /// </summary>
    internal const string StoryDescription = "Một chuyện ma nhẹ nhàng về căn nhà bên sông.";

    /// <summary>The title of the same story, the fallback summary's own title.</summary>
    internal const string StoryTitle = "Căn nhà cuối hành lang";

    /// <summary>One approved episode: the episode id and the version its summary is derived from.</summary>
    /// <param name="Id">The episode.</param>
    /// <param name="VersionId">The approved story version.</param>
    internal sealed record ApprovedEpisode(Guid Id, Guid VersionId);

    // ---------------------------------------------------------------- seeding

    /// <summary>Seeds one owner, one series, its empty bible row and the active price version.</summary>
    /// <param name="database">The class database.</param>
    /// <returns>The world the approval is sent for.</returns>
    internal static async Task<World> SeedAsync(TestDatabase database)
    {
        var world = await SeriesBibleHarness.SeedAsync(database);

        await using var db = database.CreateAppDbContext(new NightTales.TestKit.Auth.TestCurrentUser { Id = world.UserId }, new FakeClock(Start));
        if (!db.PriceVersions.Any(version => version.Version == 1))
        {
            var rates = new PriceVersionRates(
                new Dictionary<string, PriceRates>(StringComparer.Ordinal) { [Model] = new(0.10m, 0.01m, 0.125m, 0.50m) },
                new Dictionary<string, ImagePriceRate>(StringComparer.Ordinal),
                null,
                TextCostCalculator.DefaultSurchargeThresholdTokens).ToJson();

            db.PriceVersions.Add(PriceVersion.Create(
                1,
                Start.AddDays(-1),
                rates,
                PriceVersionJson.WriteLimits(PriceLimits.D4Defaults),
                "bible-job-price-version",
                Start.AddDays(-1)));
        }

        await db.SaveChangesAsync(Ct);

        return world;
    }

    /// <summary>Seeds one approved episode of the world's series.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The world.</param>
    /// <returns>The episode and its approved version.</returns>
    internal static async Task<ApprovedEpisode> ApproveEpisodeAsync(TestDatabase database, World world)
    {
        var episodeId = await SeriesBibleHarness.SeedApprovedEpisodeAsync(database, world);
        var versionId = Guid.Parse(
            SqlProbe.ScalarText(
                database,
                $"SELECT approved_story_version_id FROM episodes WHERE id = '{Pg(episodeId)}'"),
            CultureInfo.InvariantCulture);

        return new ApprovedEpisode(episodeId, versionId);
    }

    /// <summary>Writes one <c>story_approved.v1</c> row with the production serializer.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The world.</param>
    /// <param name="episode">The approved episode the event names.</param>
    /// <param name="approvalSource">Who approved: the user, or the autopilot (F27 auto-accept).</param>
    /// <returns>A task that completes when the row is written.</returns>
    internal static Task InsertStoryApprovedAsync(
        TestDatabase database,
        World world,
        ApprovedEpisode episode,
        ApprovalSource approvalSource)
    {
        var id = Guid.CreateVersion7();
        var payload = JsonSerializer.Serialize(
            new StoryApproved(episode.Id, world.SeriesId, world.UserId, episode.VersionId, approvalSource, Start),
            OutboxJson.Options);

        return database.ExecuteAsync(
            $"""
             INSERT INTO outbox_messages (id, type, payload, user_id, occurred_at, next_attempt_at, attempts)
             VALUES ('{id}', 'story_approved.v1', '{payload}', '{Pg(world.UserId)}', '{Start:O}', '{Start:O}', 0)
             """,
            Ct);
    }

    // ---------------------------------------------------------------- the host

    /// <summary>
    /// Starts the Worker host of these tests: the production graph with the provider edge scripted
    /// (<see cref="ITextClient"/>), the platform budget replaced by the scriptable guard and the credit ledger by
    /// the recording one. Every other collaborator — the generator, the estimator, the price catalog, the prompt
    /// catalog, the meter, the validator, the applier and the outbox — is production.
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="text">The scripted provider edge; its outcomes are queued before the host starts.</param>
    /// <param name="guard">The scriptable budget guard, when a test drives a refusal.</param>
    /// <param name="ledger">The recording ledger, when a test asserts the reservation's settle or refund.</param>
    /// <param name="sink">The log sink, when a test asserts what the run logged.</param>
    /// <param name="configure">Any further service replacement.</param>
    /// <param name="beforeStart">Settings and registrations that must land before the host builds, for example the <c>Bible:Proposal</c> caps.</param>
    /// <returns>The started host; the caller disposes it.</returns>
    internal static async Task<WorkerHost> StartWorkerAsync(
        TestDatabase database,
        FakeTextClient text,
        FakeBudgetGuard? guard = null,
        RecordingLedger? ledger = null,
        TestLogSink? sink = null,
        Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? configure = null,
        Action<WorkerHost>? beforeStart = null)
    {
        var host = new WorkerHost(database, new FakeClock(Start)).ConfigureServices(services =>
        {
            services.RemoveAll<ITextClient>();
            services.AddSingleton<ITextClient>(text);

            services.RemoveAll<IBudgetGuard>();
            services.AddSingleton<IBudgetGuard>(guard ?? new FakeBudgetGuard());

            services.RemoveAll<ICreditLedger>();
            services.AddSingleton<ICreditLedger>(ledger ?? new RecordingLedger());

            if (sink is not null)
            {
                services.AddSingleton<Serilog.Core.ILogEventSink>(sink);
            }
        });

        if (configure is not null)
        {
            host.ConfigureServices(configure);
        }

        if (sink is not null)
        {
            host.WithTelemetry();
        }

        if (beforeStart is not null)
        {
            beforeStart(host);
        }

        await host.StartAsync(Ct);

        return host;
    }

    // ---------------------------------------------------------------- driving

    /// <summary>Delivers one outbox batch and asserts the delivery claimed at least one row.</summary>
    /// <param name="worker">The started host.</param>
    /// <returns>How many rows the batch claimed.</returns>
    internal static async Task<int> DeliverAsync(WorkerHost worker)
    {
        var claimed = await worker.RunOutboxOnceAsync(Ct);
        claimed.ShouldBeGreaterThan(0, "the seeded story_approved row must be due");

        return claimed;
    }

    /// <summary>Runs the jobs the deliveries enqueued, in one wave, and asserts none of them threw.</summary>
    /// <param name="worker">The started host.</param>
    /// <returns>How many jobs ran.</returns>
    internal static async Task<int> RunJobsAsync(WorkerHost worker)
    {
        var ran = await worker.Jobs.RunPendingAsync(Ct);
        worker.Jobs.Failures.ShouldBeEmpty();

        return ran;
    }

    /// <summary>
    /// Runs the inline scheduler until it stops enqueueing, so a wave that enqueues (a deferred handoff that
    /// queues the next proposal) is driven to its end without sleeps. Deterministic: nothing else enqueues.
    /// </summary>
    /// <param name="worker">The started host.</param>
    /// <param name="maxWaves">The safety bound; the loop stops as soon as a wave enqueues nothing new.</param>
    /// <returns>The waves that ran and how many jobs each executed.</returns>
    internal static async Task<IReadOnlyList<int>> RunJobsUntilQuietAsync(WorkerHost worker, int maxWaves = 6)
    {
        var waves = new List<int>();

        for (var wave = 0; wave < maxWaves; wave++)
        {
            var before = worker.Jobs.Enqueued.Count;
            var ran = await RunJobsAsync(worker);
            waves.Add(ran);

            if (worker.Jobs.Enqueued.Count == before)
            {
                break;
            }
        }

        return waves;
    }

    /// <summary>The delivery of one approval, end to end: the outbox batch, then the waves of jobs it enqueues.</summary>
    /// <param name="worker">The started host.</param>
    /// <returns>A task that completes when the run of that approval reached its status.</returns>
    internal static async Task DeliverAndRunAsync(WorkerHost worker)
    {
        await DeliverAsync(worker);
        await RunJobsUntilQuietAsync(worker);
    }

    // ---------------------------------------------------------------- probes

    /// <summary>
    /// The run rows of the series as one string per row, in queue order: NULL episode, kind, trigger, status,
    /// stage, error code, error detail, attempt, counted, priced, provenanced.
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="seriesId">The series.</param>
    /// <returns>One line per run, joined with <c> || </c>.</returns>
    internal static string RunRows(TestDatabase database, Guid seriesId) =>
        SqlProbe.ScalarText(
            database,
            $"""
             SELECT coalesce(string_agg(row, ' || ' ORDER BY queued_at, id), '') FROM (
                 SELECT (episode_id IS NULL)::text || '|' || kind || '|' || trigger || '|' || status || '|' || stage
                     || '|' || coalesce(error_code, '-') || '|' || coalesce(error_detail, '-')
                     || '|' || attempt::text || '|' || counts_toward_limit::text
                     || '|' || (coalesce(cost_usd, 0) > 0)::text
                     || '|' || (coalesce(prompt_version, '') <> '')::text AS row,
                     queued_at, id
                 FROM generation_runs WHERE series_id = '{Pg(seriesId)}'
             ) AS runs
             """);

    /// <summary>How many <c>generation_runs</c> rows the series has.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="seriesId">The series.</param>
    /// <returns>The row count.</returns>
    internal static long RunCount(TestDatabase database, Guid seriesId) =>
        SqlProbe.Count(database, $"SELECT count(*) FROM generation_runs WHERE series_id = '{Pg(seriesId)}'");

    /// <summary>The single run id of the series; asserts there is exactly one.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="seriesId">The series.</param>
    /// <returns>The run id.</returns>
    internal static Guid RunId(TestDatabase database, Guid seriesId)
    {
        var id = SqlProbe.ScalarText(database, $"SELECT id::text FROM generation_runs WHERE series_id = '{Pg(seriesId)}'");

        return Guid.Parse(id, CultureInfo.InvariantCulture);
    }

    /// <summary>The reservation the series' single run records.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="seriesId">The series.</param>
    /// <returns>The reservation id.</returns>
    internal static ReservationId Reservation(TestDatabase database, Guid seriesId) =>
        new(Guid.Parse(
            SqlProbe.ScalarText(database, $"SELECT reservation_id::text FROM generation_runs WHERE series_id = '{Pg(seriesId)}'"),
            CultureInfo.InvariantCulture));

    /// <summary>
    /// The <c>bible_proposal</c> block of the run's <c>models</c> document: model ops, kept, dropped, the total
    /// cap and the four drop reasons this suite exercises, then the model.
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="seriesId">The series.</param>
    /// <returns>The block as one string.</returns>
    internal static string Provenance(TestDatabase database, Guid seriesId) =>
        SqlProbe.ScalarText(
            database,
            $"""
             SELECT coalesce(models->'bible_proposal'->>'model_ops', '0')
                 || '|' || coalesce(models->'bible_proposal'->>'kept_ops', '0')
                 || '|' || coalesce(models->'bible_proposal'->>'dropped_ops', '0')
                 || '|' || coalesce(models->'bible_proposal'->'caps'->>'max_ops', '0')
                 || '|' || coalesce(models->'bible_proposal'->'drops'->>'unknown_kind', '0')
                 || '|' || coalesce(models->'bible_proposal'->'drops'->>'forbidden_character', '0')
                 || '|' || coalesce(models->'bible_proposal'->'drops'->>'duplicate', '0')
                 || '|' || coalesce(models->'bible_proposal'->'drops'->>'over_cap', '0')
                 || '|' || coalesce(models->'bible_proposal'->>'model', '-')
             FROM generation_runs WHERE series_id = '{Pg(seriesId)}'
             """);

    /// <summary>The bible row's own columns: version, whether a proposal is pending, its source episode.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="seriesId">The series.</param>
    /// <returns>The three fields as one string.</returns>
    internal static string BibleColumns(TestDatabase database, Guid seriesId) =>
        SqlProbe.ScalarText(
            database,
            $"""
             SELECT version::text || '|' || (proposed_content IS NULL)::text
                 || '|' || coalesce(proposed_from_episode_id::text, '-')
             FROM series_bibles WHERE series_id = '{Pg(seriesId)}'
             """);

    /// <summary>How many <c>usage_records</c> rows the owner has.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="userId">The owner.</param>
    /// <returns>The row count.</returns>
    internal static long UsageCount(TestDatabase database, Guid userId) =>
        SqlProbe.Count(database, $"SELECT count(*) FROM usage_records WHERE user_id = '{Pg(userId)}'");

    /// <summary>
    /// The one <c>usage_records</c> row of the owner: operation, price version, a positive cost, whether it names
    /// the approved episode and the run, and the provider.
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="userId">The owner.</param>
    /// <param name="episodeId">The approved episode the call was bought for.</param>
    /// <param name="seriesId">The series, for the run lookup.</param>
    /// <returns>The row as one string.</returns>
    internal static string UsageRow(TestDatabase database, Guid userId, Guid episodeId, Guid seriesId) =>
        SqlProbe.ScalarText(
            database,
            $"""
             SELECT operation || '|' || coalesce(price_version::text, '-') || '|' || (coalesce(cost_usd, 0) > 0)::text
                 || '|' || (episode_id = '{Pg(episodeId)}')::text
                 || '|' || (generation_run_id = (SELECT id FROM generation_runs WHERE series_id = '{Pg(seriesId)}'))::text
                 || '|' || estimated::text
             FROM usage_records WHERE user_id = '{Pg(userId)}'
             """);

    /// <summary>How many <c>moderation_events</c> rows the owner has.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="userId">The owner.</param>
    /// <returns>The row count.</returns>
    internal static long ModerationCount(TestDatabase database, Guid userId) =>
        SqlProbe.Count(database, $"SELECT count(*) FROM moderation_events WHERE user_id = '{Pg(userId)}'");

    /// <summary>
    /// The one <c>moderation_events</c> row of the owner: source, stage, whether it names the approved episode,
    /// the categories and whether an excerpt was written.
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="userId">The owner.</param>
    /// <param name="episodeId">The approved episode.</param>
    /// <returns>The row as one string.</returns>
    internal static string ModerationRow(TestDatabase database, Guid userId, Guid episodeId) =>
        SqlProbe.ScalarText(
            database,
            $"""
             SELECT source || '|' || stage || '|' || (episode_id = '{Pg(episodeId)}')::text
                 || '|' || array_to_string(categories, ',') || '|' || (excerpt IS NULL)::text
             FROM moderation_events WHERE user_id = '{Pg(userId)}'
             """);

    /// <summary>How many <c>series.bible_proposal_accepted</c> audit rows the owner's series carries.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="userId">The owner.</param>
    /// <returns>The row count.</returns>
    internal static long AutoAcceptAuditCount(TestDatabase database, Guid userId) =>
        SqlProbe.Count(
            database,
            $"""
             SELECT count(*) FROM audit_logs
             WHERE action = 'series.bible_proposal_accepted' AND subject_user_id = '{Pg(userId)}'
             """);

    /// <summary>
    /// The autopilot audit row of the owner: action, actor type, whether the actor is the owner, the entity, the
    /// version pair, the counts and the episode the ops came from.
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="userId">The owner.</param>
    /// <param name="seriesId">The series.</param>
    /// <returns>The row as one string.</returns>
    internal static string AutoAcceptAuditRow(TestDatabase database, Guid userId, Guid seriesId) =>
        SqlProbe.ScalarText(
            database,
            $"""
             SELECT action || '|' || actor_type || '|' || (actor_user_id = '{Pg(userId)}')::text
                 || '|' || entity_type || '|' || (entity_id = '{Pg(seriesId)}')::text
                 || '|' || coalesce(data ->> 'from_version', '-') || '|' || coalesce(data ->> 'to_version', '-')
                 || '|' || coalesce(data ->> 'applied', '-') || '|' || coalesce(data ->> 'skipped', '-')
                 || '|' || coalesce(data ->> 'proposed_from_episode_id', '-')
             FROM audit_logs
             WHERE action = 'series.bible_proposal_accepted' AND subject_user_id = '{Pg(userId)}'
             """);

    /// <summary>
    /// <c>GET /api/v1/series/{id}/bible</c> on the real Api host: the stored bible as the wire contract
    /// (<c>bible.v1</c> plus the pending proposal) serves it.
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="userId">The owner the request is made as.</param>
    /// <param name="seriesId">The series.</param>
    /// <returns>The detached response body.</returns>
    internal static async Task<JsonElement> BibleAsync(TestDatabase database, Guid userId, Guid seriesId)
    {
        using var factory = SeriesBibleHarness.Factory(database);
        using var client = factory.ClientFor(userId);
        using var response = await SeriesBibleHarness.GetAsync(client, seriesId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        return await SeriesBibleHarness.JsonAsync(response);
    }

    /// <summary>The op kinds of the response's pending proposal, in order.</summary>
    /// <param name="bible">The response body.</param>
    /// <returns>The kinds, or an empty list when no proposal is pending.</returns>
    internal static IReadOnlyList<string> ProposalKinds(JsonElement bible) =>
        bible.TryGetProperty("proposal", out var proposal) && proposal.ValueKind == JsonValueKind.Object
            ? [.. proposal.GetProperty("ops").EnumerateArray().Select(op => op.GetProperty("kind").GetString()!)]
            : [];

    /// <summary>The <c>summary</c> of every summary op of the pending proposal, in order.</summary>
    /// <param name="bible">The response body.</param>
    /// <returns>The summary texts.</returns>
    internal static IReadOnlyList<string> ProposalSummaries(JsonElement bible) =>
        bible.GetProperty("proposal").GetProperty("ops").EnumerateArray()
            .Where(op => op.GetProperty("kind").GetString() == "add_summary")
            .Select(op => op.GetProperty("summary").GetString() ?? string.Empty)
            .ToList();

    /// <summary>The <c>name</c> of every character or place op of the pending proposal, in order.</summary>
    /// <param name="bible">The response body.</param>
    /// <returns>The names.</returns>
    internal static IReadOnlyList<string> ProposalNames(JsonElement bible) =>
        bible.GetProperty("proposal").GetProperty("ops").EnumerateArray()
            .Where(op => op.GetProperty("kind").GetString() is "add_character" or "add_place")
            .Select(op => op.GetProperty("name").GetString() ?? string.Empty)
            .ToList();

    /// <summary>Whether the response's content document carries the stored pending proposal.</summary>
    /// <param name="bible">The response body.</param>
    /// <returns>True when a proposal is pending.</returns>
    internal static bool HasProposal(JsonElement bible) =>
        bible.TryGetProperty("proposal", out var proposal) && proposal.ValueKind == JsonValueKind.Object;

    /// <summary>A Postgres uuid literal for the raw-SQL probes.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The literal.</returns>
    internal static string Pg(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>
    /// The credit port of these tests: it hands out a reservation per admission and records every settle and
    /// refund, so the D1 contract (exactly one release per run) is asserted rather than assumed. The P-phase
    /// implementation is a no-op, so the port is the only place the release is observable.
    /// </summary>
    internal sealed class RecordingLedger : ICreditLedger
    {
        /// <summary>Every reservation handed out, in admission order.</summary>
        public List<ReservationId> Reserved { get; } = [];

        /// <summary>Every settle, in order; a failed run never settles.</summary>
        public List<ReservationId> Settled { get; } = [];

        /// <summary>Every refund, in order: the D1 reason and the reservation it released.</summary>
        public List<(string Reason, ReservationId Reservation)> Refunds { get; } = [];

        /// <inheritdoc />
        public ValueTask<ErrorOr<ReservationId>> ReserveAsync(
            Guid userId,
            CreditOperation op,
            Guid referenceId,
            UsageBound bound,
            CancellationToken ct)
        {
            var reservation = new ReservationId(Guid.CreateVersion7());
            Reserved.Add(reservation);

            return ValueTask.FromResult<ErrorOr<ReservationId>>(reservation);
        }

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
}
