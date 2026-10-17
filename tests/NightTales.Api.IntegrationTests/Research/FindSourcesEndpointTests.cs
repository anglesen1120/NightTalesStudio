using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Flags;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Research.SourceFinder;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Generation;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Stories;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Research;

/// <summary>
/// <c>POST /api/v1/episodes/{id}/sources/find</c> against the real host and a real PostgreSQL 17 (02.7.2-T2,
/// plan §4 U1/§5): the flag and allow-list 404s, the 400 of a missing <c>Idempotency-Key</c> and an over-long
/// query, the state and active-run 409s, the tenant 404, the platform spend cap, the 202 contract with its run
/// row and outbox row, replay idempotency, and the D19 privacy rule that the typed query reaches nothing but the
/// outbox payload.
/// </summary>
/// <remarks>
/// The worst-case estimator is a fixed fake (as the fact-sheet route tests do) so the assertion never depends on
/// a price row; the budget guard, the credit ledger, the feature flag and the allow-list are the hand-written
/// doubles of this class, no mocking library (09 README D13).
/// </remarks>
[Trait("Category", "Integration")]
public sealed class FindSourcesEndpointTests(TestDatabase database) : IntegrationTest(database)
{
    private const string OutboxType = "source_find_requested.v1";

    private static readonly DateTimeOffset Start = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    private static string FindRoute(Guid episodeId) => $"/api/v1/episodes/{episodeId}/sources/find";

    [Fact]
    public async Task FindSources_FlagOff_Returns404FeatureDisabled()
    {
        var world = await SeedAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await FindAsync(client, world.EpisodeId, "k1");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("feature_disabled");
        CountRuns(world).ShouldBe(0, "a flag-off route never reaches the command");
    }

    [Fact]
    public async Task FindSources_FlagOnZeroApprovedEntries_Returns404FeatureDisabled()
    {
        var world = await SeedAsync();
        using var factory = NewFactory(
            flagOn: true,
            configure: services => services.RemoveAll<IResearchAllowList>()
                .AddSingleton<IResearchAllowList>(new EmptyAllowList()));
        using var client = factory.ClientFor(world.UserId);

        using var response = await FindAsync(client, world.EpisodeId, "k1");

        // AL-S47-1: with the flag on, a language the reviewed allow-list has nothing approved for is still
        // effectively switched off, and answers the same 404 the flag-off route does.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("feature_disabled");
        CountRuns(world).ShouldBe(0);
    }

    [Fact]
    public async Task FindSources_ActiveRun_Returns409FindSourcesInProgress()
    {
        var world = await SeedAsync();
        await SeedQueuedRunAsync(world);
        using var factory = NewFactory(flagOn: true);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FindAsync(client, world.EpisodeId, "k1");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("find_sources_in_progress");
        CountRuns(world).ShouldBe(1, "only the pre-existing queued run exists");
    }

