using NightTales.Domain.Compliance.Deletion;

namespace NightTales.Application.Features.Compliance.Deletion;

/// <summary>Resolves the <see cref="IDeletionScopeHandler"/> registered for a scope (scoped: handlers use the scope's context).</summary>
public interface IDeletionScopeHandlerRegistry
{
    /// <summary>The scopes that have a handler.</summary>
    IReadOnlyCollection<DeletionScope> RegisteredScopes { get; }

    /// <summary>The handler of <paramref name="scope"/>, or <c>null</c> when nobody registered one.</summary>
    IDeletionScopeHandler? Resolve(DeletionScope scope);
}
