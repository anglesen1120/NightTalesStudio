using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Abstractions.Realtime;

namespace NightTales.Infrastructure.Realtime;

/// <summary>
/// Registers the real-time transport shared by both hosts (09.8.1 plan §2 R2): the notifier a command
/// handler or an outbox handler calls, and the listener connection a host that serves sockets opens.
/// </summary>
/// <remarks>
/// Both the Api (commands) and the Worker (outbox handlers, 09 README D11/R6) must be able to notify, so
/// this registration is called from both composition roots. Only the Api starts the loop that reads what
/// the other side wrote — <c>AddNightTalesRealtime</c> in the Api adds that hosted service.
/// </remarks>
public static class RealtimeServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="RealtimeOptions"/>, the meter, the scoped <see cref="IRealtimeNotifier"/>, the
    /// listener connection factory and the <c>hub_tickets</c> store.
    /// </summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configuration">The host configuration; <c>Realtime</c> and the connection string are read from it.</param>
    public static IServiceCollection AddRealtime(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RealtimeOptions>()
            .BindConfiguration(RealtimeOptions.SectionName)
            .Validate(
                options => options.MaxPayloadBytes > 0,
                $"'{RealtimeOptions.SectionName}:{nameof(RealtimeOptions.MaxPayloadBytes)}' must be greater than zero.")
            .Validate(
                options => options.MaxPayloadBytes <= RealtimePayload.MaxBytes,
                $"'{RealtimeOptions.SectionName}:{nameof(RealtimeOptions.MaxPayloadBytes)}' must not exceed "
                + $"{RealtimePayload.MaxBytes}: PostgreSQL refuses a NOTIFY payload at least that long, so a "
                + "higher cap would let the database raise instead of dropping the body.")
            .Validate(
                options => options.HeartbeatSeconds > 0,
                $"'{RealtimeOptions.SectionName}:{nameof(RealtimeOptions.HeartbeatSeconds)}' must be greater than zero.")
            .Validate(
                options => options.RevalidateSeconds > 0,
                $"'{RealtimeOptions.SectionName}:{nameof(RealtimeOptions.RevalidateSeconds)}' must be greater than zero.")
            .Validate(
                options => options.DeliveryQueueCapacity > 0,
                $"'{RealtimeOptions.SectionName}:{nameof(RealtimeOptions.DeliveryQueueCapacity)}' must be greater than zero.")
            .Validate(
                options => options.DeliveryTimeoutSeconds > 0,
                $"'{RealtimeOptions.SectionName}:{nameof(RealtimeOptions.DeliveryTimeoutSeconds)}' must be greater than zero.")
            .Validate(
                options => options.ListenerBackoffSeconds is { Count: > 0 } && options.ListenerBackoffSeconds.All(seconds => seconds > 0),
                $"'{RealtimeOptions.SectionName}:{nameof(RealtimeOptions.ListenerBackoffSeconds)}' must hold at least one positive "
                + "number of seconds.")
            .Validate(
                options => options.TicketTtlSeconds > 0,
                $"'{RealtimeOptions.SectionName}:{nameof(RealtimeOptions.TicketTtlSeconds)}' must be greater than zero "
                + "(a zero-second ticket could never be claimed).")
            .Validate(
                options => options.TicketCleanupRetentionHours > 0,
                $"'{RealtimeOptions.SectionName}:{nameof(RealtimeOptions.TicketCleanupRetentionHours)}' must be greater than zero "
                + "(a zero-hour retention would sweep a row the moment its claim failed).")
            .Validate(
                options => options.TicketCleanupBatchSize > 0,
                $"'{RealtimeOptions.SectionName}:{nameof(RealtimeOptions.TicketCleanupBatchSize)}' must be greater than zero.")
            .ValidateOnStart();

        // 09.8.1-T2 registers the production IHubTicketValidator in the Api's own composition root
        // (AddNightTalesRealtime): only the host that serves sockets authenticates with a ticket, so the
        // Worker deliberately has none and the scheme fails fast on a host without one (09.8.1-T1 design).
        services.AddSingleton<RealtimeMetrics>();
        services.AddScoped<IRealtimeNotifier, PgNotifyRealtimeNotifier>();
        services.AddSingleton<INpgsqlListenerConnectionFactory, PgNotificationListenerFactory>();

        return services;
    }
}
