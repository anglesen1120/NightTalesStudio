using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Application.Abstractions.Jobs;

namespace NightTales.Application.Features.Rendering;

/// <summary>
/// The ingest leg's own registrations of 09.6.3-T2, called by <c>AddRenderJobs</c> so that a replacement of the
/// leg stays additive: the job itself (<see cref="Jobs.IngestRenderOutputJob"/>), the options it reads and the
/// ports it needs are registered by the composition root that owns them, not by this method (Application may not
/// reference <c>Microsoft.Extensions.Options</c>, so <c>Render:Ingest</c>/<c>Assets:UnapprovedRetentionDays</c>
/// are bound and validated where <c>RenderOptions</c> is).
/// </summary>
/// <remarks>
/// The only member here is the attempt port: <see cref="JobAttemptScope"/> carries the scheduler's attempt number
/// through the execution context and <see cref="AmbientJobAttempt"/> reads it, so the ingest can tell its last
/// chance from a retry (WT52) without the Application layer referencing a scheduler (D1/D12). It is stateless and
/// therefore a singleton.
/// </remarks>
public static class RenderIngestServiceCollectionExtensions
{
    /// <summary>Registers the ingest leg's own services on <paramref name="services"/>.</summary>
    /// <param name="services">The Worker's service collection.</param>
    /// <returns><paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddRenderIngest(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IJobAttempt, AmbientJobAttempt>();

        return services;
    }
}
