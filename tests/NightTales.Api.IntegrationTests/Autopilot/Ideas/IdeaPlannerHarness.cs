using System.Globalization;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Features.Autopilot.Ideas;
using NightTales.Application.Features.Autopilot.Ideas.Abstractions;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Common;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Structure;
using NightTales.Domain.Usage;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Research;
using NightTales.TestKit.Text;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;

// Aliased: the enclosing test assembly also declares a `...Api.IntegrationTests.SeriesBible` namespace, so the
// simple name `SeriesBible` would bind to that namespace and never to the Domain aggregate.
using SeriesBibleEntity = NightTales.Domain.Series.Bible.SeriesBible;

namespace NightTales.Api.IntegrationTests.Autopilot.Ideas;

/// <summary>
/// The shared seam of the idea-planner suite (02.8.1-T1): the world a run is bought in, the Worker host the
/// production planner graph runs in, and the raw-SQL probes the assertions read.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <b>not</b> replacing <see cref="IIdeaGenerator"/>, <see cref="IIdeaPlanner"/> or
/// <see cref="IIdeaContextProvider"/>: only <see cref="ITextClient"/> is scripted, so what these tests drive is
/// the production graph — the DataObject the generator builds, the prompt catalogue and the price version it
/// reads, the D14 meter inside it, the D7 outcome mapping of the planner, the real <c>IdeaValidator</c>, the
/// real context provider (bible, approved-episode history, research notes from the database) and the real
/// EF/persistence of <c>episode_ideas</c>. The paid provider edge is the one thing no test may reach.
/// </para>
/// <para>
/// <b>The caller owns the run.</b> Mirrors the production caller (02.8.3-T1): the harness creates the
/// <c>generation_runs</c> row (<c>kind = ideas</c>, <c>episode_id = NULL</c>, <c>counts_toward_limit = false</c>)
/// before the planner is called, because the planner never writes it and because both <c>episode_ideas</c> and
/// <c>usage_records</c> carry a same-owner foreign key onto it.
/// </para>
/// <para>
/// The price version is written by the fixture instead of the seeder: the test template is migrated but never
/// seeded, and both the estimator (<c>price_versions.limits.ideas.max_output_tokens</c>) and the meter price the
/// call off the run's own version.
/// </para>
/// </remarks>
internal static class IdeaPlannerHarness
{
    /// <summary>The clock of every seeded row (<c>FakeClock</c>), so stamps are deterministic.</summary>
    internal static readonly DateTimeOffset Start = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The token xUnit cancels the running test with.</summary>
    internal static CancellationToken Ct => TestContextToken.Value;

    /// <summary>The model every scripted outcome names (the fixture's priced model, <c>gpt-6-luna</c>).</summary>
    internal const string Model = FakeTextClient.Model;

    /// <summary>The beats a valid scripted candidate carries: three beats with one turn (<c>twist</c>).</summary>
    internal static readonly IReadOnlyList<string> DefaultBeats = ["setup", "clue", "twist"];

    /// <summary>The reported usage of a scripted success: positive tokens, so the usage row is real and priced.</summary>
    internal static TokenUsage Usage { get; } =
        new(InputTokens: 1_000, CachedInputTokens: 0, CacheWriteTokens: 1_000, OutputTokens: 500, ReasoningTokens: 0);

    // ---------------------------------------------------------------- the world

    /// <summary>
    /// Seeds one owner, one series and the active price version. No episode, no bible and no source is seeded;
    /// each test adds exactly what it needs.
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="topic">The series' narrative topic.</param>
    /// <param name="language">The series' content language.</param>
    /// <param name="autopilotSettings">The <c>autopilot_settings</c> document, when a test wants one.</param>
    /// <returns>The owner and the series.</returns>
    internal static async Task<World> SeedAsync(
        TestDatabase database,
        Topic topic = Topic.Ghost,
        Language language = Language.Vi,
        string? autopilotSettings = null)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(database, actor, new FakeClock(Start)).CreateAsync(
            user.Id,
            topic: topic,
            language: language,
            autopilotSettings: autopilotSettings,
            ct: Ct);

