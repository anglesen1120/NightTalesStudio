using System.Globalization;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
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
using NightTales.TestKit.Moderation;
using NightTales.TestKit.Research;
using NightTales.TestKit.Telemetry;
using NightTales.TestKit.Text;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Serilog.Core;
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
/// <see cref="IIdeaContextProvider"/> on the normal path: only <see cref="ITextClient"/> is scripted (and, from
/// 02.8.1-T2, <see cref="IContentModerator"/>, whose production implementation calls the moderation endpoint),
/// so what these tests drive is the production graph — the DataObject the generator builds, the prompt catalogue
/// and the price version it reads, the D14 meter inside it, the D7 outcome mapping of the planner, the real
/// <c>IdeaValidator</c>, the real novelty checker and policy pre-check with their bound options, the real corpus
/// reader, the real context provider (bible, approved-episode history, research notes from the database) and the
/// real EF/persistence of <c>episode_ideas</c>. Single tests pin one boundary each — the context provider's
/// count, the spend cap (C1) and the cost estimator — because those answers belong to the platform's spend and
/// the price catalogue, not to this feature. The paid provider edge is the one thing no test may reach.
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
    /// Seeds a second series of the same owner, for the one integrity case the composite key cannot catch: an
    /// episode that belongs to this user but not to the idea's series (SEC-4).
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The world whose owner the second series belongs to.</param>
    /// <returns>The second series' id.</returns>
    internal static async Task<Guid> SeedOtherSeriesAsync(TestDatabase database, World world)
    {
        var series = await new SeriesBuilder(database, new TestCurrentUser { Id = world.UserId }, new FakeClock(Start))
            .CreateAsync(world.UserId, topic: Topic.Ghost, language: Language.Vi, ct: Ct);

        return series.Id;
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
    /// <param name="guard">
    /// A replacement budget guard, for the one test that needs a deterministic refusal: the real guard reads the
    /// platform's spend and the open bounds, which a test cannot pin. A hand-written fake keeps the assertion exact.
    /// </param>
    /// <param name="estimator">A replacement cost estimator, so a guard test does not depend on the price catalogue.</param>
    /// <param name="moderator">
    /// The scripted content moderator (D12). The harness always replaces <see cref="IContentModerator"/> — the
    /// production one calls the paid endpoint — and answers clean for every stage it was not scripted for, so a
    /// test that does not care about moderation passes nothing.
    /// </param>
    /// <param name="framingLexicon">The <c>Autopilot:Ideas:Policy:FramingLexicon</c> list (F31); shipped empty.</param>
    /// <param name="crimeNouns">The <c>Autopilot:Ideas:Policy:CrimeNouns</c> list (F31); shipped empty.</param>
    /// <param name="neutralNovelty">
    /// Whether the harness queues the neutral novelty script when the test queued none: 02.8.1-T2 puts one judge
    /// call in the middle of every run that has a survivor, and an answer with no score rows leaves every
    /// candidate a null judge — which is exactly the degradation the scorer treats as lexical-only, so a T1
    /// scenario that scripts only the ideas answer keeps its meaning. A test that scripts the judge itself, or
    /// that asserts the call was never made, passes <see langword="false"/>.
    /// </param>
    /// <param name="moderationEnabled">
    /// The <c>Autopilot:Ideas:Policy:ModerationEnabled</c> switch (02.8.1-T2): <see langword="null"/> keeps the
    /// shipped <see langword="true"/>, so a test that does not care about the switch binds nothing.
    /// </param>
    /// <param name="failClosed">
    /// The <c>Autopilot:Ideas:Policy:FailClosed</c> mode (02.8.1-T2): what an unavailable moderator decides.
    /// <see langword="null"/> keeps the shipped <see langword="false"/>.
    /// </param>
    /// <param name="logs">
    /// A <see cref="TestLogSink"/> to observe the run's own log events (D19): when one is passed the host is
    /// started <see cref="WorkerHost.WithTelemetry"/> and the sink is registered like the production one, so the
    /// test asserts on the events the application loggers actually emitted.
    /// </param>
    /// <returns>The started host; the caller disposes it.</returns>
    internal static async Task<WorkerHost> StartWorkerAsync(
        TestDatabase database,
        FakeTextClient text,
        int? count = null,
        IIdeaContextProvider? contexts = null,
        IBudgetGuard? guard = null,
        IIdeaCostEstimator? estimator = null,
        FakeContentModerator? moderator = null,
        IReadOnlyList<string>? framingLexicon = null,
        IReadOnlyList<string>? crimeNouns = null,
        bool neutralNovelty = true,
        bool? moderationEnabled = null,
        bool? failClosed = null,
        TestLogSink? logs = null)
    {
        var host = new WorkerHost(database, new FakeClock(Start)).ConfigureServices(services =>
        {
            services.RemoveAll<ITextClient>();
            services.AddSingleton<ITextClient>(text);

            services.RemoveAll<IContentModerator>();
            services.AddSingleton<IContentModerator>(moderator ?? new FakeContentModerator());

            if (contexts is not null)
            {
                services.RemoveAll<IIdeaContextProvider>();
                services.AddSingleton(contexts);
            }

            if (guard is not null)
            {
                services.RemoveAll<IBudgetGuard>();
                services.AddSingleton(guard);
            }

            if (estimator is not null)
            {
                services.RemoveAll<IIdeaCostEstimator>();
                services.AddSingleton(estimator);
            }

            if (logs is not null)
            {
                services.AddSingleton<ILogEventSink>(logs);
            }
        });

        if (logs is not null)
        {
            host.WithTelemetry();
        }

        if (count is { } configured)
        {
            host.WithSetting(
                $"{IdeaPlannerOptions.SectionName}:{nameof(IdeaPlannerOptions.Count)}",
                configured.ToString(CultureInfo.InvariantCulture));
        }

        if (moderationEnabled is { } enabled)
        {
            host.WithSetting(
                $"{IdeaPolicyOptions.SectionName}:{nameof(IdeaPolicyOptions.ModerationEnabled)}",
                enabled ? "true" : "false");
        }

        if (failClosed is { } closed)
        {
            host.WithSetting(
                $"{IdeaPolicyOptions.SectionName}:{nameof(IdeaPolicyOptions.FailClosed)}",
                closed ? "true" : "false");
        }

        for (var index = 0; index < (framingLexicon?.Count ?? 0); index++)
        {
            host.WithSetting($"{IdeaPolicyOptions.SectionName}:{nameof(IdeaPolicyOptions.FramingLexicon)}:{index}", framingLexicon![index]);
        }

        for (var index = 0; index < (crimeNouns?.Count ?? 0); index++)
        {
            host.WithSetting($"{IdeaPolicyOptions.SectionName}:{nameof(IdeaPolicyOptions.CrimeNouns)}:{index}", crimeNouns![index]);
        }

        if (neutralNovelty && text.Queued(TextStep.Novelty) == 0)
        {
            // The neutral judge script: an answer with no score rows. The checker is driven through its real
            // production path, and the batch keeps the signals of a degraded judge (no judge record at all),
            // which is what the T1 scenarios were written against.
            text.EnqueueOk(TextStep.Novelty, new NoveltyV1([]), Usage);
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
                scope.ServiceProvider.GetRequiredService<IAppDbContext>(),
                scope.ServiceProvider.GetRequiredService<IIdeaNoveltyChecker>(),
                scope.ServiceProvider.GetRequiredService<IIdeaPolicyChecker>(),
                scope.ServiceProvider.GetRequiredService<IIdeaCorpusProvider>(),
                scope.ServiceProvider.GetRequiredService<IdeaNoveltyOptions>(),
                scope.ServiceProvider.GetRequiredService<IdeaPolicyOptions>());
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

    /// <summary>The scope of one planned run: the planner, the checkers of the batch's two passes, the corpus
    /// reader and the caller's own tracked write model.</summary>
    internal sealed class PlannerScope(
        IServiceScope scope,
        IDisposable actor,
        IIdeaPlanner planner,
        IAppDbContext db,
        IIdeaNoveltyChecker novelty,
        IIdeaPolicyChecker policy,
        IIdeaCorpusProvider corpus,
        IdeaNoveltyOptions noveltyOptions,
        IdeaPolicyOptions policyOptions) : IDisposable
    {
        /// <summary>The idea planner of this scope.</summary>
        internal IIdeaPlanner Planner { get; } = planner;

        /// <summary>The caller's own tracked write model: a flush here is the caller's flush, not the planner's.</summary>
        internal IAppDbContext Db { get; } = db;

        /// <summary>The novelty pass of 02.8.1-T2, resolved from the production graph with its bound options.</summary>
        internal IIdeaNoveltyChecker Novelty { get; } = novelty;

        /// <summary>The content-policy pre-check of 02.8.1-T2, resolved from the production graph with its bound options.</summary>
        internal IIdeaPolicyChecker Policy { get; } = policy;

        /// <summary>The corpus reader the novelty pass compares against.</summary>
        internal IIdeaCorpusProvider Corpus { get; } = corpus;

        /// <summary>The thresholds and the corpus cap the host bound (<c>Autopilot:Ideas:Novelty</c>).</summary>
        internal IdeaNoveltyOptions NoveltyOptions { get; } = noveltyOptions;

        /// <summary>The policy switches and keyword data the host bound (<c>Autopilot:Ideas:Policy</c>).</summary>
        internal IdeaPolicyOptions PolicyOptions { get; } = policyOptions;

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

    /// <summary>The structure of a scripted candidate (the five D3 values, at their defaults).</summary>
    private static IdeaStructureV1 Structure { get; } =
        new("truyện ma", "ngôi nhà cũ", "một cuộc điện thoại lúc nửa đêm", "người kể là nguyên nhân", "first_person");

    /// <summary>The structure tags of a scripted candidate, outline included.</summary>
    /// <param name="beats">The outline.</param>
    /// <returns>The tags.</returns>
    private static StoryStructureTags Tags(IReadOnlyList<string> beats) =>
        new("truyện ma", "ngôi nhà cũ", "một cuộc điện thoại lúc nửa đêm", "người kể là nguyên nhân", "first_person", beats);

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
            Structure,
            beats ?? DefaultBeats);

    /// <summary>
    /// One <c>ideas.v1</c> idea whose three lexical texts (title, logline, setting) are unique to its title, so a
    /// batch of them shares no shingle window and the lexical pass can tell them apart. The shared
    /// <see cref="Idea"/> shape would otherwise make every candidate a near copy of every other one, which is
    /// exactly what a novelty test must not start from.
    /// </summary>
    /// <param name="title">The episode title; short, because it is repeated in the three texts.</param>
    /// <returns>The wire idea.</returns>
    internal static IdeaV1 DistinctIdea(string title) =>
        new(
            title,
            $"Lời giới thiệu riêng về {title}.",
            $"Móc riêng về {title}.",
            $"Bối cảnh riêng về {title}",
            Structure,
            DefaultBeats);

    /// <summary>
    /// One <c>ideas.v1</c> idea of the shared <see cref="Idea"/> shape whose logline is supplied by the caller,
    /// so a test can drive a specific text — a banned theme, a framing phrase or an injection payload — through
    /// the batch without inventing the rest of the five texts.
    /// </summary>
    /// <param name="title">The episode title.</param>
    /// <param name="logline">The logline the model "wrote".</param>
    /// <returns>The wire idea.</returns>
    internal static IdeaV1 IdeaWithLogline(string title, string logline) =>
        new(title, logline, "Đêm ấy có tiếng gõ cửa lúc nửa đêm.", "một ngôi nhà cũ bên sông", Structure, DefaultBeats);

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
            Tags(outline),
            outline);
    }

    /// <summary>The Domain candidate of the same shape as <see cref="DistinctIdea"/>.</summary>
    /// <param name="title">The episode title.</param>
    /// <returns>The candidate.</returns>
    internal static IdeaCandidate DistinctCandidate(string title) =>
        new(
            title,
            $"Lời giới thiệu riêng về {title}.",
            $"Móc riêng về {title}.",
            $"Bối cảnh riêng về {title}",
            Tags(DefaultBeats),
            DefaultBeats);

    /// <summary>
    /// The Domain candidate of the same shape as <see cref="IdeaWithLogline"/>, for a test that drives the checkers
    /// directly instead of through a run.
    /// </summary>
    /// <param name="title">The episode title.</param>
    /// <param name="logline">The candidate's logline.</param>
    /// <returns>The candidate.</returns>
    internal static IdeaCandidate CandidateWithLogline(string title, string logline) =>
        new(
            title,
            logline,
            "Đêm ấy có tiếng gõ cửa lúc nửa đêm.",
            "một ngôi nhà cũ bên sông",
            Tags(DefaultBeats),
            DefaultBeats);

    /// <summary>The text the lexical pass scores a candidate on, as the Domain fixes it.</summary>
    /// <param name="candidate">The candidate.</param>
    /// <returns><c>title + " " + logline + " " + setting</c>.</returns>
    internal static string LexicalText(IdeaCandidate candidate) =>
        IdeaNoveltyLexicalScorer.CandidateText(candidate);

    /// <summary>The <c>novelty.v1</c> answer of one batch: one score row per candidate the judge scored.</summary>
    /// <param name="rows">The score rows, in the model's order.</param>
    /// <returns>The answer document.</returns>
    internal static NoveltyV1 NoveltyBatch(params NoveltyScoreV1[] rows) => new(rows);

    /// <summary>One <c>novelty.v1</c> score row, with neutral craft components by default.</summary>
    /// <param name="index">The candidate's 0-based position in the batch.</param>
    /// <param name="score">The judge's novelty score, 0…1.</param>
    /// <param name="fit">The judge's fit score, 0…1.</param>
    /// <param name="hookStrength">The judge's hook reading, 0…1.</param>
    /// <param name="overlapIndex">The 1-based index of the closest past episode as text, or <see langword="null"/>.</param>
    /// <param name="reason">The judge's justification, as the answer carries it.</param>
    /// <returns>The score row.</returns>
    internal static NoveltyScoreV1 JudgeRow(
        int index,
        decimal score,
        decimal fit = 1m,
        decimal hookStrength = 0.5m,
        string? overlapIndex = null,
        string reason = "Gần với một tập đã kể.") =>
        new(index, overlapIndex, score, fit, hookStrength, reason);

    // ---------------------------------------------------------------- the second corpus source

    /// <summary>
    /// Seeds one approved episode of the series and then rewrites the two fields the novelty corpus projects: the
    /// version's <c>title</c> and its <c>content-&gt;&gt;'description'</c>. The row itself is the production
    /// fixture's (a real <c>story_versions</c> row with a real approval), because only the projection is what the
    /// corpus — and so the test — can see.
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The world.</param>
    /// <param name="title">The approved version's title.</param>
    /// <param name="summary">The approved version's description, the corpus summary.</param>
    /// <returns>The episode id.</returns>
    internal static async Task<Guid> SeedApprovedEpisodeAsync(
        TestDatabase database,
        World world,
        string title = "Tập đã duyệt",
        string summary = "Một chuyện ma nhẹ nhàng về căn nhà bên sông.")
    {
        var episodeId = await NightTales.Api.IntegrationTests.SeriesBible.SeriesBibleHarness
            .SeedApprovedEpisodeAsync(
                database,
                new NightTales.Api.IntegrationTests.SeriesBible.World(world.UserId, world.SeriesId));

        SqlProbe.Execute(
            database,
            $"""
             UPDATE story_versions
             SET title = '{Quote(title)}',
                 content = jsonb_build_object('schema_version', 'story.v1', 'description', '{Quote(summary)}')
             WHERE episode_id = '{Pg(episodeId)}'
             """);

        return episodeId;
    }

    /// <summary>
    /// Seeds an approved episode whose corpus text equals the lexical text of <paramref name="title"/>'s
    /// <see cref="DistinctCandidate"/> exactly, so the lexical pass scores that candidate 1.0 and rejects it.
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The world.</param>
    /// <param name="title">The candidate's title (the copy is the same story under the same title).</param>
    /// <returns>The episode id.</returns>
    internal static Task<Guid> SeedCorpusCopyAsync(TestDatabase database, World world, string title)
    {
        var candidate = DistinctCandidate(title);

        return SeedApprovedEpisodeAsync(database, world, candidate.Title, $"{candidate.Logline} {candidate.Setting}");
    }

    /// <summary>One <c>proposed</c> idea of an earlier run of the series, the corpus' second source.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The world.</param>
    /// <param name="runId">The earlier run the row hangs off.</param>
    /// <param name="title">The idea's title.</param>
    /// <param name="logline">The idea's logline.</param>
    /// <returns>The idea id.</returns>
    internal static async Task<Guid> SeedPendingIdeaAsync(
        TestDatabase database,
        World world,
        Guid runId,
        string title,
        string logline)
    {
        var clock = new FakeClock(Start);
        await using var db = database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }, clock);

        var idea = EpisodeIdea.CreateProposed(
            world.SeriesId,
            world.UserId,
            runId,
            new IdeaCandidate(title, logline, "Móc của ý tưởng cũ.", "Bối cảnh của ý tưởng cũ.", Tags(DefaultBeats), DefaultBeats),
            clock);

        db.EpisodeIdeas.Add(idea);
        await db.SaveChangesAsync(Ct);

        return idea.Id;
    }

    /// <summary>
    /// Seeds a <c>proposed</c> idea of an earlier run whose corpus text equals the lexical text of
    /// <paramref name="title"/>'s <see cref="DistinctCandidate"/> exactly — the pending-idea half of
    /// <see cref="SeedCorpusCopyAsync"/>.
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The world.</param>
    /// <param name="runId">The earlier run the row hangs off.</param>
    /// <param name="title">The candidate's title; the pending idea is the same story under the same title.</param>
    /// <returns>The idea id.</returns>
    internal static Task<Guid> SeedPendingCopyAsync(TestDatabase database, World world, Guid runId, string title)
    {
        var candidate = DistinctCandidate(title);

        return SeedPendingIdeaAsync(database, world, runId, candidate.Title, $"{candidate.Logline} {candidate.Setting}");
    }

    // ---------------------------------------------------------------- call probes

    /// <summary>The requests the fake received for one step, in order.</summary>
    /// <param name="text">The scripted provider edge.</param>
    /// <param name="step">The step to select.</param>
    /// <returns>The captured calls of that step.</returns>
    internal static IReadOnlyList<TextCallRequest> CallsFor(FakeTextClient text, TextStep step) =>
        [.. text.Requests.Where(request => request.Step == step)];

    /// <summary>The idea-generation call(s) of a run, in order.</summary>
    /// <param name="text">The scripted provider edge.</param>
    /// <returns>The captured <see cref="TextStep.Ideas"/> calls.</returns>
    internal static IReadOnlyList<TextCallRequest> IdeaCalls(FakeTextClient text) => CallsFor(text, TextStep.Ideas);

    /// <summary>The novelty judge call(s) of a run, in order: the batch's one call, plus its retry when the first
    /// output was refused.</summary>
    /// <param name="text">The scripted provider edge.</param>
    /// <returns>The captured <see cref="TextStep.Novelty"/> calls.</returns>
    internal static IReadOnlyList<TextCallRequest> NoveltyCalls(FakeTextClient text) => CallsFor(text, TextStep.Novelty);

    /// <summary>The number of entries of one array property of the captured request's data object.</summary>
    /// <param name="request">The captured call.</param>
    /// <param name="property">The top-level array property.</param>
    /// <returns>The array's length.</returns>
    internal static int DataObjectArrayLength(TextCallRequest request, string property)
    {
        using var document = JsonDocument.Parse(request.UserDataJson);

        return document.RootElement.GetProperty(property).GetArrayLength();
    }

    /// <summary>One entry's property of a data-object array, as text.</summary>
    /// <param name="request">The captured call.</param>
    /// <param name="array">The top-level array property.</param>
    /// <param name="index">The entry's position.</param>
    /// <param name="property">The entry's property.</param>
    /// <returns>The property's text.</returns>
    internal static string DataObjectArrayText(TextCallRequest request, string array, int index, string property)
    {
        using var document = JsonDocument.Parse(request.UserDataJson);

        return document.RootElement.GetProperty(array)[index].GetProperty(property).GetString() ?? string.Empty;
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
    /// One idea row's disposition: status, rejection reason, score and similarity. The two numbers are rendered
    /// without their column's trailing zeros (<c>numeric(5,4)</c> would print <c>0.4200</c>) and a NULL column
    /// prints <c>-</c>.
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="ideaId">The idea.</param>
    /// <returns><c>&lt;status&gt;|&lt;reason or -&gt;|&lt;score&gt;|&lt;similarity&gt;</c>.</returns>
    internal static string IdeaOutcome(TestDatabase database, Guid ideaId) =>
        SqlProbe.ScalarText(
            database,
            $"""
             SELECT status || '|' || coalesce(rejected_reason, '-') || '|' || {Number("score")} || '|' || {Number("similarity")}
             FROM episode_ideas WHERE id = '{Pg(ideaId)}'
             """);

    /// <summary>The id of one idea row of the series, addressed by its title.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="seriesId">The series.</param>
    /// <param name="title">The idea's title.</param>
    /// <returns>The idea id.</returns>
    internal static Guid IdeaIdOf(TestDatabase database, Guid seriesId, string title) =>
        Guid.Parse(SqlProbe.ScalarText(
            database,
            $"SELECT id::text FROM episode_ideas WHERE series_id = '{Pg(seriesId)}' AND title = '{Quote(title)}'"));

    /// <summary>
    /// The id of the idea row of the series that carries this title and is <b>not</b> <paramref name="other"/>: a
    /// test that seeds a pending copy of an earlier run carries two rows under one title by construction, which
    /// <see cref="IdeaIdOf"/> cannot tell apart.
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="seriesId">The series.</param>
    /// <param name="title">The idea's title, carried by both rows.</param>
    /// <param name="other">The row to leave out.</param>
    /// <returns>The other row's id.</returns>
    internal static Guid IdeaIdOfOther(TestDatabase database, Guid seriesId, string title, Guid other) =>
        Guid.Parse(SqlProbe.ScalarText(
            database,
            $"SELECT id::text FROM episode_ideas WHERE series_id = '{Pg(seriesId)}' AND title = '{Quote(title)}'" +
            $" AND id <> '{Pg(other)}'"));

    /// <summary>
    /// One stored <c>novelty</c> document's readings: the lexical score, the judge's score, the episode the judge
    /// named, and the lexical pass' overlap kind and id. A null half prints <c>null</c>, a number without its
    /// trailing zeros.
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="ideaId">The idea.</param>
    /// <returns>The five readings.</returns>
    internal static string NoveltyScores(TestDatabase database, Guid ideaId) =>
        SqlProbe.ScalarText(
            database,
            $"""
             SELECT {Number("novelty->'lexical'->>'score'")} || '|' || {Number("novelty->'judge'->>'score'")}
                 || '|' || coalesce(novelty->'judge'->>'max_overlap_episode_id', 'null')
                 || '|' || coalesce(novelty->'lexical'->>'max_overlap_kind', 'null')
                 || '|' || coalesce(novelty->'lexical'->>'max_overlap_id', 'null')
             FROM episode_ideas WHERE id = '{Pg(ideaId)}'
             """);

    /// <summary>Whether the stored <c>novelty</c> document carries a judge half (<c>judge</c>) or its null
    /// degradation (<c>judge-null</c>) — the two are one <c>null</c> through a JSON path probe.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="ideaId">The idea.</param>
    /// <returns>The two states.</returns>
    internal static string NoveltyJudgePresence(TestDatabase database, Guid ideaId) =>
        SqlProbe.ScalarText(
            database,
            $"""
             SELECT case when novelty->'judge' = 'null'::jsonb then 'judge-null' else 'judge' end
             FROM episode_ideas WHERE id = '{Pg(ideaId)}'
             """);

    /// <summary>The corpus counts the stored document reports: <c>&lt;episodes&gt;|&lt;pending ideas&gt;</c>.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="ideaId">The idea.</param>
    /// <returns>The two counts.</returns>
    internal static string NoveltyCorpusCounts(TestDatabase database, Guid ideaId) =>
        SqlProbe.ScalarText(
            database,
            $"""
             SELECT coalesce(novelty->'corpus'->>'episodes', '-') || '|' || coalesce(novelty->'corpus'->>'pending_ideas', '-')
             FROM episode_ideas WHERE id = '{Pg(ideaId)}'
             """);

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

    /// <summary>A Postgres string literal: the value with its single quotes doubled.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The literal's body, without the surrounding quotes.</returns>
    internal static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>
    /// One numeric expression as text without its trailing zeros, or <c>-</c> when it is SQL NULL: a
    /// <c>numeric(5,4)</c> column and a <c>jsonb</c> number print <c>0.4200</c> and <c>0.42</c> for the same
    /// reading, and an assertion should not depend on which one it read.
    /// </summary>
    /// <param name="expression">The numeric expression (a column or a JSON path).</param>
    /// <returns>The SQL expression.</returns>
    private static string Number(string expression) =>
        $"case when {expression} is null then '-' "
        + $"else coalesce(nullif(trim(trailing '.' from rtrim({expression}::text, '0')), ''), '0') end";

    /// <summary>
    /// A hand-written spend cap (no mocking library): it records every check and answers what the test set, so a
    /// refusal is exact instead of depending on the platform's real spend and open bounds.
    /// </summary>
    internal sealed class StubBudgetGuard(bool allowed) : IBudgetGuard
    {
        /// <summary>What the planner asked, in order: the bound and the run it excluded.</summary>
        internal List<(CostBound Bound, Guid? ExcludingRunId)> Checks { get; } = [];

        /// <inheritdoc />
        public ValueTask<ErrorOr<Success>> CheckAsync(CostBound worstCaseUsd, CancellationToken ct, Guid? excludingRunId = null)
        {
            Checks.Add((worstCaseUsd, excludingRunId));

            return allowed
                ? ValueTask.FromResult<ErrorOr<Success>>(Result.Success)
                : ValueTask.FromResult<ErrorOr<Success>>(
                    Error.Failure(ErrorCodes.SpendCapReached, "The stub guard refuses every dispatch."));
        }
    }

    /// <summary>A hand-written estimator, so a step's admission check does not depend on the price catalogue.</summary>
    /// <param name="bound">The bound the ideas step's guard sees.</param>
    /// <param name="noveltyBound">The bound the judge's guard sees; the ideas bound unless a test sets its own.</param>
    internal sealed class StubIdeaCostEstimator(UsageBound bound, UsageBound? noveltyBound = null)
        : IIdeaCostEstimator
    {
        /// <summary>
        /// The bound the novelty judge's guard is checked with (02.8.1-T2): the same as the ideas bound by
        /// default, because most tests care that a refusal propagates, not which step's number it carries.
        /// </summary>
        internal UsageBound NoveltyBound { get; } = noveltyBound ?? bound;

        /// <inheritdoc />
        public Task<ErrorOr<UsageBound>> WorstCaseAsync(string priceVersion, CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(bound);

        /// <inheritdoc />
        public Task<ErrorOr<UsageBound>> NoveltyWorstCaseAsync(string priceVersion, CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(NoveltyBound);
    }

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
