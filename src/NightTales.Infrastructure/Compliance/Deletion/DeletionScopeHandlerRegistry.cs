using NightTales.Application.Features.Compliance.Deletion;
using NightTales.Domain.Compliance.Deletion;

namespace NightTales.Infrastructure.Compliance.Deletion;

/// <summary>Resolves handlers by scope from the scope's registered <see cref="IDeletionScopeHandler"/> set.</summary>
public sealed class DeletionScopeHandlerRegistry(IEnumerable<IDeletionScopeHandler> handlers) : IDeletionScopeHandlerRegistry
{
    private readonly Dictionary<DeletionScope, IDeletionScopeHandler> _byScope = handlers
        .GroupBy(handler => handler.Scope)
        .ToDictionary(group => group.Key, group => group.Last());

    /// <inheritdoc />
    public IReadOnlyCollection<DeletionScope> RegisteredScopes => _byScope.Keys;

    /// <inheritdoc />
    public IDeletionScopeHandler? Resolve(DeletionScope scope) => _byScope.GetValueOrDefault(scope);
}
