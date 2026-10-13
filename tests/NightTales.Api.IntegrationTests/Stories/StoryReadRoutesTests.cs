using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Features.Stories;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Generation;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Text;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Stories;

/// <summary>
/// Story read routes and the episode's embedded read model against the real host and PostgreSQL (02.3.4-T2).
/// Every assertion is scoped to newly seeded episodes, independent of other rows in the class fixture.
/// </summary>
[Trait("Category", "Integration")]
public sealed class StoryReadRoutesTests(TestDatabase database) : IntegrationTest(database)
{
    private const string SourceUrl = "https://example.test/stored-source";
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);
    private static readonly string[] HintKeys =
        ["ruleId", "severity", "kind", "segment", "messageKey", "evidence", "repairDirection", "measured", "threshold"];
    private static readonly string[] GenerationKeys = ["kind", "status", "stage", "errorCode", "startedAt"];
    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task EpisodeDetail_IncludesCurrentStoryBlock()
    {
        var world = await SeedAsync(withFactSheet: true);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await client.GetAsync(EpisodeRoute(world.EpisodeId), Ct);
        var detail = await ReadOkAsync(response);
        var story = detail.GetProperty("currentStory");

        response.Headers.ETag!.Tag.ShouldStartWith("\"ep-");
        story.GetProperty("versionId").GetGuid().ShouldBe(world.VersionId!.Value);
        story.GetProperty("version").GetInt32().ShouldBe(1);
        story.GetProperty("source").GetString().ShouldBe("generated");
        story.GetProperty("etag").GetString().ShouldBe(StoryEtag.For(world.VersionId!.Value));
        story.GetProperty("etag").GetString().ShouldBe($"\"sv-{world.VersionId}\"");
        story.GetProperty("status").GetString().ShouldBe("story_review");
        story.GetProperty("wordCount").GetInt32().ShouldBe(StoryVersion.Derive(Draft()).WordCount);
        story.GetProperty("body").GetString().ShouldBe(StoryVersion.Derive(Draft()).Body);
        story.GetProperty("keywords").EnumerateArray().Select(item => item.GetString())
            .ShouldBe(StoryVersion.Derive(Draft()).Keywords);
        story.GetProperty("similarityScore").GetDecimal().ShouldBe(0.021m);
        story.GetProperty("factSheetId").GetGuid().ShouldBe(world.FactSheetId!.Value);
        story.GetProperty("approvedStoryVersionId").ValueKind.ShouldBe(JsonValueKind.Null);
        story.GetProperty("content").GetProperty("title").GetString().ShouldBe(Draft().Title);
        story.GetProperty("auditReport").GetProperty("schema_version").GetString().ShouldBe("audit.v1");

        var hints = story.GetProperty("qualityHints").EnumerateArray().ToArray();
        hints.Length.ShouldBe(2, "R7: only local issues become editor hints, not model-review issues");
        hints.Select(hint => hint.GetProperty("kind").GetString()).ShouldBe(["objective", "taste"]);
        hints[0].EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ShouldBe(
            HintKeys.Order(StringComparer.Ordinal));
        hints[0].GetProperty("ruleId").GetString().ShouldBe("Q-KW-01");
        hints[0].GetProperty("severity").GetString().ShouldBe("blocking");
        hints[0].GetProperty("segment").GetString().ShouldBe("p:1");
        hints[0].GetProperty("messageKey").GetString().ShouldBe("quality.keyword_count_per_segment");
        hints[0].GetProperty("evidence").GetString().ShouldBe("0 keywords");
        hints[0].GetProperty("repairDirection").GetString().ShouldBe("add 1-2 keywords");
        hints[0].GetProperty("measured").GetDecimal().ShouldBe(0m);
        hints[0].GetProperty("threshold").GetDecimal().ShouldBe(1m);
        hints[1].GetProperty("messageKey").GetString().ShouldBe("quality.repetitive_phrasing");
        hints[1].GetProperty("measured").ValueKind.ShouldBe(JsonValueKind.Null);
        hints[1].GetProperty("threshold").ValueKind.ShouldBe(JsonValueKind.Null);

        var review = story.GetProperty("review");
        review.GetProperty("scores").GetProperty("hook").GetInt32().ShouldBe(4);
        review.GetProperty("issueCounts").GetProperty("blocking").GetInt32().ShouldBe(1);
        review.GetProperty("issueCounts").GetProperty("advisory").GetInt32().ShouldBe(1);
        review.GetProperty("stale").GetBoolean().ShouldBeTrue();
        JsonNode.DeepEquals(JsonNode.Parse(AuditJson()), JsonNode.Parse(story.GetProperty("auditReport").GetRawText()))
            .ShouldBeTrue("audit.v1 is returned verbatim, including model-review issues");
    }

    [Fact]
    public async Task EpisodeDetail_DraftWithoutStory_CurrentStoryNull_Still200()
    {
        var world = await SeedAsync(withStory: false, status: EpisodeStatus.Draft);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await client.GetAsync(EpisodeRoute(world.EpisodeId), Ct);
        var detail = await ReadOkAsync(response);

        detail.GetProperty("status").GetString().ShouldBe("draft");
        detail.GetProperty("currentStory").ValueKind.ShouldBe(JsonValueKind.Null);
        detail.GetProperty("generation").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task StoryVersionRoutes_ForeignUser_Return404()
    {
        var owner = await SeedAsync();
        var foreign = await SeedAsync(title: "Foreign episode secret");
        var sameOwner = await SeedAsync(owner: owner, title: "Same-owner other episode secret");
        using var factory = Factory();
        using var client = factory.ClientFor(owner.UserId);
        var unknownEpisode = Guid.CreateVersion7();
        var unknownVersion = Guid.CreateVersion7();

        var unknownListCode = await NotFoundCodeAsync(client, VersionsRoute(unknownEpisode));
        (await NotFoundCodeAsync(client, VersionsRoute(foreign.EpisodeId))).ShouldBe(unknownListCode);
        var unknownDetailCode = await NotFoundCodeAsync(client, VersionRoute(owner.EpisodeId, unknownVersion));
        (await NotFoundCodeAsync(client, VersionRoute(foreign.EpisodeId, foreign.VersionId!.Value))).ShouldBe(unknownDetailCode);
        (await NotFoundCodeAsync(client, VersionRoute(unknownEpisode, owner.VersionId!.Value))).ShouldBe(unknownDetailCode);
        (await NotFoundCodeAsync(client, VersionRoute(owner.EpisodeId, foreign.VersionId!.Value))).ShouldBe(unknownDetailCode);
        (await NotFoundCodeAsync(client, VersionRoute(owner.EpisodeId, sameOwner.VersionId!.Value))).ShouldBe(unknownDetailCode);
        unknownDetailCode.ShouldBe(unknownListCode, "unknown and foreign resources use the same episode-not-found problem");
    }

    [Fact]
    public async Task StoryVersionsList_AfterThreeEdits_ReturnsFourRows()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var ids = new List<Guid> { world.VersionId!.Value };
        for (var version = 2; version <= 4; version++)
        {
            using var response = await EditAsync(client, world.EpisodeId, Draft($"Bản sửa thứ {version}"), ids[^1]);
            var saved = await ReadOkAsync(response);
            ids.Add(saved.GetProperty("versionId").GetGuid());
        }

        // Pin approval to a non-current row: approved and isCurrent must not be aliases.
        SqlProbe.Execute(Database,
            $"UPDATE episodes SET approved_story_version_id = '{ids[0]}' WHERE id = '{world.EpisodeId}'");
        using var list = await client.GetAsync(VersionsRoute(world.EpisodeId), Ct);
        var rows = (await ReadOkAsync(list)).EnumerateArray().ToArray();
        list.Headers.CacheControl!.ToString().ShouldBe("no-cache");
        list.Headers.Vary.ShouldContain("Authorization");

        rows.Length.ShouldBe(4);
        rows.Select(row => row.GetProperty("version").GetInt32()).ShouldBe([4, 3, 2, 1]);
        rows.Select(row => row.GetProperty("versionId").GetGuid()).ShouldBe(ids.AsEnumerable().Reverse());
        foreach (var row in rows)
        {
            var id = row.GetProperty("versionId").GetGuid();
            row.TryGetProperty("content", out _).ShouldBeFalse();
            row.TryGetProperty("auditReport", out _).ShouldBeFalse();
            row.GetProperty("isCurrent").GetBoolean().ShouldBe(id == ids[^1]);
            row.GetProperty("approved").GetBoolean().ShouldBe(id == ids[0]);
            row.GetProperty("etag").GetString().ShouldBe(StoryEtag.For(id));
        }
    }

    [Fact]
    public async Task StoryVersionsList_DoesNotSelectContentOrAuditReport_FromPostgreSql()
    {
        var world = await SeedAsync();
        var interceptor = new CountingDbCommandInterceptor();
        using var factory = Factory().WithServices(services =>
            services.AddDbContext<ReadDbContext>((_, options) => options.AddInterceptors(interceptor)));
        using var client = factory.ClientFor(world.UserId);
        interceptor.Reset();
        using var response = await client.GetAsync(VersionsRoute(world.EpisodeId), Ct);
        (await ReadOkAsync(response)).GetArrayLength().ShouldBe(1);

        var versionReads = interceptor.Texts
            .Where(text => text.Contains("story_versions", StringComparison.OrdinalIgnoreCase)).ToArray();
        versionReads.Length.ShouldBe(1, string.Join(" | ", interceptor.Texts));
        versionReads[0].ShouldContain("word_count", Case.Insensitive);
        versionReads[0].ShouldNotContain("content", Case.Insensitive);
        versionReads[0].ShouldNotContain("audit_report", Case.Insensitive);
        versionReads[0].ShouldContain("episode_id", Case.Insensitive);
        versionReads[0].ShouldContain("user_id", Case.Insensitive);
    }

    [Theory]
    [InlineData(false, false, 3)]
    [InlineData(true, false, 3)]
    [InlineData(false, true, 4)]
    [InlineData(true, true, 4)]
    public async Task EpisodeDetail_WithStory_UsesExactSequentialQueryBudget(bool withRun, bool withVideo, int expectedCommands)
    {
        var world = await SeedAsync();
        if (withRun)
        {
            await SeedRunAsync(world, Start.AddMinutes(-1), terminal: false);
        }
        if (withVideo)
        {
            var asset = await new EpisodeBuilder(Database, new TestCurrentUser { Id = world.UserId })
                .CreateAssetAsync(world.UserId, world.EpisodeId, ct: Ct);
            SqlProbe.Execute(Database,
                $"UPDATE episodes SET final_video_asset_id = '{asset.Id}' WHERE id = '{world.EpisodeId}'");
        }

        var interceptor = new CountingDbCommandInterceptor();
        using var factory = Factory().WithServices(services =>
            services.AddDbContext<ReadDbContext>((_, options) => options.AddInterceptors(interceptor)));
        using var client = factory.ClientFor(world.UserId);
        interceptor.Reset();
        using var response = await client.GetAsync(EpisodeRoute(world.EpisodeId), Ct);
        var body = await ReadOkAsync(response);
        body.GetProperty("currentStory").GetProperty("versionId").GetGuid().ShouldBe(world.VersionId!.Value);
        body.GetProperty("generation").ValueKind.ShouldBe(withRun ? JsonValueKind.Object : JsonValueKind.Null);
        body.GetProperty("finalVideo").ValueKind.ShouldBe(withVideo ? JsonValueKind.Object : JsonValueKind.Null);
        interceptor.Count.ShouldBe(expectedCommands, string.Join(" | ", interceptor.Texts));
        interceptor.ObservedConcurrentExecution.ShouldBeFalse();
        interceptor.Commands.ShouldAllBe(command => command.ActiveWhenStarted == 0);
        // 02.3.3-T1: the episode read joins `series` for its topic and language, so the attestation
        // member costs no extra command for a fiction episode; the table names below are unchanged and
        // the join is asserted with the episode command.
        string[] expectedTables = withVideo
            ? ["episodes", "assets", "story_versions", "generation_runs"]
            : ["episodes", "story_versions", "generation_runs"];
        for (var index = 0; index < expectedTables.Length; index++)
        {
            interceptor.Commands[index].Text.ShouldContain(expectedTables[index], Case.Insensitive);
        }
    }

    [Fact]
    public async Task StoryVersionDetail_EtagHeader_MatchesApprovePrecondition()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await client.GetAsync(VersionRoute(world.EpisodeId, world.VersionId!.Value), Ct);
        var body = await ReadOkAsync(response);
        response.Headers.CacheControl!.ToString().ShouldBe("no-cache");
        response.Headers.Vary.ShouldContain("Authorization");

        response.Headers.ETag!.Tag.ShouldBe(StoryEtag.For(world.VersionId!.Value));
        body.GetProperty("etag").GetString().ShouldBe(response.Headers.ETag!.Tag);
        body.GetProperty("versionId").GetGuid().ShouldBe(world.VersionId!.Value);
        body.GetProperty("createdBy").GetString().ShouldBe("system");
        body.GetProperty("factSheetId").ValueKind.ShouldBe(JsonValueKind.Null);
        body.GetProperty("content").GetProperty("title").GetString().ShouldBe(Draft().Title);
        body.GetProperty("auditReport").GetProperty("schema_version").GetString().ShouldBe("audit.v1");
        JsonNode.DeepEquals(JsonNode.Parse(AuditJson()), JsonNode.Parse(body.GetProperty("auditReport").GetRawText())).ShouldBeTrue();

        using var edit = await EditAsync(client, world.EpisodeId, Draft("Căn nhà ven sông"), world.VersionId!.Value);
        var saved = await ReadOkAsync(edit);
        var editedId = saved.GetProperty("versionId").GetGuid();
        using var editedDetail = await client.GetAsync(VersionRoute(world.EpisodeId, editedId), Ct);
        var editedBody = await ReadOkAsync(editedDetail);
        editedDetail.Headers.ETag!.Tag.ShouldBe(edit.Headers.ETag!.Tag);
        editedBody.GetProperty("etag").GetString().ShouldBe(StoryEtag.For(editedId));
        editedBody.GetProperty("createdBy").GetString().ShouldBe("user");
    }

    [Fact]
    public async Task EpisodeDetail_Generating_ExposesGenerationBlock()
    {
        var world = await SeedAsync(status: EpisodeStatus.StoryGenerating);
        await SeedRunAsync(world, Start.AddMinutes(-2), terminal: true);
        var latest = await SeedRunAsync(world, Start.AddMinutes(-1), terminal: false);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await client.GetAsync(EpisodeRoute(world.EpisodeId), Ct);
        var detail = await ReadOkAsync(response);
        var generation = detail.GetProperty("generation");

        detail.GetProperty("status").GetString().ShouldBe("story_generating");
        detail.GetProperty("currentStory").GetProperty("versionId").GetGuid().ShouldBe(world.VersionId!.Value);
        generation.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
            .ShouldBe(GenerationKeys.Order(StringComparer.Ordinal));
        generation.GetProperty("kind").GetString().ShouldBe("story");
        generation.GetProperty("status").GetString().ShouldBe("running");
        generation.GetProperty("stage").GetString().ShouldBe("reviewing");
        generation.GetProperty("errorCode").ValueKind.ShouldBe(JsonValueKind.Null);
        generation.GetProperty("startedAt").GetDateTimeOffset().ShouldBe(latest.StartedAt!.Value);
    }

    [Fact]
    public async Task EpisodeDetail_LatestGenerationTie_UsesIdDescending_AndExposesErrorCode()
    {
        var world = await SeedAsync();
        var first = await SeedRunAsync(world, Start.AddMinutes(-1), terminal: true);
        var second = await SeedRunAsync(world, Start.AddMinutes(-1), terminal: true);
        var latest = new[] { first, second }.OrderByDescending(run => run.Id).First();
        SqlProbe.Execute(Database,
            $"UPDATE generation_runs SET error_code = 'story_invalid_output' WHERE id = '{latest.Id}'");
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await client.GetAsync(EpisodeRoute(world.EpisodeId), Ct);
        var generation = (await ReadOkAsync(response)).GetProperty("generation");

        generation.GetProperty("status").GetString().ShouldBe("failed");
        generation.GetProperty("errorCode").GetString().ShouldBe("story_invalid_output");
        generation.GetProperty("startedAt").GetDateTimeOffset().ShouldBe(latest.StartedAt!.Value);
    }

    [Fact]
    public async Task EpisodeDetail_QueuedGeneration_StartedAtNull()
    {
        var world = await SeedAsync(withStory: false, status: EpisodeStatus.StoryGenerating);
        await SeedRunAsync(world, Start, terminal: false, queued: true);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await client.GetAsync(EpisodeRoute(world.EpisodeId), Ct);
        var detail = await ReadOkAsync(response);
        var generation = detail.GetProperty("generation");

        detail.GetProperty("currentStory").ValueKind.ShouldBe(JsonValueKind.Null);
        generation.GetProperty("status").GetString().ShouldBe("queued");
        generation.GetProperty("stage").GetString().ShouldBe("queued");
        generation.GetProperty("startedAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task StoryVersionDetail_MalformedAuditReport_Returns200WithNullAuditReport()
    {
        var world = await SeedAsync();
        // Valid jsonb but not an audit document: SQL storage cannot hold lexically invalid JSON.
        SqlProbe.Execute(Database, $"UPDATE story_versions SET audit_report = '[]'::jsonb WHERE id = '{world.VersionId}'");
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await client.GetAsync(VersionRoute(world.EpisodeId, world.VersionId!.Value), Ct);
        var detail = await ReadOkAsync(response);

        detail.GetProperty("auditReport").ValueKind.ShouldBe(JsonValueKind.Null);
        detail.GetProperty("qualityHints").GetArrayLength().ShouldBe(0);
        detail.GetProperty("content").GetProperty("title").GetString().ShouldBe(Draft().Title);
        AssertNoExceptionText(detail);
        using var episode = await client.GetAsync(EpisodeRoute(world.EpisodeId), Ct);
        var current = (await ReadOkAsync(episode)).GetProperty("currentStory");
        current.GetProperty("auditReport").ValueKind.ShouldBe(JsonValueKind.Null);
        current.GetProperty("qualityHints").GetArrayLength().ShouldBe(0);
        current.GetProperty("review").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task StoryVersionDetail_LegacyContent_Returns200WithNullContent_AndHistoryStaysUsable()
    {
        var world = await SeedAsync();
        SqlProbe.Execute(Database, $"UPDATE story_versions SET content = '42'::jsonb WHERE id = '{world.VersionId}'");
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await client.GetAsync(VersionRoute(world.EpisodeId, world.VersionId!.Value), Ct);
        var detail = await ReadOkAsync(response);

        detail.GetProperty("content").ValueKind.ShouldBe(JsonValueKind.Null);
        detail.GetProperty("wordCount").GetInt32().ShouldBe(StoryVersion.Derive(Draft()).WordCount);
        detail.GetProperty("body").GetString().ShouldBe(StoryVersion.Derive(Draft()).Body);
        detail.GetProperty("auditReport").GetProperty("schema_version").GetString().ShouldBe("audit.v1");
        AssertNoExceptionText(detail);
        using var episode = await client.GetAsync(EpisodeRoute(world.EpisodeId), Ct);
        (await ReadOkAsync(episode)).GetProperty("currentStory").GetProperty("content").ValueKind.ShouldBe(JsonValueKind.Null);
        using var history = await client.GetAsync(VersionsRoute(world.EpisodeId), Ct);
        (await ReadOkAsync(history)).GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task StoryVersionDetail_AbsentAuditReport_ExposesNoHintsOrReview()
    {
        var world = await SeedAsync();
        SqlProbe.Execute(Database, $"UPDATE story_versions SET audit_report = NULL WHERE id = '{world.VersionId}'");
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await client.GetAsync(VersionRoute(world.EpisodeId, world.VersionId!.Value), Ct);
        var detail = await ReadOkAsync(response);
        detail.GetProperty("auditReport").ValueKind.ShouldBe(JsonValueKind.Null);
        detail.GetProperty("qualityHints").GetArrayLength().ShouldBe(0);
        using var episode = await client.GetAsync(EpisodeRoute(world.EpisodeId), Ct);
        (await ReadOkAsync(episode)).GetProperty("currentStory").GetProperty("review").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task EpisodeDetail_Reopened_ClearsApprovalWithoutHidingCurrentStory()
    {
        var world = await SeedAsync();
        SqlProbe.Execute(Database,
            $"UPDATE episodes SET approved_story_version_id = '{world.VersionId}' WHERE id = '{world.EpisodeId}'");
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var approved = await client.GetAsync(EpisodeRoute(world.EpisodeId), Ct);
        (await ReadOkAsync(approved)).GetProperty("currentStory").GetProperty("approvedStoryVersionId").GetGuid()
            .ShouldBe(world.VersionId!.Value);
        SqlProbe.Execute(Database,
            $"UPDATE episodes SET approved_story_version_id = NULL WHERE id = '{world.EpisodeId}'");
        using var reopened = await client.GetAsync(EpisodeRoute(world.EpisodeId), Ct);
        var current = (await ReadOkAsync(reopened)).GetProperty("currentStory");
        current.GetProperty("approvedStoryVersionId").ValueKind.ShouldBe(JsonValueKind.Null);
        current.GetProperty("versionId").GetGuid().ShouldBe(world.VersionId!.Value);
        using var history = await client.GetAsync(VersionsRoute(world.EpisodeId), Ct);
        (await ReadOkAsync(history)).EnumerateArray().All(row => !row.GetProperty("approved").GetBoolean()).ShouldBeTrue();
    }

    [Fact]
    public async Task StoryReadBodies_OnlyExposeSourceUrlsInsideStoredFactSources_AndNoOtherEpisodeData()
    {
        var world = await SeedAsync();
        var other = await SeedAsync(owner: world, title: "Other episode private title");
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        foreach (var route in new[] { EpisodeRoute(world.EpisodeId), VersionsRoute(world.EpisodeId), VersionRoute(world.EpisodeId, world.VersionId!.Value) })
        {
            using var response = await client.GetAsync(route, Ct);
            var body = await ReadOkAsync(response);
            AssertSourceUrlPaths(body);
            body.GetRawText().ShouldNotContain("Other episode private title");
            body.GetRawText().ShouldNotContain(other.VersionId!.Value.ToString());
        }
        using var detail = await client.GetAsync(VersionRoute(world.EpisodeId, world.VersionId!.Value), Ct);
        (await ReadOkAsync(detail)).GetProperty("content").GetProperty("fact_sources")[0].GetProperty("url").GetString()
            .ShouldBe(SourceUrl, "sources are the stored D3 document, not re-resolved from story_sources");
    }

    [Fact]
    public async Task StoryVersionRoutes_Unauthenticated_Return401()
    {
        using var factory = Factory();
        using var client = factory.AnonymousClient();
        var episodeId = Guid.CreateVersion7();
        foreach (var route in new[] { VersionsRoute(episodeId), VersionRoute(episodeId, Guid.CreateVersion7()) })
        {
            using var response = await client.GetAsync(route, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
    }

    private ApiFactory Factory() =>
        new ApiFactory(Database, new FakeClock(Start))
            .WithServices(services =>
            {
                services.RemoveAll<ITextClient>();
                services.AddSingleton<ITextClient>(new FakeTextClient());
            });

    private async Task<World> SeedAsync(
        bool withStory = true,
        EpisodeStatus status = EpisodeStatus.StoryReview,
        World? owner = null,
        string? title = null,
        bool withFactSheet = false)
    {
        var actor = new TestCurrentUser();
        Guid userId;
        Guid seriesId;
        if (owner is null)
        {
            var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
            var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
            userId = user.Id;
            seriesId = series.Id;
        }
        else
        {
            userId = owner.UserId;
            seriesId = owner.SeriesId;
            actor.Id = userId;
        }
        var episode = await new EpisodeBuilder(Database, actor)
            .CreateAsync(userId, seriesId, lengthTier: LengthTier.Short, status: status, ct: Ct);
        // story_versions.fact_sheet_id is a same-owner FK to fact_sheets (02 CR-29), so a seeded version that
        // carries one names a real draft row of this episode.
        var sheet = withFactSheet
            ? await new FactSheetBuilder(Database, actor).CreateAsync(userId, episode.Id, Ct)
            : null;
        if (!withStory)
        {
            return new World(userId, seriesId, episode.Id, null, sheet?.Id);
        }

        var draft = Draft(title);
        var sources = new JsonArray(new JsonObject
        {
            ["source_id"] = "11111111-1111-7111-8111-111111111111",
            ["title"] = "Stored source",
            ["url"] = SourceUrl,
        });
        var version = StoryVersion.Create(userId, episode.Id, 1, StoryVersionSource.Generated, draft,
            StoryContentJson.Write(draft, sources), "model-x", "rel=1", null, null, sheet?.Id, AuditJson(), 0.021m, Start);
        await using (var db = Database.CreateAppDbContext(new TestCurrentUser { Id = userId }))
        {
            db.StoryVersions.Add(version);
            await db.SaveChangesAsync(Ct);
        }
        SqlProbe.Execute(Database, $"UPDATE episodes SET current_story_version_id = '{version.Id}' WHERE id = '{episode.Id}'");
        return new World(userId, seriesId, episode.Id, version.Id, sheet?.Id);
    }

    private async Task<GenerationRun> SeedRunAsync(World world, DateTimeOffset createdAt, bool terminal, bool queued = false)
    {
        var queuedRun = GenerationRun.Queue(world.UserId, world.SeriesId, world.EpisodeId,
            GenerationKind.Story, GenerationTrigger.User, null, EpisodeStatus.StoryReview, null, null, 0m, createdAt);
        queuedRun.IsError.ShouldBeFalse();
        var run = queuedRun.Value;
        if (!queued)
        {
            run.Start(createdAt.AddSeconds(1));
            run.AdvanceStage(GenerationStage.Reviewing, createdAt.AddSeconds(2)).IsError.ShouldBeFalse();
        }
        if (terminal)
        {
            run.Fail("story_generation_failed", "safe test outcome", true, createdAt.AddSeconds(3));
        }
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }, new FakeClock(createdAt));
        db.GenerationRuns.Add(run);
        await db.SaveChangesAsync(Ct);
        return run;
    }

    private static async Task<HttpResponseMessage> EditAsync(HttpClient client, Guid episodeId, StoryContentV1 content, Guid previousId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{EpisodeRoute(episodeId)}/story")
        {
            Content = new StringContent(JsonSerializer.Serialize(content, StoryContentJson.SerializerOptions), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", StoryEtag.For(previousId));
        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> ReadOkAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, json);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<string> NotFoundCodeAsync(HttpClient client, string route)
    {
        using var response = await client.GetAsync(route, Ct);
        var json = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, json);
        using var document = JsonDocument.Parse(json);
        var code = document.RootElement.GetProperty("code").GetString();
        code.ShouldNotBeNullOrWhiteSpace();
        json.ShouldNotContain("episode secret");
        return code!;
    }

    private static void AssertNoExceptionText(JsonElement body)
    {
        body.GetRawText().ShouldNotContain("Exception");
        body.GetRawText().ShouldNotContain("System.Text.Json");
        body.GetRawText().ShouldNotContain("stackTrace");
    }

    private static void AssertSourceUrlPaths(JsonElement value, string path = "")
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                AssertSourceUrlPaths(property.Value, $"{path}.{property.Name}");
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                AssertSourceUrlPaths(item, $"{path}[]");
            }
        }
        else if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text
            && (text.Contains("https://", StringComparison.Ordinal) || text.Contains("http://", StringComparison.Ordinal)))
        {
            path.ShouldEndWith(".content.fact_sources[].url");
            text.ShouldBe(SourceUrl);
        }
    }

    private static string EpisodeRoute(Guid episodeId) => $"/api/v1/episodes/{episodeId}";
    private static string VersionsRoute(Guid episodeId) => $"{EpisodeRoute(episodeId)}/story/versions";
    private static string VersionRoute(Guid episodeId, Guid versionId) => $"{VersionsRoute(episodeId)}/{versionId}";

    private static string AuditJson() =>
        """
        {"schema_version":"audit.v1","local":{"issues":[
          {"rule":"Q-REP-02","severity":"advisory","kind":"taste","segment":"body","evidence":"repeated phrase","repair_direction":"vary phrasing"},
          {"rule":"Q-KW-01","severity":"blocking","kind":"objective","segment":"p:1","message_key":"quality.keyword_count_per_segment","evidence":"0 keywords","repair_direction":"add 1-2 keywords","measured":0,"threshold":1}]},
         "moderation":{"flagged":false,"categories":[],"stale":false},
         "model_review":{"scores":{"hook":4,"coherence":3},"issues":[
           {"id":"M1","severity":"blocking","type":"ear_rule","segment":"p:1","evidence":"test evidence","repair_direction":"simplify"},
           {"id":"M2","severity":"advisory","type":"ear_rule","segment":"p:2","evidence":"test evidence","repair_direction":"shorten"}],"stale":true},
         "revision":{"rounds":1},"similarity":{"score":0.021}}
        """;

    /// <summary>The valid Short/vi draft used by the edit endpoint precedent, padded to 220 D4 tokens.</summary>
    private static StoryContentV1 Draft(string? title = null)
    {
        StorySegmentV1[] paragraphs =
        [
            new("Căn nhà thuê rẻ bất thường, chủ nhà chỉ dặn đừng mở cánh cửa cuối hành lang.", ["empty chair in the hallway"], [], "setup"),
            new("Tiếng gõ vang lên từ trong đó, và khi anh mở ra thì thấy chiếc ghế cũ vẫn còn ấm.", ["rain on the window"], [], "twist"),
            new("Anh kể lại chuyện ấy cho người hàng xóm, và người ấy chỉ im lặng nhìn ra sân.", ["figure at the door"], [], "reveal"),
        ];
        var hook = new StorySegmentV1("Đêm ấy mưa rất to, người kể ngồi một mình trong căn nhà cũ ven sông.",
            ["old house by the river", "stormy night over the river"], []);
        var ending = new StorySegmentV1("Anh trả nhà ngay hôm sau, không dám kể cho ai nghe chuyện mình đã thấy.", ["letter on the table"], []);
        var others = Words(hook.Text) + Words(ending.Text) + Words(paragraphs[0].Text) + Words(paragraphs[1].Text);
        var last = paragraphs[^1];
        paragraphs[^1] = last with { Text = last.Text + " " + string.Join(' ', Enumerable.Repeat("rồi", 220 - others - Words(last.Text))) };
        return new StoryContentV1(title ?? "Căn nhà cuối hành lang", hook, paragraphs, ending,
            "Một chuyện ma nhẹ nhàng về căn nhà bên sông.", ["truyện ma", "tâm linh"], ["supernatural"],
            new StoryStructureV1("truyện ma tâm linh", "ngôi nhà cũ", "một cuộc điện thoại lúc nửa đêm", "người kể là nguyên nhân", "first_person"));
    }

    private static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId, Guid? VersionId, Guid? FactSheetId = null);
}
