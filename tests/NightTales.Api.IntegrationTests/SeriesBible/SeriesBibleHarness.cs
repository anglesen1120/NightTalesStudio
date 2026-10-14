using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Features.Stories;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;

// Aliased: the enclosing namespace of this file is `…Api.IntegrationTests.SeriesBible`, so the simple name
// `SeriesBible` resolves to that namespace and never to the Domain aggregate (the trap `SeriesEndpointsTests`
// documents for `NightTales.Domain.Series`).
using SeriesBibleEntity = NightTales.Domain.Series.Bible.SeriesBible;

namespace NightTales.Api.IntegrationTests.SeriesBible;

/// <summary>
/// The shared world of the series-bible GET/PUT tests (02.6.3-T1): one owner, one series and the single
/// <c>series_bibles</c> row it owns, plus the request helpers the three test classes answer through. The seam is
/// the real HTTP surface — the tests never build an Application DTO, they send the <c>bible.v1</c> document as
/// JSON — so what they assert is the wire contract of <c>/api/v1/series/{id}/bible</c> (plan §5).
/// </summary>
internal static class SeriesBibleHarness
{
    /// <summary>The clock of every seeded row (<c>FakeClock</c>), so stamps are deterministic.</summary>
    internal static readonly DateTimeOffset Start = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The token xUnit cancels the running test with.</summary>
    internal static CancellationToken Ct => TestContextToken.Value;

    /// <summary>A new <c>ApiFactory</c> over the class database with the fixed clock.</summary>
    internal static ApiFactory Factory(TestDatabase database) => new(database, new FakeClock(Start));

    /// <summary>The strong ETag of a bible row (02 README D9: <c>"sb-&lt;series_bibles.version&gt;"</c>).</summary>
    internal static string Etag(int version) => $"\"sb-{version}\"";

    /// <summary>The route of the bible of one series.</summary>
    internal static string Url(Guid seriesId) =>
        $"/api/v1/series/{seriesId.ToString("D", CultureInfo.InvariantCulture)}/bible";

    /// <summary>
    /// Seeds one owner, one series and its <c>series_bibles</c> row at version 1 — the world a GET/PUT
    /// addresses. The row is created through <see cref="SeriesBibleEntity.CreateDefault"/> and the same
    /// <c>AppDbContext</c> production code uses, so its <c>jsonb</c> document is the one the API reads back.
    /// </summary>
    /// <remarks>
    /// The series is written by <see cref="SeriesBuilder"/> rather than by <c>POST /api/v1/series</c>, because
    /// the create-series hook that writes the row (09.4.1-T1) is a cross-feature change this task may not make;
    /// <c>CreateSeries_CreatesEmptyBible</c> is the one test that drives the hook.
    /// </remarks>
    internal static async Task<World> SeedAsync(TestDatabase database)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(database, actor, new FakeClock(Start)).CreateAsync(user.Id, ct: Ct);

        await EnsureBibleAsync(database, user.Id, series.Id);

