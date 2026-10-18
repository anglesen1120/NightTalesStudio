using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Application.Abstractions.Etags;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Features.VideoReview.Outbox;
using NightTales.Domain.Episodes.Events;

namespace NightTales.Application.Features.VideoReview;

/// <summary>
/// The video-review feature's own registrations (09.7.1-T1): the episode ETag read both hosts need, and the
/// <c>video_approved.v1</c>/<c>video_rejected.v1</c> consumers the Worker delivers.
/// </summary>
/// <remarks>
/// <para>
/// The split mirrors <c>AddRenderJobs</c>: <see cref="AddVideoReview"/> holds what a host of either kind
/// composes (the Api serves the two routes, the Worker runs the outbox), and
/// <see cref="AddVideoReviewOutbox"/> holds what only the Worker may: the consumers of an outbox row, one of
/// which sends a render. The Api writes outbox rows and delivers none (09 README D12), so it must not call the
/// second one.
/// </para>
/// <para>
/// The commands, their validators and their handlers need no call here: the Mediator source generator and
/// <c>AddValidatorsFromAssemblyContaining</c> register them from this assembly. The
/// <c>Assets:RetentionDays</c> value the approve handler takes directly is bound and validated by each
/// composition root, which owns the configuration (the Application layer may not reference
/// <c>Microsoft.Extensions.Options</c>).
/// </para>
/// </remarks>
public static class VideoReviewServiceCollectionExtensions
{
    /// <summary>
    /// Registers the services both hosts compose: the episode ETag read of the re-render path.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <returns><paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddVideoReview(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IEpisodeEtagReader, EpisodeEtagReader>();

        return services;
    }

    /// <summary>
    /// Registers the outbox consumers of the two video-review events, in the order they run for one message:
    /// both real-time projections first, then the re-render, so a failed render cannot cost the page update.
    /// </summary>
    /// <remarks>
    /// The Worker is the only caller: a consumer runs in a delivery scope with a real mediator and a real job
    /// scheduler behind it, which the Api does not have (09 README D12).
    /// </remarks>
    /// <param name="services">The Worker's service collection.</param>
    /// <returns><paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddVideoReviewOutbox(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOutboxHandler<VideoApproved, VideoApprovedRealtimeHandler>();
        services.AddOutboxHandler<VideoRejected, VideoRejectedRealtimeHandler>();
        services.AddOutboxHandler<VideoRejected, VideoReRenderHandler>();

        return services;
    }
}
