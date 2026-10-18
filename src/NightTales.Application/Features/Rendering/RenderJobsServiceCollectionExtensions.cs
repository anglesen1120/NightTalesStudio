using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Features.Rendering.Jobs;
using NightTales.Application.Features.Rendering.Outbox;
using NightTales.Application.Rendering;
using NightTales.Domain.Rendering;

namespace NightTales.Application.Features.Rendering;

/// <summary>
/// The registration of the render pipeline of 09.6 (05 D5, 09 README D12): the submit and poll jobs, the retry
/// policy and the failure recorder, the metrics and the two extension-point defaults, plus the outbox handler
/// that turns <c>render_requested.v1</c> into a <see cref="SubmitRenderJob"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every registration is a <c>TryAdd</c>: 03.4.2-T1 replaces <see cref="IRenderTerminalHooks"/>,
/// 09.6.3-T2 replaces <see cref="IRenderIngestEnqueuer"/>, and both replacements are additive rather than a
/// second registration of the same service.
/// </para>
/// <para>
/// <b>The Worker is the only caller.</b> The jobs and the handler need a real <see cref="IJobScheduler"/>; the
/// Api's <c>ThrowingJobScheduler</c> refuses every call, so the Api must never compose this method (09 README
/// D12: the Api enqueues nothing, side effects go through the outbox and are delivered in the Worker).
/// </para>
/// </remarks>
public static class RenderJobsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the render jobs, their ports and the <c>render_requested.v1</c> handler on
    /// <paramref name="services"/>.
    /// </summary>
    /// <param name="services">The Worker's service collection.</param>
    /// <returns><paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddRenderJobs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<SubmitRenderJob>();
        services.TryAddScoped<PollRenderJob>();
        services.TryAddScoped<IRenderRetryPolicy, RenderRetryPolicy>();
        services.TryAddScoped<IRenderFailureRecorder, RenderFailureRecorder>();
        services.TryAddScoped<IRenderTerminalHooks, DefaultRenderTerminalHooks>();

        services.TryAddSingleton<RenderMetrics>();
        services.TryAddSingleton<IRenderIngestEnqueuer, LoggingRenderIngestEnqueuer>();

        services.AddOutboxHandler<RenderRequested, RenderRequestedHandler>();

        return services;
    }
}
