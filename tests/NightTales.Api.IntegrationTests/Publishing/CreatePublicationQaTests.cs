using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Mediator;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions;
using NightTales.Application.Common;
using NightTales.Application.Features.Publishing.Commands.CreatePublication;
using NightTales.Application.Publishing;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Publishing;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// QA pass over <c>POST</c>/<c>GET /api/v1/episodes/{id}/publications</c> (06.2.1-T1, 06 README D6/D10/D12/D13/D17/
/// D18/YR20) against the real Api and a real PostgreSQL 17: the negative, boundary and exploratory cases the task
/// file's §Tests table does not reach — astral title counting, UTF-8 bytes after a multibyte attribution, every
/// non-active connection and non-ready episode state, malformed bodies, a foreign autopilot authorization, the
/// pre-audit flip, and the shapes the wire must never carry.
/// </summary>
/// <remarks>
/// Additive tests only: the task's own cases stay pinned by <see cref="CreatePublicationTests"/> and
/// <c>GetEpisodeDetailTests</c>. Every test seeds its own owner through the TestKit builders, so the one database a
/// class shares never makes two tests see each other's rows.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CreatePublicationQaTests(TestDatabase database) : IntegrationTest(database)
{
    private const string EpisodesRoute = "/api/v1/episodes";
    private const string FeatureKey = "youtube_publish";

    private const string DefaultTitle = "Tập QA: chuyến tàu đêm";
    private const string DefaultDescription = "Nội dung mô tả của bài kiểm thử QA.";

    private static readonly string[] DefaultTags = ["đêm", "truyện", "audio"];

    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private const string Moon = "\U0001F319";

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task Create_TitleOf100RunesWithAstralCharacters_Is201_101RunesIs400()
    {
        // G4 counts characters, and a surrogate pair is one: 98 ASCII + 2 astral runes is a legal 100-character
        // title even though the string is 102 UTF-16 units long.
        var atCap = new string('a', 98) + Moon + Moon;
        var world = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        using var factory = NewFactory(auditStatus: YouTubeAuditStatuses.Approved);
        using var client = factory.ClientFor(world.UserId);

        var (body, _) = await ReadCreatedAsync(await PostAsync(client, world, Body(world, title: atCap)));

        body.Title.ShouldBe(atCap);
        PublicationHas(body.Id, $"title = '{atCap}' AND char_length(title) = 100").ShouldBeTrue(
            "the column keeps 100 code points, not 100 UTF-16 units");

        var overCap = new string('a', 98) + Moon + Moon + Moon;
        var second = await SeedAsync(renderJob: false);
        using var secondClient = factory.ClientFor(second.UserId);
        using var refused = await PostAsync(secondClient, second, Body(second, title: overCap));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await refused.Content.ReadAsStringAsync(Ct));

        var problem = await ReadProblemAsync(refused);

        problem.Code.ShouldBe("validation_failed");
        problem.Errors!.ShouldContainKey("title");
        PublicationCount(second.EpisodeId).ShouldBe(0);
    }

    [Fact]
    public async Task Create_DescriptionOf5000BytesWithMultibyteTextAfterTheAttribution_Is201_OverIs400()
    {
        // F08 measures UTF-8 bytes of what YouTube receives: 2,488 x 'é' (2 bytes) = 4,976, the tail 9 bytes and
        // an attribution of 5 x 'ệ' (3 bytes) = 15 bytes is exactly 5,000.
        var attribution = Repeat("ệ", 5);
        var description = Repeat("é", 2_488);
        var world = await SeedAsync(attribution: attribution);
        await SetFlagAsync(true);
        using var factory = NewFactory(auditStatus: YouTubeAuditStatuses.Approved);
        using var client = factory.ClientFor(world.UserId);

        var (body, _) = await ReadCreatedAsync(await PostAsync(client, world, Body(world, description: description)));

        body.Description.ShouldBe($"{description}\n\nMusic: {attribution}");
        PublicationHas(body.Id, "octet_length(description) = 5000").ShouldBeTrue(
            "the byte count of the stored text, not its character count");

        // One more multibyte rune in the attribution alone pushes the stored text to 5,003 bytes.
        var overWorld = await SeedAsync(attribution: Repeat("ệ", 6));
        using var overClient = factory.ClientFor(overWorld.UserId);
        using var refused = await PostAsync(overClient, overWorld, Body(overWorld, description: description));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await refused.Content.ReadAsStringAsync(Ct));

        var problem = await ReadProblemAsync(refused);

        problem.Code.ShouldBe("validation_failed");
        problem.Errors!.ShouldContainKey("description");
        PublicationCount(overWorld.EpisodeId).ShouldBe(0);
    }

    [Theory]
    [InlineData("PUBLIC")]
    [InlineData("Public")]
    [InlineData("publik")]
    [InlineData("")]
    public async Task Create_PrivacyOutsideTheContract_Is400ErrorsPrivacyNot500(string privacy)
    {
        var world = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        using var factory = NewFactory(auditStatus: YouTubeAuditStatuses.Approved);
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world, Body(world, privacy: privacy));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));

        var problem = await ReadProblemAsync(response);

        problem.Code.ShouldBe("validation_failed");
        problem.Errors!.ShouldContainKey("privacy", "G6: the value is compared ordinally against the wire strings");
        PublicationCount(world.EpisodeId).ShouldBe(0);
    }

    [Theory]
    [InlineData("{ \"title\": ")]
    [InlineData("[]")]
    [InlineData("{ \"channelConnectionId\": \"not-a-guid\" }")]
    [InlineData("{ \"title\": 42, \"description\": \"d\", \"privacy\": \"private\" }")]
    [InlineData("{ \"title\": \"t\", \"description\": \"d\", \"privacy\": 7 }")]
    [InlineData("{ \"title\": \"t\", \"description\": \"d\", \"privacy\": \"private\", \"publishAt\": \"tomorrow\" }")]
    [InlineData("{ \"title\": \"t\", \"description\": \"d\", \"privacy\": \"private\", \"tags\": \"a,b\" }")]
    public async Task Create_MalformedOrWronglyTypedBody_Is400Not500(string json)
    {
        var world = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(
            $"{EpisodesRoute}/{world.EpisodeId}/publications", content, Ct);

        response.StatusCode.ShouldBe(
            HttpStatusCode.BadRequest,
            $"a body that does not bind is a 400, never a 500: {await response.Content.ReadAsStringAsync(Ct)}");
        PublicationCount(world.EpisodeId).ShouldBe(0);
    }

    [Fact]
    public async Task Create_OmittedOrNullTags_Is201WithAnEmptyTagList()
    {
        var omitted = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        using var factory = NewFactory(auditStatus: YouTubeAuditStatuses.Approved);
        using var omittedClient = factory.ClientFor(omitted.UserId);

        // The member is absent from the JSON body entirely.
        using var withoutTags = JsonContent.Create(new
        {
            channelConnectionId = omitted.ConnectionId,
            title = DefaultTitle,
            description = DefaultDescription,
            privacy = "private",
        });

        using var omittedResponse = await omittedClient.PostAsync(
            $"{EpisodesRoute}/{omitted.EpisodeId}/publications", withoutTags, Ct);
        var (omittedBody, omittedJson) = await ReadCreatedAsync(omittedResponse);

        omittedBody.Tags.ShouldBeEmpty();
        omittedJson.GetProperty("tags").GetArrayLength().ShouldBe(0);
        PublicationHas(omittedBody.Id, "tags = ARRAY[]::text[]").ShouldBeTrue(
            "an omitted member means 'no tags', the empty list the command takes");

        var nulled = await SeedAsync(renderJob: false);
        using var nulledClient = factory.ClientFor(nulled.UserId);
        using var withNullTags = JsonContent.Create(new
        {
            channelConnectionId = nulled.ConnectionId,
            title = DefaultTitle,
            description = DefaultDescription,
            tags = (string[]?)null,
            privacy = "private",
        });

        using var nulledResponse = await nulledClient.PostAsync(
            $"{EpisodesRoute}/{nulled.EpisodeId}/publications", withNullTags, Ct);
        var (nulledBody, _) = await ReadCreatedAsync(nulledResponse);

        nulledBody.Tags.ShouldBeEmpty();
        PublicationHas(nulledBody.Id, "tags = ARRAY[]::text[]").ShouldBeTrue();
    }

    [Fact]
    public async Task Create_PublishAtExactlyTheLeadBoundary_Is201_OneSecondBefore_Is400()
    {
        var boundary = Start.AddMinutes(120);
        var world = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        using var factory = NewFactory(auditStatus: YouTubeAuditStatuses.Approved);
        using var client = factory.ClientFor(world.UserId);

        var (body, _) = await ReadCreatedAsync(await PostAsync(client, world, Body(world, publishAt: boundary)));

        body.PublishAt.ShouldBe(boundary, "the boundary is inclusive (06 failure table)");

        var early = await SeedAsync(renderJob: false);
        using var earlyClient = factory.ClientFor(early.UserId);
        using var refused = await PostAsync(
            earlyClient,
            early,
            Body(early, publishAt: boundary.AddTicks(-TimeSpan.TicksPerSecond)));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await refused.Content.ReadAsStringAsync(Ct));

        var problem = await ReadProblemAsync(refused);

        problem.Code.ShouldBe("validation_failed");
        problem.Errors!.ShouldContainKey("publishAt");
        PublicationCount(early.EpisodeId).ShouldBe(0);
    }

    [Fact]
    public async Task Create_WithAnotherOwnersAutopilotAuthorization_Is400AndStoresNoRow()
    {
        // SECURITY (09 README D10, the packet's risk class): the authorization id is command-only, and the
        // ownership check is the tenant filter on `series_autopilot_authorizations`. A foreign id must be
        // refused, never stored on a row, and never linked from the table.
        var mine = await SeedAsync(renderJob: false);
        var stranger = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var scope = factory.Services.CreateScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(mine.UserId);
        var foreign = await SeedAuthorizationAsync(stranger.UserId, stranger.SeriesId);

        var sent = await scope.ServiceProvider.GetRequiredService<ISender>().Send(
            Command(mine, publishAt: Start.AddHours(5), authorizationId: foreign), Ct);

        sent.IsError.ShouldBeTrue("another owner's authorization is invisible to this tenant");
        sent.FirstError.Code.ShouldBe("validation_failed");
        sent.FirstError.Metadata!["field"]!.ToString().ShouldBe("autopilotAuthorizationId");
        PublicationCount(mine.EpisodeId).ShouldBe(0);
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM publications WHERE autopilot_authorization_id = '{foreign}'")
            .ShouldBe(0);
    }

    [Theory]
    [InlineData(EpisodeStatus.Draft)]
    [InlineData(EpisodeStatus.StoryGenerating)]
    [InlineData(EpisodeStatus.StoryReview)]
    [InlineData(EpisodeStatus.StoryApproved)]
    [InlineData(EpisodeStatus.Rendering)]
    [InlineData(EpisodeStatus.VideoReview)]
    [InlineData(EpisodeStatus.Scheduled)]
    [InlineData(EpisodeStatus.Published)]
    [InlineData(EpisodeStatus.Failed)]
    [InlineData(EpisodeStatus.Cancelled)]
    public async Task Create_EpisodeInAnyNonReadyStatus_Is409EpisodeInvalidState(EpisodeStatus status)
    {
        var world = await SeedAsync(episodeStatus: status, renderJob: false);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world, Body(world));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("episode_invalid_state");
        PublicationCount(world.EpisodeId).ShouldBe(0);
    }

    [Theory]
    [InlineData(ChannelConnectionStatus.Revoked)]
    [InlineData(ChannelConnectionStatus.Expired)]
    [InlineData(ChannelConnectionStatus.Error)]
    public async Task Create_ConnectionInAnyNonActiveStatus_Is409ChannelNotActive(ChannelConnectionStatus status)
    {
        var world = await SeedAsync(connectionStatus: status, renderJob: false);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world, Body(world));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("channel_not_active");
        PublicationCount(world.EpisodeId).ShouldBe(0);
    }

    [Theory]
    [InlineData(PublicationStatus.Cancelled)]
    [InlineData(PublicationStatus.Failed)]
    public async Task Create_AfterATerminalRow_Is201(PublicationStatus terminal)
    {
        var world = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        using var factory = NewFactory(auditStatus: YouTubeAuditStatuses.Approved);
        using var client = factory.ClientFor(world.UserId);

        var (first, _) = await ReadCreatedAsync(await PostAsync(client, world, Body(world)));

        // 06.2.2-T1's cancel route and 06.2.3-T2's failure transition do not exist yet, so the row is moved the
        // way those transitions would.
        var wire = PublicationStatusCatalogue.Wire(terminal);

        await Database.ExecuteAsync(
            $"UPDATE publications SET status = '{wire}' WHERE id = '{first.Id}'", Ct);

        var (second, _) = await ReadCreatedAsync(await PostAsync(client, world, Body(world)));

        second.Id.ShouldNotBe(first.Id);
        PublicationCount(world.EpisodeId).ShouldBe(2, "the partial unique excludes failed and cancelled rows");
    }

    [Fact]
    public async Task Create_TwoDifferentEpisodesConcurrently_BothSucceed()
    {
        var one = await SeedAsync(renderJob: false);
        var two = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        using var factory = NewFactory(auditStatus: YouTubeAuditStatuses.Approved);
        using var first = factory.ClientFor(one.UserId);
        using var second = factory.ClientFor(two.UserId);
        var start = new TaskCompletionSource();

        async Task<HttpResponseMessage> CreateAsync(HttpClient client, World world)
        {
            await start.Task;

            return await PostAsync(client, world, Body(world));
        }

        var pending = new[] { CreateAsync(first, one), CreateAsync(second, two) };
        start.SetResult();

        var responses = await Task.WhenAll(pending);

        try
        {
            responses.ShouldAllBe(
                response => response.StatusCode == HttpStatusCode.Created,
                "two episodes share no key: neither create may block the other");
            PublicationCount(one.EpisodeId).ShouldBe(1);
            PublicationCount(two.EpisodeId).ShouldBe(1);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task List_ReturnsTheRowsNewestFirst_AndEveryEtagIsItsOwnXmin()
    {
        var world = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        var builder = new PublicationBuilder(Database);

        // One active row and two terminal rows (the partial unique allows exactly one live row per episode).
        var failed = await builder.CreateAsync(
            world.UserId, world.EpisodeId, world.ConnectionId, PublicationStatus.Failed,
            createdAt: Start, ct: Ct);
        var cancelled = await builder.CreateAsync(
            world.UserId, world.EpisodeId, world.ConnectionId, PublicationStatus.Cancelled,
            createdAt: Start.AddMinutes(1), ct: Ct);
        var published = await builder.CreateAsync(
            world.UserId, world.EpisodeId, world.ConnectionId, PublicationStatus.Published,
            publishedAt: Start.AddHours(1), createdAt: Start.AddMinutes(2), ct: Ct);

        using var factory = NewFactory(auditStatus: YouTubeAuditStatuses.Approved);
        using var client = factory.ClientFor(world.UserId);

        var items = await ReadListAsync(
            await client.GetAsync($"{EpisodesRoute}/{world.EpisodeId}/publications", Ct));

        items.Select(item => item.Id).ToList().ShouldBe(
            new List<Guid> { published, cancelled, failed },
            "created_at descending, then id");

        foreach (var item in items)
        {
            item.Etag.ShouldMatch("^\"pu-[0-9]+\"$");
            item.Etag.ShouldBe(
                ETags.Publication(uint.Parse(PublicationXmin(item.Id), CultureInfo.InvariantCulture)),
                "the list's tag is the row's own xmin, the tag its later PATCH must echo");
        }
    }

    [Fact]
    public async Task List_ForeignEpisode_Is404NotFound()
    {
        var mine = await SeedAsync(renderJob: false);
        var stranger = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(mine.UserId);

        using var response = await client.GetAsync(
            $"{EpisodesRoute}/{stranger.EpisodeId}/publications", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("not_found");
    }

    [Fact]
    public async Task List_FlagOff_Is404FeatureDisabled()
    {
        var world = await SeedAsync(renderJob: false);
        await SetFlagAsync(false);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await client.GetAsync(
            $"{EpisodesRoute}/{world.EpisodeId}/publications", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("feature_disabled");
    }

    [Fact]
    public async Task PublicationRoutes_Anonymous_Are401()
    {
        var world = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var anonymous = factory.AnonymousClient();

        using var list = await anonymous.GetAsync($"{EpisodesRoute}/{world.EpisodeId}/publications", Ct);

        list.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "every message is denied by default (D4)");

        using var content = JsonContent.Create(new
        {
            channelConnectionId = world.ConnectionId,
            title = DefaultTitle,
            description = DefaultDescription,
            privacy = "private",
        });
        using var post = await anonymous.PostAsync(
            $"{EpisodesRoute}/{world.EpisodeId}/publications", content, Ct);

        post.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task List_OfAnAutopilotRow_CarriesNoPayloadAuthorizationIdOrToken()
    {
        var world = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        using var factory = NewFactory(auditStatus: YouTubeAuditStatuses.Pending);
        using var scope = factory.Services.CreateScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(world.UserId);
        var authorizationId = await SeedAuthorizationAsync(world.UserId, world.SeriesId);

        var sent = await scope.ServiceProvider.GetRequiredService<ISender>().Send(
            Command(world, publishAt: Start.AddHours(5), authorizationId: authorizationId), Ct);

        if (sent.IsError)
        {
            throw new InvalidOperationException(
                "the autopilot create failed: " + string.Join(", ", sent.Errors.Select(error => error.Code)));
        }

        using var client = factory.ClientFor(world.UserId);
        using var response = await client.GetAsync(
            $"{EpisodesRoute}/{world.EpisodeId}/publications", Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);

        var items = await ReadListAsync(response);

        items.ShouldHaveSingleItem().Id.ShouldBe(sent.Value.Id);
        items[0].Privacy.ShouldBe("private", "D12: pending audit forces the enforced value");
        items[0].PreAudit.ShouldBeTrue();
        items[0].RequestedPrivacy.ShouldBe("public");
        items[0].VetoEndsAt.ShouldNotBeNull("an autopilot create carries the YR1 window");

        text.ShouldNotContain(authorizationId.ToString(), Case.Sensitive);

        using var json = JsonDocument.Parse(text);

        AssertNoForbiddenMembers(
            json.RootElement,
            "platformPayload",
            "platform_payload",
            "autopilotAuthorizationId",
            "accessToken",
            "accessTokenEnc",
            "refreshToken",
            "refreshTokenEnc",
            "storageKey",
            "uploadSessionEnc",
            "oauthClientId",
            "scope");
    }

    [Fact]
    public async Task Create_AuditPendingThenApproved_KeepsTheCreatedRowPrivate_AndDoesNotRewriteIt()
    {
        var world = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        using var pendingFactory = NewFactory(auditStatus: YouTubeAuditStatuses.Pending);
        using var pendingClient = pendingFactory.ClientFor(world.UserId);

        var (first, _) = await ReadCreatedAsync(
            await PostAsync(pendingClient, world, Body(world, privacy: "public")));

        first.Privacy.ShouldBe("private");

        // YR20: an AuditStatus flip writes nothing; the pre-audit row stays private for good.
        using var approvedFactory = NewFactory(auditStatus: YouTubeAuditStatuses.Approved);
        using var approvedClient = approvedFactory.ClientFor(world.UserId);
        var items = await ReadListAsync(
            await approvedClient.GetAsync($"{EpisodesRoute}/{world.EpisodeId}/publications", Ct));

        items.ShouldHaveSingleItem().Privacy.ShouldBe("private");
        items[0].PreAudit.ShouldBeTrue();
        items[0].RequestedPrivacy.ShouldBe("public");
        PublicationHas(first.Id, "privacy = 'private'").ShouldBeTrue();
    }

    [Fact]
    public async Task Detail_PublicationsSummary_IsNonNullZerosForTerminalRows_AndStillThreeQueries()
    {
        var world = await SeedAsync(episodeStatus: EpisodeStatus.Draft, renderJob: false);
        await SetFlagAsync(true);
        var builder = new PublicationBuilder(Database);

        await builder.CreateAsync(
            world.UserId, world.EpisodeId, world.ConnectionId, PublicationStatus.Cancelled,
            createdAt: Start, ct: Ct);
        await builder.CreateAsync(
            world.UserId, world.EpisodeId, world.ConnectionId, PublicationStatus.Failed,
            createdAt: Start.AddMinutes(1), ct: Ct);

        var interceptor = new CountingDbCommandInterceptor();

        using var factory = NewFactory(auditStatus: YouTubeAuditStatuses.Approved);
        using var host = CountingHost(factory, interceptor);
        using var client = AuthenticatedClient(host, world.UserId);

        interceptor.Reset();
        using var response = await client.GetAsync($"{EpisodesRoute}/{world.EpisodeId}", Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);

        using var json = JsonDocument.Parse(text);
        var block = json.RootElement.GetProperty("publications");

        block.ValueKind.ShouldNotBe(
            JsonValueKind.Null,
            "an episode with only terminal rows answers a real summary with zeros (06 README D6)");
        block.GetProperty("published").GetInt32().ShouldBe(0);
        block.GetProperty("scheduled").GetInt32().ShouldBe(0);
        block.GetProperty("lastPublishedAt").ValueKind.ShouldBe(JsonValueKind.Null);

        interceptor.Count.ShouldBe(3, $"C7 adds exactly one round trip: {string.Join(" | ", interceptor.Texts)}");
        interceptor.Texts[^1].ShouldContain("publications", Case.Insensitive);
        interceptor.ObservedConcurrentExecution.ShouldBeFalse("WT46: the reads stay sequential on one context");
    }

    [Fact]
    public async Task Create_NullElementInsideTags_Is400Not500()
    {
        // The shape validator refuses a null list, and the domain refuses a blank tag — but the handler trims
        // every element before it validates, so a JSON `[ "a", null ]` reaches `tag.Trim()` as a null string.
        // A client-supplied body must still be a coded 400, never a 500.
        var world = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);
        using var content = new StringContent(
            "{ \"channelConnectionId\": \"" + world.ConnectionId + "\", \"title\": \"t\", "
            + "\"description\": \"d\", \"privacy\": \"private\", \"tags\": [\"a\", null] }",
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync(
            $"{EpisodesRoute}/{world.EpisodeId}/publications", content, Ct);

        response.StatusCode.ShouldBe(
            HttpStatusCode.BadRequest,
            "a null tag element is a body error: " + await response.Content.ReadAsStringAsync(Ct));
        PublicationCount(world.EpisodeId).ShouldBe(0);
    }

    [Fact]
    public async Task Create_AutopilotWithoutPublishAt_Is400ErrorsPublishAt()
    {
        // YR1's formula is min(now + window, (publishAt ?? now) - lead): with no publishAt the second term is
        // already the lead time in the past, so the veto floor refuses the create. 02.8.3 always sets a
        // publishAt; this pins what the formula does when it does not.
        var world = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var scope = factory.Services.CreateScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(world.UserId);
        var authorizationId = await SeedAuthorizationAsync(world.UserId, world.SeriesId);

        var sent = await scope.ServiceProvider.GetRequiredService<ISender>().Send(
            Command(world, publishAt: null, authorizationId: authorizationId), Ct);

        sent.IsError.ShouldBeTrue();
        sent.FirstError.Code.ShouldBe("validation_failed");
        sent.FirstError.Metadata!["field"]!.ToString().ShouldBe("publishAt");
        PublicationCount(world.EpisodeId).ShouldBe(0);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// One owner with a series, an episode and a live connection — the world a create needs — plus the render job
    /// whose <c>music.attribution</c> the handler appends.
    /// </summary>
    private async Task<World> SeedAsync(
        EpisodeStatus episodeStatus = EpisodeStatus.Ready,
        ChannelConnectionStatus connectionStatus = ChannelConnectionStatus.Active,
        string? attribution = RenderJobBuilder.DefaultAttribution,
        bool renderJob = true,
        string? autopilotSettings = null)
    {
        var owner = new TestCurrentUser();
        var userId = (await new UserBuilder(Database, owner).CreateAsync(ct: Ct)).Id;
        var actor = new TestCurrentUser { Id = userId };
        var clock = new FakeClock(Start);

        var series = await new SeriesBuilder(Database, actor, clock).CreateAsync(
            userId,
            title: "Chuỗi QA 06.2.1",
            autopilotEnabled: autopilotSettings is not null,
            autopilotSettings: autopilotSettings,
            createdAt: Start,
            ct: Ct);

        var episode = await new EpisodeBuilder(Database, actor, clock).CreateAsync(
            userId,
            series.Id,
            brief: "Nội dung tập QA.",
            status: episodeStatus,
            createdAt: Start,
            ct: Ct);

        var displayName = $"NTS QA Channel {Guid.CreateVersion7():N}";
        var connection = await new ChannelConnectionBuilder(Database, actor, clock).CreateAsync(
            userId,
            connectionStatus,
            displayName,
            ct: Ct);

        if (renderJob)
        {
            await new RenderJobBuilder(Database, actor, clock).CreateAsync(
                userId, episode.Id, attribution: attribution, ct: Ct);
        }

        return new World(userId, series.Id, episode.Id, connection.Id);
    }

    /// <summary>Writes the one flag row this route reads.</summary>
    private async Task SetFlagAsync(bool enabled)
    {
        await Database.ExecuteAsync($"DELETE FROM feature_flags WHERE key = '{FeatureKey}'", Ct);

        if (enabled)
        {
            await Database.ExecuteAsync(
                "INSERT INTO feature_flags (key, enabled, rules, created_at, updated_at) "
                + $"VALUES ('{FeatureKey}', true, '{{}}', now(), now())",
                Ct);
        }
    }

    /// <summary>The host; the audit status is in the configuration before the first <c>Services</c> read.</summary>
    private ApiFactory NewFactory(string auditStatus = YouTubeAuditStatuses.Pending) =>
        new ApiFactory(Database, new FakeClock(Start))
            .WithSetting($"{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.AuditStatus)}", auditStatus);

    /// <summary>The request body of the route, by its D14 wire names.</summary>
    private static object Body(
        World world,
        string? title = null,
        string? description = null,
        IReadOnlyList<string>? tags = null,
        string privacy = "private",
        DateTimeOffset? publishAt = null)
    {
        var tagsValue = tags ?? DefaultTags;

        return new
        {
            channelConnectionId = world.ConnectionId,
            title = title ?? DefaultTitle,
            description = description ?? DefaultDescription,
            tags = tagsValue,
            privacy,
            publishAt,
        };
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, World world, object body) =>
        client.PostAsJsonAsync($"{EpisodesRoute}/{world.EpisodeId}/publications", body, Ct);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task<(PublicationBody Body, JsonElement Json)> ReadCreatedAsync(
        HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, text);

        var body = JsonSerializer.Deserialize<PublicationBody>(text, JsonOptions);

        body.ShouldNotBeNull();

        JsonElement json;

        using (var document = JsonDocument.Parse(text))
        {
            json = document.RootElement.Clone();
        }

        return (body, json);
    }

    private static async Task<List<PublicationBody>> ReadListAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);

        var page = JsonSerializer.Deserialize<PublicationListBody>(text, JsonOptions);

        page.ShouldNotBeNull();

        return page.Items;
    }

    private static async Task<ProblemBody> ReadProblemAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);

        problem.ShouldNotBeNull();
        problem.Status.ShouldBe((int)response.StatusCode);

        return problem;
    }

    private static void AssertNoForbiddenMembers(JsonElement element, params string[] forbidden)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    forbidden.ShouldNotContain(property.Name, $"the response must not carry '{property.Name}'");
                    AssertNoForbiddenMembers(property.Value, forbidden);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AssertNoForbiddenMembers(item, forbidden);
                }

                break;
        }
    }

    private long PublicationCount(Guid episodeId) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM publications WHERE episode_id = '{episodeId}'");

    private bool PublicationHas(Guid publicationId, string predicate) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM publications WHERE id = '{publicationId}' AND ({predicate})") == 1;

    private string PublicationXmin(Guid publicationId) =>
        SqlProbe.ScalarText(Database, $"SELECT xmin::text FROM publications WHERE id = '{publicationId}'");

    /// <summary>
    /// One live autopilot authorization of the given owner and series, written straight into 02's table: the
    /// handler only reads it.
    /// </summary>
    private async Task<Guid> SeedAuthorizationAsync(Guid userId, Guid seriesId, bool revoked = false)
    {
        var id = Guid.CreateVersion7();
        var stamp = $"'{Start.ToString("O", CultureInfo.InvariantCulture)}'::timestamptz";

        await Database.ExecuteAsync(
            "INSERT INTO series_autopilot_authorizations (id, user_id, series_id, version, text_version, "
            + "text_hash, topic, cadence, privacy_status, accepted_at, revoked_at, created_at, updated_at) "
            + $"VALUES ('{id}', '{userId}', '{seriesId}', 1, 'pc.v1', '{new string('a', 64)}', "
            + $"'ghost', 'daily', 'public', {stamp}, {(revoked ? stamp : "NULL")}, {stamp}, {stamp})",
            Ct);

        return id;
    }

    /// <summary>The same command the route sends, with the two members only an in-process caller can carry (C1).</summary>
    private static CreatePublicationCommand Command(
        World world,
        DateTimeOffset? publishAt,
        Guid authorizationId) =>
        new(
            world.EpisodeId,
            world.ConnectionId,
            DefaultTitle,
            DefaultDescription,
            DefaultTags,
            "public",
            publishAt,
            authorizationId);

    /// <summary>Attaches a counting interceptor to the read context of a host.</summary>
    private static WebApplicationFactory<TEntryPoint> CountingHost<TEntryPoint>(
        WebApplicationFactory<TEntryPoint> factory,
        CountingDbCommandInterceptor interceptor)
        where TEntryPoint : class =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<ReadDbContext>((_, options) => options.AddInterceptors(interceptor))));

    /// <summary>An authenticated client for a host that <see cref="ApiFactory.ClientFor"/> cannot reach.</summary>
    private static HttpClient AuthenticatedClient<TEntryPoint>(
        WebApplicationFactory<TEntryPoint> host,
        Guid userId)
        where TEntryPoint : class
    {
        var client = host.CreateClient();

        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userId.ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, TestAuthHandler.RoleClaimValue(PlatformRole.User));

        return client;
    }

    private static string Repeat(string value, int count) => string.Concat(Enumerable.Repeat(value, count));

    /// <summary>One seeded owner: the episode the create targets and the connection it would upload through.</summary>
    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId, Guid ConnectionId);

    /// <summary>The <c>publication</c> resource of D6, in the member order the task file fixes.</summary>
    private sealed record PublicationBody(
        Guid Id,
        Guid EpisodeId,
        ChannelBody Channel,
        string Status,
        string Title,
        string Description,
        List<string> Tags,
        string Privacy,
        bool PreAudit,
        string? RequestedPrivacy,
        DateTimeOffset? PublishAt,
        DateTimeOffset? VetoEndsAt,
        DateTimeOffset? ConsentAt,
        string? ConsentSource,
        string? ExternalPostId,
        string? ExternalUrl,
        string? ErrorCode,
        DateTimeOffset? NextAttemptAt,
        string Etag);

    private sealed record ChannelBody(Guid Id, string? DisplayName);

    private sealed record PublicationListBody(List<PublicationBody> Items);

    private sealed record ProblemBody(string Code, int Status, Dictionary<string, string[]>? Errors);

    /// <summary>The snake_case status wire values, for the raw <c>UPDATE</c> the route's sibling tasks will own.</summary>
    private static class PublicationStatusCatalogue
    {
        internal static string Wire(PublicationStatus status) => status switch
        {
            PublicationStatus.AwaitingConsent => "awaiting_consent",
            PublicationStatus.Scheduled => "scheduled",
            PublicationStatus.Uploading => "uploading",
            PublicationStatus.Processing => "processing",
            PublicationStatus.Published => "published",
            PublicationStatus.Failed => "failed",
            PublicationStatus.Cancelled => "cancelled",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "No wire value declared."),
        };
    }
}
