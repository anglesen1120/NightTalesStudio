using Metrics = System.Diagnostics.Metrics;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Application.Features.Autopilot.Ideas.Abstractions;
using NightTales.Application.Features.Autopilot.Jobs;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Application.Features.Autopilot.Orchestration.Steps;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Autopilot;
using NightTales.Domain.Stories.Gates;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Structure;
using NightTales.Domain.Usage;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Telemetry;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Serilog.Core;

namespace NightTales.Api.IntegrationTests.Autopilot;

/// <summary>
/// The shared harness of 02.8.3-T1's integration tests: a real <see cref="WorkerHost"/> over the test database with
/// the production <c>AutopilotPlanJob</c>, <see cref="AutopilotRunOrchestrator"/> and step handlers, a
/// <see cref="FakeClock"/>, the inline job scheduler, and two scripted seams.
/// </summary>
/// <remarks>
/// <para>
/// <b>Public surface, for the tests that only read this file.</b>
/// <list type="bullet">
/// <item><see cref="StartAsync"/> starts the host (flag row, budget settings, price version).</item>
/// <item><see cref="CreateOwnerAsync"/>, <see cref="CreateChannelAsync"/>,
/// <see cref="CreateSeriesAsync"/> and <see cref="CreateAuthorizedSeriesAsync"/> seed the world.</item>
/// <item><see cref="TickAsync"/> runs one <c>AutopilotPlanJob</c> tick; <see cref="CountRunsAsync"/>,
/// <see cref="RunsAsync"/> and <see cref="RunAsync"/> read what it wrote.</item>
/// <item><see cref="CreateRunAsync"/> seeds a run position, <see cref="AdvanceAsync"/> drives one tick of a run
/// through the real orchestrator, and <see cref="CountGenerationRunsAsync"/> counts the paid-run rows.</item>
/// <item><see cref="Ideas"/> and <see cref="Gate"/> are the scripted seams; <see cref="Logs"/> is the log sink.</item>
/// </list>
/// </para>
/// <para>
/// <b>Why only two seams are scripted.</b> <see cref="IIdeaPlanner"/> and <see cref="IAutoApprovalGate"/> are the
/// two collaborators of the steps these tests drive (idea, story gate). The publishing seams (the consent gate, the
/// upload/publish port, <c>IChannelAuditPolicy</c>) are deliberately not faked here: a fake
/// <c>IChannelAuditPolicy</c> in DI is dead code, because <c>AutopilotPublicationCreator</c>'s DI constructor
/// builds <c>PlatformChannelAuditPolicy</c> itself, so a test that reaches publication must set
/// <c>YouTube:AuditStatus</c> through <see cref="WorkerHost.WithSetting"/> and fake the publisher port instead.
/// A test that needs the real idea planner (with the TestKit text client) can re-register it through
/// <see cref="StartAsync"/>'s <c>configureServices</c>, which runs after this harness's own registrations.
/// </para>
/// <para>
/// <b>Seeding.</b> Every row goes through a TestKit builder or, where the TestKit has no factory (the series'
/// autopilot pointer, the authorization row, a pause), through the same raw SQL a migration test uses. A run is
/// seeded through <see cref="AutopilotRunBuilder"/>, which writes the scenario columns of a position the
/// transition table would otherwise refuse to reach in one step.
/// </para>
/// </remarks>
public sealed class AutopilotTickHarness : IAsyncDisposable
{
    /// <summary>The <c>feature_flags</c> key the plan job's registration and its guard read.</summary>
    public const string AutopilotFlagKey = AutopilotPlanJob.FeatureKey;

    /// <summary>The <c>text_hash</c> every seeded authorization carries (the column is <c>char(64)</c>).</summary>
    private static readonly string TestTextHash = new('a', 64);

    private AutopilotTickHarness(
        TestDatabase database,
        FakeClock clock,
        WorkerHost host,
        TestLogSink logs,
        ScriptedIdeaPlanner ideas,
        ScriptedAutoApprovalGate gate)
    {
        Database = database;
        Clock = clock;
        Host = host;
        Logs = logs;
        Ideas = ideas;
        Gate = gate;
    }

    /// <summary>The class's test database.</summary>
    public TestDatabase Database { get; }

    /// <summary>The host's clock: every step's "now" is this instance.</summary>
    public FakeClock Clock { get; }

