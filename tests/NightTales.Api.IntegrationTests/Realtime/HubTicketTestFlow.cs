using System.Net;
using System.Text.Json;
using ErrorOr;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.Endpoints;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.Realtime;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Application.Common.Errors;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Realtime;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Shouldly;

namespace NightTales.Api.IntegrationTests.Realtime;

/// <summary>
/// The shared plumbing of 09.8.1-T2's ticket tests: a real user row, the issue call, a claim through
/// the host's own production validator, and the 401 shape every failed claim answers.
/// </summary>
/// <remarks>
/// The host is always the production Api composition root with a real PostgreSQL (09 README D13), so
/// the row a test asserts on is the one the pipeline committed — never a double's.
/// </remarks>
internal static class HubTicketTestFlow
{
    /// <summary>The issue route, as the client sees it.</summary>
    internal const string TicketPath = HubEndpoints.GroupPrefix + HubEndpoints.TicketRoute;

    /// <summary>The route the hub client handshakes on; a negotiate carrying a ticket is a claim.</summary>
    internal const string NegotiatePath = EpisodeHub.Path + "/negotiate";

    /// <summary>The shipped ticket lifetime (BD11), read from the options' own default.</summary>
    internal static readonly TimeSpan ShippedTtl =
        TimeSpan.FromSeconds(RealtimeOptions.DefaultTicketTtlSeconds);

    /// <summary>Web defaults, so the documented camelCase body binds to these records.</summary>
    internal static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);

    /// <summary>Inserts one real <c>users</c> row (the ticket's foreign key needs it).</summary>
    /// <param name="database">The class database.</param>
    /// <param name="ct">The test's token.</param>
    internal static async Task<AppUser> CreateUserAsync(TestDatabase database, CancellationToken ct) =>
        await new UserBuilder(database, new TestCurrentUser()).CreateAsync(ct: ct);

    /// <summary>
    /// Issues one ticket and returns the documented body. Asserts the 200 and the <c>no-store</c> a
    /// bearer-secret response must carry, so every test that issues a ticket also proves the header.
    /// </summary>
    /// <param name="client">An authenticated client of the host.</param>
    /// <param name="ct">The test's token.</param>
    internal static async Task<IssuedTicket> IssueAsync(HttpClient client, CancellationToken ct)
    {
        using var response = await client.PostAsync(TicketPath, content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoStore.ShouldBe(
            true,
            "a ticket is a bearer secret for its 60 seconds: no cache may keep it");

        return await ReadIssuedAsync(response, ct);
    }

    /// <summary>Reads the documented <c>{ ticket, expiresAt }</c> body of a 200.</summary>
    /// <param name="response">The issue response.</param>
    /// <param name="ct">The test's token.</param>
    internal static async Task<IssuedTicket> ReadIssuedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        var issued = JsonSerializer.Deserialize<IssuedTicket>(body, Json);

        issued.ShouldNotBeNull();

        return issued!;
    }

    /// <summary>Claims one ticket through the host's own production validator.</summary>
    /// <param name="factory">The host under test.</param>
    /// <param name="ticket">The plaintext the client was handed.</param>
    /// <param name="ct">The test's token.</param>
    internal static async Task<ErrorOr<HubTicketClaim>> ClaimAsync(
        ApiFactory factory,
        string ticket,
        CancellationToken ct)
    {
        using var scope = factory.Services.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<IHubTicketValidator>();

        return await validator.ClaimAsync(ticket, ct);
    }

    /// <summary>Asserts the documented 401 <c>hub_ticket_invalid</c> problem of a refused connect.</summary>
    /// <param name="response">The negotiate response.</param>
    /// <param name="ct">The test's token.</param>
    internal static async Task AssertHubTicketInvalidAsync(HttpResponseMessage response, CancellationToken ct)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var problem = await ProblemAsync(response, ct);

        problem.Code.ShouldBe(ErrorCodes.HubTicketInvalid);
        problem.Status.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    /// <summary>Deserializes a problem body.</summary>
    /// <param name="response">Any error response.</param>
    /// <param name="ct">The test's token.</param>
    internal static async Task<ProblemResponse> ProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        var problem = JsonSerializer.Deserialize<ProblemResponse>(body, ProblemResponse.Options);

        problem.ShouldNotBeNull();

        return problem!;
    }
}

/// <summary>The documented 200 body of <c>POST /api/v1/hubs/ticket</c> (BD11).</summary>
/// <param name="Ticket">The 43-character base64url ticket.</param>
/// <param name="ExpiresAt">The instant the ticket stops being claimable.</param>
internal sealed record IssuedTicket(string Ticket, DateTimeOffset ExpiresAt);
