using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Api.RateLimiting;
using NightTales.Application.Abstractions.Compliance;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
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

public sealed class LegalEndpointsTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Route = "/api/v1/me/legal";
    private const string AcceptRoute = Route + "/accept";
    private const string RequestIp = "203.0.113.42";
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetMeLegal_NeverAccepted_ReturnsBothDocumentsWithNullAcceptedVersion()
    {
        var user = await UserAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(user.Id);
        using var response = await client.GetAsync(Route, Ct);
        var body = await OkAsync(response);
        var documents = body.GetProperty("documents").EnumerateArray().ToArray();
        documents.Select(document => document.GetProperty("document").GetString()).ShouldBe(["tos", "privacy"]);
        foreach (var document in documents)
        {
            document.GetProperty("currentVersion").GetString().ShouldBe("1.0.0");
            document.GetProperty("acceptedVersion").ValueKind.ShouldBe(JsonValueKind.Null);
            document.GetProperty("acceptedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetMeLegal_Unauthenticated_Returns401(bool accept)
    {
        using var factory = Factory();
        using var client = factory.AnonymousClient();
        client.DefaultRequestHeaders.Add("Origin", ApiFactory.PublicOrigin);
        using var response = accept
            ? await client.PostAsJsonAsync(AcceptRoute, new { tos = "1.0.0", privacy = "1.0.0" }, Ct)
            : await client.GetAsync(Route, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await JsonAsync(response)).GetProperty("code").GetString().ShouldBe(ErrorCodes.Unauthorized);
    }

    [Theory]
    [InlineData(RequestIp)]
    [InlineData("not-an-ip")]
    [InlineData(null)]
    public async Task Accept_RecordsBothDocumentsAndExactlyOneAuditRow_AndReturnsFreshProjection(string? ip)
    {
        var user = await UserAsync();
        using var factory = Factory(ip);
        using var client = Client(factory, user.Id);
        using var response = await AcceptAsync(client);
        var body = await OkAsync(response);
        foreach (var document in body.GetProperty("documents").EnumerateArray())
        {
            document.GetProperty("acceptedVersion").GetString().ShouldBe("1.0.0");
            document.GetProperty("acceptedAt").GetDateTimeOffset().ShouldBe(_clock.UtcNow);
        }
        SqlProbe.QueryStrings(Database, $"SELECT document || '|' || version FROM user_consents WHERE user_id = '{user.Id}' ORDER BY document")
            .ShouldBe(["privacy|1.0.0", "tos|1.0.0"]);
        SqlProbe.Count(Database, $"SELECT count(*) FROM user_consents WHERE user_id = '{user.Id}' AND accepted_at = '{_clock.UtcNow:O}'")
            .ShouldBe(2);
        var parsed = IPAddress.TryParse(ip, out _);
        SqlProbe.Count(Database, $"SELECT count(*) FROM user_consents WHERE user_id = '{user.Id}' AND ip IS {(parsed ? "NOT NULL" : "NULL")}")
            .ShouldBe(2);
        if (parsed)
        {
            SqlProbe.QueryStrings(Database, $"SELECT host(ip) FROM user_consents WHERE user_id = '{user.Id}'").ShouldBe([ip!, ip!]);
        }
        SqlProbe.Count(Database, $"SELECT count(*) FROM audit_logs WHERE subject_user_id = '{user.Id}' AND action = 'account.legal_accepted' AND visibility = 'user'")
            .ShouldBe(1);
        var data = SqlProbe.ScalarText(Database, $"SELECT data::text FROM audit_logs WHERE subject_user_id = '{user.Id}' AND action = 'account.legal_accepted'");
        data.ShouldContain("1.0.0");
        data.ShouldNotContain(RequestIp);
        data.ShouldNotContain(user.Email!);
        data.ShouldNotContain("userAgent", Case.Insensitive);
    }

    [Fact]
    public async Task Accept_Twice_AppendsRowsWithoutUniqueViolation()
    {
        var user = await UserAsync();
        using var factory = Factory();
        using var client = Client(factory, user.Id);
        using var first = await AcceptAsync(client);
        await OkAsync(first);
        _clock.Advance(TimeSpan.FromSeconds(1));
        using var second = await AcceptAsync(client);
        var accepted = await OkAsync(second);
        accepted.GetProperty("documents")[0].GetProperty("acceptedAt").GetDateTimeOffset().ShouldBe(_clock.UtcNow);
        SqlProbe.Count(Database, $"SELECT count(*) FROM user_consents WHERE user_id = '{user.Id}'").ShouldBe(4);
    }

    [Fact]
    public async Task Accept_PaidRateLimit_AllowsTwoAccepts_ThenRejectsWithoutAppending()
    {
        var user = await UserAsync();
        using var factory = Factory()
            .WithSetting($"{RateLimitingOptions.SectionName}:{nameof(RateLimitingOptions.Paid)}:PermitLimit", "2")
            .WithSetting($"{RateLimitingOptions.SectionName}:{nameof(RateLimitingOptions.Paid)}:SegmentsPerWindow", "2");
        using var client = Client(factory, user.Id);

        using var first = await AcceptAsync(client);
        await OkAsync(first);
        using var second = await AcceptAsync(client);
        await OkAsync(second);

        using var rejected = await AcceptAsync(client);
        rejected.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await JsonAsync(rejected)).GetProperty("code").GetString().ShouldBe(ErrorCodes.RateLimited);
        SqlProbe.Count(Database, $"SELECT count(*) FROM user_consents WHERE user_id = '{user.Id}'").ShouldBe(4);
        SqlProbe.Count(Database, $"SELECT count(*) FROM audit_logs WHERE subject_user_id = '{user.Id}' AND action = 'account.legal_accepted'")
            .ShouldBe(2);
    }

    [Theory]
    [InlineData("0.9.0", "0.9.0", "privacy,tos")]
    [InlineData("1.0.0", "0.9.0", "privacy")]
    [InlineData("0.9.0", "1.0.0", "tos")]
    public async Task Accept_OutdatedVersion_Returns409LegalVersionOutdated_WritesNothing(string tos, string privacy, string expected)
    {
        var user = await UserAsync();
        using var factory = Factory();
        using var client = Client(factory, user.Id);
        using var response = await client.PostAsJsonAsync(AcceptRoute, new { tos, privacy }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = await JsonAsync(response);
        body.GetProperty("code").GetString().ShouldBe(ErrorCodes.LegalVersionOutdated);
        body.GetProperty("errors").GetProperty("documents").EnumerateArray().Select(document => document.GetString())
            .ShouldBe(expected.Split(','));
        SqlProbe.Count(Database, $"SELECT count(*) FROM user_consents WHERE user_id = '{user.Id}'").ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM audit_logs WHERE subject_user_id = '{user.Id}' AND action = 'account.legal_accepted'").ShouldBe(0);
    }

    [Theory]
    [InlineData("{}", "privacy,tos")]
    [InlineData("{\"tos\":\"v1\",\"privacy\":\"1.0.0\"}", "tos")]
    [InlineData("{\"tos\":\"1.0.0\",\"privacy\":null}", "privacy")]
    [InlineData("{\"tos\":\"1.0.0\"}", "privacy")]
    public async Task Accept_InvalidFields_ReturnsCamelCaseValidationErrors(string json, string expected)
    {
        var user = await UserAsync();
        using var factory = Factory();
        using var client = Client(factory, user.Id);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(AcceptRoute, content, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await JsonAsync(response);
        body.GetProperty("code").GetString().ShouldBe(ErrorCodes.ValidationFailed);
        body.GetProperty("errors").EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal)
            .ShouldBe(expected.Split(','));
        SqlProbe.Count(Database, $"SELECT count(*) FROM user_consents WHERE user_id = '{user.Id}'").ShouldBe(0);
    }

    [Fact]
    public async Task Accept_ResponseContainsNeitherIpNorEmail()
    {
        var user = await UserAsync();
        var sink = new CapturingLoggerProvider();
        using var factory = Factory().WithLoggerProvider(sink);
        using var client = Client(factory, user.Id);
        using var success = await AcceptAsync(client);
        success.StatusCode.ShouldBe(HttpStatusCode.OK);
        var text = await success.Content.ReadAsStringAsync(Ct);
        using var stale = await client.PostAsJsonAsync(AcceptRoute, new { tos = "0.9.0", privacy = "0.9.0" }, Ct);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        text += await stale.Content.ReadAsStringAsync(Ct);
        sink.Text.ShouldContain("AcceptLegalDocumentsCommand");
        foreach (var value in new[] { RequestIp, user.Email!, "private-user-agent", "private-token-marker" })
        {
            text.ShouldNotContain(value);
            sink.Text.ShouldNotContain(value);
        }
        text.ShouldNotContain("\"ip\"");
        text.ShouldNotContain("\"email\"");
    }

    [Fact]
    public async Task GetMeLegal_IsOneQueryForBothDocuments()
    {
        var user = await UserAsync();
        await SeedAsync(user.Id, LegalDocument.Tos, "0.9.0", _clock.UtcNow.AddMinutes(-1));
        await SeedAsync(user.Id, LegalDocument.Tos, "1.0.0", _clock.UtcNow);
        await SeedAsync(user.Id, LegalDocument.Privacy, "1.0.0", _clock.UtcNow);
        var interceptor = new CountingDbCommandInterceptor();
        using var factory = Factory().WithServices(services =>
            services.AddDbContext<AppDbContext>((_, options) => options.AddInterceptors(interceptor)));
        using var client = factory.ClientFor(user.Id);
        interceptor.Reset();
        using var response = await client.GetAsync(Route, Ct);
        var body = await OkAsync(response);
        body.GetProperty("documents")[0].GetProperty("acceptedVersion").GetString().ShouldBe("1.0.0");
        interceptor.Count.ShouldBe(1, string.Join(" | ", interceptor.Texts));
        interceptor.Texts[0].ShouldContain("DISTINCT ON", Case.Insensitive);
        interceptor.Texts[0].ShouldNotContain("ip", Case.Insensitive);
        interceptor.ObservedConcurrentExecution.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false, null, "privacy,tos")]
    [InlineData(true, null, "privacy")]
    [InlineData(true, "0.9.0", "privacy")]
    [InlineData(true, "1.0.0", "")]
    public async Task Gate_ResolvedFromHost_EnforcesBothCurrentVersions(bool tos, string? privacy, string expected)
    {
        var user = await UserAsync();
        if (tos)
        {
            await SeedAsync(user.Id, LegalDocument.Tos, "1.0.0", _clock.UtcNow);
        }
        if (privacy is not null)
        {
            await SeedAsync(user.Id, LegalDocument.Privacy, privacy, _clock.UtcNow);
        }
        using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var gate = scope.ServiceProvider.GetRequiredService<ILegalAcceptance>();
        var result = await gate.EnsureCurrentAsync(user.Id, Ct);
        result.IsError.ShouldBe(expected.Length != 0);
        if (result.IsError)
        {
            result.FirstError.Code.ShouldBe(ErrorCodes.LegalAcceptanceRequired);
            result.FirstError.Metadata!["documents"].ShouldBeOfType<string[]>().ShouldBe(expected.Split(','));
        }
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.ChangeTracker.Entries<UserConsent>().ShouldBeEmpty();
        SqlProbe.Count(Database, $"SELECT count(*) FROM user_consents WHERE user_id = '{user.Id}'")
            .ShouldBe((tos ? 1 : 0) + (privacy is null ? 0 : 1));
    }

    [Fact]
    public async Task Gate_WithPendingConsents_DoesNotTreatThemAsCommitted()
    {
        var user = await UserAsync();
        using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.UserConsents.Add(UserConsent.Create(user.Id, LegalDocument.Tos, "1.0.0", _clock.UtcNow, null));
        context.UserConsents.Add(UserConsent.Create(user.Id, LegalDocument.Privacy, "1.0.0", _clock.UtcNow, null));
        var result = await scope.ServiceProvider.GetRequiredService<ILegalAcceptance>().EnsureCurrentAsync(user.Id, Ct);
        result.IsError.ShouldBeTrue();
        result.FirstError.Metadata!["documents"].ShouldBeOfType<string[]>().ShouldBe(["privacy", "tos"]);
    }

    [Fact]
    public async Task GetMeLegal_OnlyReadsCurrentUser()
    {
        var mine = await UserAsync();
        var other = await UserAsync();
        await SeedAsync(other.Id, LegalDocument.Tos, "1.0.0", _clock.UtcNow);
        await SeedAsync(other.Id, LegalDocument.Privacy, "1.0.0", _clock.UtcNow);
        using var factory = Factory();
        using var client = factory.ClientFor(mine.Id);
        using var response = await client.GetAsync(Route, Ct);
        var body = await OkAsync(response);
        body.GetProperty("documents").EnumerateArray().ShouldAllBe(document =>
            document.GetProperty("acceptedVersion").ValueKind == JsonValueKind.Null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://evil.example")]
    public async Task Accept_MissingOrForeignOrigin_IsForbiddenAndWritesNothing(string? origin)
    {
        var user = await UserAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(user.Id);
        if (origin is not null)
        {
            client.DefaultRequestHeaders.Add("Origin", origin);
        }
        using var response = await AcceptAsync(client);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        SqlProbe.Count(Database, $"SELECT count(*) FROM user_consents WHERE user_id = '{user.Id}'").ShouldBe(0);
    }

    private ApiFactory Factory(string? ip = RequestIp) => new ApiFactory(Database, _clock).WithServices(services =>
    {
        services.RemoveAll<IRequestInfoAccessor>();
        services.AddSingleton<IRequestInfoAccessor>(new RequestMetadata(ip));
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

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await JsonAsync(response);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.Clone();
    }

    private sealed class RequestMetadata(string? ip) : IRequestInfoAccessor
    {
        public RequestInfo Current => new(ip, "private-user-agent");
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
                    owner._lines.Add(formatter(state, exception));
                }
            }
        }
    }
}
