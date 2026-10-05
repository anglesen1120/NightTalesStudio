using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Features.Stories;
using NightTales.Application.Features.Stories.Queries.GetStoryVersions;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
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

/// <summary>Independent negative and boundary coverage for 02.3.4-T2 against PostgreSQL and the real API.</summary>
[Trait("Category", "Integration")]
public sealed class StoryReadRoutesQaTests(TestDatabase database) : IntegrationTest(database)
{
    private const string SourceUrl = "https://example.test/qa-stored-source";
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task StoryRoutes_UnknownAndForeignProblems_ByteIdenticalExceptTraceId()
    {
        var own = await SeedAsync();
        var foreign = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(own.UserId);
        var missingId = Guid.CreateVersion7();
        var listProblem = await ProblemWithoutTraceAsync(client, VersionsRoute(missingId));
        (await ProblemWithoutTraceAsync(client, VersionsRoute(foreign.EpisodeId))).ShouldBe(listProblem);
        var detailProblem = await ProblemWithoutTraceAsync(client, VersionRoute(missingId, own.VersionId!.Value));
        (await ProblemWithoutTraceAsync(client, VersionRoute(foreign.EpisodeId, foreign.VersionId!.Value)))
            .ShouldBe(detailProblem);
        // A valid owner episode must not make a foreign version discoverable.
        (await ProblemWithoutTraceAsync(client, VersionRoute(own.EpisodeId, foreign.VersionId.Value)))
            .ShouldBe(detailProblem);
        (await ProblemWithoutTraceAsync(client, VersionRoute(own.EpisodeId, Guid.CreateVersion7())))
            .ShouldBe(detailProblem);
        detailProblem.ShouldBe(listProblem);
    }

    [Fact]
    public async Task StoryRoutes_CancelledEpisode_ReturnIdentical404ToUnknown()
    {
        var world = await SeedAsync();
        SqlProbe.Execute(Database, $"UPDATE episodes SET status = 'cancelled' WHERE id = '{world.EpisodeId}'");
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        foreach (var detail in new[] { false, true })
        {
            var missingId = Guid.CreateVersion7();
            var missing = detail ? VersionRoute(missingId, world.VersionId!.Value) : VersionsRoute(missingId);
            var cancelled = detail ? VersionRoute(world.EpisodeId, world.VersionId!.Value) : VersionsRoute(world.EpisodeId);
            (await ProblemWithoutTraceAsync(client, cancelled)).ShouldBe(await ProblemWithoutTraceAsync(client, missing));
        }
    }

