using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Features.Autopilot.Orchestration.Steps;
using NightTales.Domain.Episodes.Events;
using NightTales.Domain.Publishing;
using NightTales.Domain.Rendering;
using NightTales.Domain.Research.Events;
using NightTales.Domain.Series.Events;
using NightTales.Domain.Stories.Events;
using NightTales.Domain.Stories.Generation.Events;

namespace NightTales.Application.Features.Autopilot.Events;

/// <summary>
/// The composition root of the autopilot's event-driven half (02.8.3 plan §7.2): every event that can advance a run,
/// the locator the handlers resolve runs with, and the two step ports no handler scan can register.
/// </summary>
/// <remarks>
/// <para>
/// <b>Several handlers per event are supported.</b> The dispatcher runs every registration of the event's type in
/// registration order (09 README D11), so these deliveries sit beside the existing consumers of
/// <c>story_approved.v1</c>, <c>video_approved.v1</c> and <c>publication_updated.v1</c> instead of replacing them.
/// </para>
/// <para>
/// <b>Order within an event.</b> The handlers of one event are independent: each resolves the run and asks the
/// orchestrator for the next step, and the compare-and-set makes the second call of a delivery a no-op. The order
/// below is therefore the plan's reading order, not a dependency.
/// </para>
/// <para>
/// <b>What is registered here that is not an outbox handler.</b> <see cref="IAutopilotRunLocator"/> is the handlers'
/// own dependency. The step handlers' own collaborators (<c>ISourceFinderGo</c>, the step reader, the consent gate)
/// are registered by <c>AddAutopilotOrchestration</c>, which owns the handler scan: a port lives with the code that
/// resolves it.
/// </para>
/// <para>
/// <b>Why this extension is the home of the accepted-event consumer.</b> This is the Worker-only side of the
/// autopilot composition (<c>AddAutopilotOrchestration</c> and this call are made by the Worker host alone), and
/// <see cref="AutopilotAuthorizationAcceptedHandler"/> drives <c>IAutopilotRunOrchestrator</c>, which only that
/// Worker-only extension registers. Registering it from a shared extension — <c>AddAutopilotAuthorization</c> is
/// called by <c>AddNightTalesApplication</c>, i.e. by the Api too — would give the Api a concrete descriptor it can
/// never construct, so the Api's Development build (<c>ValidateOnBuild</c>) would fail at start-up (02.8.3-T2 QA
/// D11).
/// </para>
/// </remarks>
/// <param name="services">The host's service collection.</param>
/// <returns>The same collection, for chaining.</returns>
public static class AutopilotEventsServiceCollectionExtensions
{
    /// <summary>Registers the run locator, every step-trigger handler and the accepted-event consumer.</summary>
    /// <param name="services">The host's service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddAutopilotOutboxHandlers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IAutopilotRunLocator, AutopilotRunLocator>();

        // S71-7: an acceptance does not advance a run by itself. The aggregate raises the event and this consumer
        // routes it to T1's orchestrator after the accepting transaction has committed. It lives here, not beside the
        // renderer, because the orchestrator is this extension's (see the remarks).
        services.AddOutboxHandler<AutopilotAuthorizationAccepted, AutopilotAuthorizationAcceptedHandler>();

        services.AddOutboxHandler<StoryGenerated, StoryGeneratedHandler>();
        services.AddOutboxHandler<StoryGenerationFailed, StoryGenerationFailedHandler>();
        services.AddOutboxHandler<StoryApproved, StoryApprovedHandler>();
        services.AddOutboxHandler<FactSheetGenerated, FactSheetGeneratedHandler>();
        services.AddOutboxHandler<FactSheetApproved, FactSheetApprovedHandler>();
        services.AddOutboxHandler<RenderSucceeded, RenderSucceededHandler>();
        services.AddOutboxHandler<RenderFailed, RenderFailedHandler>();
        services.AddOutboxHandler<VideoApproved, VideoApprovedHandler>();
        services.AddOutboxHandler<PublicationUpdated, PublicationUpdatedHandler>();

        return services;
    }
}
