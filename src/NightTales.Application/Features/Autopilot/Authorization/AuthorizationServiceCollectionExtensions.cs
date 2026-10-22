using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Features.Autopilot.Events;
using NightTales.Domain.Series.Events;

namespace NightTales.Application.Features.Autopilot.Authorization;

/// <summary>
/// The authorization text's composition (02.8.3-T2, task 'Authorization text', step 1): the one renderer every
/// caller of the text shares, in one call.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a singleton.</b> <see cref="AuthorizationTextRenderer"/> holds only the two templates it read once from
/// this assembly, so one instance per process is enough and the second request of a locale performs no resource
/// I/O. It keeps no request state: the inputs arrive as <see cref="AuthorizationTextRequest"/>.
/// </para>
/// <para>
/// <b>What is registered elsewhere.</b> The commands that accept and revoke an authorization and the queries that
/// show it are Mediator handlers, discovered from this assembly by the pipeline the host composes; nothing here
/// calls them. The one delivery this extension does register is the outbox consumer of
/// <see cref="AutopilotAuthorizationAccepted"/>, because it belongs to the same accept flow (S71-7).
/// </para>
/// </remarks>
public static class AuthorizationServiceCollectionExtensions
{
    /// <summary>Registers the authorization text renderer.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddAutopilotAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<AuthorizationTextRenderer>();

        // S71-7: an acceptance does not advance a run by itself. The aggregate raises the event and this consumer
        // routes it to T1's orchestrator after the accepting transaction has committed.
        services.AddOutboxHandler<AutopilotAuthorizationAccepted, AutopilotAuthorizationAcceptedHandler>();

        return services;
    }
}
