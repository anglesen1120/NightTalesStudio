using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Api.IntegrationTests.StoryGeneration;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories;
using NightTales.Domain.Catalog;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Keywords;
using NightTales.Domain.Usage;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Npgsql;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Rendering;

/// <summary>
/// The 03 D10 keyword lint on the render path (03.2.2-T2): <c>POST /api/v1/episodes/{id}/render</c> re-runs the
/// linter because the episode's research can change between the approval and the render, answers 409
/// <c>keywords_flagged</c> with the D10 <c>errors[]</c> array, and refuses an episode whose plan has no term
/// with the single <c>no_scenes</c> row.
/// </summary>
/// <remarks>
/// The real <c>NameCorpusProvider</c> runs over real <c>story_sources</c> and <c>fact_sheets</c> rows
/// (<see cref="RealGuardFixtures"/>) and the real gate approves the episode the first test renders, so the
/// sequence "approved while the research was clean, blocked once it was not" is the production chain's.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class RenderEpisode_KeywordsFlaggedTests(TestDatabase database) : IntegrationTest(database)
{
    private const string RenderRequested = "render_requested.v1";

    private const string Attestation = """{"attestation":{"accepted":true,"textVersion":"ta.v1"}}""";

    /// <summary>A clean draft moderation verdict (02 D12): the approve step below never calls the provider.</summary>
    private const string Clean = """{"flagged":false,"categories":[],"blocking":[],"stale":false,"unavailable":false}""";

    /// <summary>A fact statement with no capitalised two-token run: the corpus stays empty of names.</summary>
    private const string NamelessStatement = "The case began in a quiet ward.";

    /// <summary>An approved fact statement that names two people.</summary>
    private const string NamingStatement = "Vụ án Nguyễn Văn An";

    /// <summary>A valid keyword whose folded tokens repeat the statement's <c>nguyen van</c> run.</summary>
    private const string NamingKeyword = "nguyen van at the market";

    /// <summary>A valid keyword with no name run.</summary>
    private const string CleanKeyword = "empty wooden pier at dusk";

    private static readonly DateTimeOffset Start = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task Render_FactSheetGainedNameAfterApprove_Returns409()
    {
        var world = await SeedAsync(Topic.TrueCrime, NamingKeyword, NamelessStatement, emptyKeywords: false, reviewable: true);
        var ledger = new RecordingLedger();
        using var factory = Factory(ledger: ledger);
        using var client = factory.ClientFor(world.UserId);

        // The approval runs the production chain over a corpus without a single name run, so the story passes
        // the lint (03 D10/CR-14) and really reaches story_approved before the render below.
        using (var approved = await ApproveAsync(client, world))
        {
            approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(Ct));
        }

        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("story_approved");

        // A re-approved sheet then names a person: the render's own lint is the backstop for research that
        // changed after the approval.
        await RenameTheSheetAsync(world);

        using var response = await RenderAsync(client, world.EpisodeId, Etag(world.EpisodeId), "render-renamed");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        var root = await BodyAsync(response);
        root.GetProperty("code").GetString().ShouldBe("keywords_flagged");
        var flags = root.GetProperty("errors").EnumerateArray().ToArray();
        flags.Length.ShouldBe(1);
        flags[0].GetProperty("segment").GetString().ShouldBe("p:1");
        flags[0].GetProperty("index").GetInt32().ShouldBe(0);
        flags[0].GetProperty("rule").GetString().ShouldBe(RenderKeywordLinter.NameDetected);
        AssertNothingRendered(world);

        // The refusal sits before the reserve: the ledger was never asked (03.2.2 §1 step 3, AC2's "no reserve").
        ledger.ReserveCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Render_NoScenes_Returns409NoScenes()
    {
        var world = await SeedAsync(Topic.Ghost, CleanKeyword, factStatement: null, emptyKeywords: true);
        var ledger = new RecordingLedger();
        using var factory = Factory(ledger: ledger);
        using var client = factory.ClientFor(world.UserId);

        using var response = await RenderAsync(client, world.EpisodeId, Etag(world.EpisodeId), "render-no-scenes");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        var root = await BodyAsync(response);
        root.GetProperty("code").GetString().ShouldBe("keywords_flagged");
        var flags = root.GetProperty("errors").EnumerateArray().ToArray();
        flags.Length.ShouldBe(1);
        flags[0].GetProperty("segment").GetString().ShouldBe(RenderErrorRules.MetaSegment);
        flags[0].GetProperty("index").GetInt32().ShouldBe(0);
        flags[0].GetProperty("rule").GetString().ShouldBe(RenderErrorRules.NoScenes);
        AssertNothingRendered(world);
        ledger.ReserveCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Render_CleanKeywords_Proceeds()
    {
        // A true-crime episode with a real corpus whose statements carry no name run: a corpus with no match is
        // not by itself a refusal, so the render is queued as usual.
        var world = await SeedAsync(Topic.TrueCrime, CleanKeyword, NamelessStatement, emptyKeywords: false);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await RenderAsync(client, world.EpisodeId, Etag(world.EpisodeId), "render-clean");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var root = await BodyAsync(response);
        root.GetProperty("status").GetString().ShouldBe("queued");
        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE episode_id = '{world.EpisodeId}'").ShouldBe(1);
        SqlProbe.Count(Database, $"SELECT count(*) FROM outbox_messages WHERE type = '{RenderRequested}' AND user_id = '{world.UserId}'").ShouldBe(1);
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("rendering");
    }

    [Fact]
    public async Task ProblemDetails_KeywordsFlagged_HasErrorsArray()
    {
        var world = await SeedAsync(Topic.TrueCrime, NamingKeyword, NamingStatement, emptyKeywords: false);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await RenderAsync(client, world.EpisodeId, Etag(world.EpisodeId), "render-problem");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        var text = await response.Content.ReadAsStringAsync(Ct);

        // The wire body is the D10 shape: the problem members plus an `errors` array of row objects. The body is
        // never read through ProblemResponse here, because this one code's `errors` member is not a field group.
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        root.GetProperty("type").GetString().ShouldBe("https://nighttales.app/errors/keywords_flagged");
        root.GetProperty("status").GetInt32().ShouldBe(409);
        root.GetProperty("code").GetString().ShouldBe("keywords_flagged");
        root.TryGetProperty("detail", out _).ShouldBeFalse("the D10 body carries no detail");
        root.TryGetProperty("flags", out _).ShouldBeFalse();
        var flags = root.GetProperty("errors").EnumerateArray().ToArray();
        flags.ShouldNotBeEmpty();
        foreach (var flag in flags)
        {
            string.Join(',', flag.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal))
                .ShouldBe("index,rule,segment");
        }

        // Neither the keyword nor the name of the statement reaches any part of the wire body.
        text.ShouldNotContain(NamingKeyword);
        text.ShouldNotContain("nguyen");
        text.ShouldNotContain("market");
        text.ShouldNotContain(NamingStatement);
    }

    [Fact]
    public async Task Render_NameCorpusLoadFails_Returns503_NoRenderJob()
    {
        // The corpus read is the render's own precondition (03 D11): a transient failure must never be read as
        // "nothing flagged". The episode keeps its approval, no job row and no reserve are written, and the answer
        // is the documented 503 (09 README D5), not a 500 or a pass.
        var world = await SeedAsync(Topic.TrueCrime, CleanKeyword, NamelessStatement, emptyKeywords: false);
        using var factory = Factory(corpus: new UnavailableCorpusProvider());
        using var client = factory.ClientFor(world.UserId);
        using var response = await RenderAsync(client, world.EpisodeId, Etag(world.EpisodeId), "render-corpus-fails");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, await response.Content.ReadAsStringAsync(Ct));
        var problem = await ProblemAsync(response);
        problem.Code.ShouldBe(ErrorCodes.ServiceUnavailable);
        problem.Type.ShouldBe("https://nighttales.app/errors/service_unavailable");
        AssertNothingRendered(world);
    }

    [Fact]
    public async Task Render_ContentUnreadable_FailsClosedWithoutQueueing()
    {
        // A version whose stored content is JSON but not a story.v1 draft. StoryRenderInputProvider (09.6.1) throws
        // on it, so the pipeline answers 500 internal_error rather than D5's 503 — QA finding F-1, pre-existing in
        // the provider and out of this task's scope. The invariant this test pins is the fail-closed one: no
        // render_jobs row, no outbox message and no reserve for a story the linter never saw.
        var world = await SeedAsync(Topic.TrueCrime, CleanKeyword, NamelessStatement, emptyKeywords: false);
        BreakStoredContent(world);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await RenderAsync(client, world.EpisodeId, Etag(world.EpisodeId), "render-unreadable");

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError, await response.Content.ReadAsStringAsync(Ct));
        (await ProblemAsync(response)).Code.ShouldBe(ErrorCodes.InternalError);
        AssertNothingRendered(world);
    }

    private ApiFactory Factory(ICreditLedger? ledger = null, INameCorpusProvider? corpus = null) =>
        new ApiFactory(Database, new FakeClock(Start))
            .WithSetting("Budget:MaxPlatformCostUsdPerDay", "1000")
            .WithSetting("Budget:MaxPlatformCostUsdPerMonth", "100000")
            .WithServices(services =>
            {
                if (ledger is not null)
                {
                    services.RemoveAll<ICreditLedger>();
                    services.AddSingleton(ledger);
                }

                if (corpus is not null)
                {
                    services.RemoveAll<INameCorpusProvider>();
                    services.AddSingleton(corpus);
                }
            });

    /// <summary>Rewrites the version's stored content into a document that is JSON but not a <c>story.v1</c> draft.</summary>
    /// <param name="world">The world whose version becomes unreadable.</param>
    private void BreakStoredContent(World world) =>
        SqlProbe.Execute(
            Database,
            $$"""UPDATE story_versions SET content = '{"schema_version":"story.v1"}'::jsonb WHERE id = '{{world.VersionId}}'""");

    private static async Task<ProblemResponse> ProblemAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<ProblemResponse>(await response.Content.ReadAsStringAsync(Ct), ProblemResponse.Options)!;

    /// <summary>The corpus read of a database that answers with a transient failure (SQLSTATE 40P01).</summary>
    private sealed class UnavailableCorpusProvider : INameCorpusProvider
    {
        /// <inheritdoc />
        public ValueTask<NameCorpus> LoadAsync(Guid episodeId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            throw new PostgresException("The name corpus read failed.", "ERROR", "ERROR", "40P01");
        }
    }

    /// <summary>
    /// A ledger that counts its reserves: the phase-P production ledger writes nothing (metering only), so a
    /// queue refused before the reserve can only be shown by the port never being called.
    /// </summary>
    private sealed class RecordingLedger : ICreditLedger
    {
        /// <summary>How often the command reserved credits.</summary>
        public int ReserveCalls { get; private set; }

        /// <inheritdoc />
        public ValueTask<ErrorOr<ReservationId>> ReserveAsync(
            Guid userId,
            CreditOperation op,
            Guid referenceId,
            UsageBound bound,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ReserveCalls++;
            return ValueTask.FromResult<ErrorOr<ReservationId>>(new ReservationId(Guid.CreateVersion7()));
        }

        /// <inheritdoc />
        public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<Success>>(Result.Success);

        /// <inheritdoc />
        public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
    }

    /// <summary>
    /// One owner, a series of <paramref name="topic"/> and an episode. With <paramref name="factStatement"/> the
    /// episode carries two fetched sources and the approved fact sheet the version is pinned to;
    /// <paramref name="emptyKeywords"/> stores a draft whose segments carry no keyword. The episode is seeded
    /// already approved, unless <paramref name="reviewable"/> leaves it in <c>story_review</c> with its current
    /// version pointer set, so a test's own approve call runs the production chain instead of the idempotent
    /// replay of an already approved episode (02 D8 stage 1).
    /// </summary>
    /// <param name="topic">The series topic; only true crime builds a corpus.</param>
    /// <param name="paragraphKeyword">The second paragraph's keyword, the linter's input.</param>
    /// <param name="factStatement">The approved sheet's first statement, or null for no sheet at all.</param>
    /// <param name="emptyKeywords">Whether every segment's keyword list is empty.</param>
    /// <param name="reviewable">Whether to leave the episode to be approved by the test rather than approving it here.</param>
    /// <returns>The seeded ids.</returns>
    private async Task<World> SeedAsync(Topic topic, string paragraphKeyword, string? factStatement, bool emptyKeywords, bool reviewable = false)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, topic: topic, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(
            user.Id, series.Id, lengthTier: LengthTier.Short, status: EpisodeStatus.StoryReview, ct: Ct);
        await SeedPriceVersionAsync(user.Id);

        Guid sheetId = Guid.Empty;
        decimal? similarity = null;
        var sources = new List<Guid>(2);
        if (factStatement is not null)
        {
            var first = await RealGuardFixtures.SeedFetchedSourceAsync(Database, user.Id, episode.Id, new string('a', 64), Start, Ct);
            var second = await RealGuardFixtures.SeedFetchedSourceAsync(Database, user.Id, episode.Id, new string('b', 64), Start, Ct);
            sources.Add(first.Id);
            sources.Add(second.Id);
            var sheet = await RealGuardFixtures.SheetAsync(
                Database,
                user.Id,
                episode.Id,
                1,
                [
                    RealGuardFixtures.CitedFact("f1", factStatement, first.Id, "s1"),
                    RealGuardFixtures.CitedFact("f2", "One more detail of the case.", second.Id, "s2"),
                ],
                RealGuardFixtures.Aliases(("s1", first.Id), ("s2", second.Id)),
                FactSheetStatus.Approved,
                Start,
                Ct);
            sheetId = sheet.Id;
            similarity = 0m;
        }

        var draft = Draft(paragraphKeyword, emptyKeywords);
        var version = StoryVersion.Create(
            user.Id,
            episode.Id,
            1,
            StoryVersionSource.Generated,
            draft,
            StoryContentJson.Write(draft, new JsonArray()),
            "model-x",
            "rel=1",
            null,
            null,
            factStatement is null ? null : sheetId,
            Audit(),
            similarity,
            Start);
        await using (var db = Database.CreateAppDbContext(new TestCurrentUser { Id = user.Id }))
        {
            db.StoryVersions.Add(version);
            await db.SaveChangesAsync(Ct);
        }

        SqlProbe.Execute(
            Database,
            reviewable
                ? $"UPDATE episodes SET current_story_version_id = '{version.Id}' WHERE id = '{episode.Id}'"
                : $"UPDATE episodes SET status = 'story_approved', approved_story_version_id = '{version.Id}', approved_at = '{Start:O}' WHERE id = '{episode.Id}'");

        return new World(user.Id, episode.Id, version.Id, sheetId, sources);
    }

    /// <summary>
    /// The re-approval of 02.5.4: the sheet the story was written from becomes superseded and a newer approved
    /// version carries the statement that names two people, which is what the render's lint then reads.
    /// </summary>
    /// <param name="world">The world whose sheet gains the name.</param>
    private async Task RenameTheSheetAsync(World world)
    {
        world.SheetId.ShouldNotBe(Guid.Empty);
        world.SourceIds.Count.ShouldBe(2);

        SqlProbe.Execute(Database, $"UPDATE fact_sheets SET status = {(int)FactSheetStatus.Superseded} WHERE id = '{world.SheetId}'");

        await RealGuardFixtures.SheetAsync(
            Database,
            world.UserId,
            world.EpisodeId,
            2,
            [
                RealGuardFixtures.CitedFact("f1", NamingStatement, world.SourceIds[0], "s1"),
                RealGuardFixtures.CitedFact("f2", "One more detail of the case.", world.SourceIds[1], "s2"),
            ],
            RealGuardFixtures.Aliases(("s1", world.SourceIds[0]), ("s2", world.SourceIds[1])),
            FactSheetStatus.Approved,
            Start,
            Ct);
    }

    private void AssertNothingRendered(World world)
    {
        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE episode_id = '{world.EpisodeId}'").ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM outbox_messages WHERE type = '{RenderRequested}' AND user_id = '{world.UserId}'").ShouldBe(0);
        SqlProbe.Count(Database, "SELECT count(*) FROM usage_records").ShouldBe(0);
        SqlProbe.ScalarText(Database, $"SELECT status || '|' || approved_story_version_id::text FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe($"story_approved|{world.VersionId}");
    }

    private async Task SeedPriceVersionAsync(Guid userId)
    {
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = userId }, new FakeClock(Start));
        if (db.PriceVersions.Any(version => version.Version == 1))
        {
            return;
        }

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
            "render-keywords-price-version",
            Start.AddDays(-1)));
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>The clean <c>audit.v1</c> report of an approved-looking story, so the approve step needs no refresh.</summary>
    /// <returns>The stored report JSON.</returns>
    private static string Audit() =>
        $$$"""{"schema_version":"audit.v1","local":{"issues":[]},"moderation":{{{Clean}}},"model_review":{"scores":{"overall":7.5},"stale":true},"revision":{"rounds":2},"similarity":{"score":null,"n":0}}""";

    /// <summary>
    /// The story the linter reads: one keyword per segment, all of them valid, and the second paragraph's
    /// keyword carrying the name run a naming statement stores.
    /// </summary>
    /// <param name="paragraphKeyword">The second paragraph's keyword.</param>
    /// <param name="emptyKeywords">Whether every keyword list is empty, so the plan has no term at all.</param>
    /// <returns>The content document the version stores.</returns>
    private static StoryContentV1 Draft(string paragraphKeyword, bool emptyKeywords)
    {
        IReadOnlyList<string> Keywords(params string[] keywords) => emptyKeywords ? [] : keywords;

        return new StoryContentV1(
            "The house by the river",
            new StorySegmentV1("The night was quiet.", Keywords("quiet riverside morning"), []),
            [
                new StorySegmentV1("A knock woke the narrator.", Keywords("empty corridor at dawn"), [], "setup"),
                new StorySegmentV1("Nobody stood outside.", Keywords(paragraphKeyword), [], "turn"),
            ],
            new StorySegmentV1("Cold morning light returned.", Keywords("cold morning light"), []),
            "A quiet true-crime story.",
            ["case", "night"],
            ["documentary"],
            new StoryStructureV1("case", "old house", "a knock", "the narrator", "first_person"));
    }

    private static async Task<HttpResponseMessage> ApproveAsync(HttpClient client, World world)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/episodes/{world.EpisodeId}/story/approve")
        {
            Content = new StringContent(Attestation, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", StoryEtag.For(world.VersionId));

        return await client.SendAsync(request, Ct);
    }

    private static async Task<HttpResponseMessage> RenderAsync(HttpClient client, Guid episodeId, string tag, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/episodes/{episodeId}/render");
        request.Headers.TryAddWithoutValidation("If-Match", tag);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);

        return await client.SendAsync(request, Ct);
    }

    /// <summary>The episode's current strong ETag, read from <c>xmin</c> like the client's own copy.</summary>
    /// <param name="episodeId">The episode to read.</param>
    /// <returns>The tag, quotes included.</returns>
    private string Etag(Guid episodeId) =>
        $"\"ep-{SqlProbe.ScalarText(Database, $"SELECT xmin::text FROM episodes WHERE id = '{episodeId}'")}\"";

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.Clone();
    }

    private sealed record World(Guid UserId, Guid EpisodeId, Guid VersionId, Guid SheetId, IReadOnlyList<Guid> SourceIds);
}