    [Fact]
    public async Task StoryHistory_DraftWithZeroVersions_Returns200EmptyArray()
    {
        var world = await SeedAsync(withStory: false);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await client.GetAsync(VersionsRoute(world.EpisodeId), Ct);
        var rows = await ReadOkAsync(response);
        rows.ValueKind.ShouldBe(JsonValueKind.Array);
        rows.GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task StoryHistory_SparseVersionNumbers_OrdersByVersionNotCreationTime_AndPins200Cap()
    {
        GetStoryVersionsHandler.MaximumHistoryVersions.ShouldBe(200);
        var world = await SeedAsync();
        await using (var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }))
        {
            foreach (var number in new[] { 200, 3, 71 })
            {
                var content = Draft($"Version {number}");
                db.StoryVersions.Add(StoryVersion.Create(world.UserId, world.EpisodeId, number,
                    StoryVersionSource.Edited, content, StoryContentJson.Write(content, new JsonArray()),
                    "qa-model", "qa-prompt", null, null, null, null, null, Start.AddMinutes(-number)));
            }
            await db.SaveChangesAsync(Ct);
        }
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await client.GetAsync(VersionsRoute(world.EpisodeId), Ct);
        var rows = (await ReadOkAsync(response)).EnumerateArray().ToArray();
        rows.Select(row => row.GetProperty("version").GetInt32()).ShouldBe([200, 71, 3, 1]);
        rows.Count(row => row.GetProperty("isCurrent").GetBoolean()).ShouldBe(1);
        rows[^1].GetProperty("isCurrent").GetBoolean().ShouldBeTrue();
        rows.All(row => !row.GetProperty("approved").GetBoolean()).ShouldBeTrue();
        // Existing unit GetStoryVersions_BoundsNewestHistory exercises 201 rows; this pins its public cap independently.
    }

    [Theory]
    [InlineData("\"legacy string\"")]
    [InlineData("[{},42,null]")]
    public async Task StoryContent_StringOrArrayRoot_ReturnsNullWithoutLosingSummary(string content)
    {
        var world = await SeedAsync();
        UpdateJson(world, "content", content);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await client.GetAsync(VersionRoute(world.EpisodeId, world.VersionId!.Value), Ct);
        var row = await ReadOkAsync(response);
        row.GetProperty("content").ValueKind.ShouldBe(JsonValueKind.Null);
        row.GetProperty("versionId").GetGuid().ShouldBe(world.VersionId.Value);
        row.GetProperty("body").GetString().ShouldBe(StoryVersion.Derive(Draft()).Body);
        row.GetProperty("auditReport").GetProperty("schema_version").GetString().ShouldBe("audit.v1");
        using var episode = await client.GetAsync(EpisodeRoute(world.EpisodeId), Ct);
        (await ReadOkAsync(episode)).GetProperty("currentStory").GetProperty("content").ValueKind.ShouldBe(JsonValueKind.Null);
        using var history = await client.GetAsync(VersionsRoute(world.EpisodeId), Ct);
        (await ReadOkAsync(history)).GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task StoryAudit_UnknownMissingAndNonStringIssueFields_Returns200AndSafeHints()
    {
        const string audit = """
            {"schema_version":"audit.v1","local":{"issues":[
              {"rule":"unknown-no-key"},{},{"rule":42},null,7,
              {"rule":"Q-KW-01","severity":5,"segment":false,"measured":"NaN","threshold":{}},
              {"rule":"legacy-with-key","message_key":"quality.legacy","severity":"advisory","kind":"taste"}]}}
            """;
        var world = await SeedAsync();
        UpdateJson(world, "audit_report", audit);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await client.GetAsync(VersionRoute(world.EpisodeId, world.VersionId!.Value), Ct);
        var row = await ReadOkAsync(response);
        JsonNode.DeepEquals(JsonNode.Parse(audit), JsonNode.Parse(row.GetProperty("auditReport").GetRawText())).ShouldBeTrue();
        var hints = row.GetProperty("qualityHints").EnumerateArray().ToArray();
        hints.Length.ShouldBe(2);
        hints[0].GetProperty("ruleId").GetString().ShouldBe("Q-KW-01");
        hints[0].GetProperty("messageKey").GetString().ShouldBe("quality.keyword_count_per_segment");
        hints[0].GetProperty("severity").GetString().ShouldBe("advisory");
        hints[0].GetProperty("kind").GetString().ShouldBe("objective");
        hints[0].GetProperty("segment").GetString().ShouldBe("");
        hints[0].GetProperty("measured").ValueKind.ShouldBe(JsonValueKind.Null);
        hints[0].GetProperty("threshold").ValueKind.ShouldBe(JsonValueKind.Null);
        hints[1].GetProperty("messageKey").GetString().ShouldBe("quality.legacy");
        using var episode = await client.GetAsync(EpisodeRoute(world.EpisodeId), Ct);
        var current = (await ReadOkAsync(episode)).GetProperty("currentStory");
        current.GetProperty("qualityHints").GetArrayLength().ShouldBe(2);
        current.GetProperty("review").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("{\"model_review\":{\"scores\":{\"hook\":4},\"stale\":false}}", false)]
    [InlineData("{\"model_review\":{\"issues\":{},\"stale\":null}}", true)]
    public async Task StoryReview_NoIssuesArray_ReturnsZeroCounts(string audit, bool stale)
    {
        var world = await SeedAsync();
        UpdateJson(world, "audit_report", audit);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await client.GetAsync(EpisodeRoute(world.EpisodeId), Ct);
        var current = (await ReadOkAsync(response)).GetProperty("currentStory");
        var review = current.GetProperty("review");
        review.GetProperty("issueCounts").GetProperty("blocking").GetInt32().ShouldBe(0);
        review.GetProperty("issueCounts").GetProperty("advisory").GetInt32().ShouldBe(0);
        review.GetProperty("stale").GetBoolean().ShouldBe(stale);
        current.GetProperty("qualityHints").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task StoryStoredText_C0Characters_RoundTripAsEscapedJson()
    {
        const string text = "stored\u0001\b\t\n\f\r\u001ftext";
        var draft = Draft() with { Hook = Draft().Hook with { Text = text } };
        var world = await SeedAsync(draft: draft);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        foreach (var route in new[] { VersionRoute(world.EpisodeId, world.VersionId!.Value), EpisodeRoute(world.EpisodeId) })
        {
            using var response = await client.GetAsync(route, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var raw = await response.Content.ReadAsStringAsync(Ct);
            raw.ShouldNotContain('\u0001'.ToString());
            raw.ShouldNotContain('\u001f'.ToString());
            using var document = JsonDocument.Parse(raw);
            var story = route == EpisodeRoute(world.EpisodeId) ? document.RootElement.GetProperty("currentStory") : document.RootElement;
            story.GetProperty("content").GetProperty("hook").GetProperty("text").GetString().ShouldBe(text);
            story.GetProperty("body").GetString().ShouldBe(StoryVersion.Derive(draft).Body);
        }
    }

    [Fact]
    public async Task StoryStorage_NulEscape_IsRejectedByPostgreSqlBeforeReadRoute()
    {
        var world = await SeedAsync();
        // PostgreSQL jsonb cannot hold U+0000, even when the JSON input uses an escape.
        SqlProbe.SqlState(Database,
            $"UPDATE story_versions SET content = '{{\"text\":\"\\u0000\"}}'::jsonb WHERE id = '{world.VersionId}'")
            .ShouldBe("22P05");
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await client.GetAsync(VersionRoute(world.EpisodeId, world.VersionId!.Value), Ct);
        (await ReadOkAsync(response)).GetProperty("content").GetProperty("title").GetString().ShouldBe(Draft().Title);
    }

    [Fact]
    public async Task StoryVersion_IfNoneMatch_IsCurrentlyIgnoredUnlikeEpisodeGet()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var request = new HttpRequestMessage(HttpMethod.Get, VersionRoute(world.EpisodeId, world.VersionId!.Value));
        request.Headers.TryAddWithoutValidation("If-None-Match", StoryEtag.For(world.VersionId.Value));
        using var response = await client.SendAsync(request, Ct);
        (await ReadOkAsync(response)).GetProperty("versionId").GetGuid().ShouldBe(world.VersionId.Value);
        response.Headers.ETag!.Tag.ShouldBe(StoryEtag.For(world.VersionId.Value));
        using var episode = await client.GetAsync(EpisodeRoute(world.EpisodeId), Ct);
        episode.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var conditional = new HttpRequestMessage(HttpMethod.Get, EpisodeRoute(world.EpisodeId));
        conditional.Headers.TryAddWithoutValidation("If-None-Match", episode.Headers.ETag!.Tag);
        using var unchanged = await client.SendAsync(conditional, Ct);
        unchanged.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        // Observation, not an added 304 requirement for the story routes.
    }

    [Fact]
    public async Task StoryRead_CacheHeaders_CurrentlyDifferFromEpisodeGet()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var episode = await client.GetAsync(EpisodeRoute(world.EpisodeId), Ct);
        episode.StatusCode.ShouldBe(HttpStatusCode.OK);
        episode.Headers.CacheControl!.ToString().ShouldBe("no-cache");
        episode.Headers.Vary.ShouldContain("Authorization");
        foreach (var route in new[] { VersionsRoute(world.EpisodeId), VersionRoute(world.EpisodeId, world.VersionId!.Value) })
        {
            using var response = await client.GetAsync(route, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Headers.CacheControl.ShouldBeNull();
            response.Headers.Vary.ShouldNotContain("Authorization");
        }
    }

    [Theory]
    [InlineData("HEAD", false)]
    [InlineData("HEAD", true)]
    [InlineData("POST", false)]
    [InlineData("POST", true)]
    public async Task StoryRead_UnsupportedMethods_Return405(string method, bool detail)
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var route = detail ? VersionRoute(world.EpisodeId, world.VersionId!.Value) : VersionsRoute(world.EpisodeId);
        using var request = new HttpRequestMessage(new HttpMethod(method), route);
        using var response = await client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        response.Content.Headers.Allow.ShouldContain("GET");
    }

    [Fact]
    public async Task StoryRead_SourceSnapshotSurvivesMissingSourceRow_OnlyFactSourcesExposeUrl()
    {
        var world = await SeedAsync();
        SqlProbe.ScalarText(Database, $"SELECT count(*) FROM story_sources WHERE episode_id = '{world.EpisodeId}'").ShouldBe("0");
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        foreach (var route in new[] { EpisodeRoute(world.EpisodeId), VersionsRoute(world.EpisodeId), VersionRoute(world.EpisodeId, world.VersionId!.Value) })
        {
            using var response = await client.GetAsync(route, Ct);
            var body = await ReadOkAsync(response);
            AssertSourcePaths(body);
            if (body.ValueKind != JsonValueKind.Array)
            {
                var story = body.TryGetProperty("currentStory", out var current) ? current : body;
                story.GetProperty("content").GetProperty("fact_sources")[0].GetProperty("url").GetString().ShouldBe(SourceUrl);
            }
        }
    }

    private ApiFactory Factory() => new ApiFactory(Database, new FakeClock(Start)).WithServices(services =>
    {
        services.RemoveAll<ITextClient>();
        services.AddSingleton<ITextClient>(new FakeTextClient());
    });

    private async Task<World> SeedAsync(bool withStory = true, StoryContentV1? draft = null)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id,
            lengthTier: LengthTier.Short, status: withStory ? EpisodeStatus.StoryReview : EpisodeStatus.Draft, ct: Ct);
        if (!withStory)
        {
            return new World(user.Id, episode.Id, null);
        }
        draft ??= Draft();
        var sources = new JsonArray(new JsonObject
        {
            ["source_id"] = "11111111-1111-7111-8111-111111111111",
            ["title"] = "QA source snapshot",
            ["url"] = SourceUrl,
        });
        var version = StoryVersion.Create(user.Id, episode.Id, 1, StoryVersionSource.Generated, draft,
            StoryContentJson.Write(draft, sources), "qa-model", "qa-prompt", null, null, null,
            "{\"schema_version\":\"audit.v1\"}", null, Start);
        await using (var db = Database.CreateAppDbContext(new TestCurrentUser { Id = user.Id }))
        {
            db.StoryVersions.Add(version);
            await db.SaveChangesAsync(Ct);
        }
        SqlProbe.Execute(Database, $"UPDATE episodes SET current_story_version_id = '{version.Id}' WHERE id = '{episode.Id}'");
        return new World(user.Id, episode.Id, version.Id);
    }

    private void UpdateJson(World world, string column, string json) => SqlProbe.Execute(Database,
        $"UPDATE story_versions SET {column} = '{json.Replace("'", "''", StringComparison.Ordinal)}'::jsonb WHERE id = '{world.VersionId}'");

    private static async Task<JsonElement> ReadOkAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static async Task<string> ProblemWithoutTraceAsync(HttpClient client, string route)
    {
        using var response = await client.GetAsync(route, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, text);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = JsonNode.Parse(text)!.AsObject();
        body.Remove("traceId").ShouldBeTrue();
        // Reserialise without sorting: both values and property order must match.
        return body.ToJsonString();
    }

    private static void AssertSourcePaths(JsonElement value, string path = "")
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                AssertSourcePaths(property.Value, $"{path}.{property.Name}");
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                AssertSourcePaths(item, $"{path}[]");
            }
        }
        else if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text
            && (text.Contains("https://", StringComparison.Ordinal) || text.Contains("http://", StringComparison.Ordinal)))
        {
            path.ShouldEndWith(".content.fact_sources[].url");
            text.ShouldBe(SourceUrl);
        }
    }

    private static StoryContentV1 Draft(string title = "QA stored story") => new(title,
        new StorySegmentV1("A quiet opening.", ["old house"], []),
        [new StorySegmentV1("A quiet paragraph.", ["empty chair"], [], "setup")],
        new StorySegmentV1("A quiet ending.", ["closed door"], []),
        "Stored description.", ["ghost story"], ["supernatural"],
        new StoryStructureV1("ghost story", "old house", "quiet call", "hidden narrator", "first_person"));

    private static string EpisodeRoute(Guid id) => $"/api/v1/episodes/{id}";
    private static string VersionsRoute(Guid id) => $"{EpisodeRoute(id)}/story/versions";
    private static string VersionRoute(Guid id, Guid versionId) => $"{VersionsRoute(id)}/{versionId}";
    private sealed record World(Guid UserId, Guid EpisodeId, Guid? VersionId);
}
