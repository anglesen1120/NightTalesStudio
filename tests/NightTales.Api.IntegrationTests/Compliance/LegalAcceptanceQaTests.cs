using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Compliance;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Identity;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Entities;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Compliance;

/// <summary>
/// QA probes for 13.2.1-T1 (T3): current-user-only isolation, hostile version strings, all-or-nothing writes,
/// Origin gate, fail-closed reads, cache headers and log hygiene. Consumer-visible behaviour only.
/// </summary>
public sealed class LegalAcceptanceQaTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Route = "/api/v1/me/legal";
    private const string AcceptRoute = Route + "/accept";
    private const string RequestIp = "203.0.113.42";
    private const string BodyIp = "198.51.100.9";
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- isolation: current user only -------------------------------------------------------------

    [Fact]
    public async Task Accept_UserIdInBodyOrQuery_IsIgnored_RowsBelongToCaller()
    {
        var victim = await UserAsync();
        var caller = await UserAsync();
        using var factory = Factory();
        using var client = Client(factory, caller.Id);
        var json = $$"""
            {"tos":"1.0.0","privacy":"1.0.0","userId":"{{victim.Id}}","user_id":"{{victim.Id}}",
             "subjectUserId":"{{victim.Id}}","ip":"{{BodyIp}}","acceptedAt":"2001-01-01T00:00:00Z"}
            """;
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync($"{AcceptRoute}?userId={victim.Id}&user_id={victim.Id}", content, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        SqlProbe.Count(Database, $"SELECT count(*) FROM user_consents WHERE user_id = '{victim.Id}'").ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM user_consents WHERE user_id = '{caller.Id}'").ShouldBe(2);
        SqlProbe.QueryStrings(Database, $"SELECT DISTINCT host(ip) FROM user_consents WHERE user_id = '{caller.Id}'")
            .ShouldBe([RequestIp]);
        SqlProbe.Count(Database, $"SELECT count(*) FROM user_consents WHERE accepted_at < '2020-01-01'").ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM audit_logs WHERE subject_user_id = '{victim.Id}'").ShouldBe(0);
    }

    [Fact]
    public async Task UserBNeverSeesOrSatisfiesUserAsAcceptance()
    {
        var a = await UserAsync();
        var b = await UserAsync();
        using var factory = Factory();
        using (var clientA = Client(factory, a.Id))
        {
            using var accepted = await AcceptAsync(clientA);
            accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var clientB = Client(factory, b.Id);
        using var get = await clientB.GetAsync($"{Route}?userId={a.Id}", Ct);
        var body = await JsonAsync(get);
        body.GetProperty("documents").EnumerateArray().ShouldAllBe(document =>
            document.GetProperty("acceptedVersion").ValueKind == JsonValueKind.Null
            && document.GetProperty("acceptedAt").ValueKind == JsonValueKind.Null);

        using var scope = factory.Services.CreateScope();
        var gate = scope.ServiceProvider.GetRequiredService<ILegalAcceptance>();
        (await gate.EnsureCurrentAsync(b.Id, Ct)).IsError.ShouldBeTrue();
        (await gate.EnsureCurrentAsync(a.Id, Ct)).IsError.ShouldBeFalse();

        using var bAccepts = await AcceptAsync(clientB);
        bAccepts.StatusCode.ShouldBe(HttpStatusCode.OK);
        SqlProbe.Count(Database, $"SELECT count(*) FROM user_consents WHERE user_id = '{a.Id}'").ShouldBe(2);
        SqlProbe.Count(Database, $"SELECT count(*) FROM user_consents WHERE user_id = '{b.Id}'").ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnrolmentOnlyToken_CannotReachLegalRoutes(bool accept)
    {
        var user = await UserAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(user.Id, scope: TestAuthHandler.EnrolmentScope);
        client.DefaultRequestHeaders.Add("Origin", ApiFactory.PublicOrigin);
        using var response = accept ? await AcceptAsync(client) : await client.GetAsync(Route, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        SqlProbe.Count(Database, $"SELECT count(*) FROM user_consents WHERE user_id = '{user.Id}'").ShouldBe(0);
    }

    // ---- hostile version strings ------------------------------------------------------------------

    [Theory]
    [InlineData("\" 1.0.0\"")]
    [InlineData("\"1.0.0 \"")]
    [InlineData("\"1.0.0\\n\"")]
    [InlineData("\"1.0.0\\r\\n\"")]
    [InlineData("\"1.0.0\\u0000\"")]
    [InlineData("\"\\u00a01.0.0\"")]
    [InlineData("\"1.0.0.0\"")]
    [InlineData("\"1.0\"")]
    [InlineData("\"1.0.0-rc1\"")]
    [InlineData("\"+1.0.0\"")]
    [InlineData("\"-1.0.0\"")]
    [InlineData("\"1..0\"")]
    [InlineData("\"v1\"")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public async Task Accept_MalformedVersion_Returns400_WritesNothing(string badJson)
    {
        var user = await UserAsync();
        using var factory = Factory();
        using var client = Client(factory, user.Id);
        foreach (var (field, body) in new[]
        {
            ("tos", $"{{\"tos\":{badJson},\"privacy\":\"1.0.0\"}}"),
            ("privacy", $"{{\"tos\":\"1.0.0\",\"privacy\":{badJson}}}"),
        })
        {
            using var response = await PostRawAsync(client, body);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"{field}: {body}");
            var problem = await JsonAsync(response);
            problem.GetProperty("code").GetString().ShouldBe(ErrorCodes.ValidationFailed);
            problem.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue(body);
        }
        NothingWritten(user.Id);
    }

    [Theory]
    [InlineData("\"\\u0661.\\u0660.\\u0660\"")] // Arabic-Indic digits
    [InlineData("\"\\uff11.\\uff10.\\uff10\"")] // full-width digits
    public async Task Accept_NonAsciiDigitVersion_IsRejected_WritesNothing(string badJson)
    {
        var user = await UserAsync();
        using var factory = Factory();
        using var client = Client(factory, user.Id);
        using var response = await PostRawAsync(client, $"{{\"tos\":{badJson},\"privacy\":\"1.0.0\"}}");
        ((int)response.StatusCode).ShouldBeInRange(400, 499, await response.Content.ReadAsStringAsync(Ct));
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest,
            "task design: version must match ^\\d+\\.\\d+\\.\\d+$ with ASCII digits -> validation_failed, not a version conflict");
        NothingWritten(user.Id);
    }

    [Fact]
    public async Task Accept_VersionLengthBoundary_32CharsPassesValidation_33CharsIsValidationFailed()
    {
        var user = await UserAsync();
        using var factory = Factory();
        using var client = Client(factory, user.Id);
        var at32 = new string('1', 28) + ".0.0";
        var at33 = new string('1', 29) + ".0.0";
        at32.Length.ShouldBe(32);
        using var boundary = await PostRawAsync(client, $"{{\"tos\":\"{at32}\",\"privacy\":\"1.0.0\"}}");
        boundary.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await JsonAsync(boundary)).GetProperty("code").GetString().ShouldBe(ErrorCodes.LegalVersionOutdated);
        using var over = await PostRawAsync(client, $"{{\"tos\":\"1.0.0\",\"privacy\":\"{at33}\"}}");
        over.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await JsonAsync(over)).GetProperty("errors").TryGetProperty("privacy", out _).ShouldBeTrue();
        NothingWritten(user.Id);
    }

    [Theory]
    [InlineData("{\"tos\":1,\"privacy\":\"1.0.0\"}")]
    [InlineData("{\"tos\":true,\"privacy\":\"1.0.0\"}")]
    [InlineData("{\"tos\":[\"1.0.0\"],\"privacy\":\"1.0.0\"}")]
    [InlineData("{\"tos\":{},\"privacy\":\"1.0.0\"}")]
    [InlineData("{\"tos\":\"1.0.0\",\"privacy\":1.0}")]
    [InlineData("{\"tos\":null,\"privacy\":null}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"1.0.0\"")]
    [InlineData("42")]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"tos\":\"1.0.0\",\"privacy\":\"1.0.0\"} trailing")]
    [InlineData("{\"tos\":\"1.0.0\\ud800\",\"privacy\":\"1.0.0\"}")]
    public async Task Accept_WrongJsonShape_Returns4xxWithoutInternals_WritesNothing(string raw)
    {
        var user = await UserAsync();
        using var factory = Factory();
        using var client = Client(factory, user.Id);
        using var response = await PostRawAsync(client, raw);
        var text = await response.Content.ReadAsStringAsync(Ct);
        ((int)response.StatusCode).ShouldBeInRange(400, 499, text);
        text.ShouldNotContain("System.", Case.Sensitive);
        text.ShouldNotContain("StackTrace", Case.Insensitive);
        NothingWritten(user.Id);
    }

    [Fact]
    public async Task Accept_WrongContentType_Returns4xx_WritesNothing()
    {
        var user = await UserAsync();
        using var factory = Factory();
        using var client = Client(factory, user.Id);
        using var content = new StringContent("{\"tos\":\"1.0.0\",\"privacy\":\"1.0.0\"}", Encoding.UTF8, "text/plain");
        using var response = await client.PostAsync(AcceptRoute, content, Ct);
        ((int)response.StatusCode).ShouldBeInRange(400, 499);
        NothingWritten(user.Id);
    }

    [Fact]
    public async Task Accept_HugeOrDeeplyNestedBody_IsNotA5xx_AndOversizedVersionWritesNothing()
    {
        var user = await UserAsync();
        using var factory = Factory();
        using var client = Client(factory, user.Id);

        using var longVersion = await PostRawAsync(client, $"{{\"tos\":\"{new string('1', 200_000)}\",\"privacy\":\"1.0.0\"}}");
        longVersion.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await longVersion.Content.ReadAsStringAsync(Ct)).Length.ShouldBeLessThan(4096, "the error must not echo the oversized input");

        var depth = 5000;
        var nested = $"{{\"tos\":\"1.0.0\",\"privacy\":\"1.0.0\",\"x\":{new string('[', depth)}{new string(']', depth)}}}";
        using var deep = await PostRawAsync(client, nested);
        ((int)deep.StatusCode).ShouldBeInRange(400, 499);

        var padded = $"{{\"tos\":\"1.0.0\",\"privacy\":\"1.0.0\",\"pad\":\"{new string('x', 3_000_000)}\"}}";
        using var big = await PostRawAsync(client, padded);
        ((int)big.StatusCode).ShouldBeLessThan(500);

        // Whatever the unknown-member policy answered for the padded body, consents never exceed what was accepted.
        SqlProbe.Count(Database, $"SELECT count(*) FROM user_consents WHERE user_id = '{user.Id}'")
            .ShouldBeOneOf(0, 2);
    }

    // ---- server value wins, config drives gate -----------------------------------------------------

    [Fact]
    public async Task Accept_UsesConfiguredVersions_NotHardcoded_AndOldClientGets409()
    {
        var user = await UserAsync();
        using var factory = Factory().WithSetting("LegalDocuments:Current:Tos", "2.3.4")
            .WithSetting("LegalDocuments:Current:Privacy", "3.4.5");
        using var client = Client(factory, user.Id);

        using var get = await client.GetAsync(Route, Ct);
        var documents = (await JsonAsync(get)).GetProperty("documents");
        documents[0].GetProperty("currentVersion").GetString().ShouldBe("2.3.4");
        documents[1].GetProperty("currentVersion").GetString().ShouldBe("3.4.5");

        using var stale = await AcceptAsync(client);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        NothingWritten(user.Id);

        using var fresh = await PostRawAsync(client, "{\"tos\":\"2.3.4\",\"privacy\":\"3.4.5\"}");
        fresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        SqlProbe.QueryStrings(Database, $"SELECT document || '|' || version FROM user_consents WHERE user_id = '{user.Id}' ORDER BY document")
            .ShouldBe(["privacy|3.4.5", "tos|2.3.4"]);
    }

    [Fact]
    public async Task VersionBump_MakesPriorAcceptanceStale_ThenAcceptingNewVersionSatisfiesGate()
    {
        var user = await UserAsync();
        await SeedAsync(user.Id, LegalDocument.Tos, "1.0.0", _clock.UtcNow);
        await SeedAsync(user.Id, LegalDocument.Privacy, "1.0.0", _clock.UtcNow);
        using var factory = Factory().WithSetting("LegalDocuments:Current:Privacy", "1.1.0");
        using var scope = factory.Services.CreateScope();
        var gate = scope.ServiceProvider.GetRequiredService<ILegalAcceptance>();
        var before = await gate.EnsureCurrentAsync(user.Id, Ct);
        before.IsError.ShouldBeTrue();
        before.FirstError.Metadata!["documents"].ShouldBeOfType<string[]>().ShouldBe(["privacy"]);

        _clock.Advance(TimeSpan.FromMinutes(1));
        using var client = Client(factory, user.Id);
        using var response = await PostRawAsync(client, "{\"tos\":\"1.0.0\",\"privacy\":\"1.1.0\"}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var scope2 = factory.Services.CreateScope();
        (await scope2.ServiceProvider.GetRequiredService<ILegalAcceptance>().EnsureCurrentAsync(user.Id, Ct)).IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task LatestRowPerDocumentWins_NewerStaleRowAfterCurrentOne_RequiresAcceptance()
    {
        var user = await UserAsync();
        await SeedAsync(user.Id, LegalDocument.Tos, "1.0.0", _clock.UtcNow.AddMinutes(-5));
        await SeedAsync(user.Id, LegalDocument.Tos, "0.9.0", _clock.UtcNow);
        await SeedAsync(user.Id, LegalDocument.Privacy, "1.0.0", _clock.UtcNow);
        using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ILegalAcceptance>().EnsureCurrentAsync(user.Id, Ct);
        result.IsError.ShouldBeTrue();
        result.FirstError.Metadata!["documents"].ShouldBeOfType<string[]>().ShouldBe(["tos"]);
    }

    // ---- all-or-nothing, fail closed ----------------------------------------------------------------

    [Fact]
    public async Task Accept_SecondDocumentWriteFails_LeavesNoConsentAndNoAuditRow()
    {
        var user = await UserAsync();
        using var factory = Factory().WithServices(services =>
        {
            services.RemoveAll<IConsentWriter>();
            services.AddScoped<IConsentWriter>(provider => new FailingPrivacyWriter(
                new ConsentWriter(provider.GetRequiredService<AppDbContext>(), provider.GetRequiredService<IClock>())));
        });
        using var client = Client(factory, user.Id);
        using var response = await AcceptAsync(client);
        ((int)response.StatusCode).ShouldBeGreaterThanOrEqualTo(400);
        NothingWritten(user.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsentReaderThrows_RequestFailsClosed_WithoutLeakingMessage_AndWritesNothing(bool accept)
    {
        var user = await UserAsync();
        using var factory = Factory().WithServices(services =>
        {
            services.RemoveAll<IConsentReader>();
            services.AddScoped<IConsentReader, ThrowingReader>();
        });
        using var client = Client(factory, user.Id);
        using var response = accept ? await AcceptAsync(client) : await client.GetAsync(Route, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        var text = await response.Content.ReadAsStringAsync(Ct);
        text.ShouldContain("traceId");
        text.ShouldNotContain("reader exploded");
        NothingWritten(user.Id);
    }

    [Fact]
    public async Task Gate_ReaderThrows_NeverReturnsSuccess()
    {
        var user = await UserAsync();
        using var factory = Factory().WithServices(services =>
        {
            services.RemoveAll<IConsentReader>();
            services.AddScoped<IConsentReader, ThrowingReader>();
        });
        using var scope = factory.Services.CreateScope();
        await Should.ThrowAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<ILegalAcceptance>().EnsureCurrentAsync(user.Id, Ct));
    }

    [Fact]
    public async Task Accept_Parallel_AllSucceedAndAppendRows()
    {
        var user = await UserAsync();
        using var factory = Factory();
        using var client = Client(factory, user.Id);
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => AcceptAsync(client)));
        try
        {
            responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.OK);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
        SqlProbe.Count(Database, $"SELECT count(*) FROM user_consents WHERE user_id = '{user.Id}'").ShouldBe(16);
        SqlProbe.Count(Database, $"SELECT count(*) FROM audit_logs WHERE subject_user_id = '{user.Id}' AND action = 'account.legal_accepted'").ShouldBe(8);
    }

    // ---- Origin gate ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("https://api.nighttales.test.evil.test")]
    [InlineData("https://evil.test/https://api.nighttales.test")]
    [InlineData("http://api.nighttales.test")]
    [InlineData("https://api.nighttales.test:8443")]
    [InlineData("api.nighttales.test")]
    public async Task Accept_NearMissOrigin_Is403_WritesNothing(string origin)
    {
        var user = await UserAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(user.Id);
        using var request = new HttpRequestMessage(HttpMethod.Post, AcceptRoute)
        {
            Content = JsonContent.Create(new { tos = "1.0.0", privacy = "1.0.0" }),
        };
        request.Headers.TryAddWithoutValidation("Origin", origin);
        using var response = await client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        NothingWritten(user.Id);
    }

    [Fact]
    public async Task Accept_DuplicateOriginHeader_Is403()
    {
        var user = await UserAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(user.Id);
        using var request = new HttpRequestMessage(HttpMethod.Post, AcceptRoute)
        {
            Content = JsonContent.Create(new { tos = "1.0.0", privacy = "1.0.0" }),
        };
        request.Headers.TryAddWithoutValidation("Origin", [ApiFactory.PublicOrigin, ApiFactory.PublicOrigin]);
        using var response = await client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        NothingWritten(user.Id);
    }

    [Fact]
    public async Task Accept_AnonymousWithForeignOrigin_Is401NotForbidden_AndGetNeedsNoOrigin()
    {
        var user = await UserAsync();
        using var factory = Factory();
        using var anonymous = factory.AnonymousClient();
        anonymous.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        using var denied = await AcceptAsync(anonymous);
        denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var client = factory.ClientFor(user.Id);
        using var get = await client.GetAsync(Route, Ct);
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---- headers, audit, logs ------------------------------------------------------------------------

    [Fact]
    public async Task Responses_AreNoStore_OnSuccessAndConflict()
    {
        var user = await UserAsync();
        using var factory = Factory();
        using var client = Client(factory, user.Id);
        using var get = await client.GetAsync(Route, Ct);
        get.Headers.CacheControl!.NoStore.ShouldBeTrue();
        using var conflict = await PostRawAsync(client, "{\"tos\":\"0.0.1\",\"privacy\":\"0.0.1\"}");
        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        conflict.Headers.CacheControl!.NoStore.ShouldBeTrue();
        using var ok = await AcceptAsync(client);
        ok.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task Accept_AuditRow_CarriesOnlyTheTwoVersions()
    {
        var user = await UserAsync();
        using var factory = Factory();
        using var client = Client(factory, user.Id);
        using var response = await AcceptAsync(client);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var data = JsonDocument.Parse(SqlProbe.ScalarText(Database,
            $"SELECT data::text FROM audit_logs WHERE subject_user_id = '{user.Id}' AND action = 'account.legal_accepted'"));
        data.RootElement.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal)
            .ShouldBe(["privacy", "tos"]);
        SqlProbe.ScalarText(Database,
                $"SELECT coalesce(host(ip), '') || '|' || coalesce(user_agent, '') FROM audit_logs WHERE subject_user_id = '{user.Id}' AND action = 'account.legal_accepted'")
            .ShouldNotContain(RequestIp);
    }

    [Fact]
    public async Task Logs_AcrossAllPaths_ContainNoIpEmailTokenOrSubmittedGarbage()
    {
        var user = await UserAsync();
        var sink = new CapturingLoggerProvider();
        using var factory = Factory().WithLoggerProvider(sink);
        using var client = Client(factory, user.Id);
        var attacker = "victim@example.com";
        using (await client.GetAsync(Route, Ct))
        {
        }
        using (await AcceptAsync(client))
        {
        }
        using (await PostRawAsync(client, "{\"tos\":\"0.0.1\",\"privacy\":\"0.0.1\"}"))
        {
        }
        using (var bad = await PostRawAsync(client, $"{{\"tos\":\"{attacker}\",\"privacy\":\"1.0.0\"}}"))
        {
            (await bad.Content.ReadAsStringAsync(Ct)).ShouldNotContain(attacker);
        }
        using var foreign = new HttpRequestMessage(HttpMethod.Post, AcceptRoute) { Content = JsonContent.Create(new { tos = "1.0.0", privacy = "1.0.0" }) };
        foreign.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        using (await client.SendAsync(foreign, Ct))
        {
        }
        var logs = sink.Text;
        logs.Length.ShouldBeGreaterThan(0);
        foreach (var secret in new[] { RequestIp, user.Email!, "private-user-agent", "private-token-marker", attacker })
        {
            logs.ShouldNotContain(secret);
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private void NothingWritten(Guid userId)
    {
        SqlProbe.Count(Database, $"SELECT count(*) FROM user_consents WHERE user_id = '{userId}'").ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM audit_logs WHERE subject_user_id = '{userId}' AND action = 'account.legal_accepted'").ShouldBe(0);
    }

    private ApiFactory Factory() => new ApiFactory(Database, _clock).WithServices(services =>
    {
        services.RemoveAll<IRequestInfoAccessor>();
        services.AddSingleton<IRequestInfoAccessor>(new RequestMetadata(RequestIp));
    });

    private Task<AppUser> UserAsync() => new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: Ct);

    private async Task SeedAsync(Guid userId, LegalDocument document, string version, DateTimeOffset acceptedAt)
    {
        await using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = userId }, _clock);
        context.UserConsents.Add(UserConsent.Create(userId, document, version, acceptedAt, null));
        await context.SaveChangesAsync(Ct);
    }

    private static HttpClient Client(ApiFactory factory, Guid userId)
    {
        var client = factory.ClientFor(userId);
        client.DefaultRequestHeaders.Add("Origin", ApiFactory.PublicOrigin);
        client.DefaultRequestHeaders.Add("Authorization", "Bearer private-token-marker");
        return client;
    }

    private static Task<HttpResponseMessage> AcceptAsync(HttpClient client) =>
        client.PostAsJsonAsync(AcceptRoute, new { tos = "1.0.0", privacy = "1.0.0" }, Ct);

    private static Task<HttpResponseMessage> PostRawAsync(HttpClient client, string json) =>
        client.PostAsync(AcceptRoute, new StringContent(json, Encoding.UTF8, "application/json"), Ct);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.Clone();
    }

    private sealed class RequestMetadata(string? ip) : IRequestInfoAccessor
    {
        public RequestInfo Current => new(ip, "private-user-agent");
    }

    private sealed class ThrowingReader : IConsentReader
    {
        public Task<IReadOnlyList<LegalConsentSnapshot>> LatestAsync(Guid userId, CancellationToken ct, bool includePending = false) =>
            throw new InvalidOperationException("reader exploded");
    }

    private sealed class FailingPrivacyWriter(IConsentWriter inner) : IConsentWriter
    {
        public Task<ErrorOr<Success>> RecordAsync(Guid userId, LegalDocument document, string version, IPAddress? ip, CancellationToken ct) =>
            document == LegalDocument.Privacy
                ? Task.FromResult<ErrorOr<Success>>(Error.Validation(ErrorCodes.ValidationFailed, "forced second-write failure"))
                : inner.RecordAsync(userId, document, version, ip, ct);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _lines = [];
        private readonly Lock _gate = new();

        public string Text
        {
            get
            {
                lock (_gate)
                {
                    return string.Join('\n', _lines);
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner._gate)
                {
                    owner._lines.Add(formatter(state, exception) + (exception?.ToString() ?? string.Empty));
                }
            }
        }
    }
}
