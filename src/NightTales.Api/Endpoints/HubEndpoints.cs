using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NightTales.Api.Http;
using NightTales.Api.RateLimiting;
using NightTales.Application.Features.Realtime.Commands.IssueHubTicket;

namespace NightTales.Api.Endpoints;

/// <summary>
/// The <c>/api/v1/hubs</c> route group of 09.8.1-T2 (09 README D14, BD11): the one route that hands a
/// logged-in client the short-lived ticket it connects with.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint is deliberately thin (09.1.2-T1): it binds nothing, sends the command through the D4
/// pipeline and turns the outcome into the documented response. Everything about the ticket — its
/// entropy, its hash-at-rest table, its TTL and its single use — belongs to the handler and the claim,
/// not here.
/// </para>
/// <para>
/// The access token is the only accepted credential on this route, and the rate-limit policy is
/// <c>hub</c> (10 tickets a minute per caller, BD14): a connect loop that exhausts it is backed off by
/// the <c>Retry-After</c> the policy's rejection writes, and a ticket is needed only once per
/// connection.
/// </para>
/// </remarks>
public static class HubEndpoints
{
    /// <summary>The group's prefix; the ticket route hangs under it.</summary>
    public const string GroupPrefix = "/api/v1/hubs";

    /// <summary>The ticket route: <c>POST /api/v1/hubs/ticket</c> (BD11).</summary>
    public const string TicketRoute = "/ticket";

    /// <summary>Maps the hub's ticket route onto the host's route builder.</summary>
    /// <param name="routes">The host's endpoint route builder.</param>
    public static IEndpointRouteBuilder MapHubEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var hubs = routes.MapGroup(GroupPrefix);

        // 09.9.4-T1 (BD12): the versioned API group carries the non-credentialed CORS policy. The
        // ticket travels in the body, never in a cookie, so the credentialed policy is not for it.
        hubs.RequireCors(SecurityHeadersExtensions.SpaCorsPolicy);

        hubs.MapPost(TicketRoute, IssueTicketAsync)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.Hub)
            .Produces<HubTicketDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return routes;
    }

    /// <summary>
    /// <c>POST /api/v1/hubs/ticket</c>: 200 with the ticket and its expiry, or the documented 401/429/503.
    /// </summary>
    /// <remarks>
    /// The response is a bearer secret for its 60-second life, so it is marked <c>no-store</c> exactly
    /// like the auth responses of 09.3.2-T1: no proxy or browser cache may keep a ticket a client did
    /// not ask for.
    /// </remarks>
    private static async Task<IResult> IssueTicketAsync(ISender sender, HttpContext http, CancellationToken ct)
    {
        var result = await sender.Send(new IssueHubTicketCommand(), ct).ConfigureAwait(false);

        if (!result.IsError)
        {
            http.Response.Headers.CacheControl = "no-store";
        }

        return result.ToHttpResult(http);
    }
}
