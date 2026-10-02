using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NightTales.Api.Http.Filters;

namespace NightTales.Api.IntegrationTests.Flags;

/// <summary>
/// The test-only guarded route of 09.9.2-T2: it carries <c>RequireFeature("youtube_publish")</c> exactly
/// as 02.7 / 02.8.3 / 06 will carry it, mapped onto the real host through <c>ApiFactory.MapEndpoints</c>.
/// </summary>
/// <remarks>
/// This task adds no route (the guarded routes are its consumers'), so a probe route is the only honest
/// way to observe the guard against the real pipeline — the same reason 09.10.4-T1's route guards are
/// observed on <c>MfaProbeEndpoints</c>. The counter is how "the handler was not invoked" is asserted.
/// </remarks>
internal static class FlagsProbeEndpoints
{
    /// <summary>The flag the probe route is guarded by.</summary>
    internal const string GuardedKey = "youtube_publish";

    /// <summary>A route that exists only while <see cref="GuardedKey"/> is on.</summary>
    internal const string GuardedPath = "/__probe/flags/guarded";

    /// <summary>The body the guarded handler returns when it runs.</summary>
    internal const string HandlerBody = "flags-probe-handler-ran";

    private static int _invocations;

    /// <summary>How many times the guarded handler ran since the last <see cref="Reset"/>.</summary>
    internal static int Invocations => Volatile.Read(ref _invocations);

    /// <summary>Clears the invocation count, so one test cannot read another's.</summary>
    internal static void Reset() => Volatile.Write(ref _invocations, 0);

    internal static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet(GuardedPath, () =>
            {
                Interlocked.Increment(ref _invocations);

                return Results.Text(HandlerBody);
            })
            // No RequireAuthorization and no AllowAnonymous: the route is subject to the host's fallback
            // policy only, which asserts nothing about a principal — that is what lets an anonymous caller
            // reach the guard and observe the 404 the guard is supposed to answer.
            .RequireFeature(GuardedKey);
    }
}