    /// <summary>The started Worker host (its <see cref="WorkerHost.Jobs"/> recorded the recurring registrations).</summary>
    public WorkerHost Host { get; }

    /// <summary>Every Serilog event the host emitted (the host is started with telemetry, so log assertions work).</summary>
    public TestLogSink Logs { get; }

    /// <summary>The scripted <see cref="IIdeaPlanner"/>: it persists a real idea row, so <c>create_episode</c> can adopt it.</summary>
    public ScriptedIdeaPlanner Ideas { get; }

    /// <summary>The scripted <see cref="IAutoApprovalGate"/>: a deterministic story verdict, and a barrier hook.</summary>
    public ScriptedAutoApprovalGate Gate { get; }

    /// <summary>
    /// Writes the flag row, builds and starts the Worker host with the budget settings a paid step needs, and pins
    /// the price version every priced step reads.
    /// </summary>
    /// <param name="database">The class's database.</param>
    /// <param name="clock">The clock the host resolves.</param>
    /// <param name="autopilotFlagEnabled">The value of the <c>youtube_publish</c> row.</param>
    /// <param name="configureServices">Extra registrations, applied last (so they can replace this harness's fakes).</param>
    /// <param name="ct">Cancels the seed and the boot.</param>
    public static async Task<AutopilotTickHarness> StartAsync(
        TestDatabase database,
        FakeClock clock,
        bool autopilotFlagEnabled = true,
        Action<IServiceCollection>? configureServices = null,
        CancellationToken ct = default)
    {
        await SetFlagAsync(database, autopilotFlagEnabled, clock.UtcNow, ct);

        var logs = new TestLogSink();
        var ideas = new ScriptedIdeaPlanner(database, clock);
        var gate = new ScriptedAutoApprovalGate();

        var host = new WorkerHost(database, clock)
            .WithSetting("Budget:MaxPlatformCostUsdPerDay", "1000")
            .WithSetting("Budget:MaxPlatformCostUsdPerMonth", "100000")
            .WithTelemetry()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IIdeaPlanner>();
                services.AddSingleton<IIdeaPlanner>(ideas);
                services.RemoveAll<IAutoApprovalGate>();
                services.AddSingleton<IAutoApprovalGate>(gate);
                services.AddSingleton<ILogEventSink>(logs);
                configureServices?.Invoke(services);
            });

        await host.StartAsync(ct);

        var harness = new AutopilotTickHarness(database, clock, host, logs, ideas, gate);
        await harness.EnsurePriceVersionAsync(ct);

        return harness;
    }

    /// <summary>The default settings a test series needs: a valid §3.1 document.</summary>
    /// <param name="daysOfWeek">The cadence days; every day when omitted.</param>
    /// <param name="publishTime">The local publish time; 19:00 when omitted.</param>
    /// <param name="timeZoneId">The cadence zone; <c>Asia/Ho_Chi_Minh</c> when omitted.</param>
    /// <param name="dailyCap">The per-channel cap; 1 when omitted.</param>
    /// <param name="bufferDays">The ready buffer; 2 when omitted.</param>
    /// <param name="vetoWindowHours">The owner veto window; 12 when omitted.</param>
    public static AutopilotSettings Settings(
        IReadOnlyList<DayOfWeek>? daysOfWeek = null,
        TimeOnly? publishTime = null,
        string timeZoneId = "Asia/Ho_Chi_Minh",
        int dailyCap = 1,
        int bufferDays = 2,
        int vetoWindowHours = 12) =>
        new(
            AutopilotSettingsValidator.SupportedSchemaVersion,
            new AutopilotCadence(
                daysOfWeek ?? [.. Enum.GetValues<DayOfWeek>()],
                publishTime ?? new TimeOnly(19, 0),
                timeZoneId),
            dailyCap,
            bufferDays,
            PrivacyStatus.Public,
            vetoWindowHours,
            PreAuditMode.KeepCadencePrivate,
            1,
            "d4",
            "ghien",
            [],
            null,
            null);

    /// <summary>The canonical <c>series.autopilot_settings</c> document of <paramref name="settings"/>.</summary>
    public static string SettingsJson(AutopilotSettings settings) => AutopilotSettingsValidator.Serialize(settings);

    /// <summary>Inserts one user row and returns its id.</summary>
    public async Task<Guid> CreateOwnerAsync(CancellationToken ct = default)
    {
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);

        return user.Id;
    }

    /// <summary>Inserts one active <c>channel_connections</c> row owned by <paramref name="userId"/>.</summary>
    public async Task<Guid> CreateChannelAsync(Guid userId, string? displayName = null, CancellationToken ct = default)
    {
        var connection = await new ChannelConnectionBuilder(Database, AsUser(userId), Clock)
            .CreateActiveAsync(userId, displayName, ct);

        return connection.Id;
    }

    /// <summary>
    /// Inserts one autopilot series: the series row, its channel (when one is passed), its authorization row and the
    /// <c>current_autopilot_authorization_id</c> pointer the planner snapshots onto every slot.
    /// </summary>
    /// <param name="userId">The owner.</param>
    /// <param name="settings">The cadence document.</param>
    /// <param name="channelConnectionId">The series' default channel; created when omitted.</param>
    /// <param name="topic">The series topic; <see cref="Topic.Ghost"/> when omitted.</param>
    /// <param name="autopilotEnabled">The <c>autopilot_enabled</c> column.</param>
    /// <param name="pausedAt">A <c>autopilot_paused_at</c> stamp, for the pause cases.</param>
    /// <param name="archivedAt">An <c>archived_at</c> stamp, for the archived case.</param>
    /// <param name="authorize">False to leave the series unauthorized (the <c>awaiting_authorization</c> case).</param>
    /// <param name="ct">Cancels the seed.</param>
    public async Task<SeededSeries> CreateSeriesAsync(
        Guid userId,
        AutopilotSettings settings,
        Guid? channelConnectionId = null,
        Topic topic = Topic.Ghost,
        bool autopilotEnabled = true,
        DateTimeOffset? pausedAt = null,
        DateTimeOffset? archivedAt = null,
        bool authorize = true,
        CancellationToken ct = default)
    {
        var channel = channelConnectionId ?? await CreateChannelAsync(userId, ct: ct);

        var series = await new SeriesBuilder(Database, AsUser(userId), Clock).CreateAsync(
            userId,
            topic: topic,
            autopilotEnabled: autopilotEnabled,
            autopilotSettings: SettingsJson(settings),
            ct: ct);

        var authorizationId = authorize ? await InsertAuthorizationAsync(userId, series.Id, topic, ct) : (Guid?)null;

        await UpdateSeriesAsync(
            series.Id,
            $"default_channel_id = '{channel}', current_autopilot_authorization_id = "
            + (authorizationId is { } id ? $"'{id}'" : "NULL")
            + (pausedAt is { } paused ? $", autopilot_paused_at = '{paused:O}'" : string.Empty)
            + (archivedAt is { } archived ? $", archived_at = '{archived:O}'" : string.Empty),
            ct);

        return new SeededSeries(userId, series.Id, channel, authorizationId, settings);
    }

    /// <summary>A <see cref="CreateSeriesAsync"/> with the defaults a run-producing test wants.</summary>
    public Task<SeededSeries> CreateAuthorizedSeriesAsync(
        Guid userId,
        AutopilotSettings? settings = null,
        Guid? channelConnectionId = null,
        Topic topic = Topic.Ghost,
        CancellationToken ct = default) =>
        CreateSeriesAsync(userId, settings ?? Settings(), channelConnectionId, topic, ct: ct);

    /// <summary>Runs one <c>AutopilotPlanJob</c> tick at the harness's clock reading.</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        using var scope = Host.CreateScope();

        await scope.ServiceProvider.GetRequiredService<AutopilotPlanJob>().RunAsync(ct);
    }

    /// <summary>Every <c>autopilot_runs</c> row of one series (all rows when <paramref name="seriesId"/> is null).</summary>
    public async Task<IReadOnlyList<AutopilotRun>> RunsAsync(
        Guid userId,
        Guid? seriesId = null,
        CancellationToken ct = default)
    {
        await using var db = Database.CreateAppDbContext(AsUser(userId), Clock);

        var query = db.AutopilotRuns.AsNoTracking().Where(run => run.UserId == userId);
        if (seriesId is { } series)
        {
            query = query.Where(run => run.SeriesId == series);
        }

        return await query.OrderBy(run => run.SlotAt).ToListAsync(ct);
    }

    /// <summary>One run row, read through the owner's tenant filter.</summary>
    public async Task<AutopilotRun> RunAsync(Guid userId, Guid runId, CancellationToken ct = default)
    {
        await using var db = Database.CreateAppDbContext(AsUser(userId), Clock);

        return await db.AutopilotRuns.AsNoTracking().SingleAsync(run => run.Id == runId, ct);
    }

    /// <summary>Counts every <c>autopilot_runs</c> row of the database.</summary>
    public async Task<int> CountRunsAsync(CancellationToken ct = default)
    {
        var rows = await Database.QueryStringsAsync("SELECT count(*)::text FROM autopilot_runs", ct);

        return int.Parse(rows[0], System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Counts the <c>generation_runs</c> rows of one series (the paid side effect a step must not repeat).</summary>
    public async Task<int> CountGenerationRunsAsync(Guid seriesId, CancellationToken ct = default)
    {
        var rows = await Database.QueryStringsAsync(
            $"SELECT count(*)::text FROM generation_runs WHERE series_id = '{seriesId}'",
            ct);

        return int.Parse(rows[0], System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Reads the <c>idempotency_key</c> of every <c>generation_runs</c> row of one series.</summary>
    public Task<IReadOnlyList<string>> GenerationRunKeysAsync(Guid seriesId, CancellationToken ct = default) =>
        Database.QueryStringsAsync(
            $"SELECT idempotency_key FROM generation_runs WHERE series_id = '{seriesId}' ORDER BY created_at",
            ct);

    /// <summary>Inserts one run at a chosen position of the state machine.</summary>
    public Task<AutopilotRun> CreateRunAsync(
        Guid userId,
        Guid seriesId,
        DateTimeOffset slotAt,
        DateTimeOffset startAt,
        Guid? authorizationId = null,
        AutopilotRunState? state = null,
        string? step = null,
        Guid? episodeId = null,
        Guid? ideaId = null,
        Guid? publicationId = null,
        AutopilotRunOutcome? outcome = null,
        AutopilotPauseReason? pauseReason = null,
        int attempt = 0,
        int renderAttempts = 0,
        CancellationToken ct = default) =>
        new AutopilotRunBuilder(Database, AsUser(userId), Clock).CreateAsync(
            userId,
            seriesId,
            slotAt: slotAt,
            startAt: startAt,
            now: Clock.UtcNow,
            authorizationId: authorizationId,
            episodeId: episodeId,
            ideaId: ideaId,
            publicationId: publicationId,
            state: state,
            outcome: outcome,
            step: step,
            pauseReason: pauseReason,
            attempt: attempt,
            renderAttempts: renderAttempts,
            ct: ct);

    /// <summary>Drives one tick of one run through the real orchestrator, as a system context of its owner.</summary>
    public async Task<ErrorOr<AdvanceResult>> AdvanceAsync(
        Guid runId,
        Guid userId,
        CancellationToken ct = default)
    {
        using var scope = Host.CreateScope();

        var orchestrator = scope.ServiceProvider.GetRequiredService<IAutopilotRunOrchestrator>();

        return await orchestrator.AdvanceAsync(runId, userId, ct);
    }

    /// <summary>Inserts one episode of a series through the TestKit builder.</summary>
    public async Task<Guid> CreateEpisodeAsync(
        Guid userId,
        Guid seriesId,
        EpisodeStatus status = EpisodeStatus.StoryReview,
        CancellationToken ct = default)
    {
        var episode = await new EpisodeBuilder(Database, AsUser(userId), Clock)
            .CreateAsync(userId, seriesId, status: status, ct: ct);

        return episode.Id;
    }

    /// <summary>Runs one raw <c>UPDATE series SET …</c> fragment; the harness's escape hatch for the plan-job cases.</summary>
    public Task UpdateSeriesAsync(Guid seriesId, string setClause, CancellationToken ct = default) =>
        Database.ExecuteAsync($"UPDATE series SET {setClause} WHERE id = '{seriesId}'", ct);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Host.DisposeAsync();

    /// <summary>Writes (or deletes) the one flag row the plan job reads; a missing row is the flag-off case.</summary>
    private static async Task SetFlagAsync(TestDatabase database, bool enabled, DateTimeOffset now, CancellationToken ct)
    {
        await database.ExecuteAsync($"DELETE FROM feature_flags WHERE key = '{AutopilotFlagKey}'", ct);

        if (enabled)
        {
            await database.ExecuteAsync(
                "INSERT INTO feature_flags (key, enabled, rules, created_at, updated_at) "
                + $"VALUES ('{AutopilotFlagKey}', true, '{{}}', '{now:O}', '{now:O}')",
                ct);
        }
    }

    /// <summary>
    /// Pins the price version every priced step reads. Copied from the story-generation end-to-end host: the
    /// version is active from yesterday, so the run's <c>now</c> always finds it.
    /// </summary>
    private async Task EnsurePriceVersionAsync(CancellationToken ct)
    {
        await using var db = Database.CreateAppDbContext(clock: Clock);

        if (await db.PriceVersions.AnyAsync(version => version.Version == 1, ct))
        {
            return;
        }

        var activeFrom = Clock.UtcNow.AddDays(-1);
        var rates = new PriceVersionRates(
            new Dictionary<string, PriceRates> { ["gpt-6-luna"] = new(0.10m, 0.01m, 0.125m, 0.50m) },
            new Dictionary<string, ImagePriceRate>(),
            null,
            TextCostCalculator.DefaultSurchargeThresholdTokens).ToJson();

        db.PriceVersions.Add(PriceVersion.Create(
            1,
            activeFrom,
            rates,
            PriceVersionJson.WriteLimits(PriceLimits.D4Defaults),
            "autopilot-tick-harness",
            activeFrom));

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Inserts the accepted authorization row the plan job snapshots onto a run.</summary>
    private async Task<Guid> InsertAuthorizationAsync(
        Guid userId,
        Guid seriesId,
        Topic topic,
        CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        var stamp = Clock.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        await Database.ExecuteAsync(
            "INSERT INTO series_autopilot_authorizations (id, user_id, series_id, version, text_version, "
            + "text_hash, topic, cadence, privacy_status, accepted_at, created_at, updated_at) "
            + $"VALUES ('{id}', '{userId}', '{seriesId}', 1, 'ai.v1', '{TestTextHash}', '{(topic == Topic.TrueCrime ? "true_crime" : "ghost")}', "
            + $"'weekly', 'public', '{stamp}', '{stamp}', '{stamp}')",
            ct);

        return id;
    }

    private static TestCurrentUser AsUser(Guid userId) => new() { Id = userId };
}

/// <summary>One seeded autopilot series and the channel and authorization it points at.</summary>
/// <param name="UserId">The owner.</param>
/// <param name="SeriesId">The series.</param>
/// <param name="ChannelConnectionId">The series' default channel.</param>
/// <param name="AuthorizationId">The accepted authorization, or <see langword="null"/> when the series is not authorized.</param>
/// <param name="Settings">The cadence document the series carries.</param>
public sealed record SeededSeries(
    Guid UserId,
    Guid SeriesId,
    Guid ChannelConnectionId,
    Guid? AuthorizationId,
    AutopilotSettings Settings);

/// <summary>
/// The scripted <see cref="IIdeaPlanner"/> of the tick harness. It persists a real <c>episode_ideas</c> row through
/// the domain factory and marks it picked, because <c>create_episode</c> re-reads the row by <c>run.idea_id</c> and
/// calls <see cref="MarkUsedAsync"/> after the episode exists: a fake that answered an <see cref="IdeaPick"/> without
/// persisting the idea would fail the next step with <c>idea_missing</c>.
/// </summary>
/// <remarks>
/// <see cref="BeforeAnswer"/> is the barrier hook: it is awaited after the call is recorded and before the idea is
/// written, which is where a test parks the first tick to race a second one.
/// </remarks>
public sealed class ScriptedIdeaPlanner(TestDatabase database, NightTales.Domain.Common.IClock clock) : IIdeaPlanner
{
    private readonly List<Guid> _calls = [];
    private readonly Dictionary<Guid, Guid> _owners = [];
    private readonly Lock _sync = new();

    /// <summary>The title every scripted pick answers.</summary>
    public string Title { get; set; } = "Chiếc đèn dầu trong ngõ vắng";

    /// <summary>The logline every scripted pick answers.</summary>
    public string Logline { get; set; } = "Một người kể chuyện tìm lại ký ức tuổi thơ qua một vật cũ.";

    /// <summary>The hook every scripted pick answers.</summary>
    public string Hook { get; set; } = "Đêm ấy, ngọn đèn tự cháy khi không ai châm.";

    /// <summary>The setting every scripted pick answers.</summary>
    public string Setting { get; set; } = "Một làng chài ven biển miền Trung những năm 1990.";

    /// <summary>The score the pick carries (0…1).</summary>
    public decimal Score { get; set; } = 0.42m;

    /// <summary>Whether the pick reports that facts must be gathered first (a true-crime series).</summary>
    public bool RequiresResearch { get; set; }

    /// <summary>A failure code: when set, the call answers that error and persists nothing.</summary>
    public string? FailureCode { get; set; }

    /// <summary>Awaited after the call is recorded and before anything is written (the race barrier).</summary>
    public Func<Task>? BeforeAnswer { get; set; }

    /// <summary>How many times <see cref="ProposeAndPickAsync"/> was called.</summary>
    public int CallCount
    {
        get
        {
            lock (_sync)
            {
                return _calls.Count;
            }
        }
    }

    /// <summary>The series ids the calls were made for, in order.</summary>
    public IReadOnlyList<Guid> Calls
    {
        get
        {
            lock (_sync)
            {
                return [.. _calls];
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<IdeaPick>> ProposeAndPickAsync(
        Guid seriesId,
        DateTimeOffset slotAt,
        RunContext run,
        CancellationToken ct)
    {
        lock (_sync)
        {
            _calls.Add(seriesId);
        }

        if (BeforeAnswer is { } beforeAnswer)
        {
            await beforeAnswer().ConfigureAwait(false);
        }

        if (FailureCode is { } code)
        {
            return Error.Failure(code, code);
        }

        var idea = EpisodeIdea.CreateProposed(
            seriesId,
            run.UserId,
            run.GenerationRunId,
            new IdeaCandidate(Title, Logline, Hook, Setting, StoryStructureTags.Empty, []),
            clock);

        idea.MarkPicked(Score, clock);

        await using var db = database.CreateAppDbContext(new TestCurrentUser { Id = run.UserId }, clock);
        db.EpisodeIdeas.Add(idea);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        lock (_sync)
        {
            _owners[idea.Id] = run.UserId;
        }

        return new IdeaPick(
            idea.Id,
            Title,
            Logline,
            Hook,
            Setting,
            StoryStructureTags.Empty,
            RequiresResearch,
            Score,
            0m);
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> MarkUsedAsync(Guid ideaId, Guid episodeId, CancellationToken ct)
    {
        Guid owner;
        lock (_sync)
        {
            owner = _owners.TryGetValue(ideaId, out var known) ? known : Guid.Empty;
        }

        if (owner == Guid.Empty)
        {
            return Error.NotFound("idea_not_found", "The harness did not propose this idea.");
        }

        await using var db = database.CreateAppDbContext(new TestCurrentUser { Id = owner }, clock);
        var idea = await db.EpisodeIdeas.SingleOrDefaultAsync(row => row.Id == ideaId, ct).ConfigureAwait(false);
        if (idea is null)
        {
            return Error.NotFound("idea_not_found", "The idea row is gone.");
        }

        var used = idea.MarkUsed(episodeId, clock);
        if (used.IsError)
        {
            return used.Errors;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Result.Success;
    }
}

/// <summary>
/// The scripted <see cref="IAutoApprovalGate"/> of the tick harness: a deterministic story verdict (auto-approved by
/// default) and no video verdict (the video gate is not on these tests' path).
/// </summary>
public sealed class ScriptedAutoApprovalGate : IAutoApprovalGate
{
    private readonly Lock _sync = new();
    private int _storyCalls;

    /// <summary>Whether the story verdict auto-approves.</summary>
    public bool AutoApproved { get; set; } = true;

    /// <summary>The reason the verdict carries when it does not approve.</summary>
    public GateStopReason? StopReason { get; set; }

    /// <summary>When set, the story evaluation answers this error instead of a verdict.</summary>
    public string? FailureCode { get; set; }

    /// <summary>Awaited inside the story evaluation (the CAS barrier).</summary>
    public Func<Task>? BeforeStoryAnswer { get; set; }

    /// <summary>How many story evaluations ran.</summary>
    public int StoryCallCount
    {
        get
        {
            lock (_sync)
            {
                return _storyCalls;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<StoryGateOutcome>> EvaluateStoryAsync(
        Guid runId,
        int attempt,
        CancellationToken ct)
    {
        lock (_sync)
        {
            _storyCalls++;
        }

        if (BeforeStoryAnswer is { } beforeAnswer)
        {
            await beforeAnswer().ConfigureAwait(false);
        }

        if (FailureCode is { } code)
        {
            return Error.Failure(code, code);
        }

        var result = new StoryGateResult(
            Pass: AutoApproved,
            Hold: !AutoApproved && StopReason == GateStopReason.Hold,
            Attempt: attempt,
            StoryVersionId: Guid.CreateVersion7(),
            Checks: [],
            Failures: AutoApproved ? [] : ["autopilot_test_failure"]);

        return new StoryGateOutcome(result, AutoApproved, AutoApproved ? null : StopReason ?? GateStopReason.Hold);
    }

    /// <inheritdoc />
    public ValueTask<ErrorOr<VideoGateOutcome>> EvaluateVideoAsync(Guid runId, CancellationToken ct) =>
        ValueTask.FromResult<ErrorOr<VideoGateOutcome>>(
            Error.Failure("video_gate_not_scripted", "The tick harness does not script the video gate."));
}

/// <summary>
/// Counts one autopilot metric of this process: every measurement whose instrument name matches, grouped by a tag
/// value. Instruments are process-global, so a test that asserts a count must run in the
/// <c>Process-global host state</c> collection.
/// </summary>
public sealed class AutopilotMetricProbe : IDisposable
{
    private readonly Metrics.MeterListener _listener;

    private AutopilotMetricProbe(string instrumentName, string? tagName, Metrics.MeterListener listener)
    {
        InstrumentName = instrumentName;
        TagName = tagName;
        _listener = listener;
    }

    /// <summary>The instrument the probe watches.</summary>
    public string InstrumentName { get; }

    /// <summary>The tag the probe groups by, or <see langword="null"/> to count every measurement.</summary>
    public string? TagName { get; }

    /// <summary>How many measurements were recorded, per tag value (<c>""</c> when the instrument has no tag).</summary>
    public Dictionary<string, long> Counts { get; } = [];

    /// <summary>Starts a probe for one <c>autopilot_*</c> counter.</summary>
    /// <param name="instrumentName">The instrument name, e.g. <see cref="AutopilotRunMetrics.ConcurrencyViolationTotalName"/>.</param>
    /// <param name="tagName">The tag to group by, or <see langword="null"/> for a single total.</param>
    public static AutopilotMetricProbe Start(string instrumentName, string? tagName = null)
    {

        Metrics.MeterListener? listener = null;
        var probe = new AutopilotMetricProbe(
            instrumentName,
            tagName,
            listener = new Metrics.MeterListener
            {
                InstrumentPublished = (instrument, meterListener) =>
                {
                    if (instrument.Name == instrumentName
                        && instrument.Meter.Name == AutopilotRunMetrics.MeterName)
                    {
                        meterListener.EnableMeasurementEvents(instrument);
                    }
                },
            });

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            probe.Record(instrument, tags, measurement));
        listener.SetMeasurementEventCallback<int>((instrument, measurement, tags, _) =>
            probe.Record(instrument, tags, measurement));
        listener.Start();

        return probe;
    }

    /// <summary>The measurement count for one tag value (or the total, without a tag).</summary>
    public long Count(string? tagValue = null) =>
        Counts.TryGetValue(tagValue ?? string.Empty, out var count) ? count : 0;

    /// <inheritdoc />
    public void Dispose() => _listener.Dispose();

    private void Record(Metrics.Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags, long measurement)
    {
        if (!instrument.Name.Equals(InstrumentName, StringComparison.Ordinal))
        {
            return;
        }

        var key = string.Empty;
        if (TagName is { } wanted)
        {
            foreach (var tag in tags)
            {
                if (tag.Key == wanted)
                {
                    key = tag.Value?.ToString() ?? string.Empty;

                    break;
                }
            }
        }

        Counts[key] = Counts.TryGetValue(key, out var current) ? current + measurement : measurement;
    }
}
