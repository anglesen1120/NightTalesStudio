using Microsoft.Extensions.DependencyInjection;
using NightTales.Domain.Common;

namespace NightTales.Application.Abstractions.Outbox;

/// <summary>
/// The registration of the outbox handlers (09 README D11, 09.2.4 plan §3). It is called by the feature
/// composition roots, in the order the handlers of one event must run in.
/// </summary>
public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="THandler"/> as one handler of <typeparamref name="TEvent"/>,
    /// <c>Scoped</c>, behind the <see cref="IOutboxHandler{TEvent}"/> port.
    /// </summary>
    /// <remarks>
    /// The call order of this method is the call order of the handlers for one message. The handler is
    /// built once per delivery, in the delivery's own scope — already pinned to the message's user — so a
    /// handler may take scoped dependencies (<c>ISender</c>, <c>IAppDbContext</c>,
    /// <see cref="NightTales.Application.Abstractions.Jobs.IJobScheduler"/>).
    /// </remarks>
    /// <typeparam name="TEvent">The event the handler consumes.</typeparam>
    /// <typeparam name="THandler">The handler class; one instance per delivery.</typeparam>
    /// <param name="services">The service collection of the host.</param>
    public static IServiceCollection AddOutboxHandler<TEvent, THandler>(this IServiceCollection services)
        where TEvent : IDomainEvent
        where THandler : class, IOutboxHandler<TEvent>
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<THandler>();
        services.AddScoped<IOutboxHandler<TEvent>>(provider => provider.GetRequiredService<THandler>());

        // One singleton descriptor per call, so the dispatcher sees the registrations in this order
        // without building a handler for an event it is not delivering (09 README D11).
        services.AddSingleton(new OutboxHandlerRegistration(
            typeof(TEvent),
            scope => new OutboxHandlerInvoker<TEvent, THandler>(scope.GetRequiredService<THandler>())));

        return services;
    }
}
