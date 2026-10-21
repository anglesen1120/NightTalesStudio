using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// QA pass over <c>POST /api/v1/publications/{id}/consent</c> and <c>GET /api/v1/publications/consent-text</c>
/// (06.2.2-T1, 06 README D7/D11/D17, 10 CR-17) against the real Api and a real PostgreSQL 17: the negative,
/// boundary and exploratory cases the task file's §Tests table does not reach — the validator's 32-character edge,
/// every malformed or weak <c>If-Match</c> shape, the non-consentable row states, the idempotent no-op reached with a
/// foreign owner's id, two consents racing for one row, the locale boundary of the text route, and a live
/// <c>YouTube:ConsentTextVersion</c> bump.
/// </summary>
/// <remarks>
/// Additive tests only: the task's own cases stay pinned by <see cref="ConsentPublicationTests"/> and
/// <c>ConsentPublicationHandlerTests</c>. Every test seeds its own owner through the TestKit builders, so the one
/// database a class shares never makes two tests see each other's rows.
/// </remarks>
/// <param name="database">The class database, injected by xUnit as the class fixture.</param>
[Trait("Category", "Integration")]
public sealed class ConsentPublicationQaTests(TestDatabase database) : IntegrationTest(database)
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
    public async Task ConsentTextVersion_33Characters_400_validation_failed()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        // One character past the validator's frozen maximum: a body that could never name a version.
        using var response = await PostConsentAsync(
            client,
            world.PublicationId,
            ifMatch: ETags.Publication(Xmin(world.PublicationId)),
            consentTextVersion: new string('p', 33));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));

        var problem = await ReadProblemAsync(response);
        var errors = problem.Errors;
        problem.Code.ShouldBe("validation_failed");
        errors.ShouldNotBeNull();
        errors.ShouldContainKey("consentTextVersion");

        AssertUntouched(world, "an over-long version is the validator's 400, not the domain's");
    }

    [Fact]
    public async Task ConsentTextVersion_32Characters_IsTheValidatorBoundaryAndTheDomainRefuses()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        // Exactly the maximum length: the shape check admits it, so the refusal must be the domain's
        // consent_text_outdated (D7) and not the validator's 400 — the two checks must not overlap.
        using var response = await PostConsentAsync(
            client,
            world.PublicationId,
            ifMatch: ETags.Publication(Xmin(world.PublicationId)),
            consentTextVersion: new string('p', 32));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("consent_text_outdated");

        AssertUntouched(world, "a version nobody is showing writes nothing");
    }

    [Fact]
    public async Task ConsentTextVersion_EmptyOrWhitespace_400_validation_failed()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        foreach (var value in new[] { string.Empty, " " })
        {
            using var response = await PostConsentAsync(
                client,
                world.PublicationId,
                ifMatch: ETags.Publication(Xmin(world.PublicationId)),
                consentTextVersion: value);

            var text = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, text);

            var problem = await ReadProblemAsync(response);
            var errors = problem.Errors;
            problem.Code.ShouldBe("validation_failed", $"the value was '{value}'");
            errors.ShouldNotBeNull();
            errors.ShouldContainKey("consentTextVersion");
        }

        AssertUntouched(world, "a blank version never reaches the aggregate");
    }

    [Fact]
    public async Task ConsentTextVersion_CaseVariant_400_consent_text_outdated()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        // The comparison is ordinal (D7): an upper-case rendering of the same token is a different string.
        using var response = await PostConsentAsync(
            client,
            world.PublicationId,
            ifMatch: ETags.Publication(Xmin(world.PublicationId)),
            consentTextVersion: "PC.V1");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("consent_text_outdated");

        AssertUntouched(world, "the case variant is not the version the host holds");
    }

    [Fact]
    public async Task Body_WithoutTheVersionMember_400_validation_failed()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostRawAsync(
            client,
            world.PublicationId,
            ETags.Publication(Xmin(world.PublicationId)),
            "{}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));

        var problem = await ReadProblemAsync(response);
        var errors = problem.Errors;
        problem.Code.ShouldBe("validation_failed");
        errors.ShouldNotBeNull();
        errors.ShouldContainKey("consentTextVersion");

        AssertUntouched(world, "a body without the echoed version cannot consent");
    }

    [Fact]
    public async Task IfMatch_WeakValidator_412_CarriesCurrentETag()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        var current = Xmin(world.PublicationId);

        // 09.1.4-T1's decision: a weak validator never matches, because weak validation is not enough for a
        // lost-update check.
        using var response = await PostConsentAsync(
            client,
            world.PublicationId,
            ifMatch: "W/" + ETags.Publication(current));

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("publication_conflict");

        response.Headers.ETag.ShouldNotBeNull();
        response.Headers.ETag!.Tag.ShouldBe(ETags.Publication(current));

        AssertUntouched(world, "a weak validator writes nothing");
    }

    [Fact]
    public async Task IfMatch_MalformedShapes_412()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        // An unquoted value, a quoted value that names no tag, a list of two tags and the star: none of them is
        // the row's tag, and D15's decision is that only the one exact tag the client read matches.
        foreach (var value in new[] { "pu-1", "\"pu-0\"", "\"pu-1\", \"pu-2\"", "*" })
        {
            using var response = await PostConsentAsync(client, world.PublicationId, ifMatch: value);

            response.StatusCode.ShouldBe(
                HttpStatusCode.PreconditionFailed,
                $"If-Match '{value}' -> {await response.Content.ReadAsStringAsync(Ct)}");
            (await ReadProblemAsync(response)).Code.ShouldBe("publication_conflict");
        }

        AssertUntouched(world, "no malformed validator may write");
    }

    [Fact]
    public async Task IfMatch_BlankHeader_428_if_match_required()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        // Present but blank: the route filter refuses it exactly like an absent header, before the handler runs.
        using var response = await PostConsentAsync(client, world.PublicationId, ifMatch: string.Empty);

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("if_match_required");

        AssertUntouched(world, "a blank If-Match answers 428 and writes nothing");
    }

    [Fact]
    public async Task Consent_OnCancelledRow_409_publication_invalid_state()
    {
        await AssertNonConsentableStateAsync(PublicationStatus.Cancelled);
    }

    [Fact]
    public async Task Consent_OnUploadingRow_409_publication_invalid_state()
    {
        await AssertNonConsentableStateAsync(PublicationStatus.Uploading);
    }

    [Fact]
    public async Task Consent_OnPublishedRow_409_publication_invalid_state()
    {
        await AssertNonConsentableStateAsync(PublicationStatus.Published);
    }

    [Fact]
    public async Task RepeatOfAnotherOwnersScheduledRow_404_NotDisclosed()
    {
        var owner = await SeedAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var ownerClient = factory.ClientFor(owner.UserId);

        // The owner consents once, so the row is exactly the state the idempotent no-op answers for.
        var preConsent = ETags.Publication(Xmin(owner.PublicationId));

        using var first = await PostConsentAsync(ownerClient, owner.PublicationId, preConsent);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(Ct));

        var consentedXmin = Xmin(owner.PublicationId);
        var outboxBefore = OutboxPayloads(owner.PublicationId).Count;
        var auditBefore = AuditData(owner.UserId).Count;

        // A stranger replays the very request that would be a success no-op for the owner: the tenant filter must
        // answer the same 404 an unknown id gets, before the no-op branch can disclose anything.
        var strangerId = (await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: Ct)).Id;
        using var strangerClient = factory.ClientFor(strangerId);

        using var repeat = await PostConsentAsync(strangerClient, owner.PublicationId, preConsent);

        repeat.StatusCode.ShouldBe(HttpStatusCode.NotFound, await repeat.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(repeat)).Code.ShouldBe("not_found");

        repeat.Headers.ETag.ShouldBeNull("a 404 must hand a client nothing to cache");
        repeat.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        // Nothing about the row leaked through the 404 and nothing was written on either side.
        var body = await repeat.Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain(owner.DisplayName, Case.Sensitive);

        Xmin(owner.PublicationId).ShouldBe(consentedXmin);
        OutboxPayloads(owner.PublicationId).Count.ShouldBe(outboxBefore);
        AuditData(owner.UserId).Count.ShouldBe(auditBefore);
        AuditData(strangerId).ShouldBeEmpty("a refused consent writes no audit row for the caller");
    }

    [Fact]
    public async Task UnknownPublicationId_404()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostConsentAsync(
            client,
            Guid.CreateVersion7(),
            ifMatch: ETags.Publication(1));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("not_found");
        AuditData(world.UserId).ShouldBeEmpty("a refused consent writes no audit row");
    }

    [Fact]
    public async Task RepeatWithAnotherVersion_AfterUserConsent_409_publication_invalid_state()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var first = await PostConsentAsync(
            client,
            world.PublicationId,
            ETags.Publication(Xmin(world.PublicationId)));
        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(Ct));

        // IsUserConsentedAs is deliberately narrow: a different version is not "already consented", so the request
        // falls through to the aggregate, which refuses the now non-awaiting row with its 409.
        using var repeat = await PostConsentAsync(
            client,
            world.PublicationId,
            ETags.Publication(Xmin(world.PublicationId)),
            consentTextVersion: "pc.v0");

        repeat.StatusCode.ShouldBe(HttpStatusCode.Conflict, await repeat.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(repeat)).Code.ShouldBe("publication_invalid_state");

        PublicationHas(world.PublicationId, "consent_text_version = '" + ConsentTextVersion + "'")
            .ShouldBeTrue("the first consent's evidence stands unchanged");
        OutboxPayloads(world.PublicationId).Count.ShouldBe(1);
        AuditData(world.UserId).Count.ShouldBe(1);
    }

    [Fact]
    public async Task ConcurrentDoubleConsent_WritesOneOutboxAndOneAuditRow()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var first = factory.ClientFor(world.UserId);
        using var second = factory.ClientFor(world.UserId);

        var tag = ETags.Publication(Xmin(world.PublicationId));

        // Two sessions replay the same lost-response retry against one row at the same time: the xmin token (or
        // the no-op branch) makes exactly one write, and the loser is a documented 200/409, never a 500.
        var responses = await Task.WhenAll(
            PostConsentAsync(first, world.PublicationId, tag),
            PostConsentAsync(second, world.PublicationId, tag));

        try
        {
            var codes = new List<(HttpStatusCode Status, string Code)>();
            foreach (var response in responses)
            {
                var text = await response.Content.ReadAsStringAsync(Ct);
                response.StatusCode.ShouldNotBe(
                    HttpStatusCode.InternalServerError,
                    "a lost race is never an unhandled 500: " + text);

                var problem = response.StatusCode == HttpStatusCode.OK ? null : await ReadProblemAsync(response);
                codes.Add((response.StatusCode, problem?.Code ?? string.Empty));
            }

            codes.ShouldContain(
                entry => entry.Status == HttpStatusCode.OK,
                "one of the two requests must consent the row: " + string.Join(", ", codes));

            codes.ShouldAllBe(entry =>
                entry.Status == HttpStatusCode.OK || entry.Code == "concurrency_conflict");

            PublicationHas(
                    world.PublicationId,
                    "status = 'scheduled' AND consent_source = 'user' "
                    + "AND consent_text_version = '" + ConsentTextVersion + "'")
                .ShouldBeTrue("the winner's evidence is the only consent on the row");

            EpisodeStatusOf(world.EpisodeId).ShouldBe("scheduled");

            OutboxPayloads(world.PublicationId).Count.ShouldBe(
                1,
                "D13: one publication_updated.v1 row, whatever the race did");
            AuditData(world.UserId).Count.ShouldBe(
                1,
                "the duplicate write adds no second audit row (09 README D5)");
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
    public async Task ConsentText_LocaleBoundary_400ForEveryNonWireValue()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        var tooLong = new string('v', 512);

        foreach (var locale in new[] { "VI", "EN", "vi-VN", "en-US", "../pc.v1.vi", "../../appsettings", "vi ", tooLong })
        {
            using var response = await client.GetAsync(
                $"{ConsentTextRoute}?locale={Uri.EscapeDataString(locale)}",
                Ct);

            var text = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"locale '{locale}': {text}");

            var problem = await ReadProblemAsync(response);
            var errors = problem.Errors;
            problem.Code.ShouldBe("validation_failed", $"locale '{locale}'");
            errors.ShouldNotBeNull();
            errors.ShouldContainKey("locale");
        }

        // The empty value is a failure too: defaulting it would hide a picker bug behind the evidence the owner
        // consents to.
        using var empty = await client.GetAsync($"{ConsentTextRoute}?locale=", Ct);
        empty.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await empty.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task ConsentTextVersion_BumpedLive_AcceptsTheNewValueAndRefusesTheOld()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory("pc.v2");
        using var client = factory.ClientFor(world.UserId);

        // D11: the version is read from the live option on both routes, so a bump needs no client release and the
        // text route can never advertise a version the command refuses.
        using var text = await client.GetAsync($"{ConsentTextRoute}?locale=vi", Ct);
        var textBody = await text.Content.ReadAsStringAsync(Ct);
        text.StatusCode.ShouldBe(HttpStatusCode.OK, textBody);

        var dto = JsonSerializer.Deserialize<ConsentTextBody>(textBody, JsonOptions);
        dto.ShouldNotBeNull();
        dto.Version.ShouldBe("pc.v2", "the route echoes the live option, never a compiled-in constant");
        dto.Text.ShouldNotBeNullOrWhiteSpace();

        using var outdated = await PostConsentAsync(
            client,
            world.PublicationId,
            ETags.Publication(Xmin(world.PublicationId)),
            consentTextVersion: "pc.v1");

        outdated.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await outdated.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(outdated)).Code.ShouldBe("consent_text_outdated");
        AssertUntouched(world, "the previous version is no longer the one the host holds");

        using var current = await PostConsentAsync(
            client,
            world.PublicationId,
            ETags.Publication(Xmin(world.PublicationId)),
            consentTextVersion: "pc.v2");

        var currentBody = await current.Content.ReadAsStringAsync(Ct);
        current.StatusCode.ShouldBe(HttpStatusCode.OK, currentBody);

        PublicationHas(
                world.PublicationId,
                "status = 'scheduled' AND consent_source = 'user' AND consent_text_version = 'pc.v2'")
            .ShouldBeTrue("the row records the version the bumped host accepted");

        var audit = AuditData(world.UserId);
        audit.Count.ShouldBe(1);

        // jsonb re-formats keys and spacing, so the row is parsed, not substring-matched.
        using var auditJson = JsonDocument.Parse(audit[0]);
        auditJson.RootElement.GetProperty("consentTextVersion").GetString()
            .ShouldBe("pc.v2", "the audit row records the version the host accepted");
    }

    [Fact]
    public async Task EpisodeInAPublishedState_RefusesAndRollsBackEverything()
    {
        var world = await SeedAsync(episodeStatus: EpisodeStatus.Published);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostConsentAsync(
            client,
            world.PublicationId,
            ETags.Publication(Xmin(world.PublicationId)));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("episode_invalid_state");

        // The episode transition fails the whole unit of work: no scheduled publication survives it.
        AssertUntouched(world, "the episode could not be scheduled, so nothing was written");
        EpisodeStatusOf(world.EpisodeId).ShouldBe("published");
    }

    [Fact]
    public async Task AuditRow_IsTheSelfServiceIdsOnlyShape()
    {
        var world = await SeedAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostConsentAsync(
            client,
            world.PublicationId,
            ETags.Publication(Xmin(world.PublicationId)));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        // D17: the row is about the caller (self-service), carries the publication as its entity and nothing the
        // owner typed.
        var rows = SqlProbe.QueryStrings(
            Database,
            "SELECT actor_user_id::text || '|' || coalesce(subject_user_id::text, '') || '|' || actor_type "
            + "|| '|' || category || '|' || visibility || '|' || coalesce(entity_type, '') || '|' "
            + "|| coalesce(entity_id::text, '') FROM audit_logs "
            + $"WHERE action = '{AuditAction}' AND actor_user_id = '{world.UserId}'");

        rows.Count.ShouldBe(1);

        var columns = rows[0].Split('|');
        columns.Length.ShouldBe(7);

        columns[0].ShouldBe(world.UserId.ToString("D", CultureInfo.InvariantCulture));
        columns[1].ShouldBe(
            world.UserId.ToString("D", CultureInfo.InvariantCulture),
            "a projection with no subject is the caller's own account");
        columns[2].ShouldBe("user");
        columns[3].ShouldBe("publishing");
        columns[4].ShouldBe("user");
        columns[5].ShouldBe("publication");
        columns[6].ShouldBe(world.PublicationId.ToString("D", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// One publication in a state the consent must refuse: the route answers its documented 409 and writes nothing.
    /// </summary>
    /// <param name="status">The state the seeded row is in.</param>
    private async Task AssertNonConsentableStateAsync(PublicationStatus status)
    {
        var world = await SeedAsync(publicationStatus: status);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PostConsentAsync(
            client,
            world.PublicationId,
            ETags.Publication(Xmin(world.PublicationId)));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("publication_invalid_state");

        PublicationHas(
                world.PublicationId,
                "status = '" + PublicationBuilder.Wire(status) + "' AND consent_at IS NULL "
                + "AND consent_text_version IS NULL AND consent_source IS NULL")
            .ShouldBeTrue($"a '{PublicationBuilder.Wire(status)}' row is not consentable");

        OutboxPayloads(world.PublicationId).ShouldBeEmpty("the refusal raises no event");
        AuditData(world.UserId).ShouldBeEmpty("the refusal writes no audit row");
        EpisodeStatusOf(world.EpisodeId).ShouldBe("ready", "the refused consent never moved the episode");
    }

    /// <summary>
    /// One owner with a series, an episode and an active connection, the episode's publication in
    /// <paramref name="publicationStatus"/> — the world the consent route judges.
    /// </summary>
    /// <param name="episodeStatus">The episode's state; <c>ready</c> is the one that may be scheduled.</param>
    /// <param name="publicationStatus">The publication's state; <c>awaiting_consent</c> is the consentable one.</param>
    private async Task<World> SeedAsync(
        EpisodeStatus episodeStatus = EpisodeStatus.Ready,
        PublicationStatus publicationStatus = PublicationStatus.AwaitingConsent)
    {
        var owner = new TestCurrentUser();
        var userId = (await new UserBuilder(Database, owner).CreateAsync(ct: Ct)).Id;
        var actor = new TestCurrentUser { Id = userId };
        var clock = new FakeClock(Start);

        var series = await new SeriesBuilder(Database, actor, clock).CreateAsync(
            userId,
            title: "Chuỗi thử nghiệm QA",
            createdAt: Start,
            ct: Ct);

        var episode = await new EpisodeBuilder(Database, actor, clock).CreateAsync(
            userId,
            series.Id,
            brief: "Nội dung tập thử nghiệm QA.",
            status: episodeStatus,
            createdAt: Start,
            ct: Ct);

        var displayName = $"NTS QA Channel {Guid.CreateVersion7():N}";
        var connection = await new ChannelConnectionBuilder(Database, actor, clock).CreateAsync(
            userId,
            ChannelConnectionStatus.Active,
            displayName,
            ct: Ct);

        var publicationId = await new PublicationBuilder(Database).CreateAsync(
            userId,
            episode.Id,
            connection.Id,
            status: publicationStatus,
            publishedAt: publicationStatus == PublicationStatus.Published ? Start : null,
            createdAt: Start,
            ct: Ct);

        return new World(userId, episode.Id, publicationId, displayName);
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
    private ApiFactory NewFactory(string consentTextVersion = ConsentTextVersion) =>
        new ApiFactory(Database, new FakeClock(Start))
            .WithSetting(
                $"{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.ConsentTextVersion)}",
                consentTextVersion);

    /// <summary><c>POST /api/v1/publications/{id}/consent</c> with the given <c>If-Match</c> and version.</summary>
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

    /// <summary><c>POST …/consent</c> with a literal JSON body, for the shapes a typed body cannot express.</summary>
    private static async Task<HttpResponseMessage> PostRawAsync(
        HttpClient client,
        Guid publicationId,
        string ifMatch,
        string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{PublicationsRoute}/{publicationId}/consent")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);

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

    /// <summary>One seeded owner: the publication, the episode it belongs to and the connection's channel title.</summary>
    private sealed record World(Guid UserId, Guid EpisodeId, Guid PublicationId, string DisplayName);

    /// <summary>The <c>GET /publications/consent-text</c> body of 10 CR-17.</summary>
    private sealed record ConsentTextBody(string Version, string Text);

    private sealed record ProblemBody(string Code, int Status, Dictionary<string, string[]>? Errors);
}
