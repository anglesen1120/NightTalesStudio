using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// Independent QA probes of <c>POST /api/v1/publications/cancel-by-link</c> (06.2.2-T2 acceptance checks 6-9):
/// the refusals that must not spend a live token, the parallel double click that must cancel exactly once, the
/// cross-tenant token that must not reach another owner's row, and the malformed bodies that must never answer a
/// 5xx.
/// </summary>
/// <remarks>
/// Written by a QA pass that did not write the implementation. Every database assertion is scoped to the rows this
/// class seeded, because the fixture's one database is shared by the whole run.
/// </remarks>
/// <param name="database">The class database, injected by xUnit as the class fixture.</param>
[Trait("Category", "Integration")]
public sealed class CancelByLinkQaTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The anonymous cancel-by-link route of 06.2.2-T2.</summary>
    private const string Route = "/api/v1/publications/cancel-by-link";

    private const string FeatureKey = "youtube_publish";

    private const string OutboxEventType = "publication_updated.v1";

    private const string AuditAction = "publishing.cancelled";

    private static readonly DateTimeOffset Start = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnotherPublicationOfTheSameOwner_RefusesWithoutSpendingTheToken_AndTheRealLinkStillWorks()
    {
        // QA probe (a): the consume happens before the subject comparison, so the only thing that keeps the token
        // alive is the unit of work's rollback. A refusal must leave `used_at` NULL and the publication untouched.
        var owner = await SeedOwnerAsync(publications: 2);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.AnonymousClient();

        var (first, second) = (owner.Publications[0], owner.Publications[1]);
        var token = await MintTokenAsync(factory, owner.UserId, first);

        using (var refusal = await PostAsync(client, second, token))
        {
            (await ReadProblemAsync(refusal, HttpStatusCode.BadRequest)).Code.ShouldBe("cancel_token_invalid");
        }

        IsTokenSpent(owner.UserId, first).ShouldBeFalse(
            "a refused request rolls the consume back: the token is the owner's until a cancellation commits");
        PublicationStatusOf(first).ShouldBe("awaiting_consent");
        PublicationStatusOf(second).ShouldBe("awaiting_consent");
        OutboxPayloads(first).ShouldBeEmpty();
        AuditData(first).ShouldBeEmpty();

        // The same link, presented with the id it actually names, must still work.
        using (var accepted = await PostAsync(client, first, token))
        {
            accepted.StatusCode.ShouldBe(HttpStatusCode.NoContent, await accepted.Content.ReadAsStringAsync(Ct));
        }

        PublicationStatusOf(first).ShouldBe("cancelled");
        IsTokenSpent(owner.UserId, first).ShouldBeTrue();
        PublicationStatusOf(second).ShouldBe("awaiting_consent", "the row the body named is never touched");
    }

    [Fact]
    public async Task TokenOfAnotherKind_IsTheSameGenericRefusal_AndStaysUsableForItsOwnFlow()
    {
        // QA probe (a2): a password-reset token of the same user answers the identical 400 and is not spent by it —
        // the contract filters on `kind`, and the rollback leaves the row alone.
        var owner = await SeedOwnerAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.AnonymousClient();
        var publicationId = owner.Publications[0];

        var resetToken = await AuthTestFlow.IssueActionTokenAsync(
            factory.Services,
            owner.UserId,
            UserActionKind.PasswordReset,
            Ct);

        resetToken.IsError.ShouldBeFalse($"the reset token must mint: {resetToken.FirstError}");

        string? refusalBody;
        string? unknownTokenBody;

        using (var refusal = await PostAsync(client, publicationId, resetToken.Value))
        {
            refusalBody = await FailureBodyAsync(refusal);
        }

        using (var unknown = await PostAsync(client, publicationId, "definitely-not-a-token"))
        {
            unknownTokenBody = await FailureBodyAsync(unknown);
        }

        refusalBody.ShouldBe(unknownTokenBody, "a token of another kind is indistinguishable from an unknown one");

        PublicationStatusOf(publicationId).ShouldBe("awaiting_consent");

        var consumed = await AuthTestFlow.ConsumeActionTokenAsync(
            factory.Services,
            resetToken.Value,
            UserActionKind.PasswordReset,
            Ct);

        consumed.IsError.ShouldBeFalse(
            "the refused cancel-by-link request must not have spent a password-reset token");
    }

    [Fact]
    public async Task ReplayAfterSuccess_IsTheGenericRefusalAndWritesNoSecondEventOrAudit()
    {
        // QA probe (b): the committed cancellation spent the token, so a replay is a refusal, not a second write.
        var owner = await SeedOwnerAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.AnonymousClient();
        var publicationId = owner.Publications[0];
        var token = await MintTokenAsync(factory, owner.UserId, publicationId);

        using (var first = await PostAsync(client, publicationId, token))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.NoContent, await first.Content.ReadAsStringAsync(Ct));
        }

        using (var replay = await PostAsync(client, publicationId, token))
        {
            (await ReadProblemAsync(replay, HttpStatusCode.BadRequest)).Code.ShouldBe("cancel_token_invalid");
        }

        PublicationStatusOf(publicationId).ShouldBe("cancelled");
        TokenRows(owner.UserId, publicationId).ShouldBe(1);
        IsTokenSpent(owner.UserId, publicationId).ShouldBeTrue();
        OutboxPayloads(publicationId).Count.ShouldBe(1, "one cancellation raises one publication_updated.v1");
        AuditData(publicationId).Count.ShouldBe(1, "one cancellation writes one publishing.cancelled row");
    }

    [Fact]
    public async Task TwoConcurrentCalls_YieldExactlyOne204_OneGeneric400_AndOneEventOneAudit()
    {
        // QA probe (c): the double click of the SPA. Both requests block on the single token row; the loser's
        // `UPDATE … WHERE used_at IS NULL` re-reads the committed winner and finds nothing to spend.
        var owner = await SeedOwnerAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.AnonymousClient();
        var publicationId = owner.Publications[0];
        var token = await MintTokenAsync(factory, owner.UserId, publicationId);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<HttpResponseMessage> CallAsync()
        {
            await gate.Task;

            return await PostAsync(client, publicationId, token);
        }

        var first = CallAsync();
        var second = CallAsync();

        gate.SetResult();

        using var firstResponse = await first;
        using var secondResponse = await second;

        var statuses = new[] { firstResponse.StatusCode, secondResponse.StatusCode };

        var diagnostic = string.Join(
            " | ",
            $"statuses={firstResponse.StatusCode}/{secondResponse.StatusCode}",
            $"tokenRows={TokenRows(owner.UserId, publicationId)}",
            $"spent={IsTokenSpent(owner.UserId, publicationId)}",
            $"outbox={OutboxPayloads(publicationId).Count}",
            $"audit={AuditData(publicationId).Count}",
            $"bodies=[{await firstResponse.Content.ReadAsStringAsync(Ct)}][{await secondResponse.Content.ReadAsStringAsync(Ct)}]",
            $"tokenRowsSql={string.Join(",", SqlProbe.QueryStrings(Database, "SELECT kind || ':' || id || ':' || coalesce(used_at::text, 'null') FROM user_action_tokens WHERE subject_id = '" + publicationId + "'"))}");

        statuses.Count(status => status == HttpStatusCode.NoContent).ShouldBe(1, diagnostic);
        statuses.Count(status => status == HttpStatusCode.BadRequest).ShouldBe(1, diagnostic);

        PublicationStatusOf(publicationId).ShouldBe("cancelled");
        TokenRows(owner.UserId, publicationId).ShouldBe(1);
        IsTokenSpent(owner.UserId, publicationId).ShouldBeTrue();
        OutboxPayloads(publicationId).Count.ShouldBe(1, "exactly one publication_updated.v1 for the publication");
        AuditData(publicationId).Count.ShouldBe(1, "exactly one publishing.cancelled row for the publication");

        // The loser's body is the generic refusal, not a concurrency or lock artefact.
        var loser = firstResponse.StatusCode == HttpStatusCode.BadRequest ? firstResponse : secondResponse;
        var loserBody = await ReadProblemAsync(loser, HttpStatusCode.BadRequest);

        loserBody.Code.ShouldBe("cancel_token_invalid");
    }

    [Fact]
    public async Task ScheduledPublication_AnswersTheSameGenericBody_AndWritesNothing()
    {
        // QA probe (d): only `awaiting_consent` is cancellable from the link; a consented row is refused with the
        // same bytes as an unknown token and keeps every row it had.
        var owner = await SeedOwnerAsync(status: PublicationStatus.Scheduled);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.AnonymousClient();
        var publicationId = owner.Publications[0];
        var token = await MintTokenAsync(factory, owner.UserId, publicationId);

        string? scheduledBody;
        string? unknownBody;

        using (var scheduled = await PostAsync(client, publicationId, token))
        {
            scheduledBody = await FailureBodyAsync(scheduled);

            // Acceptance check 8 applies to the refusal too: it is a 400 the route wrote.
            scheduled.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
            scheduled.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"]);
        }

        using (var unknown = await PostAsync(client, publicationId, "definitely-not-a-token"))
        {
            unknownBody = await FailureBodyAsync(unknown);
        }

        scheduledBody.ShouldBe(unknownBody);
        PublicationStatusOf(publicationId).ShouldBe("scheduled");
        OutboxPayloads(publicationId).ShouldBeEmpty();
        AuditData(publicationId).ShouldBeEmpty();
        IsTokenSpent(owner.UserId, publicationId).ShouldBeFalse("a refused state check spends nothing");
    }

    [Fact]
    public async Task TokenOfOneUserForAnotherOwnersPublication_RefusesAndLeavesTheOtherOwnerAlone()
    {
        // QA probe (i): the subject of a token can match the body while the token's owner is somebody else. The
        // locked read carries the owner as a predicate, so the row is invisible and the answer is the generic 400.
        var victim = await SeedOwnerAsync();
        var attacker = await SeedOwnerAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.AnonymousClient();
        var victimPublication = victim.Publications[0];

        var token = await MintTokenAsync(factory, attacker.UserId, victimPublication);

        using (var response = await PostAsync(client, victimPublication, token))
        {
            (await ReadProblemAsync(response, HttpStatusCode.BadRequest)).Code.ShouldBe("cancel_token_invalid");
        }

        PublicationStatusOf(victimPublication).ShouldBe("awaiting_consent");
        OutboxPayloads(victimPublication).ShouldBeEmpty();
        AuditData(victimPublication).ShouldBeEmpty();
        IsTokenSpent(attacker.UserId, victimPublication).ShouldBeFalse(
            "the refusal rolls back, so the foreign token is not spent either");
    }

    [Fact]
    public async Task MalformedBodies_NeverAnswer500_AndTheHandlerLevelOnesShareTheGenericRefusal()
    {
        // QA probe (f): the shapes a hostile or broken client can send. None may be a 5xx; the ones that reach the
        // handler must answer the route's one 400 (a binder-level 400 is reported separately, it is a different
        // body but says nothing about any token's state).
        var owner = await SeedOwnerAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory(authPermitLimit: 60);
        using var client = factory.AnonymousClient();
        var publicationId = owner.Publications[0];
        var id = publicationId.ToString("D", CultureInfo.InvariantCulture);

        // `null` expected code = a binder-level refusal (the body never reaches the handler); the test reports
        // those separately instead of pretending they share the handler's code.
        var cases = new (string Name, string? Json, string? ExpectedCode)[]
        {
            ("missing token", $"{{\"publicationId\":\"{id}\"}}", "cancel_token_invalid"),
            ("empty token", $"{{\"publicationId\":\"{id}\",\"token\":\"\"}}", "cancel_token_invalid"),
            ("null token", $"{{\"publicationId\":\"{id}\",\"token\":null}}", "cancel_token_invalid"),
            ("oversized token", $"{{\"publicationId\":\"{id}\",\"token\":\"{new string('A', 8192)}\"}}", "cancel_token_invalid"),
            ("missing publicationId", "{\"token\":\"abc\"}", "cancel_token_invalid"),
            ("empty object", "{}", "cancel_token_invalid"),
            ("malformed publicationId", "{\"publicationId\":\"not-a-guid\",\"token\":\"abc\"}", null),
            ("token not a string", $"{{\"publicationId\":\"{id}\",\"token\":12345}}", null),
            ("empty body", null, null),
        };

        var observed = new List<string>(cases.Length);

        foreach (var (name, json, expectedCode) in cases)
        {
            using var content = json is null
                ? new StringContent(string.Empty, Encoding.UTF8, "application/json")
                : new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await client.PostAsync(Route, content, Ct);
            var text = await response.Content.ReadAsStringAsync(Ct);
            var mediaType = response.Content.Headers.ContentType?.MediaType;

            ((int)response.StatusCode).ShouldBeLessThan(500, $"{name} must never be a server error: {text}");

            string? code = null;

            if (response.StatusCode == HttpStatusCode.BadRequest
                && string.Equals(mediaType, "application/problem+json", StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(text);

                code = document.RootElement.TryGetProperty("code", out var codeProperty)
                    ? codeProperty.GetString()
                    : null;
            }

            // Acceptance #9's "every response carries … no-store": the route's header filter sits on the
            // endpoint, so an answer written before the handler (binding, the rate limiter) cannot carry them.
            observed.Add(
                $"{name}: {(int)response.StatusCode} {mediaType ?? "(no content type)"} code={code ?? "(none)"}"
                + $" hdr={Header(response, "Cache-Control")}/{Header(response, "Referrer-Policy")}");

            if (expectedCode is not null)
            {
                code.ShouldBe(
                    expectedCode,
                    $"{name} reaches the handler and must answer the route's one refusal; observed: {observed[^1]}");
            }
        }

        // Recorded for the report: the binder-level refusals are the only answers whose shape differs from the
        // generic failure, and none of them says anything about a token's state.
        // Recorded for the report: the binder-level refusals are the only answers whose shape differs from the
        // generic failure, and none of them says anything about a token's state. Every one of the nine still
        // carries the route's `no-store` + `no-referrer` (acceptance #9) — the endpoint filter runs even for a
        // body the binder refused, so the *only* answer on this route without them is the 429 (QA-1).
        observed.ShouldBe(
            [
                "missing token: 400 application/problem+json code=cancel_token_invalid hdr=no-store/no-referrer",
                "empty token: 400 application/problem+json code=cancel_token_invalid hdr=no-store/no-referrer",
                "null token: 400 application/problem+json code=cancel_token_invalid hdr=no-store/no-referrer",
                "oversized token: 400 application/problem+json code=cancel_token_invalid hdr=no-store/no-referrer",
                "missing publicationId: 400 application/problem+json code=cancel_token_invalid hdr=no-store/no-referrer",
                "empty object: 400 application/problem+json code=cancel_token_invalid hdr=no-store/no-referrer",
                "malformed publicationId: 400 application/problem+json code=validation_failed hdr=no-store, no-cache/no-referrer",
                "token not a string: 400 application/problem+json code=validation_failed hdr=no-store, no-cache/no-referrer",
                "empty body: 400 application/problem+json code=validation_failed hdr=no-store, no-cache/no-referrer",
            ],
            "pinned so a future change to any refusal is visible; the three binder answers are the only ones "
            + "that differ from the generic body, and none of them depends on a token's state");

        // No malformed body may have cancelled or spent anything.
        PublicationStatusOf(publicationId).ShouldBe("awaiting_consent");
        OutboxPayloads(publicationId).ShouldBeEmpty();
        AuditData(publicationId).ShouldBeEmpty();
    }

    [Fact]
    public async Task RateLimited429_CarriesReferrerPolicyAndNoStore()
    {
        // QA-1 / acceptance check 8: `every response carries Referrer-Policy: no-referrer + Cache-Control:
        // no-store`. The 204, the 400 and the 404 prove it elsewhere; the 429 is written by the shared limiter
        // writer, which runs before the route's own header filter, so it takes the referrer policy from the
        // host's hardening headers and `no-store` from that same pass's 429 branch.
        var owner = await SeedOwnerAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory(authPermitLimit: 2);
        using var client = factory.AnonymousClient();
        var publicationId = owner.Publications[0];

        using (var first = await PostAsync(client, publicationId, "not-a-token"))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var second = await PostAsync(client, publicationId, "not-a-token"))
        {
            second.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using var third = await PostAsync(client, publicationId, "not-a-token");

        third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        third.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"]);
        third.Headers.GetValues("Cache-Control").ShouldBe(["no-store"]);
    }

    /// <summary>One response header, or <c>(absent)</c> so a pinned list can show its absence.</summary>
    /// <param name="response">The answer.</param>
    /// <param name="name">The header name.</param>
    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(',', values) : "(absent)";

    /// <summary>Mints one subject-scoped <c>publication_cancel</c> token through the real store.</summary>
    /// <param name="factory">The started host.</param>
    /// <param name="userId">The token's owner.</param>
    /// <param name="publicationId">The publication the token is scoped to.</param>
    private static async Task<string> MintTokenAsync(ApiFactory factory, Guid userId, Guid publicationId)
    {
        var issued = await AuthTestFlow.IssueSubjectActionTokenAsync(
            factory.Services,
            userId,
            UserActionKind.PublicationCancel,
            publicationId,
            Start.AddHours(1),
            Ct);

        issued.IsError.ShouldBeFalse($"the token must mint: {issued.FirstError}");

        return issued.Value;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Guid publicationId, string token) =>
        client.PostAsJsonAsync(Route, new { publicationId, token }, Ct);

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

    private static async Task<string> FailureBodyAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, text);

        var node = JsonNode.Parse(text);

        node.ShouldNotBeNull();
        node.AsObject().Remove("traceId");

        return node.ToJsonString();
    }

    /// <summary>One owner with a series, a live connection and the requested number of publications.</summary>
    /// <param name="publications">How many publications the owner gets, one episode each.</param>
    /// <param name="status">The lifecycle state every publication starts in.</param>
    private async Task<Owner> SeedOwnerAsync(
        int publications = 1,
        PublicationStatus status = PublicationStatus.AwaitingConsent)
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

        var connection = await new ChannelConnectionBuilder(Database, actor, clock).CreateAsync(
            userId,
            ChannelConnectionStatus.Active,
            $"NTS QA Channel {Guid.CreateVersion7():N}",
            ct: Ct);

        var ids = new List<Guid>(publications);

        for (var index = 0; index < publications; index++)
        {
            var episode = await new EpisodeBuilder(Database, actor, clock).CreateAsync(
                userId,
                series.Id,
                brief: $"Nội dung tập thử nghiệm QA {index}.",
                status: EpisodeStatus.Ready,
                createdAt: Start,
                ct: Ct);

            ids.Add(
                await new PublicationBuilder(Database).CreateAsync(
                    userId,
                    episode.Id,
                    connection.Id,
                    status: status,
                    title: $"Tiêu đề QA {Guid.CreateVersion7():N}",
                    createdAt: Start,
                    ct: Ct));
        }

        return new Owner(userId, ids);
    }

    /// <summary>Writes the one flag row these routes read.</summary>
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

    private ApiFactory NewFactory(int? authPermitLimit = null)
    {
        var factory = new ApiFactory(Database, new FakeClock(Start));

        return authPermitLimit is { } limit
            ? factory.WithSetting(
                "RateLimiting:Auth:PermitLimit",
                limit.ToString(CultureInfo.InvariantCulture))
            : factory;
    }

    private bool IsTokenSpent(Guid userId, Guid publicationId) =>
        SqlProbe.Count(
            Database,
            "SELECT count(*) FROM user_action_tokens WHERE kind = 'publication_cancel' "
            + $"AND user_id = '{userId}' AND subject_id = '{publicationId}' AND used_at IS NOT NULL") == 1;

    private int TokenRows(Guid userId, Guid publicationId) =>
        (int)SqlProbe.Count(
            Database,
            "SELECT count(*) FROM user_action_tokens WHERE kind = 'publication_cancel' "
            + $"AND user_id = '{userId}' AND subject_id = '{publicationId}'");

    private string PublicationStatusOf(Guid publicationId) =>
        SqlProbe.ScalarText(Database, $"SELECT status FROM publications WHERE id = '{publicationId}'");

    private List<string> AuditData(Guid publicationId) =>
        SqlProbe.QueryStrings(
            Database,
            $"SELECT data::text FROM audit_logs WHERE action = '{AuditAction}' "
            + $"AND entity_id = '{publicationId}'");

    private List<string> OutboxPayloads(Guid publicationId) =>
        SqlProbe.QueryStrings(
            Database,
            $"SELECT payload::text FROM outbox_messages WHERE type = '{OutboxEventType}' "
            + $"AND payload->>'publicationId' = '{publicationId}'");

    private sealed record Owner(Guid UserId, IReadOnlyList<Guid> Publications);

    private sealed record ProblemBody(string? Code, int Status, Dictionary<string, string[]>? Errors);
}
