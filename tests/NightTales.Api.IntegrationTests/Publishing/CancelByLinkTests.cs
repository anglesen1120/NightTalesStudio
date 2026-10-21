using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Auth;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Security;
using NightTales.Domain.Common;
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
/// <c>POST /api/v1/publications/cancel-by-link</c> against the real Api and a real PostgreSQL 17 (06.2.2-T2
/// §Design "Cancel-by-link", 06 README D7/F17/YR8/D17/BD14): the anonymous link cancels exactly one
/// <c>awaiting_consent</c> publication, spends its single-use token, leaves one <c>publication_updated.v1</c> row and
/// one <c>publishing.cancelled</c> audit row with the actor <c>system:link</c> — while a spent, expired, foreign or
/// wrong-state attempt answers one indistinguishable 400 and writes nothing, and the route answers 404 with the flag
/// off and 429 over the <c>auth</c> policy without a bearer token anywhere.
/// </summary>
/// <remarks>
/// <para>
/// <b>Anonymous by construction.</b> Every request goes through <see cref="ApiFactory.AnonymousClient"/>, so no test
/// can pass by accident through a bearer: the token in the body is the route's only authentication.
/// </para>
/// <para>
/// <b>Scoped assertions.</b> The one database is shared by the class, so every outbox, audit and token assertion is
/// filtered by the publication id this test seeded (the full-gate isolation trap).
/// </para>
/// <para>
/// <b>"Byte identical" after stripping the trace id.</b> Every problem body carries its own <c>traceId</c>
/// (<c>ProblemDetailsWriter</c>), so the four failure bodies are compared as documents with that member removed.
/// </para>
/// </remarks>
/// <param name="database">The class database, injected by xUnit as the class fixture.</param>
[Trait("Category", "Integration")]
public sealed class CancelByLinkTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The anonymous cancel-by-link route of 06.2.2-T2.</summary>
    private const string Route = "/api/v1/publications/cancel-by-link";

    private const string FeatureKey = "youtube_publish";

    /// <summary>The outbox type of D13, <c>{ ids, status }</c> only.</summary>
    private const string OutboxEventType = "publication_updated.v1";

    /// <summary>D17's audit action, shared with the authenticated cancel.</summary>
    private const string AuditAction = "publishing.cancelled";

    /// <summary>A word only the seeded title carries, so the logs and the audit row can be proven free of it.</summary>
    private const string TitleCanary = "Tựa đề canary 06.2.2-T2";

    /// <summary>A word only the seeded description carries.</summary>
    private const string DescriptionCanary = "Mô tả canary 06.2.2-T2";

    /// <summary>A word only the seeded channel title carries.</summary>
    private const string ChannelCanary = "Kênh canary 06.2.2-T2";

    private static readonly DateTimeOffset Start = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task CancelByLink_204_CancelsAndSpendsTheToken()
    {
        // Acceptance #6: one 204, one row cancelled, one token spent, one event, one audit row — anonymously.
        var world = await SeedAsync(PublicationStatus.AwaitingConsent);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.AnonymousClient();
        var token = await MintTokenAsync(factory, world.UserId, world.PublicationId);

        using var response = await PostAsync(client, world.PublicationId, token);
        var text = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, text);
        text.ShouldBeEmpty("a 204 carries no body");

        // Acceptance #8: both headers on the success answer, because the call names a publication.
        response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        response.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"]);

        PublicationStatusOf(world.PublicationId).ShouldBe("cancelled");

        IsTokenSpent(world.UserId, world.PublicationId).ShouldBeTrue(
            "a committed cancellation spends the token in its own transaction");

        var payloads = OutboxPayloads(world.PublicationId);
        payloads.Count.ShouldBe(1, "D13: exactly one publication_updated.v1 row per cancellation");

        using (var json = JsonDocument.Parse(payloads[0]))
        {
            json.RootElement.GetProperty("status").GetString().ShouldBe("cancelled");
            json.RootElement.GetProperty("publicationId").GetGuid().ShouldBe(world.PublicationId);
        }

        var data = AuditData(world.PublicationId).ShouldHaveSingleItem();

        AuditHas(world.PublicationId, "actor_type = 'system'").ShouldBeTrue("the link is the system actor");
        AuditHas(world.PublicationId, "actor_user_id IS NULL").ShouldBeTrue("no principal made this request");
        AuditHas(world.PublicationId, $"subject_user_id = '{world.UserId}'").ShouldBeTrue(
            "the row is about the token's owner");
        AuditHas(world.PublicationId, "category = 'publishing' AND visibility = 'user'").ShouldBeTrue();
        AuditHas(world.PublicationId, "entity_type = 'publication'").ShouldBeTrue();

        using (var json = JsonDocument.Parse(data))
        {
            var root = json.RootElement;

            root.EnumerateObject().Select(property => property.Name).ShouldBe(
                ["publicationId", "episodeId", "previousStatus", "actor"],
                ignoreOrder: true);

            root.GetProperty("publicationId").GetString().ShouldBe(world.PublicationId.ToString("D"));
            root.GetProperty("episodeId").GetString().ShouldBe(world.EpisodeId.ToString("D"));
            root.GetProperty("previousStatus").GetString().ShouldBe("awaiting_consent");
            root.GetProperty("actor").GetString().ShouldBe("system:link");
        }
    }

    [Fact]
    public async Task SecondCall_IsTheGenericRefusal_AndWritesNothing()
    {
        // The token is the single-use guard: a replay is a refusal, never the sibling route's idempotent 204, and it
        // adds no second event and no second audit row.
        var world = await SeedAsync(PublicationStatus.AwaitingConsent);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.AnonymousClient();
        var token = await MintTokenAsync(factory, world.UserId, world.PublicationId);

        using (var first = await PostAsync(client, world.PublicationId, token))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.NoContent, await first.Content.ReadAsStringAsync(Ct));
        }

        using (var replay = await PostAsync(client, world.PublicationId, token))
        {
            var problem = await ReadProblemAsync(replay, HttpStatusCode.BadRequest);

            problem.Code.ShouldBe("cancel_token_invalid");
            replay.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
            replay.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"]);
        }

        PublicationStatusOf(world.PublicationId).ShouldBe("cancelled");
        OutboxPayloads(world.PublicationId).Count.ShouldBe(1, "the refusal adds no event");
        AuditData(world.PublicationId).Count.ShouldBe(1, "the refusal adds no audit row");
    }

    [Fact]
    public async Task ExpiredToken_400_AndTheTokenStaysUnspent()
    {
        // The consume's UPDATE … WHERE expires_at > now matches no row, so the answer is the generic refusal. The
        // unit of work then rolls the whole request back, which is why used_at stays NULL: a refusal changes nothing.
        var world = await SeedAsync(PublicationStatus.AwaitingConsent);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.AnonymousClient();
        var token = await MintTokenAsync(factory, world.UserId, world.PublicationId, Start.AddMinutes(-1));

        using var response = await PostAsync(client, world.PublicationId, token);

        (await ReadProblemAsync(response, HttpStatusCode.BadRequest)).Code.ShouldBe("cancel_token_invalid");

        PublicationStatusOf(world.PublicationId).ShouldBe("awaiting_consent");
        IsTokenSpent(world.UserId, world.PublicationId).ShouldBeFalse("a refused request consumes nothing");
    }

    [Fact]
    public async Task TokenOfAnotherPublication_400()
    {
        // YR8: the token is scoped to one publication. A wrong pairing is the same refusal, and neither row moves.
        var mine = await SeedAsync(PublicationStatus.AwaitingConsent);
        var other = await SeedAsync(PublicationStatus.AwaitingConsent);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.AnonymousClient();
        var token = await MintTokenAsync(factory, mine.UserId, mine.PublicationId);

        using var response = await PostAsync(client, other.PublicationId, token);

        (await ReadProblemAsync(response, HttpStatusCode.BadRequest)).Code.ShouldBe("cancel_token_invalid");

        PublicationStatusOf(mine.PublicationId).ShouldBe("awaiting_consent");
        PublicationStatusOf(other.PublicationId).ShouldBe("awaiting_consent");
        OutboxPayloads(mine.PublicationId).ShouldBeEmpty();
        OutboxPayloads(other.PublicationId).ShouldBeEmpty();
        AuditData(mine.PublicationId).ShouldBeEmpty();
        IsTokenSpent(mine.UserId, mine.PublicationId).ShouldBeFalse("the mismatch rolls back with the request");
    }

    [Theory]
    [InlineData(PublicationStatus.Scheduled)]
    [InlineData(PublicationStatus.Uploading)]
    [InlineData(PublicationStatus.Published)]
    [InlineData(PublicationStatus.Cancelled)]
    public async Task OutsideTheWindow_400(PublicationStatus status)
    {
        // Only the veto window is cancellable from the link; every other state is the same refusal, never a 409.
        var world = await SeedAsync(status);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.AnonymousClient();
        var token = await MintTokenAsync(factory, world.UserId, world.PublicationId);

        using var response = await PostAsync(client, world.PublicationId, token);

        (await ReadProblemAsync(response, HttpStatusCode.BadRequest)).Code.ShouldBe("cancel_token_invalid");

        PublicationStatusOf(world.PublicationId).ShouldBe(PublicationBuilder.Wire(status));
        OutboxPayloads(world.PublicationId).ShouldBeEmpty();
        AuditData(world.PublicationId).ShouldBeEmpty();
    }

    [Fact]
    public async Task FourFailureBodies_AreIdentical()
    {
        // Acceptance #7: the probe-visible body cannot separate an unknown token from an expired one, a mismatch or a
        // wrong state. traceId is per request by design, so the comparison is on the document without it.
        var world = await SeedAsync(PublicationStatus.AwaitingConsent);
        var scheduled = await SeedAsync(PublicationStatus.Scheduled);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.AnonymousClient();

        var expired = await MintTokenAsync(factory, world.UserId, world.PublicationId, Start.AddMinutes(-1));
        var warm = await MintTokenAsync(factory, scheduled.UserId, scheduled.PublicationId);

        var bodies = new List<string>();

        using (var unknown = await PostAsync(client, world.PublicationId, "not-a-token"))
        {
            bodies.Add(await FailureBodyAsync(unknown));
        }

        using (var stale = await PostAsync(client, world.PublicationId, expired))
        {
            bodies.Add(await FailureBodyAsync(stale));
        }

        using (var mismatch = await PostAsync(client, world.PublicationId, warm))
        {
            bodies.Add(await FailureBodyAsync(mismatch));
        }

        using (var wrongState = await PostAsync(client, scheduled.PublicationId, warm))
        {
            bodies.Add(await FailureBodyAsync(wrongState));
        }

        bodies.Distinct(StringComparer.Ordinal).Count().ShouldBe(
            1,
            "one code, one title, one status and no metadata for every failure: " + string.Join(" | ", bodies));
    }

    [Fact]
    public async Task FlagOff_404_FeatureDisabled_WithoutAnyBearer()
    {
        // Acceptance #9: the flag answers like a route that does not exist, before the handler runs, and the route is
        // reachable with no bearer at all — the anonymous client carries none.
        var world = await SeedAsync(PublicationStatus.AwaitingConsent);
        await SetFlagAsync(false);
        using var factory = NewFactory();
        using var client = factory.AnonymousClient();
        var token = await MintTokenAsync(factory, world.UserId, world.PublicationId);

        using var response = await PostAsync(client, world.PublicationId, token);

        (await ReadProblemAsync(response, HttpStatusCode.NotFound)).Code.ShouldBe("feature_disabled");

        // The header filter runs before the feature guard, so even this 404 carries the route's two headers.
        response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        response.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"]);

        PublicationStatusOf(world.PublicationId).ShouldBe("awaiting_consent");
        IsTokenSpent(world.UserId, world.PublicationId).ShouldBeFalse("a disabled feature spends nothing");
    }

    [Fact]
    public async Task RateLimited_429_AfterTheAuthPolicyBurst()
    {
        // Acceptance #8: the `auth` policy (BD14) is on the route, so an anonymous burst is refused with the shared
        // 429 body. The window is shrunk to two permits so the burst is small; the host is per test, so the limiter's
        // partitions are isolated. A 429 is written by the rate-limiting middleware before the endpoint runs, so
        // both headers come from the host's hardening headers (BD12): the referrer policy it states on every answer
        // and the `no-store` it stamps on a refusal.
        var world = await SeedAsync(PublicationStatus.AwaitingConsent);
        await SetFlagAsync(true);
        using var factory = NewFactory(authPermitLimit: 2);
        using var client = factory.AnonymousClient();

        using (var first = await PostAsync(client, world.PublicationId, "not-a-token"))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var second = await PostAsync(client, world.PublicationId, "not-a-token"))
        {
            second.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using var third = await PostAsync(client, world.PublicationId, "not-a-token");

        var problem = await ReadProblemAsync(third, HttpStatusCode.TooManyRequests);

        problem.Code.ShouldBe("rate_limited");
        third.Headers.RetryAfter.ShouldNotBeNull("the rejection advertises the window it counted");
        third.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"]);
        third.Headers.CacheControl.ShouldNotBeNull("a refusal is never storable").NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task NoSecretsInTheLogsOrTheAuditRow()
    {
        // Acceptance #10 (D17): no token, no address, no title, no description and no channel name reaches a log line
        // or the audit row — the row is ids and statuses only.
        var world = await SeedAsync(
            PublicationStatus.AwaitingConsent,
            title: TitleCanary,
            description: DescriptionCanary,
            displayName: ChannelCanary);
        await SetFlagAsync(true);
        var sink = new TestLogSink();
        using var factory = NewFactory(sink: sink);
        using var client = factory.AnonymousClient();
        var token = await MintTokenAsync(factory, world.UserId, world.PublicationId);

        using (var response = await PostAsync(client, world.PublicationId, token))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
        }

        var log = sink.AllText();

        log.ShouldNotBeNullOrWhiteSpace("the sink must have seen this host's lines, or the search proves nothing");
        log.ShouldNotContain(token, Case.Sensitive);
        log.ShouldNotContain(TitleCanary, Case.Sensitive);
        log.ShouldNotContain(DescriptionCanary, Case.Sensitive);
        log.ShouldNotContain(ChannelCanary, Case.Sensitive);

        var data = AuditData(world.PublicationId).ShouldHaveSingleItem();

        data.ShouldNotContain(token, Case.Sensitive);
        data.ShouldNotContain(TitleCanary, Case.Sensitive);
        data.ShouldNotContain(DescriptionCanary, Case.Sensitive);
        data.ShouldNotContain(ChannelCanary, Case.Sensitive);
    }

    /// <summary>
    /// Mints one subject-scoped <c>publication_cancel</c> token through the real store, exactly as the send-time
    /// minter does (06 CR-6/YR8).
    /// </summary>
    /// <param name="factory">The started host, whose services own the token store.</param>
    /// <param name="userId">The publication's owner.</param>
    /// <param name="publicationId">The publication the token is scoped to.</param>
    /// <param name="expiresAt">The expiry; an hour after <see cref="Start"/> unless a test wants an expired one.</param>
    private static async Task<string> MintTokenAsync(
        ApiFactory factory,
        Guid userId,
        Guid publicationId,
        DateTimeOffset? expiresAt = null)
    {
        var issued = await AuthTestFlow.IssueSubjectActionTokenAsync(
            factory.Services,
            userId,
            UserActionKind.PublicationCancel,
            publicationId,
            expiresAt ?? Start.AddHours(1),
            Ct);

        issued.IsError.ShouldBeFalse($"the token must mint: {issued.FirstError}");

        return issued.Value;
    }

    /// <summary><c>POST /api/v1/publications/cancel-by-link</c> with the given body.</summary>
    /// <param name="client">The anonymous client.</param>
    /// <param name="publicationId">The publication the body names.</param>
    /// <param name="token">The token the body carries.</param>
    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Guid publicationId, string token) =>
        client.PostAsJsonAsync(Route, new { publicationId, token }, Ct);

    /// <summary>Asserts the status and the problem body, and returns the body; a non-problem body fails here.</summary>
    /// <param name="response">The answer.</param>
    /// <param name="expected">The status the route documents for this failure.</param>
    private static async Task<ProblemBody> ReadProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(expected, text);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);

        problem.ShouldNotBeNull();
        problem.Status.ShouldBe((int)expected);

        return problem;
    }

    /// <summary>
    /// The failure body as a canonical document with <c>traceId</c> removed — the trace id is per request, so it is
    /// the one member two identical refusals cannot share.
    /// </summary>
    /// <param name="response">A 400 answer.</param>
    private static async Task<string> FailureBodyAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, text);

        var node = JsonNode.Parse(text);

        node.ShouldNotBeNull();
        node.AsObject().Remove("traceId");

        return node.ToJsonString();
    }

    /// <summary>One owner with a series, an episode, an active connection and a publication.</summary>
    /// <param name="status">The publication's lifecycle state.</param>
    /// <param name="title">The publication's title, or the builder's default.</param>
    /// <param name="description">The publication's description, or the builder's default.</param>
    /// <param name="displayName">The connection's channel title, or a fresh one per call.</param>
    private async Task<World> SeedAsync(
        PublicationStatus status,
        string? title = null,
        string? description = null,
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
            status: EpisodeStatus.Ready,
            createdAt: Start,
            ct: Ct);

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
            status: status,
            title: title,
            description: description,
            createdAt: Start,
            ct: Ct);

        return new World(userId, episode.Id, publicationId, display);
    }

    /// <summary>Writes the one flag row these routes read; a missing row is the 404 case.</summary>
    /// <param name="enabled">Whether <c>youtube_publish</c> is on.</param>
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
    /// The host. The flag row is already in place before the first <c>Services</c> read, because the flag cache is
    /// built when the host starts.
    /// </summary>
    /// <param name="sink">A log sink, for the no-secrets test.</param>
    /// <param name="authPermitLimit">The <c>auth</c> policy's permits, for the burst test.</param>
    private ApiFactory NewFactory(TestLogSink? sink = null, int? authPermitLimit = null)
    {
        var factory = new ApiFactory(Database, new FakeClock(Start));

        if (authPermitLimit is { } limit)
        {
            factory = factory.WithSetting("RateLimiting:Auth:PermitLimit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return sink is null
            ? factory
            : factory.WithServices(services => services.AddSingleton<Serilog.Core.ILogEventSink>(sink));
    }

    /// <summary>Whether a token of this owner and publication has been spent.</summary>
    /// <param name="userId">The token's owner.</param>
    /// <param name="publicationId">The token's subject.</param>
    private bool IsTokenSpent(Guid userId, Guid publicationId) =>
        SqlProbe.Count(
            Database,
            "SELECT count(*) FROM user_action_tokens WHERE kind = 'publication_cancel' "
            + $"AND user_id = '{userId}' AND subject_id = '{publicationId}' AND used_at IS NOT NULL") == 1;

    private string PublicationStatusOf(Guid publicationId) =>
        SqlProbe.ScalarText(Database, $"SELECT status FROM publications WHERE id = '{publicationId}'");

    /// <summary>Whether this cancellation's audit row satisfies <paramref name="predicate"/>.</summary>
    /// <param name="publicationId">The publication the row is about.</param>
    /// <param name="predicate">The SQL condition to check.</param>
    private bool AuditHas(Guid publicationId, string predicate) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM audit_logs WHERE action = '{AuditAction}' "
            + $"AND entity_id = '{publicationId}' AND ({predicate})") == 1;

    /// <summary>The <c>data</c> of this command's audit rows for <paramref name="publicationId"/> (D17).</summary>
    /// <param name="publicationId">The publication the rows are about.</param>
    private List<string> AuditData(Guid publicationId) =>
        SqlProbe.QueryStrings(
            Database,
            $"SELECT data::text FROM audit_logs WHERE action = '{AuditAction}' "
            + $"AND entity_id = '{publicationId}'");

    /// <summary>The payloads of the D13 row this cancellation raised for <paramref name="publicationId"/>.</summary>
    /// <param name="publicationId">The publication the rows are about.</param>
    private List<string> OutboxPayloads(Guid publicationId) =>
        SqlProbe.QueryStrings(
            Database,
            $"SELECT payload::text FROM outbox_messages WHERE type = '{OutboxEventType}' "
            + $"AND payload->>'publicationId' = '{publicationId}'");

    /// <summary>One seeded owner: the publication, the episode it belongs to and the connection's channel title.</summary>
    /// <param name="UserId">The owner.</param>
    /// <param name="EpisodeId">The episode the publication belongs to.</param>
    /// <param name="PublicationId">The publication the token is scoped to.</param>
    /// <param name="DisplayName">The channel title.</param>
    private sealed record World(Guid UserId, Guid EpisodeId, Guid PublicationId, string DisplayName);

    /// <summary>The problem body of this Api (09 README D7), as the failure tests read it.</summary>
    /// <param name="Code">The stable machine code.</param>
    /// <param name="Status">The HTTP status the body repeats.</param>
    /// <param name="Errors">The per-field errors, absent on these refusals.</param>
    private sealed record ProblemBody(string? Code, int Status, Dictionary<string, string[]>? Errors);
}
