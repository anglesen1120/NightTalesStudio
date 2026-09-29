using ErrorOr;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using NightTales.Api.Http;
using NightTales.Api.Http.Filters;
using NightTales.Application.Common;

namespace NightTales.Api.IntegrationTests.Http;

/// <summary>
/// The probe routes of 09.1.4-T1: they map the real helpers and filters onto the host built from
/// <c>Program</c>, through <c>ApiFactory.MapEndpoints</c>, so the filters, the headers and the status
/// codes are exercised end to end instead of being asserted on a mock.
/// </summary>
internal static class ProbeEndpoints
{
    /// <summary>201 + <c>Location</c> + the ETag the delegate reads after the handler returned.</summary>
    internal const string CreatedPath = "/__probe/http/created";

    /// <summary>201 + the ETag of a value that implements <see cref="IHasETag"/> when the delegate returns null.</summary>
    internal const string TaggedPath = "/__probe/http/tagged";

    /// <summary>A route that requires <c>If-Match</c>.</summary>
    internal const string IfMatchPath = "/__probe/http/if-match";

    /// <summary>A route that requires <c>Idempotency-Key</c>.</summary>
    internal const string IdempotencyPath = "/__probe/http/idempotency";

    /// <summary>A route whose handler throws; the canary below must never reach the client.</summary>
    internal const string ThrowPath = "/__probe/http/throw";

    /// <summary>The text of the unhandled exception; a 500 body must not contain it.</summary>
    internal const string ThrowMessage = "probe-secret-leak-canary-41c9";

    /// <summary>The ETag <see cref="CreatedPath"/> answers, as if <c>xmin</c> had been read at commit.</summary>
    internal const string CreatedETag = "\"ep-11\"";

    internal static void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        // Reads the ETag through the delegate of ToCreatedResult, which runs after the handler (and,
        // in a real endpoint, after the unit of work committed) has assigned xmin (09 README D5/D15).
        // The extra CancellationToken parameter keeps this the route-handler overload of MapPost: a
        // lambda of one parameter returning Task<IResult> would bind to RequestDelegate (ASP0016).
        routes.MapPost(CreatedPath, (HttpContext http, CancellationToken _) => Value(new ProbeCreated(7))
            .ToCreatedResult(
                http,
                created => $"{CreatedPath}/{created.Id}",
                (_, _) => Task.FromResult<string?>(CreatedETag)));

        // The value itself carries the ETag; a delegate that has nothing to add must not clear it.
        routes.MapPost(TaggedPath, (HttpContext http, CancellationToken _) => Value(new ProbeTagged(5, 3))
            .ToCreatedResult(
                http,
                tagged => $"{TaggedPath}/{tagged.Id}",
                (_, _) => Task.FromResult<string?>(null)));

        routes.MapPost(IfMatchPath, ([FromHeader(Name = RequireIfMatchFilter.HeaderName)] string? ifMatch) =>
                Results.Ok(new IfMatchProbe(ifMatch)))
            .RequireIfMatch();

        routes.MapPost(IdempotencyPath, () => Results.Ok(new IdempotencyProbe("accepted")))
            .RequireIdempotencyKey();

        routes.MapGet(ThrowPath, () => Throw());
    }

    private static IResult Throw() => throw new InvalidOperationException(ThrowMessage);

    private static ErrorOr<T> Value<T>(T value) => value;

    /// <summary>The 201 body of <see cref="CreatedPath"/>.</summary>
    internal sealed record ProbeCreated(int Id);

    /// <summary>The 201 body of <see cref="TaggedPath"/>; the ETag comes from the value.</summary>
    internal sealed record ProbeTagged(int Id, uint Xmin) : IHasETag
    {
        /// <inheritdoc />
        public string ETag => ETags.Episode(Xmin);
    }

    /// <summary>Echoes the <c>If-Match</c> header the binder produced.</summary>
    internal sealed record IfMatchProbe(string? IfMatch);

    /// <summary>The body of the route behind the idempotency filter.</summary>
    internal sealed record IdempotencyProbe(string Outcome);
}
