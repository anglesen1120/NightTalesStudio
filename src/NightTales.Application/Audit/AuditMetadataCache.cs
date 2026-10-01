using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using NightTales.Application.Abstractions.Audit;

namespace NightTales.Application.Audit;

/// <summary>
/// Resolves a closed request type's <see cref="AuditedAttribute"/> once and remembers the answer
/// (09 README BD15, plan §3): the pipeline asks on every command, so the reflection lookup must happen
/// once per type, not once per request.
/// </summary>
/// <remarks>
/// Registered as a singleton. A miss is cached too — most messages carry
/// <see cref="NotAuditedAttribute"/> or no attribute at all — so the negative answer costs no lookup
/// after the first request of that type. The attribute is declared <c>Inherited = false</c>, and it is
/// read with the same flag, so a derived message can never inherit an audit contract it did not declare.
/// </remarks>
public sealed class AuditMetadataCache
{
    private readonly ConcurrentDictionary<Type, Entry> _cache = new();

    /// <summary>
    /// The request type's audit attribute, when it declares one.
    /// </summary>
    /// <typeparam name="TRequest">The closed request type the pipeline is running.</typeparam>
    /// <param name="attribute">The declared attribute, or <c>null</c> when the request declares none.</param>
    /// <returns><c>true</c> when the request must write a row.</returns>
    public bool TryGet<TRequest>([NotNullWhen(true)] out AuditedAttribute? attribute)
    {
        var entry = _cache.GetOrAdd(
            typeof(TRequest),
            static type => new Entry(type.GetCustomAttribute<AuditedAttribute>(inherit: false)));

        attribute = entry.Attribute;

        return attribute is not null;
    }

    /// <summary>
    /// One cache slot. A wrapper record is required because <see cref="ConcurrentDictionary{TKey,TValue}"/>
    /// cannot store a <c>null</c> value, and caching the negative answer is the point.
    /// </summary>
    /// <param name="Attribute">The attribute, or <c>null</c> when the type declares none.</param>
    private sealed record Entry(AuditedAttribute? Attribute);
}
