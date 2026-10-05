using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Common.Errors;
using NightTales.Infrastructure.Identity;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Auth;

/// <summary>
/// <c>POST /api/v1/auth/register</c> in P (09.3.1-T1 Tests table): the closed registration gate
/// answers 403 <c>registration_closed</c> through the real pipeline, writes nothing, and does not care
/// what <c>Registration:Mode</c> says — only 09.3.1-T3 swaps the flow.
/// </summary>
public sealed class RegistrationGateTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Path = "/api/v1/auth/register";

    private const string Password = "Abcdefghijkl";

    [Fact]
    public async Task Register_ClosedMode_Returns403RegistrationClosed()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = $"closed-{Guid.CreateVersion7():N}@nighttales.test";

        using var factory = new ApiFactory(Database, new FakeClock());
        var client = factory.AnonymousClient();

        var response = await client.PostAsJsonAsync(Path, Body(email), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, ct);
        problem.ShouldNotBeNull();
        problem.Code.ShouldBe(ErrorCodes.RegistrationClosed);
        problem.Status.ShouldBe((int)HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Register_ClosedMode_ResponseBodyHasNoCodes()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = $"nobody-{Guid.CreateVersion7():N}@nighttales.test";

        using var factory = new ApiFactory(Database, new FakeClock());
        var client = factory.AnonymousClient();

        var response = await client.PostAsJsonAsync(Path, Body(email), ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, ct);

        problem.ShouldNotBeNull();
        problem.Code.ShouldBe(ErrorCodes.RegistrationClosed);
        problem.Errors.ShouldBeNull("a closed gate reports no field errors and no account data");
        body.ShouldNotContain(email, Case.Sensitive, "the refused body echoes no request data");
        body.ShouldNotContain(Password, Case.Sensitive, "the refused body echoes no request data");
        SqlProbe.Count(Database, $"SELECT count(*) FROM users WHERE email = '{email}'").ShouldBe(
            0,
            "the refused registration wrote no user row");
    }

    [Fact]
    public async Task Register_OpenMode_StillReturns403RegistrationClosed()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = $"open-{Guid.CreateVersion7():N}@nighttales.test";

        // The real configuration key, set above appsettings.json the way the test harness sets the
        // connection string: the P flow, not the configured default, is what refuses.
        using var host = ApiFactory.ProductionHost(
            Database.ConnectionString,
            builder => builder.UseSetting(
                $"{RegistrationOptions.SectionName}:{nameof(RegistrationOptions.Mode)}",
                nameof(RegistrationMode.Open)));

        host.Services.GetRequiredService<IOptions<RegistrationOptions>>().Value.Mode
            .ShouldBe(RegistrationMode.Open);

        var response = await host.CreateClient().PostAsJsonAsync(Path, Body(email), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, ct);
        problem.ShouldNotBeNull();
        problem.Code.ShouldBe(ErrorCodes.RegistrationClosed);
    }

    [Fact]
    public async Task Register_MalformedBody_IsRejectedByValidationBeforeTheGate()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        var client = factory.AnonymousClient();

        // No consents at all: the D4 ValidationBehavior runs before the handler, so the answer is 400
        // validation_failed — the closed gate's 403 is never reached, and the account lookup is never
        // an enumeration oracle for a malformed body.
        var response = await client.PostAsJsonAsync(
            Path,
            new
            {
                email = $"shape-{Guid.CreateVersion7():N}@nighttales.test",
                password = Password,
                consents = Array.Empty<object>(),
            },
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, ct);

        problem.ShouldNotBeNull();
        problem.Code.ShouldBe(ErrorCodes.ValidationFailed);
        problem.Errors.ShouldNotBeNull();
        problem.Errors.ShouldContainKey("consents");
    }

    [Fact]
    public async Task Register_UnknownConsentDocument_IsRejectedBeforeTheGate()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        var client = factory.AnonymousClient();

        // A document outside the LegalDocument enum fails the snake_case enum binding, so the request
        // is rejected as a malformed body (400) rather than reaching the gate or the database CHECK.
        var response = await client.PostAsJsonAsync(
            Path,
            new
            {
                email = $"document-{Guid.CreateVersion7():N}@nighttales.test",
                password = Password,
                consents = new[]
                {
                    new { document = "tos", version = "1.0.0" },
                    new { document = "marketing", version = "v1" },
                },
            },
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, ct);
        problem.ShouldNotBeNull();
        problem.Code.ShouldBe(ErrorCodes.ValidationFailed, "a binding failure is a client error, never 500 internal_error");
        problem.Status.ShouldBe((int)HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Register_IntegerConsentDocument_IsRejectedByTheValidator()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        var client = factory.AnonymousClient();

        // JsonStringEnumConverter accepts integer values, so document: 7 binds to (LegalDocument)7; the
        // validator is what must refuse it (Enum.IsDefined), before the gate and before the database
        // CHECK constraint could answer with a raw DbUpdateException.
        var response = await client.PostAsJsonAsync(
            Path,
            new
            {
                email = $"numeric-{Guid.CreateVersion7():N}@nighttales.test",
                password = Password,
                consents = new object[]
                {
                    new { document = "tos", version = "1.0.0" },
                    new { document = "privacy", version = "1.0.0" },
                    new { document = 7, version = "v1" },
                },
            },
            ct);

        response.StatusCode.ShouldBe(
            HttpStatusCode.BadRequest,
            "document: 7 binds but is not a known acceptance, so the shape validation refuses it");

        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, ct);
        problem.ShouldNotBeNull();
        problem.Code.ShouldBe(ErrorCodes.ValidationFailed);
        problem.Errors.ShouldNotBeNull();
        problem.Errors.ShouldContainKey("consents");
        problem.Errors["consents"].ShouldContain("consent_document_unknown");
    }

    [Fact]
    public async Task Register_NullConsentEntry_IsRejectedBeforeTheGate()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        var client = factory.AnonymousClient();

        // `consents: [null]` is legal JSON: the shape validation must answer 400, never a
        // NullReferenceException turned into 500 (T3 C1).
        var response = await client.PostAsJsonAsync(
            Path,
            new
            {
                email = $"null-entry-{Guid.CreateVersion7():N}@nighttales.test",
                password = Password,
                consents = new object?[] { null, new { document = "privacy", version = "1.0.0" } },
            },
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "a null consent entry is a shape error, not a server fault");
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, ct);
        problem.ShouldNotBeNull();
        problem.Code.ShouldBe(ErrorCodes.ValidationFailed);
        problem.Errors.ShouldNotBeNull();
        problem.Errors.ShouldContainKey("consents[0]");
    }

    [Fact]
    public async Task Register_MissingConsentDocument_IsRejectedBeforeTheGate()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        var client = factory.AnonymousClient();

        // Without `document` the enum would bind its default (Tos) and the body would read as an implicit
        // ToS acceptance; the member is required on the wire, so this is a 400 (T3 C3).
        var response = await client.PostAsJsonAsync(
            Path,
            new
            {
                email = $"missing-document-{Guid.CreateVersion7():N}@nighttales.test",
                password = Password,
                consents = new object[] { new { version = "v1" }, new { document = "privacy", version = "1.0.0" } },
            },
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, ct);
        problem.ShouldNotBeNull();
        problem.Code.ShouldBe(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Register_MalformedJsonBody_IsRejectedWithAProblem()
    {
        var ct = TestContext.Current.CancellationToken;

        using var factory = new ApiFactory(Database, new FakeClock());
        var client = factory.AnonymousClient();
        using var content = new StringContent(
            """{"email":"broken@nighttales.test","password":"Abcdefghijkl","consents":[""",
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync(Path, content, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ProblemResponse.Options, ct);
        problem.ShouldNotBeNull();
        problem.Code.ShouldBe(ErrorCodes.ValidationFailed, "a syntactic JSON error is a client error, never 500 internal_error");
    }

    private static object Body(string email) => new
    {
        email,
        password = Password,
        consents = new[]
        {
            new { document = "tos", version = "1.0.0" },
            new { document = "privacy", version = "1.0.0" },
        },
    };
}
