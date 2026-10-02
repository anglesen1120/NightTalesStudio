using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightTales.Api.Realtime;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Infrastructure.Realtime;

namespace NightTales.Api.DependencyInjection;

/// <summary>
/// Registers the Api side of the real-time transport (09.8.1 plan §5, 09 README BD11): the hub, its
/// <c>HubTicket</c> scheme, the connection registry, the <c>LISTEN</c> loop and the hub's connection
/// policy.
/// </summary>
/// <remarks>
/// <para>
/// The Api is the only host that serves sockets, so <c>AddSignalR</c>, the listener hosted service and
/// the options validation live here; the producer half (<c>AddRealtime</c>) is registered by both hosts
/// because the Worker's outbox handlers are producers too (09 README D11/R6).
/// </para>
/// <para>
/// The production <c>IHubTicketValidator</c> is registered here with 09.8.1-T2: this is the only host
/// that serves sockets, so it is the only one that claims tickets, and its absence on another host
/// still fails fast with <see cref="HubTicketAuthenticationHandler.MissingValidatorMessage"/> — never
/// an anonymous socket and never a permissive fallback (09.8.1-T1 design, "seam with 09.8.1-T2").
/// </para>
/// <para>
/// Server-side SignalR ships in the <c>Microsoft.AspNetCore.App</c> shared framework, so this adds no
/// package reference (plan §5 ⚠️ resolved: no new NuGet package, which the locked restore mode
/// requires).
/// </para>
/// </remarks>
public static class RealtimeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the hub's services, the listener and both option types, with start-up validation.
    /// </summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configuration">The host configuration; <c>Realtime</c>, <c>Hubs</c> and the connection string are read from it.</param>
    public static IServiceCollection AddNightTalesRealtime(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // The producer half (the notifier on the caller's AppDbContext and the listener connection
        // factory) is shared with the Worker.
        services.AddRealtime(configuration);

        services.AddOptions<HubsOptions>()
            .BindConfiguration(HubsOptions.SectionName)
            .Validate(
                options => options.MaxConnectionMinutes.Values.All(minutes => minutes > 0),
                $"every '{HubsOptions.SectionName}:{nameof(HubsOptions.MaxConnectionMinutes)}' value must be a "
                + "positive number of minutes ([RULED — README §B10 WT37]).")
            .Validate(
                options => options.UnknownRoleNames().Count == 0,
                $"'{HubsOptions.SectionName}:{nameof(HubsOptions.MaxConnectionMinutes)}' may be keyed only by "
                + "PlatformRole names (owner, admin, user); a misspelled key would be a cap that never applies.")
            .ValidateOnStart();

        services.AddSingleton<RealtimeConnectionRegistry>();
        services.AddSingleton<IRealtimeListenerDelay, SystemRealtimeListenerDelay>();
        services.AddSingleton<IRealtimeGroupSender, SignalRRealtimeGroupSender>();

        // 09.8.1-T2 closes the fail-fast seam of 09.8.1-T1: the production IHubTicketValidator is
        // registered here, so a connect attempt claims the ticket against `hub_tickets` instead of
        // throwing MissingValidatorMessage. Scoped, like the AppDbContext it claims through.
        services.AddScoped<IHubTicketValidator, NpgsqlHubTicketValidator>();

        // One loop per Api instance (BD11). Registered twice on purpose: the singleton is what a test
        // resolves, the hosted service is what the host starts, and both must be the same instance so the
        // loop a test drives is the loop the host stops.
        services.AddSingleton<RealtimeListenerService>();
        services.AddHostedService(provider => provider.GetRequiredService<RealtimeListenerService>());

        services.AddSignalR();

        // The `HubTicket` scheme is registered *beside* the host's bearer scheme (AddNightTalesJwtBearer):
        // the hub's [Authorize] names exactly this scheme, so a socket is never authenticated with an
        // access token and a bearer request is never authenticated with a ticket.
        services
            .AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, HubTicketAuthenticationHandler>(
                HubTicketAuthenticationHandler.SchemeName,
                configureOptions: null);

        return services;
    }
}
