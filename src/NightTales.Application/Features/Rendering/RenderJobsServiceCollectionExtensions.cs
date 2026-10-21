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
/// The registration of the render pipeline of 09.6 (05 D5, 09 README D12): the submit, poll and ingest jobs,
/// the retry policy and the failure recorder, the metrics, the terminal-hooks default and the enqueuer the
/// poller hands a committed <c>ingesting</c> row to, plus the two outbox handlers that turn
/// <c>render_requested.v1</c> into a <see cref="SubmitRenderJob"/> and <c>render_cancel_requested.v1</c> into the
/// adapter cancel call of <see cref="RenderCancelRequestedHandler"/>. The ingest job's own dependencies come from
/// <c>AddRenderIngest</c> (09.6.3-T2), which this method calls.
/// </summary>
/// <remarks>
/// <para>
/// Every registration is a <c>TryAdd</c>: 03.4.2-T1 replaces <see cref="IRenderTerminalHooks"/> and
/// 09.6.3-T2's ingest leg adds its own services through <c>AddRenderIngest</c>, so a replacement is additive
/// rather than a second registration of the same service.
/// </para>
/// <para>
/// <b>The Worker is the only caller.</b> The jobs, the enqueuer and the <c>render_requested.v1</c> handler need
/// a real <see cref="IJobScheduler"/> — the Api's <c>ThrowingJobScheduler</c> refuses every call — and the
/// <c>render_cancel_requested.v1</c> handler resolves a node client, which the request path must never hold, so
/// the Api must never compose this method (09 README D11/D12: the Api enqueues nothing and calls no node, side
/// effects go through the outbox and are delivered in the Worker).
/// </para>
/// </remarks>
public static class RenderJobsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the render jobs, their ports and the <c>render_requested.v1</c> and
    /// <c>render_cancel_requested.v1</c> handlers on <paramref name="services"/>.
    /// </summary>
    /// <param name="services">The Worker's service collection.</param>
    /// <returns><paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddRenderJobs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<SubmitRenderJob>();
        services.TryAddScoped<PollRenderJob>();
        services.TryAddScoped<IngestRenderOutputJob>();
        services.TryAddScoped<IRenderRetryPolicy, RenderRetryPolicy>();
        services.TryAddScoped<IRenderFailureRecorder, RenderFailureRecorder>();
        services.TryAddScoped<IRenderTerminalHooks, DefaultRenderTerminalHooks>();

        services.TryAddSingleton<RenderMetrics>();
        services.TryAddSingleton<IRenderIngestEnqueuer, SchedulerRenderIngestEnqueuer>();

        services.AddOutboxHandler<RenderRequested, RenderRequestedHandler>();

        // 09.6.4-T1 (BD8): the same graph's cancel consumer. It is registered here, not in the Api's composition
        // root, because only the Worker runs the outbox dispatcher and only the Worker may hold a node client on
        // a cancel path (09 README D11/D12).
        services.AddOutboxHandler<RenderCancelRequested, RenderCancelRequestedHandler>();

        // 09.6.3-T2: the ingest leg's own graph (its temp-file workspace, its options and validators, its
        // ports), which the job above and the Worker's other render registrations resolve.
        services.AddRenderIngest();

        return services;
    }
}
