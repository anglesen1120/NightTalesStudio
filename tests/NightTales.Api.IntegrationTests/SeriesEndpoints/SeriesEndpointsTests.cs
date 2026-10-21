using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NightTales.Api.Endpoints;
using NightTales.Api.Http.Filters;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Domain.Catalog;
using NightTales.Domain.Common;
using NightTales.Domain.Series;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

// The aggregate is aliased: the test project also holds tenancy tests that name it, and this file's
// folder is `SeriesEndpoints` — not `Series` — precisely so that the assembly has no namespace that
// shadows NightTales.Domain.Series (09.4.1-T1).
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Api.IntegrationTests.SeriesEndpoints;

/// <summary>
/// The five series routes against the real host and a real PostgreSQL 17 (09.4.1 plan §9): create,
/// list, read, patch and archive, with the D15 ETag rules, the D14 cursor, the catalog checks of U1
/// and the D10 tenant isolation.
/// </summary>
/// <remarks>
/// Every test seeds its own user, so the class database being shared cannot leak rows between tests:
/// the tenant filter of the routes restricts each read to its caller. Catalog rows (voices, style
/// templates) are shared through the builders, except the rows a test needs to be <em>invalid</em> —
/// those get a unique name so they can never be flipped for another test.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SeriesEndpointsTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>A fixed instant, so the keyset ordering of the list tests is deterministic.</summary>
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The style-template text every seeded row uses: one <c>{term}</c>, 60–600 characters, no line
    /// break, no double quote and none of the words 03.1.1-T2 reserves for the negative clause.
    /// </summary>
    private const string TemplateText =
        "Một khung cảnh {term} được kể bằng nét vẽ đơn sắc trên nền giấy cũ, ánh sáng dịu và bố cục rộng.";

    /// <summary>
    /// The group's frozen path (09.4.1 plan §5). It is spelled out rather than read from
    /// <c>SeriesEndpoints.GroupPrefix</c>: this folder's namespace is
    /// <c>NightTales.Api.IntegrationTests.SeriesEndpoints</c> (never <c>…IntegrationTests.Series</c>,
    /// which would shadow the <c>NightTales.Domain.Series</c> type for the whole assembly), so the
    /// endpoint type's simple name is not usable here, and the path is a contract this test asserts.
    /// </summary>
    private const string RoutePrefix = "/api/v1/series";

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task CreateSeries_Valid_Returns201WithLocationAndETag()
    {
        var userId = await CreateUserAsync();
        var voice = await VoiceAsync();
        var template = await TemplateAsync();
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await client.PostAsJsonAsync(RoutePrefix, CreateBody(voice.Id, template.Id), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));

        var created = await response.Content.ReadFromJsonAsync<CreatedBody>(Ct);
        created.ShouldNotBeNull();
        response.Headers.Location.ShouldNotBeNull();
        response.Headers.Location!.ToString().ShouldBe($"{RoutePrefix}/{created.Id}");
        response.Headers.ETag.ShouldNotBeNull();
        response.Headers.ETag!.Tag.ShouldStartWith("\"se-");

        // The row is readable at the Location it was just given, with the same ETag in the body.
        using var read = await client.GetAsync(response.Headers.Location, Ct);
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        var series = await read.Content.ReadFromJsonAsync<SeriesBody>(Ct);
        series.ShouldNotBeNull();
        series.Id.ShouldBe(created.Id);
        series.Title.ShouldBe("Chuỗi thử nghiệm");
        series.ArchivedAt.ShouldBeNull();
        series.Etag.ShouldBe(response.Headers.ETag!.ToString());
    }

    [Fact]
    public async Task CreateSeries_TitleBoundaries_1And120Ok_0And121Rejected()
    {
        var userId = await CreateUserAsync();
        var voice = await VoiceAsync();
        var template = await TemplateAsync();
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var shortest = await client.PostAsJsonAsync(
            RoutePrefix,
            CreateBody(voice.Id, template.Id, title: new string('a', 1)),
            Ct);
        using var longest = await client.PostAsJsonAsync(
            RoutePrefix,
            CreateBody(voice.Id, template.Id, title: new string('a', 120)),
            Ct);
        using var empty = await client.PostAsJsonAsync(
            RoutePrefix,
            CreateBody(voice.Id, template.Id, title: string.Empty),
            Ct);
        using var tooLong = await client.PostAsJsonAsync(
            RoutePrefix,
            CreateBody(voice.Id, template.Id, title: new string('a', 121)),
            Ct);

        shortest.StatusCode.ShouldBe(HttpStatusCode.Created);
        longest.StatusCode.ShouldBe(HttpStatusCode.Created);
        empty.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // The bound is on the trimmed value, which is what the row stores.
        using var padded = await client.PostAsJsonAsync(
            RoutePrefix,
            CreateBody(voice.Id, template.Id, title: $"  {new string('a', 120)}  "),
            Ct);
        padded.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateSeries_VoiceLanguageMismatch_Returns400()
    {
        var userId = await CreateUserAsync();
        var englishVoice = await VoiceAsync("nts-test-voice-en-mismatch", Language.En);
        var template = await TemplateAsync();
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await client.PostAsJsonAsync(
            RoutePrefix,
            CreateBody(englishVoice.Id, template.Id, language: "vi"),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await ReadProblemAsync(response);
        problem.Code.ShouldBe("voice_language_mismatch");
        problem.Errors.ShouldBeNull("the code is the client-visible reason, so it is not spread over fields");
    }

    [Fact]
    public async Task CreateSeries_InactiveStyleTemplate_Returns400()
    {
        var userId = await CreateUserAsync();
        var voice = await VoiceAsync();
        var inactive = await CreateInactiveTemplateAsync();
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await client.PostAsJsonAsync(
            RoutePrefix,
            CreateBody(voice.Id, inactive),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await ReadProblemAsync(response);
        problem.Code.ShouldBe("validation_failed");
        problem.Errors.ShouldNotBeNull();
        problem.Errors!.ShouldContainKey("styleTemplateId");
    }

    [Fact]
    public async Task CreateSeries_TrueCrimeWithGhostTemplate_Returns400()
    {
        var userId = await CreateUserAsync();
        var voice = await VoiceAsync();
        var ghostTemplate = await TemplateAsync("nts-test-template-ghost-hint", Topic.Ghost);
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await client.PostAsJsonAsync(
            RoutePrefix,
            CreateBody(voice.Id, ghostTemplate.Id, topic: "true_crime"),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await ReadProblemAsync(response);
        problem.Errors.ShouldNotBeNull();
        problem.Errors!.ShouldContainKey("styleTemplateId", "03 CR-2: the field is the template, not the topic");

        // The reverse holds too: a true-crime series with the true-crime template is accepted.
        var trueCrimeTemplate = await TemplateAsync("nts-test-template-true-crime", Topic.TrueCrime);
        using var accepted = await client.PostAsJsonAsync(
            RoutePrefix,
            CreateBody(voice.Id, trueCrimeTemplate.Id, topic: "true_crime"),
            Ct);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task UpdateSeries_GhostToTrueCrimeKeepingGhostTemplate_Returns400()
    {
        var userId = await CreateUserAsync();
        var voice = await VoiceAsync();
        var ghostTemplate = await TemplateAsync("nts-test-template-ghost-hint", Topic.Ghost);
        var seriesId = await SeedSeriesAsync(userId, voice.Id, ghostTemplate.Id);
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await PatchAsync(client, seriesId, new { topic = "true_crime" }, await ReadETagAsync(client, seriesId));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await ReadProblemAsync(response);
        problem.Code.ShouldBe("validation_failed");
        problem.Errors.ShouldNotBeNull();
        problem.Errors!.ShouldContainKey("styleTemplateId");
    }

    [Fact]
    public async Task PatchSeries_MissingIfMatch_Returns428()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await PatchAsync(client, seriesId, new { title = "Tên mới" }, ifMatch: null);

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);
        (await ReadProblemAsync(response)).Code.ShouldBe("if_match_required");
    }

    [Fact]
    public async Task PatchSeries_StaleIfMatch_Returns412SeriesConflict()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await PatchAsync(client, seriesId, new { title = "Tên mới" }, ifMatch: "\"se-1\"");

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await ReadProblemAsync(response)).Code.ShouldBe("series_conflict");

        // The row did not move: the same request with the current tag is accepted and answers the new tag.
        var etag = await ReadETagAsync(client, seriesId);
        using var accepted = await PatchAsync(client, seriesId, new { title = "Tên mới" }, etag);
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync(Ct));
        var body = await accepted.Content.ReadFromJsonAsync<EtagBody>(Ct);
        body.ShouldNotBeNull();
        accepted.Headers.ETag.ShouldNotBeNull();
        accepted.Headers.ETag!.ToString().ShouldBe(body.Etag);
        accepted.Headers.ETag!.ToString().ShouldNotBe(etag, "a write changes xmin, so the tag must move");
    }

    [Fact]
    public async Task PatchSeries_LanguageField_IsRejected()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await PatchAsync(
            client,
            seriesId,
            new { language = "en" },
            await ReadETagAsync(client, seriesId));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await ReadProblemAsync(response);
        problem.Code.ShouldBe("validation_failed");
        problem.Errors.ShouldNotBeNull();
        problem.Errors!.ShouldContainKey("language");

        // Nothing changed: the stored language is still the created one.
        using var read = await client.GetAsync($"{RoutePrefix}/{seriesId}", Ct);
        (await read.Content.ReadFromJsonAsync<SeriesBody>(Ct))!.Language.ShouldBe("vi");
    }

    [Fact]
    public async Task PatchSeries_StoryPreset_AbsentKeepsNullClearsUnknownRefused()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        await SetStoryPresetAsync(userId, seriesId, "ghost.vi.v1");
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        // Absent: plan §5's merge semantics — the stored preset survives a patch that does not name it.
        using var withoutPreset = await PatchAsync(
            client,
            seriesId,
            new { title = "Tên khác" },
            await ReadETagAsync(client, seriesId));
        withoutPreset.StatusCode.ShouldBe(HttpStatusCode.OK, await withoutPreset.Content.ReadAsStringAsync(Ct));
        (await ReadSeriesAsync(client, seriesId)).StoryPreset.ShouldBe("ghost.vi.v1");

        // A key the 02 catalog does not know is refused, not stored (the wave-1 catalog knows none).
        using var unknown = await PatchAsync(
            client,
            seriesId,
            new { storyPreset = "ghost.vi.v1" },
            await ReadETagAsync(client, seriesId));
        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await ReadProblemAsync(unknown);
        problem.Errors.ShouldNotBeNull();
        problem.Errors!.ShouldContainKey("storyPreset");

        // An explicit null clears the column.
        using var cleared = await PatchAsync(
            client,
            seriesId,
            new { storyPreset = (string?)null },
            await ReadETagAsync(client, seriesId));
        cleared.StatusCode.ShouldBe(HttpStatusCode.OK, await cleared.Content.ReadAsStringAsync(Ct));
        (await ReadSeriesAsync(client, seriesId)).StoryPreset.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteSeries_Archives_HiddenFromList_GetStillWorks_EpisodesStay()
    {
        var userId = await CreateUserAsync();
        var voice = await VoiceAsync();
        var template = await TemplateAsync();
        var archived = await SeedSeriesAsync(userId, voice.Id, template.Id, title: "Chuỗi bị lưu trữ");
        var kept = await SeedSeriesAsync(userId, voice.Id, template.Id, title: "Chuỗi còn lại", createdAt: Start.AddMinutes(1));
        var episodeId = (await new EpisodeBuilder(Database, new TestCurrentUser(), new FakeClock(Start))
            .CreateAsync(userId, archived, ct: Ct)).Id;
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var delete = await client.DeleteAsync($"{RoutePrefix}/{archived}", Ct);
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The default list excludes it, and the same list still shows the other series.
        var list = await ReadListAsync(client, string.Empty);
        list.Items.Select(item => item.Id).ShouldBe(new[] { kept }, "archived series are hidden unless asked for");

        // Asking for them returns the archived one, with the stamp.
        var withArchived = await ReadListAsync(client, "?includeArchived=true");
        withArchived.Items.Select(item => item.Id).ShouldContain(archived);
        withArchived.Items.Single(item => item.Id == archived).ArchivedAt.ShouldNotBeNull();

        // GET still answers it — archivedAt is set, which is how a client knows it is archived.
        var series = await ReadSeriesAsync(client, archived);
        series.ArchivedAt.ShouldNotBeNull();
        series.Etag.ShouldBe(await ReadETagAsync(client, archived));

        // Its episodes are untouched: no cascade, and 09.5.1 reads them through the same tenant filter.
        await using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = userId });
        var episode = await context.Episodes.SingleOrDefaultAsync(row => row.Id == episodeId, Ct);
        episode.ShouldNotBeNull("archiving a series must not remove its episodes");
        episode.SeriesId.ShouldBe(archived);

        // A second DELETE is a 204 again: archiving is idempotent (`Series_Archive_Twice_RaisesEventOnce`).
        using var again = await client.DeleteAsync($"{RoutePrefix}/{archived}", Ct);
        again.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ListSeries_Pagination_NoDuplicatesOrGaps_WhenInsertingBetweenPages()
    {
        var userId = await CreateUserAsync();
        var voice = await VoiceAsync();
        var template = await TemplateAsync();
        var seeded = new List<Guid>();
        for (var index = 0; index < 5; index++)
        {
            seeded.Add(await SeedSeriesAsync(
                userId,
                voice.Id,
                template.Id,
                title: $"Chuỗi {index}",
                createdAt: Start.AddMinutes(index)));
        }

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        // Newest first: 4, 3, 2, 1, 0.
        var first = await ReadListAsync(client, "?limit=2");
        first.Items.Select(item => item.Id).ShouldBe(new[] { seeded[4], seeded[3] });
        first.NextCursor.ShouldNotBeNull();

        // A row inserted between the two requests, sorting between 3 and 2. An OFFSET list would shift
        // page 2 here; the keyset cannot, because the cursor is the row it stopped at.
        await SeedSeriesAsync(
            userId,
            voice.Id,
            template.Id,
            title: "Chuỗi chèn giữa",
            createdAt: Start.AddMinutes(3).AddSeconds(30));

        var second = await ReadListAsync(client, $"?limit=2&cursor={first.NextCursor}");
        second.Items.Select(item => item.Id).ShouldBe(new[] { seeded[2], seeded[1] });
        second.NextCursor.ShouldNotBeNull();

        var third = await ReadListAsync(client, $"?limit=2&cursor={second.NextCursor}");
        third.Items.Select(item => item.Id).ShouldBe(new[] { seeded[0] });
        third.NextCursor.ShouldBeNull("the last page is the one that has no extra row");
    }

    [Fact]
    public async Task ListSeries_LimitOutOfRangeAndBadCursor_Return400()
    {
        var userId = await CreateUserAsync();
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var tooLarge = await client.GetAsync($"{RoutePrefix}?limit=101", Ct);
        using var zero = await client.GetAsync($"{RoutePrefix}?limit=0", Ct);
        using var garbage = await client.GetAsync($"{RoutePrefix}?cursor=x", Ct);

        tooLarge.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(tooLarge)).Errors!.ShouldContainKey("limit");
        zero.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(zero)).Errors!.ShouldContainKey("limit");
        garbage.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(garbage)).Errors!.ShouldContainKey("cursor");
    }

    [Fact]
    public async Task Series_OtherUser_Get_Patch_Delete_Return404()
    {
        var owner = await CreateUserAsync();
        var stranger = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(owner);
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(stranger);

        using var read = await client.GetAsync($"{RoutePrefix}/{seriesId}", Ct);
        using var patch = await PatchAsync(client, seriesId, new { title = "Tên của người khác" }, ifMatch: "\"se-1\"");
        using var delete = await client.DeleteAsync($"{RoutePrefix}/{seriesId}", Ct);

        read.StatusCode.ShouldBe(HttpStatusCode.NotFound, "D10: another user's row is 404, never 403");
        patch.StatusCode.ShouldBe(HttpStatusCode.NotFound, "404 is answered before the If-Match comparison");
        delete.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadProblemAsync(patch)).Code.ShouldBe("not_found");

        // The owner still sees it, with the title the stranger tried to write not applied.
        using var ownerClient = factory.ClientFor(owner);
        (await ReadSeriesAsync(ownerClient, seriesId)).Title.ShouldBe("Chuỗi thử nghiệm");
    }

    [Fact]
    public async Task Series_AnonymousRequests_Return401()
    {
        var owner = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(owner);
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.AnonymousClient();

        using var list = await client.GetAsync(RoutePrefix, Ct);
        using var read = await client.GetAsync($"{RoutePrefix}/{seriesId}", Ct);
        using var create = await client.PostAsJsonAsync(
            RoutePrefix,
            new { title = "X", topic = "ghost", language = "vi", aspect = "landscape", voiceId = "v", styleTemplateId = Guid.NewGuid() },
            Ct);
        using var delete = await client.DeleteAsync($"{RoutePrefix}/{seriesId}", Ct);

        list.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "every message is denied by default (D4)");
        read.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        create.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        delete.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The route filter runs before the pipeline: a PATCH that never reaches the handler still
        // answers 428 when the header is absent, and 401 (not 404) once it carries one.
        using var patchWithoutHeader = await PatchAsync(client, seriesId, new { title = "x" }, ifMatch: null);
        using var patchWithHeader = await PatchAsync(client, seriesId, new { title = "x" }, ifMatch: "\"se-1\"");
        patchWithoutHeader.StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);
        patchWithHeader.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateSeries_SameTitleTwice_Allowed()
    {
        var userId = await CreateUserAsync();
        var voice = await VoiceAsync();
        var template = await TemplateAsync();
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        var body = CreateBody(voice.Id, template.Id, title: "Tên trùng nhau");
        using var first = await client.PostAsJsonAsync(RoutePrefix, body, Ct);
        using var second = await client.PostAsJsonAsync(RoutePrefix, body, Ct);

        // The edge case of plan §Failure handling: the title carries no uniqueness rule.
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        var secondId = (await second.Content.ReadFromJsonAsync<CreatedBody>(Ct))!.Id;
        secondId.ShouldNotBe((await first.Content.ReadFromJsonAsync<CreatedBody>(Ct))!.Id);
    }

    [Fact]
    public async Task CreateSeries_UnknownStoryPreset_Returns400()
    {
        var userId = await CreateUserAsync();
        var voice = await VoiceAsync();
        var template = await TemplateAsync();
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var baseline = await client.PostAsJsonAsync(
            RoutePrefix,
            CreateBody(voice.Id, template.Id),
            Ct);

        // storyPreset travels as an explicit member here so the wave-1 null catalog must refuse it.
        using var withPreset = await client.PostAsJsonAsync(
            RoutePrefix,
            new
            {
                title = "Có preset",
                topic = "ghost",
                language = "vi",
                aspect = "landscape",
                voiceId = voice.Id,
                styleTemplateId = template.Id,
                storyPreset = "unknown.vi.v1",
            },
            Ct);

        baseline.StatusCode.ShouldBe(HttpStatusCode.Created, "the same body without the member is the control");
        withPreset.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await ReadProblemAsync(withPreset);
        problem.Errors.ShouldNotBeNull();
        problem.Errors!.ShouldContainKey("storyPreset");
    }

    [Fact]
    public async Task CreateSeries_TitleTrimsAndWhitespaceOnlyIsRejected()
    {
        var userId = await CreateUserAsync();
        var voice = await VoiceAsync();
        var template = await TemplateAsync();
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var whitespace = await client.PostAsJsonAsync(
            RoutePrefix,
            CreateBody(voice.Id, template.Id, title: "   "),
            Ct);
        whitespace.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(whitespace)).Errors!.ShouldContainKey("title");

        using var padded = await client.PostAsJsonAsync(
            RoutePrefix,
            CreateBody(voice.Id, template.Id, title: "  Có khoảng trắng  "),
            Ct);
        padded.StatusCode.ShouldBe(HttpStatusCode.Created, await padded.Content.ReadAsStringAsync(Ct));
        var id = (await padded.Content.ReadFromJsonAsync<CreatedBody>(Ct))!.Id;
        (await ReadSeriesAsync(client, id)).Title.ShouldBe("Có khoảng trắng", "the stored value is the trimmed one");
    }

    [Fact]
    public async Task PatchSeries_SameETagUsedTwice_SecondReturns412()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        // The sequential twin of the concurrency edge case: two requests race with the tag they both
        // read; one commits and moves xmin, so the other's tag compares stale and answers 412.
        var etag = await ReadETagAsync(client, seriesId);
        using var first = await PatchAsync(client, seriesId, new { title = "Lần một" }, etag);
        using var second = await PatchAsync(client, seriesId, new { title = "Lần hai" }, etag);

        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(Ct));
        second.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await ReadProblemAsync(second)).Code.ShouldBe("series_conflict");
        (await ReadSeriesAsync(client, seriesId)).Title.ShouldBe("Lần một", "the loser wrote nothing");
    }

    [Fact]
    public async Task PatchSeries_IfMatchStarAndWeakTag_Return412()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var star = await PatchAsync(client, seriesId, new { title = "x" }, ifMatch: "*");
        using var weak = await PatchAsync(client, seriesId, new { title = "x" }, ifMatch: "W/\"se-1\"");

        // ETags.Matches: `*` never matches (the client must have read the row) and a weak tag is not
        // enough for a lost-update check — both are the stale-tag 412, not 400.
        star.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        weak.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    public async Task PatchSeries_StoryPreset_NonString_Returns400()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        // A number is neither a string nor an explicit null: the wire model refuses it before the
        // handler can read a preset out of it (09.4.1 plan §5).
        using var response = await PatchAsync(
            client,
            seriesId,
            new { storyPreset = 42 },
            await ReadETagAsync(client, seriesId));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await ReadProblemAsync(response);
        problem.Errors.ShouldNotBeNull();
        problem.Errors!.ShouldContainKey("storyPreset");
    }

    [Fact]
    public async Task PatchSeries_ToInactiveVoice_Refused_ButExistingDeactivatedVoiceKept()
    {
        var userId = await CreateUserAsync();
        var ownVoice = $"nts-qa-own-{Guid.CreateVersion7():N}"[..24];
        var otherVoice = $"nts-qa-alt-{Guid.CreateVersion7():N}"[..24];
        await VoiceAsync(ownVoice);
        await VoiceAsync(otherVoice);
        var seriesId = await SeedSeriesAsync(userId, voiceId: ownVoice);

        // Deactivate both voices after the series exists (plan §Failure handling).
        await DeactivateVoiceAsync(ownVoice);
        await DeactivateVoiceAsync(otherVoice);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var toInactive = await PatchAsync(
            client,
            seriesId,
            new { voiceId = otherVoice },
            await ReadETagAsync(client, seriesId));
        toInactive.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(toInactive)).Errors!.ShouldContainKey("voiceId");

        // The deactivated voice the series already has is kept: an unrelated patch succeeds, and even
        // naming the same (now inactive) voice is a no-op, not a validation failure.
        using var unrelated = await PatchAsync(
            client,
            seriesId,
            new { title = "Vẫn giữ voice cũ" },
            await ReadETagAsync(client, seriesId));
        unrelated.StatusCode.ShouldBe(HttpStatusCode.OK, await unrelated.Content.ReadAsStringAsync(Ct));

        using var sameVoice = await PatchAsync(
            client,
            seriesId,
            new { voiceId = ownVoice },
            await ReadETagAsync(client, seriesId));
        sameVoice.StatusCode.ShouldBe(HttpStatusCode.OK, await sameVoice.Content.ReadAsStringAsync(Ct));
        (await ReadSeriesAsync(client, seriesId)).VoiceId.ShouldBe(ownVoice);
    }

    [Fact]
    public async Task PatchSeries_Archived_Returns409_AndStaleETagWinsOverIt()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        var etagBeforeDelete = await ReadETagAsync(client, seriesId);
        using var delete = await client.DeleteAsync($"{RoutePrefix}/{seriesId}", Ct);
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Order pin: a stale tag is answered 412 before the aggregate's archived 409.
        using var stale = await PatchAsync(client, seriesId, new { title = "x" }, etagBeforeDelete);
        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);

        // A current tag reaches the aggregate, which answers its own 409.
        using var archived = await PatchAsync(
            client,
            seriesId,
            new { title = "x" },
            await ReadETagAsync(client, seriesId));
        archived.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(archived)).Code.ShouldBe("series_archived");
    }

    [Fact]
    public async Task ListSeries_LimitBoundary_100Accepted()
    {
        var userId = await CreateUserAsync();
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        var page = await ReadListAsync(client, "?limit=100");
        page.Items.ShouldNotBeNull();

        using var negative = await client.GetAsync($"{RoutePrefix}?limit=-5", Ct);
        negative.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(negative)).Errors!.ShouldContainKey("limit");
    }

    [Fact]
    public async Task ListSeries_IncludeArchivedFalse_ExcludesArchived()
    {
        var userId = await CreateUserAsync();
        var archived = await SeedSeriesAsync(userId, title: "Đã lưu trữ");
        var live = await SeedSeriesAsync(userId, title: "Còn hoạt động", createdAt: Start.AddMinutes(1));
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var delete = await client.DeleteAsync($"{RoutePrefix}/{archived}", Ct);
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The explicit false must equal the absent default (plan §5).
        var explicitFalse = await ReadListAsync(client, "?includeArchived=false");
        explicitFalse.Items.Select(item => item.Id).ShouldBe(new[] { live });

        var withArchived = await ReadListAsync(client, "?includeArchived=true");
        withArchived.Items.Select(item => item.Id).ShouldBe(new[] { live, archived }, "newest first");
    }

    [Fact]
    public async Task ListSeries_TamperedCursorShapes_Return400()
    {
        var userId = await CreateUserAsync();
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        // Base64url that decodes but is not the {"c","i"} payload — every shape a lenient decoder
        // would accept and then answer the wrong page for (09 README D14).
        foreach (var payload in new[] { "{\"x\":1}", "\"a string\"", "[1,2]", "42", "{\"c\":null,\"i\":null}" })
        {
            var cursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

            using var response = await client.GetAsync($"{RoutePrefix}?cursor={cursor}", Ct);
            response.StatusCode.ShouldBe(
                HttpStatusCode.BadRequest,
                $"cursor payload {payload} must not be treated as a page cursor");
        }

        // A forged but well-formed cursor is accepted by design (D14: it can only move the caller
        // around their own rows) — documented here so a future tightening is a deliberate change.
        var forged = new NightTales.Application.Common.Paging.Cursor(
            new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Guid.NewGuid()).Encode();
        var page = await ReadListAsync(client, $"?cursor={forged}");
        page.Items.ShouldBeEmpty("a cursor before every row pages past them all");
    }

    [Fact]
    public async Task GetSeries_MissingId_Returns404()
    {
        var userId = await CreateUserAsync();
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await client.GetAsync($"{RoutePrefix}/{Guid.NewGuid()}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadProblemAsync(response)).Code.ShouldBe("not_found");
    }

    [Fact]
    public async Task PatchSeries_EmptyTitle_Returns400UnderTitle()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await PatchAsync(
            client,
            seriesId,
            new { title = "" },
            await ReadETagAsync(client, seriesId));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await ReadProblemAsync(response);
        problem.Code.ShouldBe("validation_failed");

        // CR-1: the failure is reported under the wire name the client sent, not under the nested
        // `patch.title` path the validator's rule is declared on.
        problem.Errors.ShouldNotBeNull();
        problem.Errors!.ShouldContainKey("title");
        problem.Errors.ShouldNotContainKey("patch.title");
    }

    [Fact]
    public async Task ListSeries_OffsetCursor_IsNormalizedToUtcAndAccepted()
    {
        var userId = await CreateUserAsync();
        var voice = await VoiceAsync();
        var template = await TemplateAsync();
        await SeedSeriesAsync(userId, voice.Id, template.Id, createdAt: Start.AddMinutes(-1));
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        // SEC-0941-01: a client may encode any offset. Npgsql refuses a non-zero offset on a
        // `timestamptz` parameter, so the decoder normalizes it and this answers a page, not a 500.
        using var response = await client.GetAsync($"{RoutePrefix}?limit=2&cursor={OffsetCursor(userId)}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>Encodes a hand-built <c>+07:00</c> cursor for <paramref name="userId"/>'s rows.</summary>
    private static string OffsetCursor(Guid userId)
    {
        var json = $"{{\"c\":\"{Start.ToString("O", CultureInfo.InvariantCulture).Replace("+00:00", "+07:00", StringComparison.Ordinal)}\",\"i\":\"{userId}\"}}";

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private async Task<Guid> CreateUserAsync()
        => (await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: Ct)).Id;

    private SeriesBuilder Series() => new(Database, new TestCurrentUser(), new FakeClock(Start));

    private Task<Voice> VoiceAsync(string id = SeriesBuilder.DefaultVoiceId, Language language = Language.Vi)
        => Series().EnsureVoiceAsync(id, language, ct: Ct);

    private Task<StyleTemplate> TemplateAsync(string name = SeriesBuilder.DefaultTemplateName, Topic? topicHint = null)
        => Series().EnsureStyleTemplateAsync(name, topicHint, ct: Ct);

    /// <summary>Inserts one series through the TestKit builder, which writes a real row.</summary>
    private async Task<Guid> SeedSeriesAsync(
        Guid userId,
        string? voiceId = null,
        Guid? styleTemplateId = null,
        string title = "Chuỗi thử nghiệm",
        DateTimeOffset? createdAt = null)
    {
        var series = await Series().CreateAsync(
            userId,
            title: title,
            voiceId: voiceId,
            styleTemplateId: styleTemplateId,
            createdAt: createdAt,
            ct: Ct);

        return series.Id;
    }

    /// <summary>Flips a catalog voice inactive, the way the 04 seed update of a retired voice would.</summary>
    private async Task DeactivateVoiceAsync(string voiceId)
    {
        await using var context = Database.CreateAppDbContext(new TestCurrentUser());
        var voice = await context.Voices.SingleAsync(candidate => candidate.Id == voiceId, Ct);
        voice.UpdateFromCatalog(
            voice.DisplayName,
            voice.Language,
            voice.Gender,
            voice.Tags,
            active: false,
            previewStorageKey: voice.PreviewStorageKey);
        await context.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// Stores a preset key without going through the API: the wave-1 catalog knows no key, so a row
    /// carrying one is the only way to test that a patch which omits the member keeps it (plan §5).
    /// The context runs as the owner, because the D10 tenant filter hides the row from anyone else.
    /// </summary>
    private async Task SetStoryPresetAsync(Guid userId, Guid seriesId, string preset)
    {
        await using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = userId });
        var row = await context.Series.SingleAsync(series => series.Id == seriesId, Ct);
        context.Entry(row).Property(nameof(SeriesAggregate.StoryPreset)).CurrentValue = preset;
        await context.SaveChangesAsync(Ct);
    }

    /// <summary>Inserts a template that exists but cannot be used, under a name no other test shares.</summary>
    private async Task<Guid> CreateInactiveTemplateAsync()
    {
        await using var context = Database.CreateAppDbContext(new TestCurrentUser());
        var template = StyleTemplate.Create(
            $"nts-inactive-{Guid.CreateVersion7():N}"[..24],
            TemplateText,
            topicHint: null,
            active: false);
        context.StyleTemplates.Add(template);
        await context.SaveChangesAsync(Ct);

        return template.Id;
    }

    /// <summary>The POST /series body; the enum members travel snake_case (09 README D14).</summary>
    private static object CreateBody(
        string voiceId,
        Guid styleTemplateId,
        string title = "Chuỗi thử nghiệm",
        string topic = "ghost",
        string language = "vi") =>
        new
        {
            title,
            topic,
            language,
            aspect = "landscape",
            voiceId,
            styleTemplateId,
            storyPreset = (string?)null,
        };

    private static async Task<HttpResponseMessage> PatchAsync<TBody>(
        HttpClient client,
        Guid seriesId,
        TBody body,
        string? ifMatch)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"{RoutePrefix}/{seriesId}")
        {
            Content = JsonContent.Create(body),
        };

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation(RequireIfMatchFilter.HeaderName, ifMatch);
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<string> ReadETagAsync(HttpClient client, Guid seriesId)
    {
        using var response = await client.GetAsync($"{RoutePrefix}/{seriesId}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag.ShouldNotBeNull();

        return response.Headers.ETag!.ToString();
    }

    private static async Task<SeriesBody> ReadSeriesAsync(HttpClient client, Guid seriesId)
    {
        using var response = await client.GetAsync($"{RoutePrefix}/{seriesId}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var series = await response.Content.ReadFromJsonAsync<SeriesBody>(Ct);
        series.ShouldNotBeNull();

        return series;
    }

    private static async Task<PageBody> ReadListAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync($"{RoutePrefix}{query}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var page = await response.Content.ReadFromJsonAsync<PageBody>(Ct);
        page.ShouldNotBeNull();

        return page;
    }

    private static async Task<ProblemBody> ReadProblemAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);
        problem.ShouldNotBeNull();
        problem.Status.ShouldBe((int)response.StatusCode);

        return problem;
    }

    /// <summary>The 201 body: the id, which is also the <c>Location</c>.</summary>
    private sealed record CreatedBody(Guid Id);

    /// <summary>The PATCH success body: <c>{ etag }</c> (plan §5).</summary>
    private sealed record EtagBody(string Etag);

    /// <summary>The members of one series the route tests read.</summary>
    private sealed record SeriesBody(
        Guid Id,
        string Title,
        string Language,
        string VoiceId,
        string? StoryPreset,
        DateTimeOffset? ArchivedAt,
        string Etag);

    /// <summary>One row of the list, whose <c>etag</c> the client uses for its next patch.</summary>
    private sealed record SeriesItem(Guid Id, string Title, DateTimeOffset? ArchivedAt, string Etag);

    /// <summary>The <c>{ items, nextCursor }</c> body of the list (09 README D14).</summary>
    private sealed record PageBody(List<SeriesItem> Items, string? NextCursor);

    /// <summary>The ProblemDetails members these tests assert on (09 README D7).</summary>
    private sealed record ProblemBody(string Code, int Status, Dictionary<string, string[]>? Errors);
}
