namespace NightTales.Application.Abstractions;

/// <summary>
/// The fail-fast half of the 09 README D10 ordering rule. <c>AppDbContext</c> and <c>ReadDbContext</c>
/// call <see cref="MarkObserved"/> from their constructors through the scope's
/// <see cref="ICurrentUser"/>, so <see cref="ISystemContext.For"/> can refuse to run once the scope
/// has already created a context — the moment the filter's user is fixed.
/// </summary>
/// <remarks>
/// Both production providers implement it: <c>ScopedCurrentUser</c> (the Worker) and
/// <c>HttpCurrentUser</c> (the Api), the latter forwarding to the <c>ScopedCurrentUser</c> it
/// decorates. An <see cref="ICurrentUser"/> that does not implement this interface — the anonymous
/// default, the test doubles that drive a context directly — has no ordering rule to enforce and is
/// simply never marked.
/// </remarks>
public interface ITenantScopeObserver
{
    /// <summary>Records that a <c>DbContext</c> was created in this scope.</summary>
    void MarkObserved();
}
