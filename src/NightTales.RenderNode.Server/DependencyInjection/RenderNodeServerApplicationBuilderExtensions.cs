using Microsoft.AspNetCore.Builder;

namespace NightTales.RenderNode.Server;

/// <summary>
/// Adds the three middlewares of the core in the one order that satisfies 05.4.1-T2 §1, so the adapter host and
/// mock-mpt cannot assemble the pipeline differently:
/// <list type="number">
///   <item><description><see cref="RenderNodeExceptionMiddleware"/> outermost, so it can turn a Kestrel body-limit fault into the 413 envelope;</description></item>
///   <item><description><see cref="ApiKeyMiddleware"/> before routing and before any body is read (§10 T68);</description></item>
///   <item><description><see cref="BodySizeLimitMiddleware"/> after routing, because it reads the per-endpoint cap marker.</description></item>
/// </list>
/// </summary>
public static class RenderNodeServerApplicationBuilderExtensions
{
    /// <summary>
    /// Adds the core's middleware pipeline. Call it after routing is available (a <c>WebApplication</c> inserts
    /// <c>UseRouting</c> before user middleware) and before <c>MapRenderNodeV1</c>.
    /// </summary>
    public static IApplicationBuilder UseRenderNodeServer(this IApplicationBuilder application)
    {
        ArgumentNullException.ThrowIfNull(application);

        application.UseMiddleware<RenderNodeExceptionMiddleware>();
        application.UseMiddleware<ApiKeyMiddleware>();
        application.UseMiddleware<BodySizeLimitMiddleware>();
        return application;
    }
}