        return new World(user.Id, series.Id);
    }

    /// <summary>Seeds a series with no bible row, the data-drift state the read answers 500 for.</summary>
    internal static async Task<World> SeedSeriesWithoutBibleAsync(TestDatabase database)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(database, actor, new FakeClock(Start)).CreateAsync(user.Id, ct: Ct);

        // The row may exist when the create-series hook is wired into the builder's path; the drift this test
        // models is a series whose row is gone, so it is deleted rather than assumed absent.
        SqlProbe.Execute(
            database,
            $"DELETE FROM series_bibles WHERE series_id = '{series.Id.ToString("D", CultureInfo.InvariantCulture)}'");

        return new World(user.Id, series.Id);
    }

    /// <summary>
    /// Inserts the bible row of <paramref name="seriesId"/> at version 1 when it is missing, the way the
    /// migration's backfill and the create-series hook write it. Idempotent: a row the hook already wrote is
    /// left untouched.
    /// </summary>
    internal static async Task EnsureBibleAsync(TestDatabase database, Guid userId, Guid seriesId)
    {
        await using var context = database.CreateAppDbContext(new TestCurrentUser { Id = userId }, new FakeClock(Start));
        if (await context.SeriesBibles.AnyAsync(bible => bible.SeriesId == seriesId, Ct))
        {
            return;
        }

        context.SeriesBibles.Add(SeriesBibleEntity.CreateDefault(seriesId, userId, new FakeClock(Start)));
        await context.SaveChangesAsync(Ct);
    }

    /// <summary>Seeds one episode of the series that is NOT approved (no <c>approved_story_version_id</c>).</summary>
    internal static async Task<Guid> SeedDraftEpisodeAsync(TestDatabase database, World world)
    {
        var actor = new TestCurrentUser { Id = world.UserId };
        var episode = await new EpisodeBuilder(database, actor)
            .CreateAsync(world.UserId, world.SeriesId, ct: Ct);

        return episode.Id;
    }

    /// <summary>
    /// Seeds one episode of the series that carries an approved story version, so the bible may summarise it and
    /// the server can derive the summary's <c>structure</c> and <c>approved_at</c> (plan §3.1).
    /// </summary>
    /// <returns>The episode id; its summary is derived from the version's own structure.</returns>
    internal static async Task<Guid> SeedApprovedEpisodeAsync(TestDatabase database, World world)
    {
        var actor = new TestCurrentUser { Id = world.UserId };
        var episode = await new EpisodeBuilder(database, actor)
            .CreateAsync(world.UserId, world.SeriesId, status: EpisodeStatus.StoryReview, ct: Ct);

        var draft = Draft();
        var sources = new JsonArray(new JsonObject
        {
            ["source_id"] = "11111111-1111-7111-8111-111111111111",
            ["title"] = "Stored source",
            ["url"] = "https://example.com/stored-source",
        });
        var version = StoryVersion.Create(
            world.UserId,
            episode.Id,
            1,
            StoryVersionSource.Generated,
            draft,
            StoryContentJson.Write(draft, sources),
            "model-x",
            "rel=1",
            null,
            null,
            null,
            AuditJson(),
            0.021m,
            Start);

        await using (var context = database.CreateAppDbContext(actor, new FakeClock(Start)))
        {
            context.StoryVersions.Add(version);
            await context.SaveChangesAsync(Ct);
        }

        // The approval transaction writes both columns (02.3.2-T1); the fixture writes the same state, because
        // no route approves a story without a generated run behind it.
        SqlProbe.Execute(
            database,
            $"UPDATE episodes SET current_story_version_id = '{version.Id}', approved_story_version_id = '{version.Id}', approved_at = '{Start:O}' WHERE id = '{episode.Id}'");

        return episode.Id;
    }

    /// <summary>The stored row of the bible: its <c>version</c> column and its <c>content</c> document.</summary>
    internal static BibleRow Row(TestDatabase database, Guid seriesId)
    {
        var key = seriesId.ToString("D", CultureInfo.InvariantCulture);
        var version = int.Parse(
            SqlProbe.ScalarText(database, $"SELECT version FROM series_bibles WHERE series_id = '{key}'"),
            CultureInfo.InvariantCulture);
        var content = JsonNode.Parse(
            SqlProbe.ScalarText(database, $"SELECT content::text FROM series_bibles WHERE series_id = '{key}'"));

        content.ShouldNotBeNull("the seeded bible row must carry a JSON document");

        return new BibleRow(version, content!);
    }

    /// <summary>How many <c>series_bibles</c> rows the series has; the unique index promises one.</summary>
    internal static long RowCount(TestDatabase database, Guid seriesId) =>
        SqlProbe.Count(
            database,
            $"SELECT count(*) FROM series_bibles WHERE series_id = '{seriesId.ToString("D", CultureInfo.InvariantCulture)}'");

    /// <summary>The <c>series.bible_updated</c> audit rows of one actor (D13).</summary>
    internal static string AuditRow(TestDatabase database, Guid userId) =>
        SqlProbe.ScalarText(
            database,
            $"""
             SELECT action || '|' || category || '|' || visibility || '|' || actor_type || '|' || actor_user_id
                 || '|' || subject_user_id || '|' || coalesce(entity_type, '-') || '|' || coalesce(data::text, '-')
             FROM audit_logs
             WHERE action = 'series.bible_updated' AND actor_user_id = '{userId.ToString("D", CultureInfo.InvariantCulture)}'
             """);

    /// <summary>How many <c>series.bible_updated</c> audit rows the actor has.</summary>
    internal static long AuditCount(TestDatabase database, Guid userId) =>
        SqlProbe.Count(
            database,
            $"""
             SELECT count(*) FROM audit_logs
             WHERE action = 'series.bible_updated'
                 AND actor_user_id = '{userId.ToString("D", CultureInfo.InvariantCulture)}'
             """);

    /// <summary>One member of the <c>data</c> document of the actor's <c>series.bible_updated</c> audit row.</summary>
    internal static string AuditDataField(TestDatabase database, Guid userId, string key) =>
        SqlProbe.ScalarText(
            database,
            $"""
             SELECT coalesce(data ->> '{key}', '') FROM audit_logs
             WHERE action = 'series.bible_updated'
                 AND actor_user_id = '{userId.ToString("D", CultureInfo.InvariantCulture)}'
             """);

    /// <summary><c>GET /api/v1/series/{id}/bible</c>, optionally with the SPA's language.</summary>
    internal static Task<HttpResponseMessage> GetAsync(HttpClient client, Guid seriesId, string? acceptLanguage = null) =>
        SendAsync(client, HttpMethod.Get, Url(seriesId), null, null, acceptLanguage);

    /// <summary><c>PUT /api/v1/series/{id}/bible</c> with the given <c>If-Match</c> and <c>bible.v1</c> content.</summary>
    internal static Task<HttpResponseMessage> PutAsync(
        HttpClient client,
        Guid seriesId,
        string? ifMatch,
        JsonObject content,
        string? acceptLanguage = null) =>
        SendAsync(
            client,
            HttpMethod.Put,
            Url(seriesId),
            ifMatch,
            new JsonObject { ["content"] = content.DeepClone() }.ToJsonString(),
            acceptLanguage);

    /// <summary>
    /// A <c>bible.v1</c> request document (plan §3.1): a narrator with the non-empty persona the limits require
    /// and the four arrays, empty unless a test fills them.
    /// </summary>
    internal static JsonObject Content(
        string? narratorName = "Trần Văn Kể",
        string persona = "kể chuyện đêm",
        JsonArray? characters = null,
        JsonArray? places = null,
        JsonArray? rules = null,
        JsonArray? summaries = null) =>
        new()
        {
            ["narrator"] = new JsonObject
            {
                ["name"] = narratorName,
                ["persona"] = persona,
                ["voice_notes"] = "giọng chậm, ấm",
            },
            ["recurring_characters"] = characters ?? new JsonArray(),
            ["places"] = places ?? new JsonArray(),
            ["rules"] = rules ?? new JsonArray(),
            ["episode_summaries"] = summaries ?? new JsonArray(),
        };

    /// <summary>One recurring character, with the server's own id shape (<c>ch1</c>, plan §3.1).</summary>
    internal static JsonObject Character(int index, string? name = null, string? description = null) =>
        new()
        {
            ["id"] = $"ch{index}",
            ["name"] = name ?? $"Nhân vật {index}",
            ["description"] = description ?? $"Mô tả {index}.",
            ["first_episode_id"] = null,
        };

    /// <summary>One recurring place.</summary>
    internal static JsonObject Place(int index) =>
        new()
        {
            ["id"] = $"pl{index}",
            ["name"] = $"Địa điểm {index}",
            ["description"] = $"Mô tả {index}.",
        };

    /// <summary>One standing rule.</summary>
    internal static JsonObject Rule(int index) =>
        new()
        {
            ["id"] = $"rl{index}",
            ["text"] = $"Luật {index}.",
        };

    /// <summary>
    /// One episode summary as a client sends it: the derived <c>structure</c> and <c>approved_at</c> are the
    /// server's, so <paramref name="derived"/> adds the two members only when a test wants to prove they are
    /// ignored.
    /// </summary>
    internal static JsonObject Summary(Guid episodeId, string title, string summary, bool derived = false)
    {
        var node = new JsonObject
        {
            ["episode_id"] = episodeId.ToString("D", CultureInfo.InvariantCulture),
            ["title"] = title,
            ["summary"] = summary,
        };

        if (derived)
        {
            node["structure"] = new JsonObject { ["sub_genre"] = "client invented" };
            node["approved_at"] = "2000-01-01T00:00:00+00:00";
        }

        return node;
    }

    /// <summary>The response body as a detached <see cref="JsonElement"/>.</summary>
    internal static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(Ct));

    /// <summary>The problem body of an error response.</summary>
    internal static async Task<ProblemResponse> ProblemAsync(HttpResponseMessage response)
    {
        // Read as text, never as a stream: a stream read of the content is single-use, and a test reads the
        // problem twice (the status and code first, then the field errors it names).
        var problem = JsonSerializer.Deserialize<ProblemResponse>(
            await response.Content.ReadAsStringAsync(Ct),
            ProblemResponse.Options);
        problem.ShouldNotBeNull();

        return problem;
    }

    /// <summary>Asserts the status and the <c>code</c> of a problem response.</summary>
    internal static async Task AssertProblemAsync(HttpResponseMessage response, int status, string code)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe((HttpStatusCode)status, text);
        (await ProblemAsync(response)).Code.ShouldBe(code);
    }

    /// <summary>The field-error keys of a problem response, in wire order.</summary>
    internal static IReadOnlyList<string> FieldKeys(ProblemResponse problem) =>
        problem.Errors is null ? [] : [.. problem.Errors.Keys];

    /// <summary>The <c>sb-</c> ETag the response header carries (quotes included).</summary>
    internal static string ResponseEtag(HttpResponseMessage response)
    {
        response.Headers.ETag.ShouldNotBeNull();

        return response.Headers.ETag!.Tag;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        string? ifMatch,
        string? body,
        string? acceptLanguage)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (acceptLanguage is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
        }

        return await client.SendAsync(request, Ct);
    }

    /// <summary>The valid Short/vi draft of the story routes, whose structure the summary derives from.</summary>
    private static StoryContentV1 Draft()
    {
        StorySegmentV1[] paragraphs =
        [
            new("Căn nhà thuê rẻ bất thường, chủ nhà chỉ dặn đừng mở cánh cửa cuối hành lang.", ["empty chair in the hallway"], [], "setup"),
            new("Tiếng gõ vang lên từ trong đó, và khi anh mở ra thì thấy chiếc ghế cũ vẫn còn ấm.", ["rain on the window"], [], "twist"),
            new("Anh kể lại chuyện ấy cho người hàng xóm, và người ấy chỉ im lặng nhìn ra sân.", ["figure at the door"], [], "reveal"),
        ];
        var hook = new StorySegmentV1(
            "Đêm ấy mưa rất to, người kể ngồi một mình trong căn nhà cũ ven sông.",
            ["old house by the river", "stormy night over the river"],
            []);
        var ending = new StorySegmentV1(
            "Anh trả nhà ngay hôm sau, không dám kể cho ai nghe chuyện mình đã thấy.",
            ["letter on the table"],
            []);

        return new StoryContentV1(
            "Căn nhà cuối hành lang",
            hook,
            paragraphs,
            ending,
            "Một chuyện ma nhẹ nhàng về căn nhà bên sông.",
            ["truyện ma", "tâm linh"],
            ["supernatural"],
            new StoryStructureV1("truyện ma tâm linh", "ngôi nhà cũ", "một cuộc điện thoại lúc nửa đêm", "người kể là nguyên nhân", "first_person"));
    }

    /// <summary>The audit document a stored version carries (the story routes' own fixture).</summary>
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
}

/// <summary>One owner and the series whose bible the routes address.</summary>
/// <param name="UserId">The owner (the tenant of every request).</param>
/// <param name="SeriesId">The series, which owns exactly one <c>series_bibles</c> row.</param>
internal sealed record World(Guid UserId, Guid SeriesId);

/// <summary>The stored bible row: its <c>version</c> column and its <c>content</c> document.</summary>
/// <param name="Version">The row's version — the value the <c>sb-</c> ETag names (D9).</param>
/// <param name="Content">The stored <c>bible.v1</c> document.</param>
internal sealed record BibleRow(int Version, JsonNode Content);
