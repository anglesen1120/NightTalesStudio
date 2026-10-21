using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;

namespace NightTales.TestKit.Api;

/// <summary>
/// Maps test-only endpoints on the host built from <c>Program</c> (09 README D13). Minimal-API
/// endpoints can only be added to the <see cref="IEndpointRouteBuilder"/> the host builds, and
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/> never exposes
/// it, so this <see cref="IStartupFilter"/> appends the mappings through <c>UseEndpoints</c> after the
/// host's own pipeline is configured.
/// </summary>
/// <remarks>
/// The filter runs after the host's configure delegate, which has already called <c>UseRouting</c>
/// (the app registers at least one endpoint) and registered the route builder in the pipeline
/// properties, so <c>UseEndpoints</c> maps into the same data sources routing already watches.
/// <see cref="ApiFactory"/> registers this filter for its callers; a test may also register it
/// directly to prove the production host does not authenticate through <see cref="TestAuthHandler"/>.
/// </remarks>
/// <param name="mappings">The mappings to apply to the host's endpoint route builder.</param>
public sealed class MapEndpointsStartupFilter(params Action<IEndpointRouteBuilder>[] mappings) : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return application =>
        {
            next(application);

            application.UseEndpoints(routes =>
            {
                foreach (var mapping in mappings)
                {
                    mapping(routes);
                }
            });
        };
    }
}
