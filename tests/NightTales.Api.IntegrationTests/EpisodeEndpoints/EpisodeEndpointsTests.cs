using System.Net;
using System.Net.Http.Json;
using System.Text;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.Http.Filters;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Application.Abstractions;
using NightTales.Application.Features.Episodes.Commands.CreateEpisode;
using NightTales.Application.Features.Episodes.Commands.DeleteEpisode;
using NightTales.Domain.Assets;
using NightTales.Domain.Episodes;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.EpisodeEndpoints;

/// <summary>
/// The four episode routes against the real host and a real PostgreSQL 17 (09.5.1 plan §9): create under
/// a series, list with the D14 cursor, read one, cancel one — with the D15 ETag rules, the D10 tenant
/// isolation and the <c>episode_cancelled.v1</c> retention handler of the Worker.
/// </summary>
/// <remarks>
/// <para>
/// Every test seeds its own user, so the class database being shared cannot leak rows between tests: the
/// tenant filter of the routes restricts each read to its caller. The catalog rows (voices, style
/// templates) are shared through the builders.
/// </para>
/// <para>
/// The paths are spelled out rather than read from <c>EpisodeEndpoints</c>: this folder's namespace is
/// <c>NightTales.Api.IntegrationTests.EpisodeEndpoints</c> (never <c>…IntegrationTests.Episodes</c>,
/// which would shadow the <c>NightTales.Domain.Episodes</c> namespace for the whole assembly, 09.4.1-T1),
/// so the endpoint type's simple name is not usable here — and a path is a contract this test asserts.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class EpisodeEndpointsTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>A fixed instant, so the keyset ordering and the ETag stamps are deterministic.</summary>
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    /// <summary>The episode group's frozen path (09.5.1 plan §5).</summary>
    private const string EpisodesRoute = "/api/v1/episodes";

    /// <summary>The series group's path, which the create route hangs under.</summary>
    private const string SeriesRoute = "/api/v1/series";

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task CreateEpisode_Valid_Returns201WithLocationAndETag()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await client.PostAsJsonAsync(
            CreateUrl(seriesId),
            CreateBody("Nội dung tập một.", title: "  Tập một  "),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));

        var created = await response.Content.ReadFromJsonAsync<Guid>(Ct);
        created.ShouldNotBe(Guid.Empty);

        response.Headers.Location.ShouldNotBeNull();
        response.Headers.Location!.ToString().ShouldBe($"{EpisodesRoute}/{created}");
        response.Headers.ETag.ShouldNotBeNull();
        response.Headers.ETag!.Tag.ShouldStartWith("\"ep-");

        // The row is readable at the Location it was just given, with the same ETag in the body.
        using var read = await client.GetAsync(response.Headers.Location, Ct);
        read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.Content.ReadAsStringAsync(Ct));
        read.Headers.ETag!.ToString().ShouldBe(response.Headers.ETag!.ToString());

        var episode = await read.Content.ReadFromJsonAsync<EpisodeBody>(Ct);
        episode.ShouldNotBeNull();
        episode.Id.ShouldBe(created);
        episode.SeriesId.ShouldBe(seriesId);
        episode.Title.ShouldBe("Tập một", "the stored title is the trimmed one");
        episode.Brief.ShouldBe("Nội dung tập một.");
        episode.Status.ShouldBe("draft");
        episode.LengthTier.ShouldBe("standard");
        episode.Etag.ShouldBe(response.Headers.ETag!.ToString());

        // The 02-M1-dev gate of 09.5.1 plan §11: a developer creates a series, creates an episode and
        // sees it in the list of that series.
        var list = await ReadPageAsync(client, $"?seriesId={seriesId}");
        list.Items.Select(item => item.Id).ShouldBe(new[] { created });
        list.Items[0].Etag.ShouldBe(episode.Etag, "the list row carries the tag its delete needs");
        list.Items[0].CreatedAt.ShouldBe(Start, "the command's clock is the Api's FakeClock");
    }

    [Fact]
    public async Task CreateEpisode_ArchivedSeries_Returns409SeriesArchived()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var archive = await client.DeleteAsync($"{SeriesRoute}/{seriesId}", Ct);
        archive.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var response = await client.PostAsJsonAsync(CreateUrl(seriesId), CreateBody("Nội dung"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(response)).Code.ShouldBe("series_archived");
    }

    [Fact]
    public async Task CreateEpisode_OtherUsersSeries_Returns404()
    {
        var owner = await CreateUserAsync();
        var stranger = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(owner);
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(stranger);

        using var response = await client.PostAsJsonAsync(CreateUrl(seriesId), CreateBody("Nội dung"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, "D10: another user's series is 404, never 403");
        (await ReadProblemAsync(response)).Code.ShouldBe("not_found");

        await using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = owner });
        (await context.Episodes.CountAsync(row => row.SeriesId == seriesId, Ct))
            .ShouldBe(0, "a refused create writes nothing");
    }

    [Fact]
    public async Task CreateEpisode_Brief1000Ok_1001Rejected_BlankRejected()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var longest = await client.PostAsJsonAsync(
            CreateUrl(seriesId),
            CreateBody(new string('a', 1_000)),
            Ct);
        using var tooLong = await client.PostAsJsonAsync(
            CreateUrl(seriesId),
            CreateBody(new string('a', 1_001)),
            Ct);
        using var blank = await client.PostAsJsonAsync(CreateUrl(seriesId), CreateBody("   "), Ct);
        using var control = await client.PostAsJsonAsync(CreateUrl(seriesId), CreateBody("dòng\rmột"), Ct);

        longest.StatusCode.ShouldBe(HttpStatusCode.Created, await longest.Content.ReadAsStringAsync(Ct));
        tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(tooLong)).Errors!.ShouldContainKey("brief");
        blank.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(blank)).Errors!.ShouldContainKey("brief");
        control.StatusCode.ShouldBe(
            HttpStatusCode.BadRequest,
            "a carriage return is a control character; only \\n and \\t are allowed");
        (await ReadProblemAsync(control)).Errors!.ShouldContainKey("brief");
    }

    [Fact]
    public async Task CreateEpisode_MissingOrNullBrief_Returns400Brief()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var missing = await client.PostAsJsonAsync(
            CreateUrl(seriesId),
            new { title = "Tập", lengthTier = "standard" },
            Ct);
        using var nullBrief = await client.PostAsJsonAsync(
            CreateUrl(seriesId),
            new { title = (string?)null, brief = (string?)null, lengthTier = "standard" },
            Ct);

        // Whether the JSON binder or the validator answers, a brief that is not a string is a 400
        // under field `brief` — never a 500 from a null reaching the handler.
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(missing)).Errors!.ShouldContainKey("brief");
        nullBrief.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(nullBrief)).Errors!.ShouldContainKey("brief");
    }


    [Fact]
    public async Task CreateEpisode_TitleAndLengthTierBounds()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var longest = await client.PostAsJsonAsync(
            CreateUrl(seriesId),
            CreateBody("Nội dung", title: new string('t', 120)),
            Ct);
        using var tooLong = await client.PostAsJsonAsync(
            CreateUrl(seriesId),
            CreateBody("Nội dung", title: new string('t', 121)),
            Ct);
        using var blank = await client.PostAsJsonAsync(CreateUrl(seriesId), CreateBody("Nội dung", title: "   "), Ct);
        using var unknownTier = await client.PostAsJsonAsync(
            CreateUrl(seriesId),
            new { title = (string?)null, brief = "Nội dung", lengthTier = 7 },
            Ct);
        using var unknownTierName = await client.PostAsJsonAsync(
            CreateUrl(seriesId),
            new { title = (string?)null, brief = "Nội dung", lengthTier = "epic" },
            Ct);

        longest.StatusCode.ShouldBe(HttpStatusCode.Created, await longest.Content.ReadAsStringAsync(Ct));
        tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(tooLong)).Errors!.ShouldContainKey("title");

        // An empty title is not an error: it is stored as null (09.5.1 plan §Design).
        blank.StatusCode.ShouldBe(HttpStatusCode.Created, await blank.Content.ReadAsStringAsync(Ct));
        var blankId = await blank.Content.ReadFromJsonAsync<Guid>(Ct);
        (await ReadEpisodeAsync(client, blankId)).Title.ShouldBeNull();

        // An undefined enum member is refused by the validator; a name the enum does not know never
        // reaches it, because the JSON binder refuses the body first.
        unknownTier.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(unknownTier)).Errors!.ShouldContainKey("lengthTier");
        unknownTierName.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateEpisode_UserIdCopiedFromSeries()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        // The HTTP path: the body has no `userId`/`seriesId` member at all, so both come from the
        // series the route names.
        using var response = await client.PostAsJsonAsync(CreateUrl(seriesId), CreateBody("Nội dung"), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var createdId = await response.Content.ReadFromJsonAsync<Guid>(Ct);

        // The autopilot path (09.5.1 plan §4 UC5, 02.8.3): the same command in a scope pinned with
        // ISystemContext.For before any DbContext is resolved, exactly as an outbox delivery runs it.
        Guid autopilotId;
        using (var scope = factory.Services.CreateScope())
        {
            var scopeServices = scope.ServiceProvider;
            scopeServices.GetRequiredService<ISystemContext>().For(userId);

            var sent = await scopeServices.GetRequiredService<ISender>()
                .Send(new CreateEpisodeCommand(seriesId, null, "Nội dung autopilot", LengthTier.Short), Ct);

            if (sent.IsError)
            {
                throw new InvalidOperationException(
                    "CreateEpisodeCommand failed: " + string.Join(", ", sent.Errors.Select(error => error.Code)));
            }

            autopilotId = sent.Value;
        }

        await using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = userId });
        var rows = await context.Episodes
            .Where(row => row.Id == createdId || row.Id == autopilotId)
            .ToListAsync(Ct);

        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(row => row.UserId == userId && row.SeriesId == seriesId);
        rows.Single(row => row.Id == autopilotId).LengthTier.ShouldBe(LengthTier.Short);
    }

    [Fact]
    public async Task ListEpisodes_KeysetStable_WhenInsertingBetweenPages()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var seeded = new List<Guid>();
        for (var index = 0; index < 5; index++)
        {
            seeded.Add(await SeedEpisodeAsync(userId, seriesId, createdAt: Start.AddMinutes(index)));
        }

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        // Newest first: 4, 3, 2, 1, 0.
        var first = await ReadPageAsync(client, "?limit=2");
        first.Items.Select(item => item.Id).ShouldBe(new[] { seeded[4], seeded[3] });
        first.NextCursor.ShouldNotBeNull();

        // A row created between the two requests, sorting between 3 and 2. An OFFSET list would shift
        // page 2 here; the keyset cannot, because the cursor is the row it stopped at.
        await SeedEpisodeAsync(userId, seriesId, createdAt: Start.AddMinutes(3).AddSeconds(30));

        var second = await ReadPageAsync(client, $"?limit=2&cursor={first.NextCursor}");
        second.Items.Select(item => item.Id).ShouldBe(new[] { seeded[2], seeded[1] });
        second.NextCursor.ShouldNotBeNull();

        var third = await ReadPageAsync(client, $"?limit=2&cursor={second.NextCursor}");
        third.Items.Select(item => item.Id).ShouldBe(new[] { seeded[0] });
        third.NextCursor.ShouldBeNull("the last page is the one that has no extra row");
    }

    [Fact]
    public async Task ListEpisodes_StatusAndSeriesFilters_ExcludeCancelled()
    {
        var userId = await CreateUserAsync();
        var seriesA = await SeedSeriesAsync(userId, title: "Chuỗi A");
        var seriesB = await SeedSeriesAsync(userId, title: "Chuỗi B", createdAt: Start.AddMinutes(1));

        var draftA = await SeedEpisodeAsync(userId, seriesA, createdAt: Start.AddMinutes(2));
        var reviewA = await SeedEpisodeAsync(
            userId,
            seriesA,
            status: EpisodeStatus.StoryReview,
            createdAt: Start.AddMinutes(3));
        await SeedEpisodeAsync(userId, seriesA, status: EpisodeStatus.Cancelled, createdAt: Start.AddMinutes(4));
        var draftB = await SeedEpisodeAsync(userId, seriesB, createdAt: Start.AddMinutes(5));

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        var all = await ReadPageAsync(client, string.Empty);
        all.Items.Select(item => item.Id).ShouldBe(
            new[] { draftB, reviewA, draftA },
            "cancelled episodes are invisible to reads (F14)");

        var drafts = await ReadPageAsync(client, "?status=draft");
        drafts.Items.Select(item => item.Id).ShouldBe(new[] { draftB, draftA });

        var seriesAPage = await ReadPageAsync(client, $"?seriesId={seriesA}");
        seriesAPage.Items.Select(item => item.Id).ShouldBe(new[] { reviewA, draftA });

        var combined = await ReadPageAsync(client, $"?seriesId={seriesA}&status=draft");
        combined.Items.Select(item => item.Id).ShouldBe(new[] { draftA });

        var cancelledFilter = await ReadPageAsync(client, "?status=cancelled");
        cancelledFilter.Items.ShouldBeEmpty("the exclusion wins over the filter");

        using var unknown = await client.GetAsync($"{EpisodesRoute}?status=not_a_status", Ct);
        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(unknown)).Errors!.ShouldContainKey("status");
    }

    [Fact]
    public async Task ListEpisodes_OtherUsersSeriesIdFilter_ReturnsEmptyPage()
    {
        var owner = await CreateUserAsync();
        var stranger = await CreateUserAsync();
        var ownerSeries = await SeedSeriesAsync(owner);
        await SeedEpisodeAsync(owner, ownerSeries);
        var strangerSeries = await SeedSeriesAsync(stranger);
        var strangerEpisode = await SeedEpisodeAsync(stranger, strangerSeries);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(stranger);

        // Filtering by another tenant's series is an empty page, never a 404: a refusal would leak
        // the series' existence, which is exactly what the tenant filter hides (09.5.1 plan §8, D10).
        var filtered = await ReadPageAsync(client, $"?seriesId={ownerSeries}");
        filtered.Items.ShouldBeEmpty();
        filtered.NextCursor.ShouldBeNull();

        // The filter narrows, it does not widen: the stranger still sees only their own episode.
        var all = await ReadPageAsync(client, string.Empty);
        all.Items.Select(item => item.Id).ShouldBe(new[] { strangerEpisode });
    }

    [Fact]
    public async Task ListEpisodes_CursorFromAnotherFilter_StaysInsideNewFilter()
    {
        var userId = await CreateUserAsync();
        var seriesA = await SeedSeriesAsync(userId, title: "Chuỗi A");
        var seriesB = await SeedSeriesAsync(userId, title: "Chuỗi B", createdAt: Start.AddMinutes(1));

        var a1 = await SeedEpisodeAsync(userId, seriesA, createdAt: Start.AddMinutes(2));
        var b1 = await SeedEpisodeAsync(userId, seriesB, createdAt: Start.AddMinutes(3));
        var a2 = await SeedEpisodeAsync(userId, seriesA, createdAt: Start.AddMinutes(4));
        var b2 = await SeedEpisodeAsync(userId, seriesB, createdAt: Start.AddMinutes(5));

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        // Page 1 of the unfiltered list is b2, a2. The cursor is a (createdAt, id) position, not a
        // filter, so reusing it under ?seriesId= must page inside that series and never leak B rows.
        var first = await ReadPageAsync(client, "?limit=2");
        first.Items.Select(item => item.Id).ShouldBe(new[] { b2, a2 });
        first.NextCursor.ShouldNotBeNull();

        var filtered = await ReadPageAsync(
            client,
            $"?seriesId={seriesA}&limit=10&cursor={first.NextCursor}");
        filtered.Items.Select(item => item.Id).ShouldBe(
            new[] { a1 },
            "the cursor bounds the position; the series filter still bounds the rows");
        filtered.NextCursor.ShouldBeNull();

        // The same composition under ?status=: every returned row is older than the cursor row.
        var byStatus = await ReadPageAsync(client, $"?status=draft&limit=10&cursor={first.NextCursor}");
        byStatus.Items.Select(item => item.Id).ShouldBe(new[] { b1, a1 });
    }

    [Fact]
    public async Task ListEpisodes_MalformedSeriesId_Returns400()
    {
        var userId = await CreateUserAsync();
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await client.GetAsync($"{EpisodesRoute}?seriesId=not-a-guid", Ct);
        response.StatusCode.ShouldBe(
            HttpStatusCode.BadRequest,
            "a seriesId the binder cannot parse is a malformed request, not an empty page");
    }


    [Fact]
    public async Task ListEpisodes_Limit0Or101_Returns400()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        await SeedEpisodeAsync(userId, seriesId);
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        var smallest = await ReadPageAsync(client, "?limit=1");
        smallest.Items.Count.ShouldBe(1);

        var largest = await ReadPageAsync(client, "?limit=100");
        largest.Items.Count.ShouldBe(1);
        largest.NextCursor.ShouldBeNull();

        foreach (var limit in new[] { 0, 101, -5 })
        {
            using var response = await client.GetAsync($"{EpisodesRoute}?limit={limit}", Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"limit={limit} is outside 1–100");
            (await ReadProblemAsync(response)).Errors!.ShouldContainKey("limit");
        }
    }

    [Fact]
    public async Task ListEpisodes_InvalidCursor_Returns400()
    {
        var userId = await CreateUserAsync();
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var garbage = await client.GetAsync($"{EpisodesRoute}?cursor=x", Ct);
        garbage.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(garbage)).Errors!.ShouldContainKey("cursor");

        // Base64url that decodes but is not the {"c","i"} payload — every shape a lenient decoder would
        // accept and then answer the wrong page for (09 README D14).
        foreach (var payload in new[] { "{\"x\":1}", "\"a string\"", "[1,2]", "42" })
        {
            var cursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

            using var response = await client.GetAsync($"{EpisodesRoute}?cursor={cursor}", Ct);
            response.StatusCode.ShouldBe(
                HttpStatusCode.BadRequest,
                $"cursor payload {payload} must not be treated as a page cursor");
        }

        // An empty value is "no cursor", not a failure: that is Cursor.TryDecode's own contract for the
        // shared D14 type, so the route pages normally.
        var empty = await ReadPageAsync(client, "?cursor=");
        empty.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetEpisode_Cancelled_Returns404()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var cancelledId = await SeedEpisodeAsync(userId, seriesId, status: EpisodeStatus.Cancelled);
        await SeedEpisodeAsync(userId, seriesId, createdAt: Start.AddMinutes(1));
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await client.GetAsync($"{EpisodesRoute}/{cancelledId}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadProblemAsync(response)).Code.ShouldBe("not_found");

        // The row is kept (F14): the read hides it, the table does not lose it.
        await using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = userId });
        var row = await context.Episodes.SingleOrDefaultAsync(episode => episode.Id == cancelledId, Ct);
        row.ShouldNotBeNull();
        row!.Status.ShouldBe(EpisodeStatus.Cancelled);

        // The other episode of the same user is still readable, so the 404 is about the status.
        var list = await ReadPageAsync(client, string.Empty);
        list.Items.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetEpisode_MissingId_Returns404()
    {
        var userId = await CreateUserAsync();
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await client.GetAsync($"{EpisodesRoute}/{Guid.CreateVersion7()}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadProblemAsync(response)).Code.ShouldBe("not_found");
    }


    [Theory]
    [InlineData(EpisodeStatus.StoryGenerating)]
    [InlineData(EpisodeStatus.Rendering)]
    public async Task DeleteEpisode_WhileRenderingOrGenerating_Returns409(EpisodeStatus status)
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = await SeedEpisodeAsync(userId, seriesId, status: status);
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var response = await client.DeleteAsync($"{EpisodesRoute}/{episodeId}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(response)).Code.ShouldBe("episode_invalid_state");

        await using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = userId });
        (await context.Episodes.SingleAsync(episode => episode.Id == episodeId, Ct))
            .Status.ShouldBe(status, "the refused cancel changed nothing");
    }

    [Fact]
    public async Task DeleteEpisode_StaleIfMatch_Returns412EpisodeConflict()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = await SeedEpisodeAsync(userId, seriesId);
        var optionalId = await SeedEpisodeAsync(userId, seriesId, createdAt: Start.AddMinutes(1));
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var stale = await DeleteAsync(client, episodeId, ifMatch: "\"ep-1\"");
        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await ReadProblemAsync(stale)).Code.ShouldBe("episode_conflict");

        // The row did not move: the same request with the current tag is accepted.
        var etag = await ReadETagAsync(client, episodeId);
        using var accepted = await DeleteAsync(client, episodeId, etag);
        accepted.StatusCode.ShouldBe(HttpStatusCode.NoContent, await accepted.Content.ReadAsStringAsync(Ct));

        // `If-Match` is optional on DELETE ([DECISION] 09.5.1 plan §Notes): without it the cancel runs,
        // and there is no 428 the way PATCH /series/{id} answers one.
        using var withoutHeader = await DeleteAsync(client, optionalId, ifMatch: null);
        withoutHeader.StatusCode.ShouldBe(HttpStatusCode.NoContent, await withoutHeader.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task DeleteEpisode_WhitespaceIfMatch_Returns412()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = await SeedEpisodeAsync(userId, seriesId);
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        // CR-1: a header the client sent — even one holding only whitespace — is *present*, so the handler
        // compares it and answers 412 episode_conflict. Only a request with no header at all may cancel.
        using var whitespace = await DeleteAsync(client, episodeId, "   ");

        whitespace.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await ReadProblemAsync(whitespace)).Code.ShouldBe("episode_conflict");

        await using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = userId });
        (await context.Episodes.SingleAsync(episode => episode.Id == episodeId, Ct))
            .Status.ShouldBe(EpisodeStatus.Draft, "a present but unsatisfied precondition must not cancel anything");
    }

    [Fact]
    public async Task DeleteEpisode_EmptyIfMatchInProcess_Returns412()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = await SeedEpisodeAsync(userId, seriesId);
        using var factory = new ApiFactory(Database, new FakeClock(Start));

        // CR-1 at the handler boundary: an empty `If-Match` is present, not absent, so it must not cancel.
        // This leg drives ISender in-process instead of going over HTTP because HttpClient never delivers an
        // empty-valued header — `TryAddWithoutValidation("If-Match", "")` reaches the server as a request
        // *without* that header (probed against this host, 2026-10-01), indistinguishable from an absent one.
        // Over HTTP, the reachable present-but-blank cell is therefore the whitespace case above; the empty
        // string is pinned here, where the command carries exactly what the binder would have produced.
        using (var scope = factory.Services.CreateScope())
        {
            var scopeServices = scope.ServiceProvider;
            scopeServices.GetRequiredService<ISystemContext>().For(userId);

            var sent = await scopeServices.GetRequiredService<ISender>()
                .Send(new DeleteEpisodeCommand(episodeId, string.Empty), Ct);

            sent.IsError.ShouldBeTrue();
            sent.FirstError.Code.ShouldBe("episode_conflict");
        }

        await using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = userId });
        (await context.Episodes.SingleAsync(episode => episode.Id == episodeId, Ct))
            .Status.ShouldBe(EpisodeStatus.Draft, "a present but unsatisfied precondition must not cancel anything");
    }

    [Fact]
    public async Task DeleteEpisode_IfMatchWildcardOrWeak_Returns412()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var wildcardId = await SeedEpisodeAsync(userId, seriesId);
        var weakId = await SeedEpisodeAsync(userId, seriesId, createdAt: Start.AddMinutes(1));
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        // `*` never matches: the client must have read the row (ETags.Matches, Application/Common).
        using var star = await DeleteAsync(client, wildcardId, "*");
        star.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await ReadProblemAsync(star)).Code.ShouldBe("episode_conflict");

        // A weak form of the current tag never matches either: weak comparison is not a lost-update
        // check, so `W/"ep-N"` is a stale tag for the route's purposes.
        var etag = await ReadETagAsync(client, weakId);
        using var weak = await DeleteAsync(client, weakId, $"W/{etag}");
        weak.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await ReadProblemAsync(weak)).Code.ShouldBe("episode_conflict");

        await using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = userId });
        var rows = await context.Episodes
            .Where(episode => episode.Id == wildcardId || episode.Id == weakId)
            .ToListAsync(Ct);
        rows.ShouldAllBe(
            episode => episode.Status == EpisodeStatus.Draft,
            "a refused delete changed nothing");
    }

    [Fact]
    public async Task DeleteEpisode_CancelledWithStaleIfMatch_Returns404Not412()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = await SeedEpisodeAsync(userId, seriesId);
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        var etag = await ReadETagAsync(client, episodeId);
        using var first = await DeleteAsync(client, episodeId, etag);
        first.StatusCode.ShouldBe(HttpStatusCode.NoContent, await first.Content.ReadAsStringAsync(Ct));

        // Once cancelled the row is invisible, so a second delete is 404 even though the tag the
        // client sends is now also stale: the 404 comes before the If-Match comparison (F14, Design).
        using var second = await DeleteAsync(client, episodeId, etag);
        second.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadProblemAsync(second)).Code.ShouldBe("not_found");
    }


    [Fact]
    public async Task DeleteEpisode_Twice_SecondReturns404()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = await SeedEpisodeAsync(userId, seriesId);
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);

        using var first = await client.DeleteAsync($"{EpisodesRoute}/{episodeId}", Ct);
        using var second = await client.DeleteAsync($"{EpisodesRoute}/{episodeId}", Ct);
        using var read = await client.GetAsync($"{EpisodesRoute}/{episodeId}", Ct);

        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        second.StatusCode.ShouldBe(HttpStatusCode.NotFound, "cancel is not idempotent; the row is invisible");
        (await ReadProblemAsync(second)).Code.ShouldBe("not_found");
        read.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Episode_OtherUser_Get_Delete_Return404()
    {
        var owner = await CreateUserAsync();
        var stranger = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(owner);
        var episodeId = await SeedEpisodeAsync(owner, seriesId);
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(stranger);

        using var read = await client.GetAsync($"{EpisodesRoute}/{episodeId}", Ct);
        using var delete = await DeleteAsync(client, episodeId, ifMatch: "\"ep-1\"");

        read.StatusCode.ShouldBe(HttpStatusCode.NotFound, "D10: another user's row is 404, never 403");
        delete.StatusCode.ShouldBe(HttpStatusCode.NotFound, "404 is answered before the If-Match comparison");

        // The owner still sees an uncancelled episode: the stranger's delete wrote nothing.
        using var ownerClient = factory.ClientFor(owner);
        (await ReadEpisodeAsync(ownerClient, episodeId)).Status.ShouldBe("draft");
    }

    [Fact]
    public async Task EpisodeCancelled_MarksAssetsForRetention()
    {
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId);
        var episodeId = await SeedEpisodeAsync(userId, seriesId);
        var otherEpisodeId = await SeedEpisodeAsync(userId, seriesId, createdAt: Start.AddMinutes(1));

        var builder = Episodes();
        var live = await builder.CreateAssetAsync(userId, episodeId, ct: Ct);
        var subtitle = await builder.CreateAssetAsync(userId, episodeId, AssetKind.Subtitle, ct: Ct);
        var due = await builder.CreateAssetAsync(
            userId,
            episodeId,
            AssetKind.Audio,
            retentionUntil: Start.AddMinutes(-5),
            ct: Ct);
        var later = await builder.CreateAssetAsync(
            userId,
            episodeId,
            AssetKind.Image,
            retentionUntil: Start.AddDays(1),
            ct: Ct);
        var deleted = await builder.CreateAssetAsync(
            userId,
            episodeId,
            AssetKind.Thumbnail,
            deletedAt: Start,
            ct: Ct);
        var foreign = await builder.CreateAssetAsync(userId, otherEpisodeId, ct: Ct);

        using var factory = new ApiFactory(Database, new FakeClock(Start));
        using var client = factory.ClientFor(userId);
        using var delete = await client.DeleteAsync($"{EpisodesRoute}/{episodeId}", Ct);
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent, await delete.Content.ReadAsStringAsync(Ct));

        // The Worker's own composition root (09 README D13): AddWorkerServices registers the
        // episode_cancelled consumer, so this drives the real handler through the real dispatcher.
        var workerClock = new FakeClock(Start.AddMinutes(10));
        await using var worker = new WorkerHost(Database, workerClock);
        await worker.StartAsync(Ct);
        await DrainOutboxAsync(worker);

        var now = workerClock.UtcNow;
        var stored = await ReadAssetsAsync(userId, episodeId);

        stored.Single(asset => asset.Id == live.Id).RetentionUntil.ShouldBe(
            now,
            "a live asset without a retention stamp gets one");
        stored.Single(asset => asset.Id == subtitle.Id).RetentionUntil.ShouldBe(now);
        stored.Single(asset => asset.Id == due.Id).RetentionUntil.ShouldBe(
            Start.AddMinutes(-5),
            "LEAST keeps an earlier stamp, so an asset already due is never pushed later");
        stored.Single(asset => asset.Id == later.Id).RetentionUntil.ShouldBe(
            now,
            "LEAST pulls a later stamp forward to now");
        stored.Single(asset => asset.Id == deleted.Id).RetentionUntil.ShouldBeNull(
            "a deleted asset is left alone");
        stored.Single(asset => asset.Id == live.Id).UpdatedAt.ShouldBe(
            now,
            "ExecuteUpdateAsync bypasses the audit interceptor, so the handler sets updated_at itself");

        var otherStored = await ReadAssetsAsync(userId, otherEpisodeId);
        otherStored.Single(asset => asset.Id == foreign.Id).RetentionUntil.ShouldBeNull(
            "only the cancelled episode's assets are scheduled");
    }

    /// <summary>
    /// Cancels an episode with the given <c>If-Match</c>, or without the header when
    /// <paramref name="ifMatch"/> is <see langword="null"/>.
    /// </summary>
    private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid episodeId, string? ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{EpisodesRoute}/{episodeId}");

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation(RequireIfMatchFilter.HeaderName, ifMatch);
        }

        return await client.SendAsync(request, Ct);
    }

    /// <summary>Claims and delivers every due outbox row, so no test depends on the batch size.</summary>
    private static async Task DrainOutboxAsync(WorkerHost worker)
    {
        for (var round = 0; round < 10; round++)
        {
            if (await worker.RunOutboxOnceAsync(Ct) == 0)
            {
                return;
            }
        }

        throw new InvalidOperationException("The outbox did not drain within 10 batches.");
    }

    private async Task<Guid> CreateUserAsync()
        => (await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: Ct)).Id;

    /// <summary>The series builder, over the class database and the fixed clock.</summary>
    private SeriesBuilder Series() => new(Database, new TestCurrentUser(), new FakeClock(Start));

    /// <summary>The episode builder, over the class database and the fixed clock.</summary>
    private EpisodeBuilder Episodes() => new(Database, new TestCurrentUser(), new FakeClock(Start));

    /// <summary>Inserts one series, with the catalog rows its foreign keys need.</summary>
    private async Task<Guid> SeedSeriesAsync(
        Guid userId,
        string title = "Chuỗi thử nghiệm",
        DateTimeOffset? createdAt = null)
    {
        var voice = await Series().EnsureVoiceAsync(ct: Ct);
        var template = await Series().EnsureStyleTemplateAsync(ct: Ct);

        var series = await Series().CreateAsync(
            userId,
            title: title,
            voiceId: voice.Id,
            styleTemplateId: template.Id,
            createdAt: createdAt,
            ct: Ct);

        return series.Id;
    }

    /// <summary>Inserts one episode through the TestKit builder, which writes a real row.</summary>
    private async Task<Guid> SeedEpisodeAsync(
        Guid userId,
        Guid seriesId,
        string brief = EpisodeBuilder.DefaultBrief,
        string? title = null,
        LengthTier lengthTier = LengthTier.Standard,
        EpisodeStatus status = EpisodeStatus.Draft,
        DateTimeOffset? createdAt = null)
        => (await Episodes().CreateAsync(
            userId,
            seriesId,
            brief: brief,
            title: title,
            lengthTier: lengthTier,
            status: status,
            createdAt: createdAt,
            ct: Ct)).Id;

    private async Task<IReadOnlyList<Asset>> ReadAssetsAsync(Guid userId, Guid episodeId)
    {
        await using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = userId });

        return await context.Assets
            .Where(asset => asset.EpisodeId == episodeId)
            .ToListAsync(Ct);
    }

    private static string CreateUrl(Guid seriesId) => $"{SeriesRoute}/{seriesId}/episodes";

    /// <summary>The POST body; <c>lengthTier</c> travels snake_case (09 README D14).</summary>
    private static object CreateBody(string brief, string? title = null, string lengthTier = "standard")
        => new { title, brief, lengthTier };

    private static async Task<EpisodeBody> ReadEpisodeAsync(HttpClient client, Guid episodeId)
    {
        using var response = await client.GetAsync($"{EpisodesRoute}/{episodeId}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var episode = await response.Content.ReadFromJsonAsync<EpisodeBody>(Ct);
        episode.ShouldNotBeNull();

        return episode;
    }

    private static async Task<string> ReadETagAsync(HttpClient client, Guid episodeId)
    {
        using var response = await client.GetAsync($"{EpisodesRoute}/{episodeId}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag.ShouldNotBeNull();

        return response.Headers.ETag!.ToString();
    }

    private static async Task<EpisodePage> ReadPageAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync($"{EpisodesRoute}{query}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var page = await response.Content.ReadFromJsonAsync<EpisodePage>(Ct);
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

    /// <summary>
    /// The <c>GET /episodes/{id}</c> body (09.5.1 plan §5; 09.5.3-T1 renamed the DTO and moved the block
    /// members in, keeping <c>brief</c> and dropping the list-level stamps, so this test asserts the
    /// episode's own fields plus the ETag only).
    /// </summary>
    private sealed record EpisodeBody(
        Guid Id,
        Guid SeriesId,
        string? Title,
        string Brief,
        string Status,
        string LengthTier,
        string Etag);

    /// <summary>One row of the list, whose <c>etag</c> the client uses for its next delete.</summary>
    private sealed record EpisodeItem(
        Guid Id,
        Guid SeriesId,
        string? Title,
        string Status,
        string LengthTier,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        string Etag);

    /// <summary>The <c>{ items, nextCursor }</c> body of the list (09 README D14).</summary>
    private sealed record EpisodePage(List<EpisodeItem> Items, string? NextCursor);

    /// <summary>The ProblemDetails members these tests assert on (09 README D7).</summary>
    private sealed record ProblemBody(string Code, int Status, Dictionary<string, string[]>? Errors);
}