        await EnsurePriceVersionAsync(database, user.Id);

        return new World(user.Id, series.Id);
    }

    /// <summary>Seeds one draft episode of the series (the anchor the research-note sources hang off).</summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The world.</param>
    /// <returns>The episode id.</returns>
    internal static async Task<Guid> SeedEpisodeAsync(TestDatabase database, World world)
    {
        var episode = await new EpisodeBuilder(database, new TestCurrentUser { Id = world.UserId })
            .CreateAsync(world.UserId, world.SeriesId, ct: Ct);

        return episode.Id;
    }

    /// <summary>
    /// Inserts one <c>user_note</c> source of the episode, the way the note route does (02.5.x): the real
    /// <c>StorySource.CreateNote</c> factory, so the context provider reads a row production could have written.
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The world.</param>
    /// <param name="episodeId">The episode the note belongs to.</param>
    /// <param name="text">The note text.</param>
    /// <returns>The source id.</returns>
    internal static async Task<Guid> SeedNoteAsync(TestDatabase database, World world, Guid episodeId, string text)
    {
        var source = StorySource.CreateNote(Guid.CreateVersion7(), world.UserId, episodeId, text, Start);
        source.IsError.ShouldBeFalse();

        await using var db = database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }, new FakeClock(Start));
        db.StorySources.Add(source.Value);
        await db.SaveChangesAsync(Ct);

        return source.Value.Id;
    }

    /// <summary>
    /// Inserts one fetched <c>url</c> source of the episode, with a title and an extracted-text key, the way the
    /// fetcher leaves one: it is the source kind an idea run must read nothing from (D19 — the extracted text is
    /// never read and a URL source contributes no note at all).
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The world.</param>
    /// <param name="episodeId">The episode the source belongs to.</param>
    /// <param name="title">The title the fetcher extracted, which must not reach the data object.</param>
    /// <returns>The source id.</returns>
    internal static async Task<Guid> SeedUrlSourceAsync(
        TestDatabase database,
        World world,
        Guid episodeId,
        string title)
    {
        var parsed = SourceUrl.Parse("https://www.bbc.co.uk/news/story-1234", new FakePublicSuffixList());
        parsed.IsError.ShouldBeFalse();

        var source = StorySource.CreateUrl(Guid.CreateVersion7(), world.UserId, episodeId, parsed.Value, Start);
        source.IsError.ShouldBeFalse();
        source.Value.MarkFetched(
            parsed.Value.Value,
            title,
            "text/plain",
            new string('a', 64),
            $"sources/{episodeId:D}/text.txt",
            textChars: 500,
            Start);

        await using var db = database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }, new FakeClock(Start));
        db.StorySources.Add(source.Value);
        await db.SaveChangesAsync(Ct);

        return source.Value.Id;
    }

    /// <summary>
    /// Writes the series' bible row with the given content, through the real aggregate and the real validator.
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The world.</param>
    /// <param name="content">The stored <c>bible.v1</c> document.</param>
    /// <param name="limits">
    /// The limits the validator applies. A test that seeds a document the production limits would refuse (the
    /// injection fixture's title) raises only the cap it needs, so the rest of the contract still holds.
    /// </param>
    /// <returns>A task that completes when the row is written.</returns>
    internal static async Task SeedBibleAsync(
        TestDatabase database,
        World world,
        SeriesBibleContent content,
        BibleLimits? limits = null)
    {
        var clock = new FakeClock(Start);
        var bible = SeriesBibleEntity.CreateDefault(world.SeriesId, world.UserId, clock);
        bible.ReplaceContent(content, limits ?? new BibleLimits(), clock);

        await using var db = database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }, clock);
        db.SeriesBibles.Add(bible);
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>A minimal valid bible whose only episode summary carries <paramref name="title"/>.</summary>
    /// <param name="title">The summary's title.</param>
    /// <returns>The content document.</returns>
    internal static SeriesBibleContent BibleWithSummaryTitle(string title) =>
        new(
            SeriesBibleContent.V1,
            new BibleNarrator("Trần Văn Kể", "kể chuyện đêm", "giọng chậm, ấm"),
            [],
            [],
            [],
            [
                new BibleEpisodeSummary(
                    Guid.CreateVersion7(),
                    title,
                    "Tóm tắt của tập đã duyệt.",
                    new StoryStructureTags("truyện ma", "ngôi nhà cũ", "một cuộc điện thoại lúc nửa đêm", "người kể là nguyên nhân", "first_person", DefaultBeats),
                    Start),
            ]);

    /// <summary>
    /// The <c>generation_runs</c> row the caller owns (02.8.3-T1's shape): series-level <c>ideas</c>, not counted
    /// toward the daily limit, one reservation, and the <see cref="RunContext"/> the planner is driven with.
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The world.</param>
    /// <returns>The run's facts, ready to hand to the planner.</returns>
    internal static async Task<RunContext> CreateRunAsync(TestDatabase database, World world)
    {
        var reservationId = Guid.CreateVersion7();
        var queued = GenerationRun.Queue(
            world.UserId,
            world.SeriesId,
            episodeId: null,
            GenerationKind.Ideas,
            GenerationTrigger.Autopilot,
            string.Create(CultureInfo.InvariantCulture, $"autopilot:{world.SeriesId:D}:{Start:O}:idea"),
            previousEpisodeStatus: null,
            inputFactSheetId: null,
            reservationId,
            worstCaseUsd: 0.25m,
            Start,
            countsTowardLimit: false);
        queued.IsError.ShouldBeFalse();

        await using (var db = database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }, new FakeClock(Start)))
        {
            db.GenerationRuns.Add(queued.Value);
            await db.SaveChangesAsync(Ct);
        }

        return new RunContext(
            queued.Value.Id,
            world.UserId,
            world.SeriesId,
            EpisodeId: null,
            new ReservationId(reservationId),
            GenerationTrigger.Autopilot,
            Attempt: 1,
            ActivePriceVersion(database));
    }

    /// <summary>Writes one <c>picked</c> idea row of the series, the state <c>MarkUsedAsync</c> transitions from.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The world.</param>
    /// <param name="runId">The run that proposes it.</param>
    /// <param name="title">The idea's title.</param>
    /// <returns>The idea id.</returns>
    internal static async Task<Guid> SeedPickedIdeaAsync(
        TestDatabase database,
        World world,
        Guid runId,
        string title)
    {
        var clock = new FakeClock(Start);
        await using var db = database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }, clock);

        var idea = EpisodeIdea.CreateProposed(world.SeriesId, world.UserId, runId, Candidate(title), clock);
        idea.MarkPicked(0.5m, clock);
        db.EpisodeIdeas.Add(idea);
        await db.SaveChangesAsync(Ct);

        return idea.Id;
    }

    // ---------------------------------------------------------------- the host

    /// <summary>
    /// Starts the Worker host of these tests: the production graph with the provider edge scripted
    /// (<see cref="ITextClient"/>). Everything else — the planner, the generator, the estimator, the price
    /// catalog, the prompt catalog, the meter, the moderation writer, the context provider — is production.
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="text">The scripted provider edge; its outcomes are queued before the host starts.</param>
    /// <param name="count">The <c>Autopilot:Ideas:Count</c> the host binds, when a test needs one fixed.</param>
    /// <param name="contexts">A replacement context provider, for the one test that needs an out-of-range count.</param>
    /// <returns>The started host; the caller disposes it.</returns>
    internal static async Task<WorkerHost> StartWorkerAsync(
        TestDatabase database,
        FakeTextClient text,
        int? count = null,
        IIdeaContextProvider? contexts = null)
    {
        var host = new WorkerHost(database, new FakeClock(Start)).ConfigureServices(services =>
        {
            services.RemoveAll<ITextClient>();
            services.AddSingleton<ITextClient>(text);

            if (contexts is not null)
            {
                services.RemoveAll<IIdeaContextProvider>();
                services.AddSingleton(contexts);
            }
        });

        if (count is { } configured)
        {
            host.WithSetting(
                $"{IdeaPlannerOptions.SectionName}:{nameof(IdeaPlannerOptions.Count)}",
                configured.ToString(CultureInfo.InvariantCulture));
        }

        await host.StartAsync(Ct);

        return host;
    }

    /// <summary>
    /// The scope the planner runs in, pinned to one user exactly as the outbox dispatcher pins a delivery: the
    /// tenant filter of the scope's read and write model is the owner's, and the planner's own explicit owner
    /// check runs in addition to it.
    /// </summary>
    /// <param name="worker">The started host.</param>
    /// <param name="userId">The owner the scope runs on behalf of.</param>
    /// <returns>The scope; the caller disposes it.</returns>
    internal static PlannerScope OpenScope(WorkerHost worker, Guid userId)
    {
        var scope = worker.CreateScope();
        IDisposable? actor = null;
        try
        {
            actor = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(userId);

            return new PlannerScope(
                scope,
                actor,
                scope.ServiceProvider.GetRequiredService<IIdeaPlanner>(),
                scope.ServiceProvider.GetRequiredService<IAppDbContext>());
        }
        catch
        {
            actor?.Dispose();
            scope.Dispose();
            throw;
        }
    }

    /// <summary>Runs one idea step of one slot in its own scope, the way the caller's tick does.</summary>
    /// <param name="worker">The started host.</param>
    /// <param name="run">The caller's run facts.</param>
    /// <param name="seriesId">The series to propose for; the run's own series by default.</param>
    /// <param name="asUser">The user the scope is pinned to; the run's owner by default.</param>
    /// <returns>The planner's outcome.</returns>
    internal static async Task<ErrorOr<IdeaPick>> ProposeAsync(
        WorkerHost worker,
        RunContext run,
        Guid? seriesId = null,
        Guid? asUser = null)
    {
        using var scope = OpenScope(worker, asUser ?? run.UserId);

        return await scope.Planner.ProposeAndPickAsync(seriesId ?? run.SeriesId, Start, run, Ct);
    }

    /// <summary>The scope of one planned run: the planner and the caller's own tracked write model.</summary>
    internal sealed class PlannerScope(
        IServiceScope scope,
        IDisposable actor,
        IIdeaPlanner planner,
        IAppDbContext db) : IDisposable
    {
        /// <summary>The idea planner of this scope.</summary>
        internal IIdeaPlanner Planner { get; } = planner;

        /// <summary>The caller's own tracked write model: a flush here is the caller's flush, not the planner's.</summary>
        internal IAppDbContext Db { get; } = db;

        /// <inheritdoc />
        public void Dispose()
        {
            actor.Dispose();
            scope.Dispose();
        }
    }

    /// <summary>
    /// A context provider that reports a fixed context, for the one assertion the production provider cannot
    /// produce: a count above the configured ceiling (a mis-bound provider, which the planner must clamp rather
    /// than send to a paid call).
    /// </summary>
    internal sealed class FixedContextProvider(IdeaContext context) : IIdeaContextProvider
    {
        /// <inheritdoc />
        public ValueTask<IdeaContext> GetAsync(Guid seriesId, CancellationToken ct) => ValueTask.FromResult(context);
    }

    // ---------------------------------------------------------------- model answers

    /// <summary>One <c>ideas.v1</c> idea whose five texts and outline satisfy <c>IdeaValidator</c>.</summary>
    /// <param name="title">The episode title.</param>
    /// <param name="beats">The outline; three valid beats by default.</param>
    /// <returns>The wire idea.</returns>
    internal static IdeaV1 Idea(string title, IReadOnlyList<string>? beats = null) =>
        new(
            title,
            "Một lời giới thiệu ngắn về chuyện sắp kể.",
            "Đêm ấy có tiếng gõ cửa lúc nửa đêm.",
            "một ngôi nhà cũ bên sông",
            new IdeaStructureV1("truyện ma", "ngôi nhà cũ", "một cuộc điện thoại lúc nửa đêm", "người kể là nguyên nhân", "first_person"),
            beats ?? DefaultBeats);

    /// <summary>The <c>ideas.v1</c> answer of one batch.</summary>
    /// <param name="ideas">The ideas, in the model's order.</param>
    /// <returns>The answer document.</returns>
    internal static IdeasV1 IdeaBatch(params IdeaV1[] ideas) => new(ideas);

    /// <summary>The Domain candidate of the same shape as <see cref="Idea"/>.</summary>
    /// <param name="title">The episode title.</param>
    /// <param name="beats">The outline; three valid beats by default.</param>
    /// <returns>The candidate.</returns>
    internal static IdeaCandidate Candidate(string title, IReadOnlyList<string>? beats = null)
    {
        var outline = beats ?? DefaultBeats;

        return new IdeaCandidate(
            title,
            "Một lời giới thiệu ngắn về chuyện sắp kể.",
            "Đêm ấy có tiếng gõ cửa lúc nửa đêm.",
            "một ngôi nhà cũ bên sông",
            new StoryStructureTags("truyện ma", "ngôi nhà cũ", "một cuộc điện thoại lúc nửa đêm", "người kể là nguyên nhân", "first_person", outline),
            outline);
    }

    /// <summary>A JSON path probe over the captured request's data object.</summary>
    /// <param name="request">The captured call.</param>
    /// <param name="property">The top-level property to read as text.</param>
    /// <returns>The property's text.</returns>
    internal static string DataObjectText(TextCallRequest request, string property)
    {
        using var document = JsonDocument.Parse(request.UserDataJson);

        return document.RootElement.GetProperty(property).GetString() ?? string.Empty;
    }

    /// <summary>The <c>count</c> of the captured request's data object.</summary>
    /// <param name="request">The captured call.</param>
    /// <returns>The requested candidate count.</returns>
    internal static int DataObjectCount(TextCallRequest request)
    {
        using var document = JsonDocument.Parse(request.UserDataJson);

        return document.RootElement.GetProperty("count").GetInt32();
    }

    /// <summary>Whether the captured request's data object contains <paramref name="value"/> anywhere.</summary>
    /// <param name="request">The captured call.</param>
    /// <param name="value">The text to look for.</param>
    /// <returns>True when the data object carries it.</returns>
    internal static bool DataObjectContains(TextCallRequest request, string value) =>
        request.UserDataJson.Contains(value, StringComparison.Ordinal);

    // ---------------------------------------------------------------- probes

    /// <summary>
    /// One line per idea of the series, in insertion order: the title, the status, whether it names its run, and
    /// the stored outline signature.
    /// </summary>
    /// <remarks>
    /// Returns one line per row and <b>no</b> order: the rows of one batch share the <c>FakeClock</c> instant they
    /// were stamped with, so no column of the table is a total order over them and PostgreSQL may return them in
    /// any order. Callers assert the <i>set</i> (<c>.Order()</c> on both sides); which candidate the run picked is
    /// asserted separately, because that is the only place this feature's order is a contract.
    /// </remarks>
    /// <param name="database">The class database.</param>
    /// <param name="seriesId">The series.</param>
    /// <returns>One line per row.</returns>
    internal static IReadOnlyList<string> IdeaRows(TestDatabase database, Guid seriesId) =>
        SqlProbe.QueryStrings(
            database,
            $"""
             SELECT title || '|' || status || '|' || (generation_run_id IS NOT NULL)::text
                 || '|' || (episode_id IS NULL)::text
                 || '|' || coalesce(structure->>'beat_signature', '-')
             FROM episode_ideas WHERE series_id = '{Pg(seriesId)}'
             """);

    /// <summary>
    /// The manual-smoke projection of the acceptance check: title, status and the stored beat signature, one line
    /// per row and unordered (see <see cref="IdeaRows"/> on the ordering).
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="seriesId">The series.</param>
    /// <returns>One line per row.</returns>
    internal static IReadOnlyList<string> ManualSmokeRows(TestDatabase database, Guid seriesId) =>
        SqlProbe.QueryStrings(
            database,
            $"""
             SELECT title || '|' || status || '|' || (structure->>'beat_signature')
             FROM episode_ideas WHERE series_id = '{Pg(seriesId)}'
             """);

    /// <summary>
    /// Renders a probe's rows as one comparable line, in a stable order of its own (ordinal over the whole line):
    /// the rows of one batch carry the same stamp and PostgreSQL may return them in any order, so the assertions are
    /// about the <i>set</i> of rows — this makes that comparison a plain string equality with a readable diff.
    /// </summary>
    /// <param name="lines">The probe's rows.</param>
    /// <returns>The rows, sorted ordinally and joined with <c> || </c>.</returns>
    internal static string Render(IEnumerable<string> lines) =>
        string.Join(" || ", lines.Order(StringComparer.Ordinal));

    /// <summary>How many <c>episode_ideas</c> rows the series has.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="seriesId">The series.</param>
    /// <returns>The row count.</returns>
    internal static long IdeaCount(TestDatabase database, Guid seriesId) =>
        SqlProbe.Count(database, $"SELECT count(*) FROM episode_ideas WHERE series_id = '{Pg(seriesId)}'");

    /// <summary>How many idea rows of the series name exactly <paramref name="runId"/> as their run.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="seriesId">The series.</param>
    /// <param name="runId">The run.</param>
    /// <returns>The row count.</returns>
    internal static long IdeasOfRun(TestDatabase database, Guid seriesId, Guid runId) =>
        SqlProbe.Count(
            database,
            $"SELECT count(*) FROM episode_ideas WHERE series_id = '{Pg(seriesId)}' AND generation_run_id = '{Pg(runId)}'");

    /// <summary>How many idea rows of the series contain <paramref name="text"/> in any of their stored texts.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="seriesId">The series.</param>
    /// <param name="text">The text to look for (an injection fixture's canary).</param>
    /// <returns>The row count.</returns>
    internal static long IdeasContaining(TestDatabase database, Guid seriesId, string text) =>
        SqlProbe.Count(
            database,
            $"""
             SELECT count(*) FROM episode_ideas
             WHERE series_id = '{Pg(seriesId)}'
                 AND (title LIKE '%{text}%' OR logline LIKE '%{text}%' OR hook LIKE '%{text}%' OR setting LIKE '%{text}%'
                     OR structure::text LIKE '%{text}%')
             """);

    /// <summary>The status and the episode of one idea row: <c>&lt;status&gt;|&lt;episode id or -&gt;</c>.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="ideaId">The idea.</param>
    /// <returns>The two fields.</returns>
    internal static string IdeaState(TestDatabase database, Guid ideaId) =>
        SqlProbe.ScalarText(
            database,
            $"SELECT status || '|' || coalesce(episode_id::text, '-') FROM episode_ideas WHERE id = '{Pg(ideaId)}'");

    /// <summary>
    /// The run row of the series: its id, kind, status, stage, error code, attempt, the counted flag, the
    /// series-level shape, its settled cost, its prompt version and whether <c>models</c> was written.
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="seriesId">The series.</param>
    /// <returns>The row as one string.</returns>
    internal static string RunRow(TestDatabase database, Guid seriesId) =>
        SqlProbe.ScalarText(
            database,
            $"""
             SELECT id::text || '|' || kind || '|' || status || '|' || stage || '|' || coalesce(error_code, '-')
                 || '|' || attempt::text || '|' || counts_toward_limit::text || '|' || (episode_id IS NULL)::text
                 || '|' || coalesce(cost_usd::text, '-') || '|' || coalesce(prompt_version, '-')
                 || '|' || coalesce(models::text, '-')
             FROM generation_runs WHERE series_id = '{Pg(seriesId)}'
             """);

    /// <summary>How many <c>generation_runs</c> rows the series has.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="seriesId">The series.</param>
    /// <returns>The row count.</returns>
    internal static long RunCount(TestDatabase database, Guid seriesId) =>
        SqlProbe.Count(database, $"SELECT count(*) FROM generation_runs WHERE series_id = '{Pg(seriesId)}'");

    /// <summary>
    /// One line per <c>usage_records</c> row of the owner: operation, whether the episode is NULL, whether the
    /// cost is positive, the price version and whether the row is estimated.
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="userId">The owner.</param>
    /// <returns>One line per row, joined with <c> || </c>.</returns>
    internal static string UsageRows(TestDatabase database, Guid userId) =>
        SqlProbe.ScalarText(
            database,
            $"""
             SELECT coalesce(string_agg(row, ' || ' ORDER BY created_at, id), '') FROM (
                 SELECT operation || '|' || (episode_id IS NULL)::text || '|' || (cost_usd > 0)::text
                     || '|' || coalesce(price_version::text, '-') || '|' || estimated::text AS row, created_at, id
                 FROM usage_records WHERE user_id = '{Pg(userId)}'
             ) AS rows
             """);

    /// <summary>How many <c>usage_records</c> rows the owner has.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="userId">The owner.</param>
    /// <returns>The row count.</returns>
    internal static long UsageCount(TestDatabase database, Guid userId) =>
        SqlProbe.Count(database, $"SELECT count(*) FROM usage_records WHERE user_id = '{Pg(userId)}'");

    /// <summary>
    /// One line per <c>moderation_events</c> row of the owner: source, stage, whether the episode is NULL, the
    /// categories and whether an excerpt was stored.
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="userId">The owner.</param>
    /// <returns>One line per row, joined with <c> || </c>.</returns>
    internal static string ModerationRows(TestDatabase database, Guid userId) =>
        SqlProbe.ScalarText(
            database,
            $"""
             SELECT coalesce(string_agg(row, ' || ' ORDER BY created_at, id), '') FROM (
                 SELECT source || '|' || stage || '|' || (episode_id IS NULL)::text
                     || '|' || array_to_string(categories, ',') || '|' || (excerpt IS NULL)::text AS row, created_at, id
                 FROM moderation_events WHERE user_id = '{Pg(userId)}'
             ) AS rows
             """);

    /// <summary>
    /// The price version a run queued at <see cref="Start"/> is costed with, by the catalog's own rule: the
    /// newest row whose <c>active_from</c> is not after the run's queue time. Read instead of assumed, because
    /// the host's start-up seeder row can be newer than the fixture's.
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <returns>The version number, as <c>usage_records.price_version</c> stores it.</returns>
    internal static string ActivePriceVersion(TestDatabase database) =>
        SqlProbe.ScalarText(
            database,
            $"""
             SELECT version::text FROM price_versions
             WHERE active_from <= '{Start.ToString("O", CultureInfo.InvariantCulture)}'
             ORDER BY version DESC LIMIT 1
             """);

    /// <summary>A Postgres uuid literal for the raw-SQL probes.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The literal.</returns>
    internal static string Pg(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>Writes the price version the estimator and the meter read, when the fixture has none.</summary>
    private static async Task EnsurePriceVersionAsync(TestDatabase database, Guid userId)
    {
        await using var db = database.CreateAppDbContext(new TestCurrentUser { Id = userId }, new FakeClock(Start));
        if (db.PriceVersions.Any(version => version.Version == 1))
        {
            return;
        }

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
            "ideas-planner-price-version",
            Start.AddDays(-1)));

        await db.SaveChangesAsync(Ct);
    }

    /// <summary>One owner and the series whose ideas the planner proposes.</summary>
    /// <param name="UserId">The owner (the tenant of every scope).</param>
    /// <param name="SeriesId">The series.</param>
    internal sealed record World(Guid UserId, Guid SeriesId);
}
