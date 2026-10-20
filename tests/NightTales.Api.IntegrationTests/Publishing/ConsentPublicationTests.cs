using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Common;
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
/// <c>POST /api/v1/publications/{id}/consent</c> and <c>GET /api/v1/publications/consent-text</c> against the real
/// Api and a real PostgreSQL 17 (06.2.2-T1, 06 README D7/D11/D13/D17/D18, 10 CR-17): the consent records the three
/// evidence fields under the <c>If-Match</c> precondition, moves the publication and the episode to
/// <c>scheduled</c> in one transaction, and leaves exactly one outbox row and one audit row of ids only — while a
/// repeat, a stale tag, an outdated text, a foreign row, a flag-off host and a not-ready episode all write nothing.
/// </summary>
/// <remarks>
/// <para>
/// Every test seeds its own owner through the TestKit builders, so the one database a class shares never makes two
/// tests see each other's rows (D13). The publication starts in <c>awaiting_consent</c> — the state the consent route
/// transitions — through <see cref="PublicationBuilder"/>, because the seed is about the consent and not about the
/// create route 06.2.1-T1 already pins.
/// </para>
/// <para>
/// The <c>youtube_publish</c> flag row and the clock are in place before <c>ApiFactory.Services</c> is first touched,
/// because the host caches both. The clock is a <see cref="FakeClock"/> pinned to <see cref="Start"/>, which is what
/// makes <c>consent_at</c> assertable to the microsecond.
/// </para>
/// <para>
/// No dedicated test asserts that the handler never schedules an upload: the Api host registers the throwing
/// <c>IJobScheduler</c>, so an accidental scheduling call on this path would fail every test of this class outright.
/// </para>
/// </remarks>
/// <param name="database">The class database, injected by xUnit as the class fixture.</param>
[Trait("Category", "Integration")]
public sealed class ConsentPublicationTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The publication resource group (06.2.2-T1 task §Endpoint).</summary>
    private const string PublicationsRoute = "/api/v1/publications";

    /// <summary>The consent-text route (10 CR-17).</summary>
    private const string ConsentTextRoute = PublicationsRoute + "/consent-text";

    private const string FeatureKey = "youtube_publish";

    /// <summary>The outbox type of D13, <c>{ ids, status }</c> only.</summary>
    private const string OutboxEventType = "publication_updated.v1";

    /// <summary>D17's audit action of this command.</summary>
    private const string AuditAction = "publishing.consented";

    /// <summary>The default <c>YouTube:ConsentTextVersion</c> the host serves and the route accepts (D11).</summary>
    private const string ConsentTextVersion = "pc.v1";

    private static readonly DateTimeOffset Start = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    /// <summary>The D14 wire options: camelCase members, read case-insensitively.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task IfMatch_Missing_428()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostConsentAsync(client, world.PublicationId, ifMatch: null);

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("if_match_required");

        AssertUntouched(world, "the 428 filter answers before the handler runs");
    }

    [Fact]
    public async Task IfMatch_Stale_412_CarriesCurrentETag_NoWrite()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        var current = Xmin(world.PublicationId);
        var stale = ETags.Publication(current - 1);

        using var response = await PostConsentAsync(client, world.PublicationId, ifMatch: stale);

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("publication_conflict");

        // 09.1.4-T1: the handler compares If-Match after loading the row, so the 412 can name the current tag and the
        // client retries (or reloads the changed row) without a second read.
        response.Headers.ETag.ShouldNotBeNull();
        response.Headers.ETag!.Tag.ShouldBe(ETags.Publication(current), "the 412 carries the row's current ETag");

        AssertUntouched(world, "a stale If-Match writes nothing");
    }

    [Fact]
    public async Task Consent_200_UpdatedETag_RowScheduledWithEvidenceFields()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        var before = Xmin(world.PublicationId);

        using var response = await PostConsentAsync(
            client,
            world.PublicationId,
            ifMatch: ETags.Publication(before));
        var text = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);

        var body = JsonSerializer.Deserialize<PublicationBody>(text, JsonOptions);
        body.ShouldNotBeNull();

        body.Status.ShouldBe("scheduled");
        body.EpisodeId.ShouldBe(world.EpisodeId);
        body.ConsentAt.ShouldBe(Start, "consent_at is the command's clock reading");
        body.ConsentSource.ShouldBe("user");

        // D15/06 CR-2: the tag is read after the unit of work committed, and it is the one the client sends as the
        // If-Match of its next cancel/PATCH.
        var after = Xmin(world.PublicationId);
        after.ShouldNotBe(before, "the consent write moves xmin");

        response.Headers.ETag.ShouldNotBeNull();
        response.Headers.ETag!.Tag.ShouldBe(ETags.Publication(after));
        body.Etag.ShouldBe(response.Headers.ETag.Tag, "one value in the header and in the body");

        // The four evidence fields of D7, read back from the row itself.
        PublicationHas(
                world.PublicationId,
                "status = 'scheduled' AND consent_source = 'user' "
                + "AND consent_text_version = 'pc.v1' "
                + $"AND consent_at = {Instant(Start)}")
            .ShouldBeTrue("the row carries the evidence the consent records");
    }

    [Fact]
    public async Task TextVersion_Outdated_400_NoWrite_NoAuditNoOutbox()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostConsentAsync(
            client,
            world.PublicationId,
            ifMatch: ETags.Publication(Xmin(world.PublicationId)),
            consentTextVersion: "pc.v2");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("consent_text_outdated");

        AssertUntouched(world, "the owner consented to a text nobody is showing any more (D7)");
    }

    [Fact]
    public async Task EpisodeBecomesScheduled()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        EpisodeStatusOf(world.EpisodeId).ShouldBe("ready");

        using var response = await PostConsentAsync(
            client,
            world.PublicationId,
            ifMatch: ETags.Publication(Xmin(world.PublicationId)));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        // D12/F15: ready → scheduled, in the same transaction as the publication row.
        EpisodeStatusOf(world.EpisodeId).ShouldBe("scheduled");
    }

    [Fact]
    public async Task OutboxAndAudit()
    {
        const string titleCanary = "Tựa đề canary 06.2.2";
        const string channelCanary = "Kênh canary 06.2.2";
        var world = await SeedAsync(title: titleCanary, displayName: channelCanary);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostConsentAsync(
            client,
            world.PublicationId,
            ifMatch: ETags.Publication(Xmin(world.PublicationId)));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var payloads = OutboxPayloads(world.PublicationId);
        payloads.Count.ShouldBe(1, "D13: exactly one publication_updated.v1 row per consent");

        var payload = payloads[0];
        using (var json = JsonDocument.Parse(payload))
        {
            var root = json.RootElement;

            MemberNames(root).ShouldBeSubsetOf(OutboxPayloadMembers);
            root.GetProperty("publicationId").GetGuid().ShouldBe(world.PublicationId);
            root.GetProperty("episodeId").GetGuid().ShouldBe(world.EpisodeId);
            root.GetProperty("userId").GetGuid().ShouldBe(world.UserId);
            root.GetProperty("status").GetString().ShouldBe("scheduled");
        }

        // No user text reaches the row: not the video title, not the channel's title.
        payload.ShouldNotContain(titleCanary, Case.Sensitive);
        payload.ShouldNotContain(world.DisplayName, Case.Sensitive);

        var audit = AuditData(world.UserId);
        audit.Count.ShouldBe(1, "D17: one publishing.consented row inside the same transaction");

        var data = audit[0];
        using (var auditJson = JsonDocument.Parse(data))
        {
            var root = auditJson.RootElement;

            // D17's data is evidence of consent and nothing else: the two ids, the text version and the source. An
            // extra member would be a second copy of user text in an append-only table, so the set is exact.
            MemberNames(root).ShouldBe(
                ["publicationId", "episodeId", "consentTextVersion", "source"],
                ignoreOrder: true,
                customMessage: "the audit data carries the ids, the text version and the source only");

            root.GetProperty("publicationId").GetGuid().ShouldBe(world.PublicationId);
            root.GetProperty("episodeId").GetGuid().ShouldBe(world.EpisodeId);
            root.GetProperty("consentTextVersion").GetString().ShouldBe(ConsentTextVersion);
            root.GetProperty("source").GetString().ShouldBe("user");
        }

        data.ShouldNotContain(titleCanary, Case.Sensitive);
        data.ShouldNotContain(world.DisplayName, Case.Sensitive);
    }

    [Fact]
    public async Task RepeatConsent_IsIdempotent()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        // The tag the client read before consenting: after the first write it names no current row, and a retry after a
        // lost response still carries it (D7/F04).
        var preConsentTag = ETags.Publication(Xmin(world.PublicationId));

        using var first = await PostConsentAsync(client, world.PublicationId, preConsentTag);
        var firstText = await first.Content.ReadAsStringAsync(Ct);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, firstText);

        var firstBody = JsonSerializer.Deserialize<PublicationBody>(firstText, JsonOptions);
        firstBody.ShouldNotBeNull();

        var firstTag = first.Headers.ETag!.Tag;
        var writtenXmin = Xmin(world.PublicationId);

        using var repeat = await PostConsentAsync(client, world.PublicationId, preConsentTag);
        var repeatText = await repeat.Content.ReadAsStringAsync(Ct);

        repeat.StatusCode.ShouldBe(HttpStatusCode.OK, "the lost-response retry must not fail: " + repeatText);

        var repeatBody = JsonSerializer.Deserialize<PublicationBody>(repeatText, JsonOptions);
        repeatBody.ShouldNotBeNull();

        repeat.Headers.ETag!.Tag.ShouldBe(firstTag, "the same DTO and the same ETag as the first answer");
        repeatBody.Status.ShouldBe(firstBody.Status);
        repeatBody.ConsentAt.ShouldBe(firstBody.ConsentAt);
        repeatBody.Etag.ShouldBe(firstBody.Etag);

        Xmin(world.PublicationId).ShouldBe(writtenXmin, "the repeat writes nothing at all");
        OutboxPayloads(world.PublicationId).Count.ShouldBe(1, "the repeat raises no second event");
        AuditData(world.UserId).Count.ShouldBe(1, "the repeat writes no second audit row");
    }

    [Fact]
    public async Task ForeignUser_404()
    {
        var mine = await SeedAsync();
        var stranger = await SeedAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(mine.UserId);

        using var response = await PostConsentAsync(
            client,
            stranger.PublicationId,
            ifMatch: ETags.Publication(Xmin(stranger.PublicationId)));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("not_found");

        AssertUntouched(stranger, "another owner's publication is 404, never 403 (D10)");
        AuditData(mine.UserId).ShouldBeEmpty("a refused consent writes no audit row");
    }

    [Fact]
    public async Task FlagOff_404_feature_disabled()
    {
        var world = await SeedAsync();
        await SetFlagAsync(false);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostConsentAsync(
            client,
            world.PublicationId,
            ifMatch: ETags.Publication(Xmin(world.PublicationId)));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("feature_disabled");

        AssertUntouched(world, "a disabled feature answers like a route that does not exist (BD13)");
    }

    [Fact]
    public async Task AutopilotConsented_409_publication_invalid_state()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);
        await MarkAutopilotConsentedAsync(world.PublicationId);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostConsentAsync(
            client,
            world.PublicationId,
            ifMatch: ETags.Publication(Xmin(world.PublicationId)));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("publication_invalid_state");

        PublicationHas(world.PublicationId, "status = 'scheduled' AND consent_source = 'autopilot'")
            .ShouldBeTrue("the autopilot consent is never silently merged with a user consent (D7)");
        OutboxPayloads(world.PublicationId).ShouldBeEmpty("the refused consent adds no event");
        AuditData(world.UserId).ShouldBeEmpty("the refused consent adds no audit row");
    }

    [Fact]
    public async Task EpisodeNotReady_409_episode_invalid_state_NoWrite()
    {
        var world = await SeedAsync(episodeStatus: EpisodeStatus.StoryApproved);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostConsentAsync(
            client,
            world.PublicationId,
            ifMatch: ETags.Publication(Xmin(world.PublicationId)));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("episode_invalid_state");

        // The episode transition fails the whole command: the consent the handler had already applied to the tracked
        // row never commits (one unit of work, no `scheduled` publication without its episode).
        AssertUntouched(world, "the episode could not be scheduled, so nothing was written");
        EpisodeStatusOf(world.EpisodeId).ShouldBe("story_approved");
    }

    [Fact]
    public async Task ConsentText_vi_en_200_withCacheControl_and_BadLocale_400()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var vi = await client.GetAsync($"{ConsentTextRoute}?locale=vi", Ct);
        var viText = await vi.Content.ReadAsStringAsync(Ct);
        vi.StatusCode.ShouldBe(HttpStatusCode.OK, viText);

        var viBody = JsonSerializer.Deserialize<ConsentTextBody>(viText, JsonOptions);
        viBody.ShouldNotBeNull();
        viBody.Version.ShouldBe(ConsentTextVersion, "version is the live YouTube:ConsentTextVersion (D11)");
        viBody.Text.ShouldNotBeNullOrWhiteSpace("the embedded pc.v1 text of the locale is served as-is");
        AssertPrivateHour(vi);

        using var en = await client.GetAsync($"{ConsentTextRoute}?locale=en", Ct);
        var enText = await en.Content.ReadAsStringAsync(Ct);
        en.StatusCode.ShouldBe(HttpStatusCode.OK, enText);

        var enBody = JsonSerializer.Deserialize<ConsentTextBody>(enText, JsonOptions);
        enBody.ShouldNotBeNull();
        enBody.Version.ShouldBe(ConsentTextVersion);
        enBody.Text.ShouldNotBeNullOrWhiteSpace();
        enBody.Text.ShouldNotBe(viBody.Text, "the two locales are two different texts");
        AssertPrivateHour(en);

        using var unknown = await client.GetAsync($"{ConsentTextRoute}?locale=fr", Ct);
        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await unknown.Content.ReadAsStringAsync(Ct));

        var unknownProblem = await ReadProblemAsync(unknown);
        var unknownErrors = unknownProblem.Errors;
        unknownProblem.Code.ShouldBe("validation_failed");
        unknownErrors.ShouldNotBeNull();
        unknownErrors.ShouldContainKey("locale");

        using var missing = await client.GetAsync(ConsentTextRoute, Ct);
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await missing.Content.ReadAsStringAsync(Ct));

        var missingProblem = await ReadProblemAsync(missing);
        var missingErrors = missingProblem.Errors;
        missingProblem.Code.ShouldBe("validation_failed");
        missingErrors.ShouldNotBeNull();
        missingErrors.ShouldContainKey("locale");
    }

    [Fact]
    public async Task Anonymous_401()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.AnonymousClient();

        // Authorization is middleware: it answers before the If-Match filter and before the feature guard, so an
        // anonymous caller never learns whether the flag is on or the header is missing.
        using var consent = await PostConsentAsync(client, world.PublicationId, ifMatch: null);
        consent.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await consent.Content.ReadAsStringAsync(Ct));

        using var text = await client.GetAsync($"{ConsentTextRoute}?locale=vi", Ct);
        text.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await text.Content.ReadAsStringAsync(Ct));

        AssertUntouched(world, "an anonymous request writes nothing");
    }

    [Fact]
    public async Task NoTitleOrChannelNameInTheLogs()
    {
        const string titleCanary = "Tựa đề log canary 06.2.2";
        const string channelCanary = "Kênh log canary 06.2.2";
        var world = await SeedAsync(title: titleCanary, displayName: channelCanary);
        await SetFlagAsync(true);
        var sink = new TestLogSink();
        using var factory = NewFactory(sink: sink);
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostConsentAsync(
            client,
            world.PublicationId,
            ifMatch: ETags.Publication(Xmin(world.PublicationId)));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var log = sink.AllText();

        log.ShouldNotBeNullOrWhiteSpace("the sink must have seen this host's own lines, or the search proves nothing");
        log.ShouldNotContain(titleCanary, Case.Sensitive);
        log.ShouldNotContain(channelCanary, Case.Sensitive);
    }

    /// <summary>
    /// One owner with a series, an episode, an active connection and a publication in <c>awaiting_consent</c> — the
    /// world the consent route transitions.
    /// </summary>
    /// <param name="episodeStatus">The episode's state; <c>ready</c> is the one that may be scheduled.</param>
    /// <param name="title">The publication's title, or the builder's default.</param>
    /// <param name="displayName">The connection's channel title, or a fresh one per call.</param>
    private async Task<World> SeedAsync(
        EpisodeStatus episodeStatus = EpisodeStatus.Ready,
        string? title = null,
        string? displayName = null)
    {
        var owner = new TestCurrentUser();
        var userId = (await new UserBuilder(Database, owner).CreateAsync(ct: Ct)).Id;
        var actor = new TestCurrentUser { Id = userId };
        var clock = new FakeClock(Start);

        var series = await new SeriesBuilder(Database, actor, clock).CreateAsync(
            userId,
            title: "Chuỗi thử nghiệm",
            createdAt: Start,
            ct: Ct);

        var episode = await new EpisodeBuilder(Database, actor, clock).CreateAsync(
            userId,
            series.Id,
            brief: "Nội dung tập thử nghiệm.",
            status: episodeStatus,
            createdAt: Start,
            ct: Ct);

        // `ux_channel_connections_account` and the publication's FK need one real connection per owner.
        var display = displayName ?? $"NTS Test Channel {Guid.CreateVersion7():N}";
        var connection = await new ChannelConnectionBuilder(Database, actor, clock).CreateAsync(
            userId,
            ChannelConnectionStatus.Active,
            display,
            ct: Ct);

        var publicationId = await new PublicationBuilder(Database).CreateAsync(
            userId,
            episode.Id,
            connection.Id,
            status: PublicationStatus.AwaitingConsent,
            title: title,
            createdAt: Start,
            ct: Ct);

        return new World(userId, episode.Id, publicationId, display);
    }

    /// <summary>Writes the one flag row these routes read; a missing row is the 404 case.</summary>
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
    /// The host. The consent-text version is pinned in the configuration <em>before</em> the first
    /// <c>Services</c> read, because the options monitor and the flag cache are built when the host starts.
    /// </summary>
    private ApiFactory NewFactory(TestLogSink? sink = null)
    {
        var factory = new ApiFactory(Database, new FakeClock(Start))
            .WithSetting(
                $"{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.ConsentTextVersion)}",
                ConsentTextVersion);

        return sink is null
            ? factory
            : factory.WithServices(services => services.AddSingleton<Serilog.Core.ILogEventSink>(sink));
    }

    /// <summary><c>POST /api/v1/publications/{id}/consent</c> with the given <c>If-Match</c> and body.</summary>
    private static async Task<HttpResponseMessage> PostConsentAsync(
        HttpClient client,
        Guid publicationId,
        string? ifMatch,
        string consentTextVersion = ConsentTextVersion)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{PublicationsRoute}/{publicationId}/consent")
        {
            Content = JsonContent.Create(new { consentTextVersion }),
        };

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request, Ct);
    }

    /// <summary>Asserts the problem body and returns it; a body that is not a problem fails here.</summary>
    private static async Task<ProblemBody> ReadProblemAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);
        problem.ShouldNotBeNull();
        problem.Status.ShouldBe((int)response.StatusCode);

        return problem;
    }

    /// <summary>The <c>Cache-Control</c> of 10 CR-17: private to the caller's browser for an hour.</summary>
    private static void AssertPrivateHour(HttpResponseMessage response)
    {
        var cache = response.Headers.CacheControl;

        cache.ShouldNotBeNull("the consent text must carry a Cache-Control header");
        cache!.Private.ShouldBeTrue("a shared proxy must never keep an authenticated answer");
        cache.MaxAge.ShouldBe(TimeSpan.FromHours(1));
    }

    /// <summary>The row was not written: still awaiting consent, no evidence, no event, no audit row.</summary>
    private void AssertUntouched(World world, string reason)
    {
        PublicationHas(
                world.PublicationId,
                "status = 'awaiting_consent' AND consent_at IS NULL "
                + "AND consent_text_version IS NULL AND consent_source IS NULL")
            .ShouldBeTrue(reason);
        OutboxPayloads(world.PublicationId).ShouldBeEmpty(reason);
        AuditData(world.UserId).ShouldBeEmpty(reason);
    }

    /// <summary>The D13 payload's members: the ids and the state, plus the event record's own two.</summary>
    private static readonly string[] OutboxPayloadMembers = new[]
    {
        "publicationId", "episodeId", "userId", "status", "errorCode", "occurredAt",
    };

    private static IReadOnlyList<string> MemberNames(JsonElement element) =>
        [.. element.EnumerateObject().Select(property => property.Name)];

    /// <summary>The <c>xmin</c> of the row, as <c>ETags.Publication</c> spells it.</summary>
    private uint Xmin(Guid publicationId) =>
        uint.Parse(
            SqlProbe.ScalarText(Database, $"SELECT xmin::text FROM publications WHERE id = '{publicationId}'"),
            CultureInfo.InvariantCulture);

    /// <summary>Whether the one row satisfies <paramref name="predicate"/> — every column check goes through here.</summary>
    private bool PublicationHas(Guid publicationId, string predicate) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM publications WHERE id = '{publicationId}' AND ({predicate})") == 1;

    private string EpisodeStatusOf(Guid episodeId) =>
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{episodeId}'");

    /// <summary>The <c>data</c> of this command's audit rows for one owner (D17: ids and the text version only).</summary>
    private List<string> AuditData(Guid userId) =>
        SqlProbe.QueryStrings(
            Database,
            $"SELECT data::text FROM audit_logs WHERE action = '{AuditAction}' "
            + $"AND coalesce(subject_user_id, actor_user_id) = '{userId}'");

    /// <summary>The payloads of the D13 row this consent raised for <paramref name="publicationId"/>.</summary>
    private List<string> OutboxPayloads(Guid publicationId) =>
        SqlProbe.QueryStrings(
            Database,
            $"SELECT payload::text FROM outbox_messages WHERE type = '{OutboxEventType}' "
            + $"AND payload::text LIKE '%{publicationId}%'");

    /// <summary>
    /// Seeds the <c>scheduled</c> row of an autopilot consent (06.2.2-T3 writes this in production), which the user
    /// route must refuse rather than merge.
    /// </summary>
    private async Task MarkAutopilotConsentedAsync(Guid publicationId)
    {
        var stamp = Instant(Start);

        await Database.ExecuteAsync(
            "UPDATE publications SET status = 'scheduled', "
            + $"consent_at = {stamp}, consent_text_version = '{ConsentTextVersion}', "
            + $"consent_source = 'autopilot', updated_at = {stamp} "
            + $"WHERE id = '{publicationId}'",
            Ct);
    }

    /// <summary>An instant a raw <c>WHERE</c> can compare with, in the round-tripping format.</summary>
    private static string Instant(DateTimeOffset value) =>
        $"'{value.ToString("O", CultureInfo.InvariantCulture)}'::timestamptz";

    /// <summary>One seeded owner: the publication, the episode it belongs to and the connection's channel title.</summary>
    private sealed record World(Guid UserId, Guid EpisodeId, Guid PublicationId, string DisplayName);

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

    /// <summary>The <c>GET /publications/consent-text</c> body of 10 CR-17.</summary>
    private sealed record ConsentTextBody(string Version, string Text);

    private sealed record ProblemBody(string Code, int Status, Dictionary<string, string[]>? Errors);
}
