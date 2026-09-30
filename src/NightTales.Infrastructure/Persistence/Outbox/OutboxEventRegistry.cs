using System.Reflection;
using NightTales.Domain.Common;

namespace NightTales.Infrastructure.Persistence.Outbox;

/// <summary>
/// The name ↔ type map of the domain events (09 README D11, 09.2.4 plan §3): built once at start-up from
/// every <see cref="IDomainEvent"/> type carrying <see cref="OutboxEventAttribute"/> in the scanned
/// assemblies — the Domain assembly by default.
/// </summary>
/// <remarks>
/// <para>
/// The interceptor resolves the stored <c>type</c> name from the CLR type (<see cref="NameOf"/>); the
/// dispatcher resolves the CLR type from the stored name (<see cref="TypeOf"/>). A duplicate name would
/// make the second one unreachable, so it fails at construction, which is composition time — a host with
/// two events of one name never starts.
/// </para>
/// <para>
/// An event type without the attribute is a bug the architecture test catches in CI, and this registry
/// refuses it loudly at runtime too rather than storing a row whose <c>type</c> is empty.
/// </para>
/// </remarks>
public sealed class OutboxEventRegistry
{
    private readonly Dictionary<string, Type> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> _byType = new();

    /// <summary>Scans the Domain assembly for the platform's domain events.</summary>
    public OutboxEventRegistry()
        : this(typeof(IDomainEvent).Assembly)
    {
    }

    /// <summary>
    /// Scans the Domain assembly plus <paramref name="eventAssemblies"/>.
    /// </summary>
    /// <param name="eventAssemblies">
    /// Extra assemblies whose <c>[OutboxEvent]</c> types must be dispatchable on top of the Domain's — the
    /// test harness passes the assembly of its probe events; production passes none.
    /// </param>
    public OutboxEventRegistry(params Assembly[] eventAssemblies)
    {
        ArgumentNullException.ThrowIfNull(eventAssemblies);

        foreach (var assembly in eventAssemblies.Append(typeof(IDomainEvent).Assembly).Distinct())
        {
            foreach (var (eventType, name) in EventsOf(assembly))
            {
                if (_byName.TryGetValue(name, out var other))
                {
                    throw new InvalidOperationException(
                        $"[OutboxEvent(\"{name}\")] is used by both {other.FullName} and {eventType.FullName}; "
                        + "an event name must identify exactly one type (09 README D11).");
                }

                _byName[name] = eventType;
                _byType[eventType] = name;
            }
        }
    }

    /// <summary>
    /// The stable name of <paramref name="eventType"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="eventType"/> is not a scanned <c>[OutboxEvent]</c> type, so no <c>type</c> value
    /// could be stored for it.
    /// </exception>
    public string NameOf(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);

        if (_byType.TryGetValue(eventType, out var name))
        {
            return name;
        }

        var declared = eventType.GetCustomAttribute<OutboxEventAttribute>();
        if (declared is null)
        {
            throw new InvalidOperationException(
                $"{eventType.FullName} was raised as a domain event but carries no "
                + "[OutboxEvent(\"<name>.v<n>\")] attribute, so the outbox cannot name it (09 README D11).");
        }

        throw new InvalidOperationException(
            $"[OutboxEvent(\"{declared.Name}\")] on {eventType.FullName} is not registered in this outbox "
            + "registry: its assembly was not scanned, so the dispatcher could never resolve the stored "
            + "type back to it (09 README D11).");
    }

    /// <summary>The CLR type of the stored <paramref name="name"/>, or <c>null</c> when it is unknown.</summary>
    /// <param name="name">The stored <c>outbox_messages.type</c> value.</param>
    public Type? TypeOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _byName.TryGetValue(name, out var eventType) ? eventType : null;
    }

    /// <summary>Whether <paramref name="eventType"/> may be stored without a user (09 README D11).</summary>
    /// <param name="eventType">A scanned event type.</param>
    public static bool IsPlatformEvent(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);

        return eventType.IsDefined(typeof(PlatformEventAttribute), inherit: false);
    }

    /// <summary>
    /// The <c>[OutboxEvent]</c> types of one assembly and the names they declare, in a stable order.
    /// </summary>
    /// <remarks>
    /// It reads the attribute itself rather than going through <see cref="NameOf"/>: the map is still being
    /// built here, so a lookup would refuse the very type it is registering.
    /// </remarks>
    private static IEnumerable<(Type EventType, string Name)> EventsOf(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes().OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            if (type is not { IsClass: true, IsAbstract: false }
                || !typeof(IDomainEvent).IsAssignableFrom(type)
                || type.GetCustomAttribute<OutboxEventAttribute>(inherit: false) is not { } attribute)
            {
                continue;
            }

            yield return (type, attribute.Name);
        }
    }
}
