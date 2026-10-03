using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Common.Errors;
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
/// 09.3.4-T1 over the real host: <c>GET /api/v1/me</c> and <c>PATCH /api/v1/me</c> act on the caller only,
/// expose no secret, validate per field, reject unknown members on this endpoint alone and write one
/// <c>account.profile_updated</c> audit row per successful change.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MeEndpointsTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Route = "/api/v1/me";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetMe_ReturnsOwnProfileOnly()
    {
        var mine = await CreateUserAsync("mine@nighttales.test");
        var other = await CreateUserAsync("other@nighttales.test");
        using var factory = NewFactory();
        using var client = factory.ClientFor(mine);

        using var response = await client.GetAsync(Route, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag.ShouldBeNull("D15: /me carries no ETag");

        var body = await response.Content.ReadAsStringAsync(Ct);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        root.GetProperty("id").GetGuid().ShouldBe(mine);
        root.GetProperty("email").GetString().ShouldBe("mine@nighttales.test");
        root.GetProperty("displayName").GetString().ShouldNotBeNullOrWhiteSpace();
        root.GetProperty("uiLocale").GetString().ShouldBe("vi");
        root.GetProperty("role").GetString().ShouldBe("user");
        root.GetProperty("mfaEnabled").GetBoolean().ShouldBeFalse();
        root.TryGetProperty("lastLoginAt", out _).ShouldBeTrue();
        body.ShouldNotContain("other@nighttales.test");
        body.ShouldNotContain(other.ToString());
    }

    [Fact]
    public async Task GetMe_Unauthenticated_Is401()
    {
        using var factory = NewFactory();
        using var client = factory.AnonymousClient();

        using var response = await client.GetAsync(Route, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, Ct);
        problem!.Code.ShouldBe(ErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task GetMe_Features_ListsOnlyEnabledAllowListedKeys()
    {
        var userId = await CreateUserAsync();
        await SetFlagAsync("source_finder", enabled: true);
        await SetFlagAsync("youtube_publish", enabled: false);
        await SetFlagAsync("unrelated_flag", enabled: true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        var me = await client.GetFromJsonAsync<JsonElement>(Route, Ct);

        me.GetProperty("features").EnumerateArray().Select(item => item.GetString())
            .ShouldBe(["source_finder"]);
    }

    [Fact]
    public async Task GetMe_DoesNotExposeSecurityFields()
    {
        var userId = await CreateUserAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        var body = await client.GetStringAsync(Route, Ct);

        foreach (var forbidden in new[] { "securityStamp", "passwordHash", "lastTotpStep", "normalizedEmail" })
        {
            body.ShouldNotContain(forbidden, Case.Insensitive);
        }
    }

    [Fact]
    public async Task PatchMe_UiLocaleVi_PersistsAcrossRequests()
    {
        var userId = await CreateUserAsync();
        await Database.ExecuteAsync($"UPDATE users SET ui_locale = 'en' WHERE id = '{userId}'", Ct);
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await PatchAsync(client, """{"uiLocale":"vi"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("uiLocale").GetString().ShouldBe("vi");

        var again = await client.GetFromJsonAsync<JsonElement>(Route, Ct);
        again.GetProperty("uiLocale").GetString().ShouldBe("vi");
        SqlProbe.ScalarText(Database, $"SELECT ui_locale FROM users WHERE id = '{userId}'").ShouldBe("vi");

        SqlProbe.QueryStrings(Database, AuditSql(userId)).ShouldHaveSingleItem()
            .ShouldBe("""["uiLocale"]|en->vi""");
    }

    [Fact]
    public async Task PatchMe_DisplayNameTrimmed_Succeeds()
    {
        var userId = await CreateUserAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await PatchAsync(client, """{"displayName":"Ana"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("displayName").GetString().ShouldBe("Ana");
        SqlProbe.ScalarText(Database, $"SELECT display_name FROM users WHERE id = '{userId}'").ShouldBe("Ana");

        var summary = SqlProbe.QueryStrings(Database, AuditSql(userId)).ShouldHaveSingleItem();
        summary.ShouldBe("""["displayName"]|-""");
        summary.ShouldNotContain("Ana");
    }

    [Fact]
    public async Task PatchMe_DisplayNameTooLong_Is400()
    {
        var userId = await CreateUserAsync();
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await PatchAsync(client, $$"""{"displayName":"{{new string('a', 81)}}"}""");

        await AssertValidationFailedAsync(response, "displayName");
    }

    [Fact]
    public async Task PatchMe_EmptyBody_Is400()
    {
        var userId = await CreateUserAsync();
        var before = SqlProbe.ScalarText(Database, $"SELECT display_name FROM users WHERE id = '{userId}'");
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await PatchAsync(client, "{}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        SqlProbe.ScalarText(Database, $"SELECT display_name FROM users WHERE id = '{userId}'").ShouldBe(before);
        SqlProbe.QueryStrings(Database, AuditSql(userId)).ShouldBeEmpty("a refused PATCH writes no audit row (UC3)");
    }

    [Fact]
    public async Task PatchMe_EmailMember_Is400()
    {
        var userId = await CreateUserAsync("keep@nighttales.test");
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var response = await PatchAsync(client, """{"displayName":"Ana","email":"x@y.zz"}""");

        await AssertValidationFailedAsync(response, "email");
        SqlProbe.ScalarText(Database, $"SELECT email FROM users WHERE id = '{userId}'").ShouldBe("keep@nighttales.test");
        SqlProbe.QueryStrings(Database, AuditSql(userId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task PatchMe_Strictness_IsScopedToThisEndpoint()
    {
        var userId = await CreateUserAsync();
        var seriesId = (await new SeriesBuilder(Database, new TestCurrentUser(), new FakeClock()).CreateAsync(userId, ct: Ct)).Id;
        using var factory = NewFactory();
        using var client = factory.ClientFor(userId);

        using var get = await client.GetAsync($"/api/v1/series/{seriesId}", Ct);
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/series/{seriesId}")
        {
            Content = new StringContent("""{"title":"Mới","bogusMember":1}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", get.Headers.ETag!.ToString());

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task PatchMe_UsesCurrentUserOnly_NoRouteId()
    {
        var first = await CreateUserAsync("first@nighttales.test");
        var second = await CreateUserAsync("second@nighttales.test");
        var secondName = SqlProbe.ScalarText(Database, $"SELECT display_name FROM users WHERE id = '{second}'");
        using var factory = NewFactory();
        using var client = factory.ClientFor(first);

        using var patched = await PatchAsync(client, """{"displayName":"Only Me"}""");
        using var addressed = await client.PatchAsync(
            $"{Route}/{second}",
            new StringContent("""{"displayName":"Hijack"}""", Encoding.UTF8, "application/json"),
            Ct);

        patched.StatusCode.ShouldBe(HttpStatusCode.OK);
        addressed.StatusCode.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
        SqlProbe.ScalarText(Database, $"SELECT display_name FROM users WHERE id = '{second}'").ShouldBe(secondName);
        SqlProbe.ScalarText(Database, $"SELECT display_name FROM users WHERE id = '{first}'").ShouldBe("Only Me");
    }

    [Fact]
    public async Task PatchMe_Unauthenticated_Is401()
    {
        using var factory = NewFactory();
        using var client = factory.AnonymousClient();

        using var response = await PatchAsync(client, """{"displayName":"Ana"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private ApiFactory NewFactory() => new(Database, new FakeClock());

    private async Task<Guid> CreateUserAsync(string? email = null)
        => (await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(email: email, ct: Ct)).Id;

    private Task SetFlagAsync(string key, bool enabled) =>
        Database.ExecuteAsync(
            "INSERT INTO feature_flags (key, enabled, rules, created_at, updated_at) "
            + $"VALUES ('{key}', {(enabled ? "true" : "false")}, '{{}}', now(), now()) "
            + "ON CONFLICT (key) DO UPDATE SET enabled = EXCLUDED.enabled, updated_at = now()",
            Ct);

    private static string AuditSql(Guid userId) => $"""
        SELECT coalesce(data->>'fields', '-') || '|'
            || coalesce((data->'uiLocale'->>'from') || '->' || (data->'uiLocale'->>'to'), '-')
        FROM audit_logs WHERE action = 'account.profile_updated' AND subject_user_id = '{userId}'
        """;

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, string json) =>
        client.PatchAsync(Route, new StringContent(json, Encoding.UTF8, "application/json"), Ct);

    private static async Task AssertValidationFailedAsync(HttpResponseMessage response, string field)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, Ct);
        problem!.Code.ShouldBe(ErrorCodes.ValidationFailed);
        problem.Errors.ShouldNotBeNull();
        problem.Errors!.Keys.ShouldContain(field);
    }
}
