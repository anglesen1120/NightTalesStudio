using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Application.Features.Stories;
using NightTales.Application.Features.VideoReview;
using NightTales.Domain.Assets;
using NightTales.Domain.Episodes;
using NightTales.Domain.Rendering;
using NightTales.Domain.Series;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Gates;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Autopilot;

/// <summary>
/// How the gate engine is composed and what it never exposes (02.8.2-T1 plan §4, task steps 6, 9, 10): both hosts
/// resolve the orchestrator's one entry point, the shipped mode is the dry run on both sides, an absent or unknown
/// profile falls back to <c>default</c> and never to a disabled gate, the Api publishes no route for the gates, and
/// the gate's own log lines carry ids and codes, never a line of story text.
/// </summary>
/// <remarks>
/// <para>
/// The Worker half composes the production Worker graph through <see cref="WorkerHost"/> (09 README D13) and the
/// Api half through <see cref="ApiFactory"/>; both resolve the real registrations, so a missing
/// <c>AddAutopilotGates</c> or <c>AddAutopilotGatesOptions</c> call fails here instead of in 02.8.3-T1.
/// </para>
/// <para>
/// The route check reads the OpenAPI document the Api build writes (<c>src/NightTales.Api/openapi.json</c>, 09
/// README D14), the same artifact the <c>OpenApiDocumentTests</c> and <c>SeriesBibleOpenApiTests</c> read: the gate
/// is an application service, so the document must describe no path for it at all.
/// </para>
/// </remarks>
/// <param name="database">The class's own migrated database, from <see cref="IntegrationTest"/>.</param>
[Trait("Category", "Integration")]
public sealed class GateEngineHostCompositionTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The one instant this class's clock reads.</summary>
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The SHA-256 the stored report and the stored asset share.</summary>
    private static readonly string ReportSha256 = new('d', 64);

    /// <summary>Text that exists only in the seeded story: it must reach no gate log line.</summary>
    private const string StoryMarker = "pier that never reaches a log line";

    private static CancellationToken Ct => TestContextToken.Value;

    /// <summary>
    /// Both hosts resolve <see cref="IAutoApprovalGate"/> from the real composition root, with the shipped dry run
    /// in force, an absent or unknown profile resolving to the <c>default</c> thresholds, and the retention options
    /// the video approval depends on bound in the same graph (task step 9, plan §3.3, §4).
    /// </summary>
    [Fact]
    public async Task GateEngineHostComposition_WorkerAndApi_ResolveAutoApprovalGate()
    {
        await using (var worker = new WorkerHost(Database, new FakeClock(Start)))
        {
            await worker.StartAsync(Ct);

            await using var workerScope = worker.Services.CreateAsyncScope();
            AssertComposed(workerScope.ServiceProvider);
        }

        using var factory = new ApiFactory(Database, new FakeClock(Start));

        await using var apiScope = factory.Services.CreateAsyncScope();
        AssertComposed(apiScope.ServiceProvider);
    }

    /// <summary>
    /// The Api publishes no route for the gate engine: the OpenAPI document of this build describes no path that
    /// mentions the gates at all, while it still describes the rest of the catalogue (task step 10, plan §4: "not
    /// HTTP endpoints ... No new route").
    /// </summary>
    [Fact]
    public void GateEngine_NoPublicRouteAdded()
    {
        var document = ReadOpenApiDocument();

        document.RootElement.TryGetProperty("info", out _).ShouldBeTrue("the document is an OpenAPI document, not an empty object");
        document.RootElement.TryGetProperty("paths", out var paths)
            .ShouldBeTrue("every OpenAPI document carries a paths object");

        var described = paths.EnumerateObject().Select(property => property.Name).ToList();

        // Non-vacuity: the catalogue of this build is in the document, so the absence below is evidence.
        described.Count.ShouldBeGreaterThan(0);
        described.ShouldContain(path => path.Contains("episodes", StringComparison.OrdinalIgnoreCase));

        described
            .Where(path => path.Contains("gate", StringComparison.OrdinalIgnoreCase))
            .ShouldBeEmpty("the gate engine is an application service (02.8.2 plan §4): no route, no error code in the HTTP catalogue");
    }

    /// <summary>
    /// The gate's own log lines carry ids, codes and counts — never a line of story text: the seeded title and
    /// segment text appear in no line of the gate's category while the run id does (09 README D19, plan §4).
    /// </summary>
    [Fact]
    public async Task GateEngine_Logs_ContainNoStoryText()
    {
        var world = await SeedAsync();
        var logs = new CapturingLoggerProvider();
        using var factory = new ApiFactory(Database, new FakeClock(Start))
            .WithLoggerProvider(logs)
            .WithSetting("Autopilot:Gates:DryRun", "false");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            using var pin = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(world.UserId);

            var evaluated = await scope.ServiceProvider.GetRequiredService<IAutoApprovalGate>()
                .EvaluateVideoAsync(world.RunId, Ct);
            evaluated.IsError.ShouldBeFalse(string.Join(", ", evaluated.Errors.Select(error => error.Code)));
        }

        // Prove the sink is live and the gate wrote through it, so the absence below is evidence.
        var gateLines = logs.LinesOf(typeof(VideoGateEvaluator).FullName!);

        gateLines.ShouldNotBeEmpty("the gate logs every evaluation; an empty capture would make the absence below meaningless");
        gateLines.Any(line => line.Message.Contains(world.RunId.ToString(), StringComparison.Ordinal))
            .ShouldBeTrue("the gate logs the run it evaluated, so this capture is the gate's own");

        foreach (var line in gateLines)
        {
            line.Message.ShouldNotContain(StoryMarker, Case.Sensitive, $"the gate never logs story text: {line.Message}");
            line.Message.ShouldNotContain("Grey mist drifted", Case.Sensitive, $"the gate never logs story text: {line.Message}");
            line.Message.ShouldNotContain("Bản dựng", Case.Sensitive, $"the gate never logs episode copy: {line.Message}");
        }

        // No line of the gate's category may carry the render's content hash either: ids and codes only (D19).
        logs.Lines()
            .Where(line => line.Category.StartsWith("NightTales.Application.Features.Autopilot.Gates", StringComparison.Ordinal)
                && line.Message.Contains(ReportSha256, StringComparison.Ordinal))
            .ShouldBeEmpty("a gate log line names ids and codes, never the render's content hash");
    }

    /// <summary>Asserts one host's composed graph the way 02.8.3-T1 will consume it.</summary>
    /// <param name="provider">A scope's service provider of the started host.</param>
    private static void AssertComposed(IServiceProvider provider)
    {
        var gate = provider.GetRequiredService<IAutoApprovalGate>();
        gate.ShouldBeOfType<AutoApprovalGate>("the facade 02.8.3-T1 depends on");

        var options = provider.GetRequiredService<AutopilotGateOptions>();
        options.DryRun.ShouldBe(AutopilotGateOptions.DefaultDryRun, "a host that says nothing about the gates never auto-approves");
        options.Mode.ShouldBe(GateMode.DryRun);

        // An absent, blank or unknown reference resolves to the shipped `default` profile, never to a disabled gate.
        var defaultProfile = options.Resolve(null);
        defaultProfile.Story.MinMeanReviewScore.ShouldBeGreaterThan(0);
        defaultProfile.Video.LoudnessMaxLufs.ShouldBeGreaterThan(defaultProfile.Video.LoudnessMinLufs);
        options.Resolve("no-such-profile").Story.MinMeanReviewScore.ShouldBe(defaultProfile.Story.MinMeanReviewScore);
        options.Resolve("no-such-profile").Video.DurationTolerance.ShouldBe(defaultProfile.Video.DurationTolerance);

        // The approval the gate sends re-stamps retention, so the handler's options must be bound in the same graph.
        var retention = provider.GetRequiredService<AssetRetentionOptions>();
        retention.ShouldNotBeNull();
        retention.RetentionDays.ShouldBe(30, "Assets:RetentionDays is the window the approve command re-stamps with");
    }

    /// <summary>
    /// One owner whose episode carries a finished render: the world one live video-gate evaluation needs, seeded
    /// with the builders and raw SQL (<c>PollRenderJobTests.SeedJobAsync</c>, <c>AutopilotNotificationTests</c>).
    /// </summary>
    /// <returns>The seeded ids.</returns>
    private async Task<World> SeedAsync()
    {
        var actor = new TestCurrentUser();
        var clock = new FakeClock(Start);
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        actor.Id = user.Id;

        var series = await new SeriesBuilder(Database, actor, clock).CreateAsync(
            user.Id,
            aspect: Aspect.Landscape,
            ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor, clock).CreateAsync(
            user.Id,
            series.Id,
            title: $"Bản dựng {StoryMarker}",
            status: EpisodeStatus.VideoReview,
            ct: Ct);

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

        await using (var seedWriter = Database.CreateAppDbContext(actor, clock))
        {
            seedWriter.StoryVersions.Add(version);
            await seedWriter.SaveChangesAsync(Ct);
        }

        var builder = new EpisodeBuilder(Database, actor, clock);
        var asset = await builder.CreateAssetAsync(
            user.Id,
            episode.Id,
            kind: AssetKind.Video,
            durationSec: 301.37m,
            sha256: ReportSha256,
            ct: Ct);
        await builder.AttachFinalVideoAsync(user.Id, episode.Id, asset.Id, Ct);

        var runId = Guid.CreateVersion7();
        var renderJobId = Guid.CreateVersion7();
        await Database.ExecuteAsync(
            "INSERT INTO autopilot_runs (id, user_id, series_id, episode_id, started_at, outcome, gate_results, "
            + "created_at, updated_at) "
            + $"VALUES ('{runId}', '{user.Id}', '{series.Id}', '{episode.Id}', '{Start:O}', 'gate_failed', NULL, now(), now())",
            Ct);

        await Database.ExecuteAsync(
            "INSERT INTO render_jobs (id, user_id, episode_id, story_version_id, reservation_id, status, stage, "
            + "percent, params, attempt, quality_report, submitted_at, started_at, finished_at, worst_case_usd) "
            + $"VALUES ('{renderJobId}', '{user.Id}', '{episode.Id}', '{version.Id}', '{Guid.CreateVersion7()}', "
            + $"'succeeded', 'done', 100, '{ParamsJson(version.Id)}'::jsonb, 1, '{ReportJson()}'::jsonb, "
            + $"'{Start.AddMinutes(-10):O}', '{Start.AddMinutes(-9):O}', '{Start.AddMinutes(-5):O}', 0)",
            Ct);

        return new World(user.Id, episode.Id, runId);
    }

    /// <summary>
    /// The OpenAPI document the Api build wrote (<c>src/NightTales.Api/openapi.json</c>, or the copy next to the test
    /// assembly): the artifact the CI <c>contract</c> job diffs.
    /// </summary>
    /// <returns>The parsed document.</returns>
    private static System.Text.Json.JsonDocument ReadOpenApiDocument()
    {
        var candidates = new[]
        {
            System.IO.Path.Combine(RepositoryRoot(), "src", "NightTales.Api", "openapi.json"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "openapi.json"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(candidate));
            }
        }

        throw new InvalidOperationException(
            "the Api's OpenAPI document was not found; build the solution first (the build writes "
            + "src/NightTales.Api/openapi.json, 09 README D14). Looked at: " + string.Join(", ", candidates));
    }

    /// <summary>The repository root: walks up from the test assembly to the solution file.</summary>
    /// <returns>The root directory.</returns>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "NightTales.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    /// <summary>A complete <c>render-params.v1</c> document (05 D11): every member is <c>[JsonRequired]</c>.</summary>
    /// <param name="storyVersionId">The version the params name.</param>
    /// <returns>The serialised document.</returns>
    private static string ParamsJson(Guid storyVersionId) =>
        JsonSerializer.Serialize(new RenderParamsV1(
            RenderParamsV1.CurrentMapperVersion,
            storyVersionId,
            "landscape",
            "vi",
            "kokoro:test",
            Guid.CreateVersion7(),
            "style=test@0123abcd;neg=v1",
            new ImagePlanSummary(3, 1, 2, 1, 0, [], ["paragraph:1"], "style=test@0123abcd;neg=v1"),
            new SubtitlesParams(true, "landscape@1"),
            null,
            3));

    /// <summary>The stored <c>render-quality.v1</c> document (05 README D10) of a render that passed every check.</summary>
    /// <returns>The serialised document.</returns>
    private static string ReportJson() =>
        JsonSerializer.Serialize(new RenderQualityReportV1
        {
            Pass = true,
            PolicyVersion = 1,
            CheckToolVersion = "render_check.py@1",
            DurationSec = 300.0,
            TtsDurationSec = 301.37,
            IntegratedLufs = -15.0,
            TruePeakDbtp = -1.0,
            Width = 1920,
            Height = 1080,
            SubtitlesPresent = true,
            Sha256 = ReportSha256,
            Normalized = true,
            Checks = [],
            Hints = [],
            Images = JsonNode.Parse("""{"skipped":[],"moderated_count":0}"""),
        });

    /// <summary>The smallest clean story document the version factory accepts (the <c>PollRenderJobTests</c> shape).</summary>
    /// <returns>The story content.</returns>
    private static StoryContentV1 Draft() => new(
        StoryMarker,
        new StorySegmentV1("Grey mist drifted over the riverside.", ["quiet riverside morning"], []),
        [new StorySegmentV1("A wooden pier stood empty at dusk.", ["empty wooden pier at dusk"], [], "setup")],
        new StorySegmentV1("Cold morning light returned.", ["cold morning light"], []),
        "A quiet story about an empty pier.",
        ["mist", "pier"],
        ["atmosphere"],
        new StoryStructureV1("ghost", "riverside", "a misty dawn", "the narrator", "first_person"));

    /// <summary>One line the host rendered, with the category it came from.</summary>
    /// <param name="Category">The logger category.</param>
    /// <param name="Level">The level.</param>
    /// <param name="EventId">The event id.</param>
    /// <param name="Message">The rendered message.</param>
    private sealed record LogEntry(string Category, LogLevel Level, int EventId, string Message);

    /// <summary>
    /// A log provider that keeps every line of every category, so a test can assert what a component never writes
    /// (the <c>PollRenderJobTests</c> and <c>FindSourcesEndpointTests</c> precedent).
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<LogEntry> _entries = [];

        /// <summary>Every line captured so far, in write order.</summary>
        /// <returns>The lines.</returns>
        public List<LogEntry> Lines()
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }

        /// <summary>Every line of one logger category.</summary>
        /// <param name="category">The full category name, for example the evaluator's type name.</param>
        /// <returns>The lines of that category.</returns>
        public List<LogEntry> LinesOf(string category) =>
            [.. Lines().Where(entry => entry.Category.StartsWith(category, StringComparison.Ordinal))];

        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);

        /// <inheritdoc />
        public void Dispose()
        {
        }

        private sealed class Sink(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull =>
                null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner._entries)
                {
                    owner._entries.Add(new LogEntry(category, logLevel, eventId.Id, formatter(state, exception)));
                }
            }
        }
    }

    /// <summary>The ids of one seeded world.</summary>
    /// <param name="UserId">The run's owner.</param>
    /// <param name="EpisodeId">The episode under review.</param>
    /// <param name="RunId">The autopilot run being gated.</param>
    private sealed record World(Guid UserId, Guid EpisodeId, Guid RunId);
}
