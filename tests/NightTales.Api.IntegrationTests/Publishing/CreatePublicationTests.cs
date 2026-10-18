using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions;
using NightTales.Application.Common;
using NightTales.Application.Features.Publishing.Commands.CreatePublication;
using NightTales.Application.Publishing;
using NightTales.Domain.Episodes;
using NightTales.Domain.Publishing;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Telemetry;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// <c>POST /api/v1/episodes/{id}/publications</c> against the real Api and a real PostgreSQL 17
/// (06.2.1-T1, 06 README D6/D12/D13/D17/D18): a ready episode with an active connection becomes an
/// <c>awaiting_consent</c> publication carrying the YouTube limits, the render's music attribution, the
/// pre-audit override, the <c>"pu-&lt;xmin&gt;"</c> ETag, one outbox row and one audit row.
/// </summary>
/// <remarks>
/// <para>
/// Every test seeds its own owner through the TestKit builders, so the one database a class shares never
/// makes two tests see each other's rows (D13). The flag row and the clock are in place before
/// <c>ApiFactory.Services</c> is first touched, because the host caches both.
/// </para>
/// <para>
/// The <c>youtube_publish</c> flag is written directly (the connect harness's precedent): the offline
/// validation of 06.1.x features is 06.1.2-T2's, and a create with no flag row at all is the
/// <c>feature_disabled</c> case.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CreatePublicationTests(TestDatabase database) : IntegrationTest(database)
{
    private const string EpisodesRoute = "/api/v1/episodes";
    private const string FeatureKey = "youtube_publish";

    /// <summary>The outbox type of D13, <c>{ ids, status }</c> only.</summary>
    private const string OutboxEventType = "publication_updated.v1";

    /// <summary>D17's audit action of this command.</summary>
    private const string AuditAction = "publishing.publication_created";

    /// <summary>D18's partial unique index: one live row per (episode, platform).</summary>
    private const string ActiveEpisodeIndex = "ux_publications_active_episode_platform";

    private const string DefaultTitle = "Tập một: chuyến tàu đêm";
    private const string DefaultDescription = "Nội dung mô tả thử nghiệm cho video YouTube.";

    private static readonly string[] DefaultTags = new[] { "đêm", "truyện", "audio" };

    /// <summary>G4's hand-computed case: (3 + 2) + 1 + 2 + 2 separators = 10 characters of tag text.</summary>
    private static readonly string[] SpacedTags = new[] { "a b", "c", "dd" };

    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task Create_ReadyEpisode_Returns201WithLocationEtagAndTheListRow()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory(auditStatus: YouTubeAuditStatuses.Approved);
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world, Body(world, privacy: "public"));
        var (body, json) = await ReadCreatedAsync(response);

        // 09.1.4-T1's ToCreatedResult: the Location is the list the row can be read back from.
        response.Headers.Location.ShouldNotBeNull();
        response.Headers.Location!.AbsolutePath.ShouldBe($"{EpisodesRoute}/{world.EpisodeId}/publications");

        var xmin = PublicationXmin(body.Id);
        uint.Parse(xmin, CultureInfo.InvariantCulture).ShouldBeGreaterThan(0u);
        response.Headers.ETag!.Tag.ShouldBe(ETags.Publication(uint.Parse(xmin, CultureInfo.InvariantCulture)));
        body.Etag.ShouldBe(response.Headers.ETag.Tag, "D15: one value in the header and in the body");

        body.EpisodeId.ShouldBe(world.EpisodeId);
        body.Channel.Id.ShouldBe(world.ConnectionId);
        body.Channel.DisplayName.ShouldBe(world.DisplayName);
        body.Status.ShouldBe("awaiting_consent");
        body.Title.ShouldBe(DefaultTitle);
        body.Description.ShouldBe(
            DefaultDescription,
            "an episode with no render job has no attribution to append (05 README:332-336)");
        body.Tags.ShouldBe(DefaultTags);
        body.Privacy.ShouldBe("public", "AuditStatus=approved keeps the submitted visibility (D12)");
        body.PreAudit.ShouldBeFalse();
        body.RequestedPrivacy.ShouldBeNull();
        body.PublishAt.ShouldBeNull();
        body.VetoEndsAt.ShouldBeNull("a manual publication carries no veto window (YR1)");
        body.ConsentAt.ShouldBeNull();
        body.ConsentSource.ShouldBeNull();
        body.ExternalPostId.ShouldBeNull();
        body.ExternalUrl.ShouldBeNull();
        body.ErrorCode.ShouldBeNull();
        body.NextAttemptAt.ShouldBeNull();

        // The 201 body is the DTO of D6 and nothing more: never the platform payload, a token or the
        // authorization id (D4, 06 CR-11).
        AssertNoForbiddenMembers(
            json,
            "platformPayload",
            "platform_payload",
            "autopilotAuthorizationId",
            "accessToken",
            "accessTokenEnc",
            "refreshToken",
            "refreshTokenEnc",
            "storageKey",
            "oauthClientId",
            "scope",
            "uploadSessionEnc");

        AssertCommittedRow(world, body.Id, status: "awaiting_consent", privacy: "public");

        // The same row comes back from the list route the Location names, ETag included.
        using var list = await client.GetAsync(response.Headers.Location, Ct);
        var items = await ReadListAsync(list);

        items.Count.ShouldBe(1);
        items[0].Id.ShouldBe(body.Id);
        items[0].Status.ShouldBe("awaiting_consent");
        items[0].Title.ShouldBe(DefaultTitle);
        items[0].Etag.ShouldBe(body.Etag);
    }

    [Fact]
    public async Task Create_WithRenderAttribution_StoresItByteForByteAfterTheDescription()
    {
        const string attribution = "Bài hát thử nghiệm — NTS (CC-BY 4.0)";
        var world = await SeedAsync(attribution: attribution);
        await SetFlagAsync(true);
        using var factory = NewFactory(auditStatus: YouTubeAuditStatuses.Approved);
        using var client = factory.ClientFor(world.UserId);

        var (body, _) = await ReadCreatedAsync(await PostAsync(client, world, Body(world, privacy: "public")));

        // C8/T9: `description + "\n\nMusic: " + attribution`, written as the render's params carry it.
        var expected = $"{DefaultDescription}\n\nMusic: {attribution}";

        body.Description.ShouldBe(expected);
        PublicationColumn(body.Id, "description").ShouldBe(expected);

        var bytes = Encoding.UTF8.GetByteCount(expected);
        PublicationHas(body.Id, $"octet_length(description) = {bytes}").ShouldBeTrue(
            "the column holds exactly those bytes, not an escaped or normalized form");

        AssertCommittedRow(world, body.Id, status: "awaiting_consent", privacy: "public");
    }

    [Fact]
    public async Task Create_NoRenderJob_StoresTheDescriptionAsSent()
    {
        var world = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        var (body, _) = await ReadCreatedAsync(await PostAsync(client, world, Body(world)));

        body.Description.ShouldBe(DefaultDescription, "no job means no attribution and no error");
        PublicationColumn(body.Id, "description").ShouldBe(DefaultDescription);
    }

    [Fact]
    public async Task Create_EmptyAttribution_IsTreatedAsAbsent()
    {
        var world = await SeedAsync(attribution: string.Empty);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        var (body, _) = await ReadCreatedAsync(await PostAsync(client, world, Body(world)));

        body.Description.ShouldBe(
            DefaultDescription,
            "an empty attribution never appends a bare `Music: ` tail (06 failure table)");
    }

    [Fact]
    public async Task Create_Description5001Bytes_Is400ErrorsDescription()
    {
        var world = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world, Body(world, description: new string('a', 5_001)));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        var problem = await ReadProblemAsync(response);

        problem.Code.ShouldBe("validation_failed");
        problem.Errors.ShouldNotBeNull();
        problem.Errors!.ShouldContainKey("description", "F08: the cap is UTF-8 bytes, reported per field");
        PublicationCount(world.EpisodeId).ShouldBe(0, "a refused create writes nothing");
    }

    [Fact]
    public async Task Create_Description4999Bytes_WithAttributionPushingItOver_Is400ErrorsDescription()
    {
        var attribution = new string('x', 10);
        var world = await SeedAsync(attribution: attribution);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        // 4,999 + "\n\nMusic: " (9) + 10 = 5,018 bytes: the cap applies after the attribution is appended.
        var description = new string('a', 4_999);

        using var response = await PostAsync(client, world, Body(world, description: description));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        var problem = await ReadProblemAsync(response);

        problem.Code.ShouldBe("validation_failed");
        problem.Errors!.ShouldContainKey("description");
        PublicationCount(world.EpisodeId).ShouldBe(0);

        // The same description with no attribution is accepted, so the refusal above is the tail, not the
        // text: the control is another owner, so it needs its own client.
        var quiet = await SeedAsync(renderJob: false);
        using var quietClient = factory.ClientFor(quiet.UserId);
        using var accepted = await PostAsync(quietClient, quiet, Body(quiet, description: description));

        accepted.StatusCode.ShouldBe(HttpStatusCode.Created, await accepted.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Create_TitleWithAngleBrackets_Is400ErrorsTitle()
    {
        var world = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world, Body(world, title: "Đêm <tàu> về"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        var problem = await ReadProblemAsync(response);

        problem.Code.ShouldBe("validation_failed");
        problem.Errors!.ShouldContainKey("title");
        PublicationCount(world.EpisodeId).ShouldBe(0);
    }

    [Fact]
    public async Task Create_SixteenTags_Is400ErrorsTags()
    {
        var world = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        var tags = Enumerable.Range(0, 16).Select(index => $"tag{index}").ToList();

        using var response = await PostAsync(client, world, Body(world, tags: tags));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        var problem = await ReadProblemAsync(response);

        problem.Code.ShouldBe("validation_failed");
        problem.Errors!.ShouldContainKey("tags", "F09: 15 is the cap");
        PublicationCount(world.EpisodeId).ShouldBe(0);
    }

    [Fact]
    public async Task Create_FifteenTagsOfThirtyChars_Is201()
    {
        var world = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        // 15 tags of 30 characters each: 450 + 14 commas = 464, under the 500 the counter allows (G4).
        var tags = Enumerable.Range(0, 15).Select(_ => new string('t', 30)).ToList();

        var (body, _) = await ReadCreatedAsync(await PostAsync(client, world, Body(world, tags: tags)));

        body.Tags.ShouldBe(tags);
        PublicationHas(
            body.Id,
            $"tags @> ARRAY['{new string('t', 30)}']::text[]").ShouldBeTrue(
            "all fifteen tags are stored in the column, as the text[] DDL declares");
    }

    [Fact]
    public void Tags_Length_CountsCommasAndImplicitQuotes()
    {
        YouTubeMetadataRules.TagsLength(SpacedTags).ShouldBe(
            10,
            "a spaced tag carries YouTube's implicit quotes, and every separator costs one character (F09)");
    }

    [Theory]
    [InlineData(119, HttpStatusCode.BadRequest)]
    [InlineData(120, HttpStatusCode.Created)]
    [InlineData(121, HttpStatusCode.Created)]
    [InlineData(null, HttpStatusCode.Created)]
    public async Task Create_PublishAtFollowsThePublishLeadTime(int? minutesAhead, HttpStatusCode expected)
    {
        var world = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        var publishAt = minutesAhead is null ? (DateTimeOffset?)null : Start.AddMinutes(minutesAhead.Value);

        using var response = await PostAsync(client, world, Body(world, publishAt: publishAt));

        response.StatusCode.ShouldBe(expected, await response.Content.ReadAsStringAsync(Ct));

        if (expected == HttpStatusCode.Created)
        {
            var (body, _) = await ReadCreatedAsync(response);

            body.PublishAt.ShouldBe(publishAt);
            PublicationCount(world.EpisodeId).ShouldBe(1);
            return;
        }

        var problem = await ReadProblemAsync(response);

        problem.Code.ShouldBe("validation_failed");
        problem.Errors!.ShouldContainKey("publishAt", "D6: the lead is 120 minutes, and exactly 120 is accepted");
        PublicationCount(world.EpisodeId).ShouldBe(0);
    }

    [Fact]
    public async Task Create_AuditPending_ForcesPrivateAndStampsTheRequestedPrivacy()
    {
        var world = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        using var factory = NewFactory(auditStatus: YouTubeAuditStatuses.Pending);
        using var client = factory.ClientFor(world.UserId);

        var (body, _) = await ReadCreatedAsync(
            await PostAsync(client, world, Body(world, privacy: "public")));

        body.Privacy.ShouldBe(
            "private",
            "D12/C4: the enforced value is what every later reader sees, never the submitted one");
        body.PreAudit.ShouldBeTrue();
        body.RequestedPrivacy.ShouldBe("public");

        PublicationHas(body.Id, "privacy = 'private'").ShouldBeTrue();
        using var payload = JsonDocument.Parse(PublicationColumn(body.Id, "platform_payload"));

        payload.RootElement.GetProperty("preAudit").GetBoolean().ShouldBeTrue();
        payload.RootElement.GetProperty("requestedPrivacy").GetString().ShouldBe("public");
    }

    [Fact]
    public async Task Create_AuditApproved_KeepsTheSubmittedPrivacyAndStampsNothing()
    {
        var world = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        using var factory = NewFactory(auditStatus: YouTubeAuditStatuses.Approved);
        using var client = factory.ClientFor(world.UserId);

        var (body, _) = await ReadCreatedAsync(
            await PostAsync(client, world, Body(world, privacy: "public")));

        body.Privacy.ShouldBe("public");
        body.PreAudit.ShouldBeFalse();
        body.RequestedPrivacy.ShouldBeNull();

        PublicationHas(body.Id, "privacy = 'public'").ShouldBeTrue();
        PublicationHas(
            body.Id,
            "(platform_payload IS NULL OR NOT (platform_payload ? 'preAudit'))").ShouldBeTrue(
            "an approved submission writes no pre-audit stamp (D12)");
    }

    [Fact]
    public async Task DuplicateCreate_Is409PublicationInvalidState_AndSucceedsAfterTheFirstIsCancelled()
    {
        var world = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        var (first, _) = await ReadCreatedAsync(await PostAsync(client, world, Body(world)));

        using var duplicate = await PostAsync(client, world, Body(world, title: "Tập hai"));

        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict, await duplicate.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(duplicate)).Code.ShouldBe(
            "publication_invalid_state",
            "the partial unique index is translated, never a 500 (06 CR-9, step 8)");
        PublicationCount(world.EpisodeId).ShouldBe(1);
        AuditData(world.UserId).Count.ShouldBe(
            1, "the loser maps the exception before the audit step, so the refusal writes no row");

        // The predicate is what lets a replacement row in: only failed/cancelled rows leave the key.
        var index = SqlProbe.QueryStrings(
                Database,
                "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'publications' "
                + $"AND indexname = '{ActiveEpisodeIndex}'")
            .ShouldHaveSingleItem();

        index.ShouldContain("UNIQUE");
        index.ShouldContain("(episode_id, platform)");
        index.ShouldContain("failed");
        index.ShouldContain("cancelled");

        // 06.2.2-T1's cancel route does not exist yet, so the row is moved the way its transition would.
        await Database.ExecuteAsync($"UPDATE publications SET status = 'cancelled' WHERE id = '{first.Id}'", Ct);

        var (second, _) = await ReadCreatedAsync(await PostAsync(client, world, Body(world)));

        second.Id.ShouldNotBe(first.Id);
        PublicationCount(world.EpisodeId).ShouldBe(2);
    }

    [Fact]
    public async Task Create_EpisodeNotReady_Is409EpisodeInvalidState()
    {
        var world = await SeedAsync(ready: false, renderJob: false);
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
    public async Task Create_ConnectionNotActive_Is409ChannelNotActive(ChannelConnectionStatus status)
    {
        var world = await SeedAsync(renderJob: false, connectionStatus: status);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world, Body(world));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("channel_not_active");
        PublicationCount(world.EpisodeId).ShouldBe(0);
    }

    /// <summary>
    /// One owner with a series, a ready episode and a live connection — the world a create needs — plus the
    /// render job whose <c>music.attribution</c> the handler appends when <paramref name="renderJob"/> is set.
    /// </summary>
    private async Task<World> SeedAsync(
        bool ready = true,
        ChannelConnectionStatus connectionStatus = ChannelConnectionStatus.Active,
        string? attribution = RenderJobBuilder.DefaultAttribution,
        bool renderJob = true,
        string? autopilotSettings = null,
        bool withMusic = true)
    {
        var owner = new TestCurrentUser();
        var userId = (await new UserBuilder(Database, owner).CreateAsync(ct: Ct)).Id;
        var actor = new TestCurrentUser { Id = userId };
        var clock = new FakeClock(Start);

        var series = await new SeriesBuilder(Database, actor, clock).CreateAsync(
            userId,
            title: "Chuỗi thử nghiệm",
            autopilotEnabled: autopilotSettings is not null,
            autopilotSettings: autopilotSettings,
            createdAt: Start,
            ct: Ct);

        var episode = await new EpisodeBuilder(Database, actor, clock).CreateAsync(
            userId,
            series.Id,
            brief: "Nội dung tập thử nghiệm.",
            status: ready ? EpisodeStatus.Ready : EpisodeStatus.StoryApproved,
            createdAt: Start,
            ct: Ct);

        var displayName = $"NTS Test Channel {Guid.CreateVersion7():N}";
        var connection = await new ChannelConnectionBuilder(Database, actor, clock).CreateAsync(
            userId,
            connectionStatus,
            displayName,
            ct: Ct);

        if (renderJob)
        {
            await new RenderJobBuilder(Database, actor, clock).CreateAsync(
                userId, episode.Id, attribution: attribution, withMusic: withMusic, ct: Ct);
        }

        return new World(userId, series.Id, episode.Id, connection.Id, displayName);
    }

    /// <summary>Writes the one flag row this route reads; a missing row is the 404 case.</summary>
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

    /// <summary>
    /// The host. The audit status and the log sink are in the configuration and the container <em>before</em>
    /// the first <c>Services</c> read, because the flag cache, the options monitor and Serilog are all built
    /// when the host starts.
    /// </summary>
    private ApiFactory NewFactory(string auditStatus = YouTubeAuditStatuses.Pending, TestLogSink? sink = null)
    {
        var factory = new ApiFactory(Database, new FakeClock(Start))
            .WithSetting($"{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.AuditStatus)}", auditStatus);

        return sink is null
            ? factory
            : factory.WithServices(services => services.AddSingleton<Serilog.Core.ILogEventSink>(sink));
    }

    /// <summary>The request body of the route, by its D14 wire names — <c>autopilotAuthorizationId</c> is not one.</summary>
    private static object Body(
        World world,
        string? title = null,
        string? description = null,
        IReadOnlyList<string>? tags = null,
        string privacy = "private",
        DateTimeOffset? publishAt = null,
        Guid? channelConnectionId = null)
    {
        var tagsValue = tags ?? DefaultTags;

        return new
        {
            channelConnectionId = channelConnectionId ?? world.ConnectionId,
            title = title ?? DefaultTitle,
            description = description ?? DefaultDescription,
            tags = tagsValue,
            privacy,
            publishAt,
        };
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, World world, object body) =>
        PostAsync(client, world.EpisodeId, body);

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Guid episodeId, object body) =>
        client.PostAsJsonAsync($"{EpisodesRoute}/{episodeId}/publications", body, Ct);

    /// <summary>Asserts the 201 and returns the parsed body with the raw JSON beside it.</summary>
    private static async Task<(PublicationBody Body, JsonElement Json)> ReadCreatedAsync(HttpResponseMessage response)
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

    /// <summary>The D14 wire options: camelCase members, read case-insensitively.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task<ProblemBody> ReadProblemAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);
        problem.ShouldNotBeNull();
        problem.Status.ShouldBe((int)response.StatusCode);

        return problem;
    }

    /// <summary>The committed row, column by column, as the D6 aggregate left it.</summary>
    private void AssertCommittedRow(World world, Guid publicationId, string status, string privacy) =>
        PublicationHas(
            publicationId,
            $"episode_id = '{world.EpisodeId}' AND user_id = '{world.UserId}' "
            + $"AND channel_connection_id = '{world.ConnectionId}' AND platform = 'youtube' "
            + $"AND status = '{status}' AND privacy = '{privacy}' "
            + "AND contains_synthetic_media AND NOT self_declared_made_for_kids "
            + "AND consent_at IS NULL AND consent_text_version IS NULL AND consent_source IS NULL "
            + "AND external_post_id IS NULL AND external_url IS NULL AND error_code IS NULL")
            .ShouldBeTrue("the committed row is the aggregate in its first state, never a default-filled row");

    private static IReadOnlyList<string> MemberNames(JsonElement element) =>
        [.. element.EnumerateObject().Select(property => property.Name)];

    private static void AssertNoForbiddenMembers(JsonElement element, params string[] forbidden)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    forbidden.ShouldNotContain(
                        property.Name,
                        $"the response must not carry '{property.Name}'");
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

    [Fact]
    public async Task Create_ForeignEpisode_Is404NotFound()
    {
        var mine = await SeedAsync(renderJob: false);
        var stranger = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(mine.UserId);

        // The stranger's episode with the caller's own connection: the episode read is the first step, so
        // the answer is one 404 and never a 403 or a validation error (D10).
        using var response = await PostAsync(client, stranger.EpisodeId, Body(mine));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("not_found");
        PublicationCount(stranger.EpisodeId).ShouldBe(0);
    }

    [Fact]
    public async Task Create_ForeignConnection_Is404NotFound()
    {
        var mine = await SeedAsync(renderJob: false);
        var stranger = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(mine.UserId);

        // The caller's own ready episode and another owner's connection: the tenant filter on the
        // connection read answers the same 404 (D10).
        using var response = await PostAsync(
            client,
            mine.EpisodeId,
            Body(mine, channelConnectionId: stranger.ConnectionId));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("not_found");
        PublicationCount(mine.EpisodeId).ShouldBe(0);
    }

    [Fact]
    public async Task Create_FlagOff_Is404FeatureDisabled()
    {
        var world = await SeedAsync(renderJob: false);
        await SetFlagAsync(false);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostAsync(client, world, Body(world));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("feature_disabled");
        PublicationCount(world.EpisodeId).ShouldBe(0);
    }

    /// <summary>The D13 payload's members: the ids and the state, plus the event record's own two.</summary>
    private static readonly string[] OutboxPayloadMembers = new[]
    {
        "publicationId", "episodeId", "userId", "status", "errorCode", "occurredAt",
    };

    [Fact]
    public async Task Create_WritesOneOutboxRowAndOneAuditRowWithIdsOnly()
    {
        const string titleCanary = "Tựa đề canary 06.2.1";
        const string descriptionCanary = "Canary mô tả 06.2.1.";
        var world = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        var (body, _) = await ReadCreatedAsync(
            await PostAsync(client, world, Body(world, title: titleCanary, description: descriptionCanary)));

        var payloads = OutboxPayloads(body.Id);
        payloads.Count.ShouldBe(1, "D13: exactly one publication_updated.v1 row per create");

        var payload = payloads[0];

        using (var json = JsonDocument.Parse(payload))
        {
            var root = json.RootElement;

            MemberNames(root).ShouldBeSubsetOf(OutboxPayloadMembers);
            root.GetProperty("publicationId").GetGuid().ShouldBe(body.Id);
            root.GetProperty("episodeId").GetGuid().ShouldBe(world.EpisodeId);
            root.GetProperty("userId").GetGuid().ShouldBe(world.UserId);
            root.GetProperty("status").GetString().ShouldBe("awaiting_consent");
        }

        // No user text reaches the row: not the title, not the description, not the channel's title.
        payload.ShouldNotContain(titleCanary, Case.Sensitive);
        payload.ShouldNotContain(descriptionCanary, Case.Sensitive);
        payload.ShouldNotContain(world.DisplayName, Case.Sensitive);

        var audit = AuditData(world.UserId);

        audit.Count.ShouldBe(1, "D17: one publishing.publication_created row inside the same transaction");
        audit[0].ShouldNotContain(titleCanary, Case.Sensitive);
        audit[0].ShouldNotContain(descriptionCanary, Case.Sensitive);
        audit[0].ShouldNotContain(world.DisplayName, Case.Sensitive);

        // The projection holds ids only: every value of the row's `data` is a UUID.
        SqlProbe.Count(
                Database,
                "SELECT count(*) FROM audit_logs log, jsonb_each_text(log.data) entry "
                + $"WHERE log.action = '{AuditAction}' AND coalesce(log.subject_user_id, log.actor_user_id) = '{world.UserId}' "
                + "AND entry.value !~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'")
            .ShouldBe(0, "the audit data carries ids only, never a title or a privacy string");
    }

    [Fact]
    public async Task Create_WritesNoTitleDescriptionOrChannelNameToTheLogs()
    {
        const string titleCanary = "Tựa đề log canary";
        const string descriptionCanary = "Mô tả log canary.";
        var world = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        var sink = new TestLogSink();
        using var factory = NewFactory(sink: sink);
        using var client = factory.ClientFor(world.UserId);

        var created = await ReadCreatedAsync(
            await PostAsync(client, world, Body(world, title: titleCanary, description: descriptionCanary)));

        var log = sink.AllText();

        created.Body.Id.ShouldNotBe(Guid.Empty);
        log.ShouldNotBeNullOrWhiteSpace("the sink must have seen this host's own lines, or the search proves nothing");
        log.ShouldNotContain(titleCanary, Case.Sensitive);
        log.ShouldNotContain(descriptionCanary, Case.Sensitive);
        log.ShouldNotContain(world.DisplayName, Case.Sensitive);
    }

    [Fact]
    public async Task Create_TwoConcurrentRequests_OneCreatedOneConflictAndOneRow()
    {
        var world = await SeedAsync(renderJob: false);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var first = factory.ClientFor(world.UserId);
        using var second = factory.ClientFor(world.UserId);
        var start = new TaskCompletionSource();

        async Task<HttpResponseMessage> CreateAsync(HttpClient client, string marker)
        {
            await start.Task;

            return await PostAsync(client, world, Body(world, title: $"{DefaultTitle} {marker}"));
        }

        var one = CreateAsync(first, "a");
        var two = CreateAsync(second, "b");
        start.SetResult();

        var responses = await Task.WhenAll(one, two);

        try
        {
            var statuses = responses.Select(response => response.StatusCode).ToList();

            statuses.Count(status => status == HttpStatusCode.Created).ShouldBe(
                1, $"the partial unique index is the arbiter, not a lock: {string.Join(",", statuses)}");
            statuses.Count(status => status == HttpStatusCode.Conflict).ShouldBe(1);

            var loser = responses.Single(response => response.StatusCode == HttpStatusCode.Conflict);

            (await ReadProblemAsync(loser)).Code.ShouldBe("publication_invalid_state");
            PublicationCount(world.EpisodeId).ShouldBe(1, "one row survives, never two");
            AuditData(world.UserId).Count.ShouldBe(1, "the loser's refusal writes no audit row");
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    /// <summary>The 02.8.3 default veto window floor: YR1 clamps <c>veto_window_hours</c> to 2..48.</summary>
    private const string VetoWindowTwoHours = "{\"veto_window_hours\": 2}";

    [Fact]
    public async Task Create_AutopilotShortenedVetoBelowTheMinimum_Is400ErrorsPublishAtAndWritesNoRow()
    {
        var world = await SeedAsync(renderJob: false, autopilotSettings: VetoWindowTwoHours);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var scope = factory.Services.CreateScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(world.UserId);
        var authorizationId = await SeedAuthorizationAsync(world);

        // YR1 on a late video: min(now + 2h, publishAt - 120min) collapses to now + 1min, which is below
        // MinVetoWindowHours (2h) — 02.8.3 records a missed slot instead of publishing immediately.
        var sent = await scope.ServiceProvider.GetRequiredService<ISender>().Send(
            Command(world, publishAt: Start.AddMinutes(121), authorizationId: authorizationId),
            Ct);

        sent.IsError.ShouldBeTrue("the veto refused it, so nothing may slip through to consent");
        sent.FirstError.Code.ShouldBe("validation_failed");
        sent.FirstError.Metadata.ShouldNotBeNull();
        sent.FirstError.Metadata!["field"]!.ToString().ShouldBe("publishAt");
        PublicationCount(world.EpisodeId).ShouldBe(0, "no row, no veto notice and no upload (YR1)");
    }

    [Fact]
    public async Task Create_AutopilotVetoWindow_IsTheEarlierOfTheTwoInstants()
    {
        var world = await SeedAsync(renderJob: false, autopilotSettings: VetoWindowTwoHours);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var scope = factory.Services.CreateScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(world.UserId);
        var authorizationId = await SeedAuthorizationAsync(world);

        // publishAt is five hours out, so the series window (now + 2h) is earlier than the instant the
        // publish would land: the veto is that window, and it is not below the minimum.
        var sent = await scope.ServiceProvider.GetRequiredService<ISender>().Send(
            Command(world, publishAt: Start.AddHours(5), authorizationId: authorizationId),
            Ct);

        if (sent.IsError)
        {
            throw new InvalidOperationException(
                "CreatePublicationCommand failed: "
                + string.Join(", ", sent.Errors.Select(error => error.Code)));
        }

        var id = sent.Value.Id;

        id.ShouldNotBe(Guid.Empty);
        PublicationColumn(id, "status").ShouldBe("awaiting_consent");
        PublicationHas(id, $"veto_ends_at = {Instant(Start.AddHours(2))}").ShouldBeTrue(
            "YR1: min(now + 2h, publishAt - 120min) is the window 02.8.3's authorisation asked for");
        PublicationHas(id, $"autopilot_authorization_id = '{authorizationId}'").ShouldBeTrue();
    }

    [Fact]
    public async Task Create_AutopilotWithRevokedAuthorization_Is400ErrorsAutopilotAuthorizationIdAndWritesNoRow()
    {
        var world = await SeedAsync(renderJob: false, autopilotSettings: VetoWindowTwoHours);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var scope = factory.Services.CreateScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(world.UserId);
        var authorizationId = await SeedAuthorizationAsync(world, revoked: true);

        // The veto window itself is legal here, so the only refusal left is the revoked authorisation.
        var sent = await scope.ServiceProvider.GetRequiredService<ISender>().Send(
            Command(world, publishAt: Start.AddHours(5), authorizationId: authorizationId),
            Ct);

        sent.IsError.ShouldBeTrue("02.8.3's own check is authoritative; this is the defence in depth");
        sent.FirstError.Code.ShouldBe("validation_failed");
        sent.FirstError.Metadata.ShouldNotBeNull();
        sent.FirstError.Metadata!["field"]!.ToString().ShouldBe("autopilotAuthorizationId");
        PublicationCount(world.EpisodeId).ShouldBe(0);
    }

    /// <summary>Rows of <c>publications</c> for one episode: 0 is the refusal's proof.</summary>
    private long PublicationCount(Guid episodeId) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM publications WHERE episode_id = '{episodeId}'");

    private string PublicationColumn(Guid publicationId, string column) =>
        SqlProbe.ScalarText(Database, $"SELECT {column} FROM publications WHERE id = '{publicationId}'");

    /// <summary>Whether the one row satisfies <paramref name="predicate"/> — every column check goes through here.</summary>
    private bool PublicationHas(Guid publicationId, string predicate) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM publications WHERE id = '{publicationId}' AND ({predicate})") == 1;

    private string PublicationXmin(Guid publicationId) =>
        SqlProbe.ScalarText(Database, $"SELECT xmin::text FROM publications WHERE id = '{publicationId}'");

    /// <summary>The <c>data</c> of this command's audit rows for one owner (D17: ids only).</summary>
    private List<string> AuditData(Guid userId) =>
        SqlProbe.QueryStrings(
            Database,
            $"SELECT data::text FROM audit_logs WHERE action = '{AuditAction}' "
            + $"AND coalesce(subject_user_id, actor_user_id) = '{userId}'");

    /// <summary>The payloads of the D13 row this create raised for <paramref name="publicationId"/>.</summary>
    private List<string> OutboxPayloads(Guid publicationId) =>
        SqlProbe.QueryStrings(
            Database,
            $"SELECT payload::text FROM outbox_messages WHERE type = '{OutboxEventType}' "
            + $"AND payload::text LIKE '%{publicationId}%'");

    /// <summary>An instant a raw <c>WHERE</c> can compare with, in the round-tripping format.</summary>
    private static string Instant(DateTimeOffset value) =>
        $"'{value.ToString("O", CultureInfo.InvariantCulture)}'::timestamptz";

    /// <summary>
    /// One live (or revoked) autopilot authorization of the series, written straight into 02's table: the
    /// handler only reads it, and 02.8.3-T2 owns every write on that path.
    /// </summary>
    private async Task<Guid> SeedAuthorizationAsync(World world, bool revoked = false)
    {
        var id = Guid.CreateVersion7();
        var stamp = Instant(Start);

        await Database.ExecuteAsync(
            "INSERT INTO series_autopilot_authorizations (id, user_id, series_id, version, text_version, "
            + "text_hash, topic, cadence, privacy_status, accepted_at, revoked_at, created_at, updated_at) "
            + $"VALUES ('{id}', '{world.UserId}', '{world.SeriesId}', 1, 'pc.v1', '{new string('a', 64)}', "
            + $"'ghost', 'daily', 'public', {stamp}, {(revoked ? stamp : "NULL")}, {stamp}, {stamp})",
            Ct);

        return id;
    }

    /// <summary>
    /// The same command the route sends, with the two members only an in-process caller can carry (C1: 02.8.3
    /// is the caller) — there is no HTTP body for them.
    /// </summary>
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

    /// <summary>One seeded owner: the episode the create targets and the connection it would upload through.</summary>
    private sealed record World(
        Guid UserId,
        Guid SeriesId,
        Guid EpisodeId,
        Guid ConnectionId,
        string DisplayName);

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

    private sealed record ChannelBody(Guid Id, string DisplayName);

    private sealed record PublicationListBody(List<PublicationBody> Items);

    private sealed record ProblemBody(string Code, int Status, Dictionary<string, string[]>? Errors);
}
