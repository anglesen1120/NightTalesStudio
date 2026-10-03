using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.AccountProfile;

/// <summary>
/// QA additions for 09.3.4-T1 (agent QA0934T1, 2026-10-03): the blank-name and "VI" locale rows of the
/// plan's failure table, the deleted-account 401, the audit row's documented <c>data</c> shape
/// (plan §5: <c>{"fields":[...],"uiLocale":{"from","to"}}</c>), the enrolment-scope refusal, the
/// suspended-account refusal the failure table promises, and the pinned answer of
/// <c>PATCH /me/{id}</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MeEndpointsQaTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Route = "/api/v1/me";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PatchMe_DisplayNameSurroundingBlanks_TrimmedToAna()
    {
        var userId = await CreateUserAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await PatchAsync(client, """{"displayName":" Ana "}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        SqlProbe.ScalarText(Database, $"SELECT display_name FROM users WHERE id = '{userId}'").ShouldBe("Ana");
    }

    [Fact]
    public async Task PatchMe_DisplayNameOnlyBlanks_Is400()
    {
        var userId = await CreateUserAsync();
        var before = SqlProbe.ScalarText(Database, $"SELECT display_name FROM users WHERE id = '{userId}'");
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await PatchAsync(client, """{"displayName":"   "}""");

        await AssertValidationFailedAsync(response, "displayName");
        SqlProbe.ScalarText(Database, $"SELECT display_name FROM users WHERE id = '{userId}'").ShouldBe(before);
        SqlProbe.QueryStrings(Database, AuditSql(userId)).ShouldBeEmpty("a refused PATCH writes no audit row (UC3)");
    }

    [Theory]
    [InlineData("""{"uiLocale":"VI"}""")]
    [InlineData("""{"uiLocale":"Vi"}""")]
    [InlineData("""{"uiLocale":"EN"}""")]
    public async Task PatchMe_NonCanonicalCaseLocale_Is400(string json)
    {
        // QA-3: D14 fixes the wire value to lowercase; only the exact "vi"/"en" is accepted.
        var userId = await CreateUserAsync();
        var before = SqlProbe.ScalarText(Database, $"SELECT ui_locale FROM users WHERE id = '{userId}'");
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await PatchAsync(client, json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, Ct);
        problem!.Code.ShouldBe("validation_failed");
        SqlProbe.ScalarText(Database, $"SELECT ui_locale FROM users WHERE id = '{userId}'").ShouldBe(before);
        SqlProbe.QueryStrings(Database, AuditSql(userId)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""{"displayName":"\u200B"}""")]
    [InlineData("""{"displayName":"Ana\u200B"}""")]
    [InlineData("""{"displayName":"\u202EAna"}""")]
    [InlineData("""{"displayName":"An\u2028a"}""")]
    public async Task PatchMe_InvisibleFormattingInDisplayName_Is400(string json)
    {
        // SEC-1: zero-width, bidi override and line-separator characters are refused.
        var userId = await CreateUserAsync();
        var before = SqlProbe.ScalarText(Database, $"SELECT display_name FROM users WHERE id = '{userId}'");
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await PatchAsync(client, json);

        await AssertValidationFailedAsync(response, "displayName");
        SqlProbe.ScalarText(Database, $"SELECT display_name FROM users WHERE id = '{userId}'").ShouldBe(before);
        SqlProbe.QueryStrings(Database, AuditSql(userId)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""{"uiLocale":"fr"}""")]
    [InlineData("""{"uiLocale":2}""")]
    [InlineData("""{"uiLocale":null}""")]
    public async Task PatchMe_BadUiLocale_Is400(string json)
    {
        var userId = await CreateUserAsync();
        var before = SqlProbe.ScalarText(Database, $"SELECT ui_locale FROM users WHERE id = '{userId}'");
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await PatchAsync(client, json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, Ct);
        problem!.Code.ShouldBe("validation_failed", "the failure table promises validation_failed for a bad locale");
        SqlProbe.ScalarText(Database, $"SELECT ui_locale FROM users WHERE id = '{userId}'").ShouldBe(before);
        SqlProbe.QueryStrings(Database, AuditSql(userId)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""{"displayName":null,"uiLocale":null}""")]
    [InlineData("""{"displayName":"Ana","bogus":1}""")]
    [InlineData("""{"mfaEnabled":true}""")]
    [InlineData("""{"role":"owner"}""")]
    [InlineData("""{"displayName":"Ana","unknownMembers":1}""")]
    [InlineData("""{"displayName":"Ana","UnknownMembers":{}}""")]
    [InlineData("""{"displayName":"Ana","extra":1}""")]
    public async Task PatchMe_NullOrUnknownMembers_Is400_NoAuditRow(string json)
    {
        var userId = await CreateUserAsync();
        var before = SqlProbe.ScalarText(Database, $"SELECT display_name FROM users WHERE id = '{userId}'");
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await PatchAsync(client, json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        SqlProbe.ScalarText(Database, $"SELECT display_name FROM users WHERE id = '{userId}'").ShouldBe(before);
        SqlProbe.QueryStrings(Database, AuditSql(userId)).ShouldBeEmpty("a refused PATCH writes no audit row");
    }

    [Fact]
    public async Task PatchMe_MalformedJson_Is400ValidationFailed()
    {
        var userId = await CreateUserAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await PatchAsync(client, "{ not json");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, Ct);
        problem!.Code.ShouldBe("validation_failed");
        SqlProbe.QueryStrings(Database, AuditSql(userId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task GetMe_DeletedUser_Is401()
    {
        var userId = await CreateUserAsync("gone@nighttales.test");
        await Database.ExecuteAsync($"UPDATE users SET status = 'deleted' WHERE id = '{userId}'", Ct);
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await client.GetAsync(Route, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task PatchMe_DeletedUser_Is401_AndWritesNothing()
    {
        var userId = await CreateUserAsync("gone-patch@nighttales.test");
        var before = SqlProbe.ScalarText(Database, $"SELECT display_name FROM users WHERE id = '{userId}'");
        await Database.ExecuteAsync($"UPDATE users SET status = 'deleted' WHERE id = '{userId}'", Ct);
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await PatchAsync(client, """{"displayName":"Ana"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await response.Content.ReadAsStringAsync(Ct));
        SqlProbe.ScalarText(Database, $"SELECT display_name FROM users WHERE id = '{userId}'").ShouldBe(before);
        SqlProbe.QueryStrings(Database, AuditSql(userId)).ShouldBeEmpty("the token outlived the account: no row, no write");
    }

    [Fact]
    public async Task PatchMe_SuspendedUserWithLiveToken_FollowsPhaseP_RefreshOnlyEnforcement()
    {
        // QA-2 [CONTRACT]: 09.3.2-T1-login-refresh-logout.md:162-164 — "In P this is the only enforcement point
        // for suspension — the per-request stamp/status check is S (09.3.2-T2, WF12)". The task failure table's
        // "refused earlier by the authorisation behaviour" names a per-request check that does not exist in P
        // (AuthorizationBehavior is role-only, D10). A live token therefore still reaches /me until its refresh
        // fails with session_revoked. This pins that contract; it flips when 09.3.2-T2 lands.
        var userId = await CreateUserAsync("held@nighttales.test");
        await Database.ExecuteAsync($"UPDATE users SET status = 'suspended' WHERE id = '{userId}'", Ct);
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await PatchAsync(client, """{"displayName":"Ana"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        SqlProbe.ScalarText(Database, $"SELECT display_name FROM users WHERE id = '{userId}'").ShouldBe("Ana");
    }

    [Fact]
    public async Task PatchMe_SubRouteId_Is404()
    {
        // Pins the ambiguity the implementation left open (404 or 405): there is no route pattern that
        // matches /api/v1/me/{anything}, so routing answers before the method is considered.
        var userId = await CreateUserAsync();
        var other = await CreateUserAsync("other@nighttales.test");
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await client.PatchAsync(
            $"{Route}/{other}",
            new StringContent("""{"displayName":"Hijack"}""", Encoding.UTF8, "application/json"),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task GetMe_EnrolmentScopeToken_IsRefused()
    {
        // WT34: the enrolment-only token reaches /me/mfa/setup + verify and nothing else; the fallback
        // policy refuses it everywhere, answered 404 like every other refusal (D10).
        var userId = await CreateUserAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId, scope: TestAuthHandler.EnrolmentScope);

        using var response = await client.GetAsync(Route, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task PatchMe_BothFields_WritesAuditRow_DocumentedShape()
    {
        // QA-1: plan §5 / AC L73 — {"fields":["displayName","uiLocale"],"uiLocale":{"from","to"}}, no name text.
        var userId = await CreateUserAsync();
        await Database.ExecuteAsync($"UPDATE users SET ui_locale = 'en' WHERE id = '{userId}'", Ct);
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await PatchAsync(client, """{"displayName":"Ana","uiLocale":"vi"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var rows = SqlProbe.QueryStrings(Database, $"""
            SELECT actor_type || '|' || category || '|' || visibility || '|' || coalesce(entity_type, '-')
                 || '|' || data::text
            FROM audit_logs
            WHERE action = 'account.profile_updated' AND subject_user_id = '{userId}' AND actor_user_id = '{userId}'
            """);
        var row = rows.ShouldHaveSingleItem();
        var columns = row.Split('|', 5);
        columns[0].ShouldBe("user");
        columns[1].ShouldBe("account_security");
        columns[2].ShouldBe("user");
        columns[3].ShouldBe("user");

        using var data = JsonDocument.Parse(columns[4]);
        data.RootElement.GetProperty("fields").EnumerateArray().Select(field => field.GetString())
            .ShouldBe(["displayName", "uiLocale"]);
        data.RootElement.GetProperty("uiLocale").GetProperty("from").GetString().ShouldBe("en");
        data.RootElement.GetProperty("uiLocale").GetProperty("to").GetString().ShouldBe("vi");
        data.RootElement.TryGetProperty("displayName", out _).ShouldBeFalse();
        columns[4].Contains("Ana", StringComparison.Ordinal).ShouldBeFalse("BD16: the name text is never audited");
    }

    [Fact]
    public async Task PatchMe_NoOpChange_Is200_AndWritesNoAuditRow()
    {
        // SEC-2: a PATCH that changes nothing returns the current profile and records nothing.
        var userId = await CreateUserAsync();
        var current = SqlProbe.ScalarText(Database, $"SELECT display_name FROM users WHERE id = '{userId}'");
        var locale = SqlProbe.ScalarText(Database, $"SELECT ui_locale FROM users WHERE id = '{userId}'");
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await PatchAsync(client, $$"""{"displayName":"{{current}}","uiLocale":"{{locale}}"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("displayName").GetString().ShouldBe(current);
        SqlProbe.QueryStrings(Database, AuditSql(userId)).ShouldBeEmpty("a no-op PATCH writes zero audit rows");
    }

    private ApiFactory NewFactory() => new(Database, new FakeClock());

    private async Task<Guid> CreateUserAsync(string? email = null)
        => (await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(email: email, ct: Ct)).Id;

    private static string AuditSql(Guid userId) => $"""
        SELECT coalesce(data->>'fields', '-') || '|' || coalesce(data->>'displayName', data->>'uiLocale', '-')
        FROM audit_logs WHERE action = 'account.profile_updated' AND subject_user_id = '{userId}'
        """;

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, string json) =>
        client.PatchAsync(Route, new StringContent(json, Encoding.UTF8, "application/json"), Ct);

    private static async Task AssertValidationFailedAsync(HttpResponseMessage response, string field)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, Ct);
        problem!.Code.ShouldBe("validation_failed");
        problem.Errors.ShouldNotBeNull();
        problem.Errors!.Keys.ShouldContain(field);
    }
}
