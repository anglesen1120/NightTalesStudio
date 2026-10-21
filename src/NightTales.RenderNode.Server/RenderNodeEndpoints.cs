using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NightTales.RenderNode.Contract;

namespace NightTales.RenderNode.Server;

/// <summary>
/// Maps the render-node interface: the eight routes of 05.4.1-T2 §2 and nothing else, plus a terminal catch-all
/// that answers every other path with <c>404 adapter_route_not_found</c>.
/// </summary>
/// <remarks>
/// The catch-all is an explicit <c>/{**path}</c> route with <see cref="int.MaxValue"/> order rather than
/// <c>MapFallback</c>: the built-in fallback carries the <c>nonfile</c> constraint, so a path with a dot would
/// fall through to a bare 404 and violate §1's "any path outside §2". Authentication still runs first
/// (<see cref="ApiKeyMiddleware"/> is registered by <c>UseRenderNodeServer</c> before routing hands the request
/// to an endpoint), which is why an unknown path with a bad key is 401 and with a valid key is 404.
/// </remarks>
public static class RenderNodeEndpoints
{
    /// <summary>Every path outside the contract.</summary>
    private const string CatchAllPattern = "/{**path}";

    /// <summary>Maps the eight routes and the catch-all onto <paramref name="routes"/>.</summary>
    public static IEndpointRouteBuilder MapRenderNodeV1(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost(RenderNodeRoutes.Renders, SubmitEndpoint.HandleAsync)
            .WithMetadata(new RequestBodyMetadata());
        routes.MapGet(RenderNodeRoutes.RenderById, StatusEndpoint.HandleAsync);
        routes.MapGet(RenderNodeRoutes.RenderVideo, OutputEndpoint.VideoAsync);
        routes.MapGet(RenderNodeRoutes.RenderSubtitles, OutputEndpoint.SubtitlesAsync);
        routes.MapPost(RenderNodeRoutes.RenderCancel, LifecycleEndpoints.CancelAsync);
        routes.MapDelete(RenderNodeRoutes.RenderById, LifecycleEndpoints.DeleteAsync);
        routes.MapGet(RenderNodeRoutes.Health, HealthEndpoint.HealthAsync);
        routes.MapPost(RenderNodeRoutes.ProbeImage, HealthEndpoint.ProbeImageAsync)
            .WithMetadata(new RequestBodyMetadata());

        routes.Map(CatchAllPattern, RouteNotFoundAsync).WithOrder(int.MaxValue);
        return routes;
    }

    private static Task RouteNotFoundAsync(HttpContext context) =>
        RenderNodeEnvelope.WriteErrorAsync(context, StatusCodes.Status404NotFound, AdapterErrorCodes.RouteNotFound);
}