    [Fact]
    public async Task FindSources_SameKey_ReturnsSameRunId_NoSecondRun()
    {
        var world = await SeedAsync();
        using var factory = NewFactory(flagOn: true);
        using var client = factory.ClientFor(world.UserId);

        using var first = await FindAsync(client, world.EpisodeId, "same-key");
        var firstBody = await first.Content.ReadAsStringAsync(Ct);
        using var replay = await FindAsync(client, world.EpisodeId, "same-key");
        var replayBody = await replay.Content.ReadAsStringAsync(Ct);

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted, firstBody);
        replay.StatusCode.ShouldBe(HttpStatusCode.Accepted, replayBody);
        replayBody.ShouldBe(firstBody, "a replay answers the first body, including the run id");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}' AND kind = 'find_sources'")
            .ShouldBe(1);
        SqlProbe.Count(Database, $"SELECT count(*) FROM outbox_messages WHERE type = '{OutboxType}' AND user_id = '{world.UserId}'")
            .ShouldBe(1, "the replay queues nothing new");
    }

    [Fact]
    public async Task FindSources_MissingKey_Returns400()
    {
        var world = await SeedAsync();
        using var factory = NewFactory(flagOn: true);
        using var client = factory.ClientFor(world.UserId);

        using var response = await client.PostAsJsonAsync(FindRoute(world.EpisodeId), new { query = "a search" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("idempotency_key_required");
        CountRuns(world).ShouldBe(0);
    }

    [Fact]
    public async Task FindSources_OverBudget_Returns429SpendCapReached()
    {
        var world = await SeedAsync();
        var guard = new FakeBudgetGuard { Deny = true };
        var ledger = new RecordingLedger();
        using var factory = NewFactory(flagOn: true, services =>
        {
            services.RemoveAll<IBudgetGuard>().AddSingleton<IBudgetGuard>(guard);
            services.RemoveAll<ICreditLedger>().AddSingleton<ICreditLedger>(ledger);
        });
        using var client = factory.ClientFor(world.UserId);

        using var response = await FindAsync(client, world.EpisodeId, "k1");

        response.StatusCode.ShouldBe((HttpStatusCode)429, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("spend_cap_reached");
        CountRuns(world).ShouldBe(0, "the denied command writes no run row");
        SqlProbe.Count(Database, $"SELECT count(*) FROM outbox_messages WHERE user_id = '{world.UserId}'")
            .ShouldBe(0, "and no outbox row");
        ledger.Reservations.ShouldBeEmpty("the reservation is taken only after the cap check");
        SqlProbe.Count(Database, $"SELECT count(*) FROM audit_logs WHERE subject_user_id = '{world.UserId}'")
            .ShouldBe(0, "a denied command is audited nowhere");
    }

    [Fact]
    public async Task FindSources_EpisodeNotDraftOrReview_Returns409()
    {
        var world = await SeedAsync(status: EpisodeStatus.StoryApproved);
        using var factory = NewFactory(flagOn: true);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FindAsync(client, world.EpisodeId, "k1");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("episode_invalid_state");
        CountRuns(world).ShouldBe(0);
    }

    [Fact]
    public async Task FindSources_OtherUsersEpisode_Returns404()
    {
        var owner = await SeedAsync();
        var stranger = await SeedAsync();
        using var factory = NewFactory(flagOn: true);
        using var client = factory.ClientFor(stranger.UserId);

        using var response = await FindAsync(client, owner.EpisodeId, "k1");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync(Ct));
        CountRuns(owner).ShouldBe(0);
    }

    [Fact]
    public async Task FindSources_Accepted_Returns202WithLocationAndRunRowAndOutboxQueryOnlyInOutbox()
    {
        var world = await SeedAsync();
        using var factory = NewFactory(flagOn: true);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FindAsync(client, world.EpisodeId, "k1", query: "harbour murders 1994");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        response.Headers.Location.ShouldNotBeNull();
        response.Headers.Location!.ToString().ShouldBe($"/api/v1/episodes/{world.EpisodeId}");
        var runId = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("generationRunId").GetGuid();

        SqlProbe.ScalarText(
                Database,
                "SELECT kind || '/' || trigger || '/' || status || '/' || counts_toward_limit::text "
                + $"|| '/' || (reservation_id IS NOT NULL)::text || '/' || idempotency_key "
                + $"FROM generation_runs WHERE id = '{runId}'")
            .ShouldBe("find_sources/user/queued/false/true/k1");

        SqlProbe.ScalarText(
                Database,
                $"SELECT payload->>'query' FROM outbox_messages WHERE type = '{OutboxType}' AND payload->>'generationRunId' = '{runId}'")
            .ShouldBe("harbour murders 1994", "the outbox payload is the only carrier of the query (D19)");

        SqlProbe.ScalarText(
                Database,
                $"SELECT data->>'generationRunId' FROM audit_logs WHERE action = 'episode.sources_find_requested' AND subject_user_id = '{world.UserId}'")
            .ShouldBe(runId.ToString("D"), "the audit row holds ids and money only");

        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}' AND kind = 'find_sources'")
            .ShouldBe(1);
    }

    [Fact]
    public async Task FindSources_QueryNotInJobArgsOrLogs()
    {
        var marker = "qa-marker-3f81b7c9";
        var world = await SeedAsync();
        var logs = new CapturingLoggerProvider();
        using var factory = NewFactory(flagOn: true).WithLoggerProvider(logs);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FindAsync(client, world.EpisodeId, "privacy-key", query: marker);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));

        // Prove the sink is live, so the absence below is evidence and not an empty capture.
        factory.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("SourceFinderPrivacyProbe")
            .Log(LogLevel.Warning, new EventId(1), "source-finder log capture probe", null, static (state, _) => state);
        logs.Text.ShouldContain("source-finder log capture probe");

        logs.Text.ShouldNotContain(marker, Case.Sensitive, "D19: the query never reaches a log line");
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE row_to_json(generation_runs)::text LIKE '%{marker}%'")
            .ShouldBe(0, "the query is not stored on the run");
        SqlProbe.Count(Database, $"SELECT count(*) FROM audit_logs WHERE row_to_json(audit_logs)::text LIKE '%{marker}%'")
            .ShouldBe(0, "the query is not stored in the audit trail");

        // The host's Hangfire storage prepares its schema on start; if this host never created it, no job could
        // have been queued at all, which is the same evidence the count gives when it exists.
        if (SqlProbe.ScalarText(Database, "SELECT (to_regclass('hangfire.job') IS NOT NULL)::text") == "true")
        {
            SqlProbe.Count(Database, $"SELECT count(*) FROM hangfire.job WHERE arguments::text LIKE '%{marker}%'")
                .ShouldBe(0, "the query is never a job argument");
        }
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = '{OutboxType}' AND payload->>'query' = '{marker}'")
            .ShouldBe(1, "the outbox payload is the one place the query lives");
    }

    [Fact]
    public async Task FindSources_QueryOver300_Returns400()
    {
        var world = await SeedAsync();
        using var factory = NewFactory(flagOn: true);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FindAsync(client, world.EpisodeId, "k1", query: new string('q', 301));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("validation_failed");
        CountRuns(world).ShouldBe(0);
    }

    [Fact]
    public async Task FindSources_ArchivedSeries_Returns409SeriesArchived_TakesNoRunAndNoReservation()
    {
        var world = await SeedAsync();
        SqlProbe.Execute(Database, $"UPDATE series SET archived_at = now() WHERE id = '{world.SeriesId}'");
        var ledger = new RecordingLedger();
        using var factory = ApprovedOnlyFactory(Language.Vi, ledger);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FindAsync(client, world.EpisodeId, "archived-k");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("series_archived");
        CountRuns(world).ShouldBe(0, "an archived series is refused before a run row is written");
        OutboxCount(world).ShouldBe(0);
        ledger.Reservations.ShouldBeEmpty("an archived series is refused before any credit is reserved");
    }

    [Fact]
    public async Task FindSources_ReusedKeyWithDifferentQuery_Returns422_AndNoSecondRun()
    {
        var world = await SeedAsync();
        using var factory = ApprovedOnlyFactory(Language.Vi);
        using var client = factory.ClientFor(world.UserId);

        using var first = await FindAsync(client, world.EpisodeId, "reuse-k", query: "first query");
        using var reused = await FindAsync(client, world.EpisodeId, "reuse-k", query: "a different query");

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted, await first.Content.ReadAsStringAsync(Ct));
        reused.StatusCode.ShouldBe(
            HttpStatusCode.UnprocessableEntity,
            await reused.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(reused)).Code.ShouldBe("idempotency_key_reused");
        CountRuns(world).ShouldBe(1, "the reused key must not queue a second run");
        OutboxCount(world).ShouldBe(1);
    }

    [Fact]
    public async Task FindSources_SeriesLanguageHasNoApprovedSite_Returns404FeatureDisabled_EvenWhenOtherLanguageIsApproved()
    {
        var world = await SeedAsync(language: Language.En);
        using var factory = ApprovedOnlyFactory(Language.Vi);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FindAsync(client, world.EpisodeId, "en-k");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("feature_disabled");
        CountRuns(world).ShouldBe(0);
        OutboxCount(world).ShouldBe(0);
    }

    [Fact]
    public async Task FindSources_SeriesLanguageHasApprovedSite_Returns202_PositiveControl()
    {
        var world = await SeedAsync(language: Language.Vi);
        using var factory = ApprovedOnlyFactory(Language.Vi);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FindAsync(client, world.EpisodeId, "vi-k");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        CountRuns(world).ShouldBe(1);
        OutboxCount(world).ShouldBe(1);
    }

    [Fact]
    public async Task FindSources_StoryReviewEpisode_Returns202()
    {
        var world = await SeedAsync(status: EpisodeStatus.StoryReview);
        using var factory = ApprovedOnlyFactory(Language.Vi);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FindAsync(client, world.EpisodeId, "review-k");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        CountRuns(world).ShouldBe(1);
    }

    [Fact]
    public async Task FindSources_QueryExactly300Chars_Returns202()
    {
        var world = await SeedAsync();
        using var factory = ApprovedOnlyFactory(Language.Vi);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FindAsync(client, world.EpisodeId, "q300-k", query: new string('q', 300));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        CountRuns(world).ShouldBe(1);
    }

    [Fact]
    public async Task FindSources_WhitespaceOnlyQuery_Returns202_AsIfNoQuery()
    {
        var world = await SeedAsync();
        using var factory = ApprovedOnlyFactory(Language.Vi);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FindAsync(client, world.EpisodeId, "blank-k", query: "   ");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        CountRuns(world).ShouldBe(1);
    }

    [Fact]
    public async Task FindSources_UnknownEpisode_Returns404NotFound()
    {
        var world = await SeedAsync();
        using var factory = ApprovedOnlyFactory(Language.Vi);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FindAsync(client, Guid.CreateVersion7(), "missing-k");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("not_found");
        CountRuns(world).ShouldBe(0);
    }

    private ApiFactory NewFactory(bool flagOn = false, Action<IServiceCollection>? configure = null) =>
        new ApiFactory(Database, new FakeClock(Start))
            .WithSetting("Budget:MaxPlatformCostUsdPerDay", "1000")
            .WithSetting("Budget:MaxPlatformCostUsdPerMonth", "100000")
            .WithServices(services =>
            {
                services.RemoveAll<IFindSourcesCostEstimator>();
                services.AddSingleton<IFindSourcesCostEstimator, FixedEstimator>();

                if (flagOn)
                {
                    services.RemoveAll<IFeatureFlags>();
                    services.AddSingleton<IFeatureFlags, AlwaysOnFeatureFlags>();
                }

                configure?.Invoke(services);
            });

    private ApiFactory ApprovedOnlyFactory(Language approvedFor, RecordingLedger? ledger = null) =>
        NewFactory(
            flagOn: true,
            configure: services =>
            {
                services.RemoveAll<IResearchAllowList>();
                services.AddSingleton<IResearchAllowList>(new ApprovedOnlyFor(approvedFor));

                if (ledger is not null)
                {
                    services.RemoveAll<ICreditLedger>();
                    services.AddSingleton<ICreditLedger>(ledger);
                }
            });

    private static Task<HttpResponseMessage> FindAsync(
        HttpClient client,
        Guid episodeId,
        string? key,
        string? query = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, FindRoute(episodeId))
        {
            Content = JsonContent.Create(new { query }),
        };

        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return client.SendAsync(request, Ct);
    }

    private static async Task<ProblemResponse> ReadProblemAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, Ct);
        problem.ShouldNotBeNull();

        return problem;
    }

    private long CountRuns(World world) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM generation_runs WHERE episode_id = '{world.EpisodeId}' AND kind = 'find_sources'");

    private long OutboxCount(World world) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM outbox_messages WHERE type = '{OutboxType}' AND user_id = '{world.UserId}'");

    private async Task<World> SeedAsync(
        EpisodeStatus status = EpisodeStatus.Draft,
        Language language = Language.Vi)
    {
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, new TestCurrentUser(), new FakeClock(Start))
            .CreateAsync(user.Id, topic: Topic.TrueCrime, language: language, ct: Ct);
        var episode = await new EpisodeBuilder(Database, new TestCurrentUser(), new FakeClock(Start))
            .CreateAsync(user.Id, series.Id, status: status, ct: Ct);

        return new World(user.Id, series.Id, episode.Id);
    }

    /// <summary>Queues one active <c>find_sources</c> run, the state a second request must answer 409 to.</summary>
    private async Task SeedQueuedRunAsync(World world)
    {
        await using var context = Database.CreateAppDbContext(
            new TestCurrentUser { Id = world.UserId },
            new FakeClock(Start));

        var run = GenerationRun.Queue(
            world.UserId,
            world.SeriesId,
            world.EpisodeId,
            GenerationKind.FindSources,
            GenerationTrigger.User,
            idempotencyKey: null,
            previousEpisodeStatus: EpisodeStatus.Draft,
            inputFactSheetId: null,
            reservationId: null,
            worstCaseUsd: 0.25m,
            now: Start,
            countsTowardLimit: false).Value;

        context.GenerationRuns.Add(run);
        await context.SaveChangesAsync(Ct);
    }

    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId);

    /// <summary>The worst case is fixed: the route test never depends on a price version row.</summary>
    private sealed class FixedEstimator : IFindSourcesCostEstimator
    {
        public Task<ErrorOr<UsageBound>> WorstCaseAsync(CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(new UsageBound(0.25m, 1_000, 2_000));
    }

    /// <summary>Every flag on: a flag-on test exercises one gate, not the kill switch.</summary>
    private sealed class AlwaysOnFeatureFlags : IFeatureFlags
    {
        public Task<bool> IsEnabledAsync(string key, CancellationToken ct) => Task.FromResult(true);
    }

    /// <summary>A reviewed allow-list with nothing approved, so the handler's gate is the one that answers.</summary>
    private sealed class EmptyAllowList : IResearchAllowList
    {
        public IReadOnlyList<AllowedSite> Approved(Language language) => [];

        public bool IsAllowed(string registrableOrHost) => false;

        public string Hash => string.Empty;
    }

    /// <summary>Approves one site for exactly one language and nothing for the other, so the gate is per language.</summary>
    private sealed class ApprovedOnlyFor(Language language) : IResearchAllowList
    {
        public IReadOnlyList<AllowedSite> Approved(Language requested) =>
            requested == language
                ? [new AllowedSite("www.example.org", "Example", requested.ToString().ToLowerInvariant(), "news", "approved")]
                : [];

        public bool IsAllowed(string registrableOrHost) => false;

        public string Hash => string.Empty;
    }

    /// <summary>Counts reservations, so a refused command is proven to have taken none.</summary>
    private sealed class RecordingLedger : ICreditLedger
    {
        public List<ReservationId> Reservations { get; } = [];

        public ValueTask<ErrorOr<ReservationId>> ReserveAsync(
            Guid userId,
            CreditOperation op,
            Guid referenceId,
            UsageBound bound,
            CancellationToken ct)
        {
            var reservation = new ReservationId(Guid.CreateVersion7());
            Reservations.Add(reservation);

            return ValueTask.FromResult<ErrorOr<ReservationId>>(reservation);
        }

        public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<Success>>(Result.Success);

        public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
    }

    /// <summary>Keeps every rendered line the host's loggers produce, so the privacy assertion scans all of them.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];
        private readonly Lock _gate = new();

        internal string Text
        {
            get
            {
                lock (_gate)
                {
                    return string.Join('\n', _messages);
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private void Capture(string rendered)
        {
            lock (_gate)
            {
                _messages.Add(rendered);
            }
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                owner.Capture(formatter(state, exception));
            }
        }
    }
}
