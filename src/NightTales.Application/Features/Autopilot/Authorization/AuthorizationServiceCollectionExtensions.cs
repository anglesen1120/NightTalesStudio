using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
/// calls them.
/// </para>
/// <para>
/// <b>Nothing here needs the Worker.</b> This extension is called by <c>AddNightTalesApplication</c>, i.e. by
/// <em>both</em> hosts, so a descriptor it added has to be constructible in the Api as well. The outbox consumer of
/// <see cref="AutopilotAuthorizationAccepted"/> (S71-7) is not: it drives <c>IAutopilotRunOrchestrator</c>, which only
/// <c>AddAutopilotOrchestration</c> registers, so it is registered by the Worker-only
/// <c>AddAutopilotOutboxHandlers</c> instead — a Worker-only port never hangs off a shared concrete descriptor
/// (02.8.3-T2 QA D11).
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

        return services;
    }
}
