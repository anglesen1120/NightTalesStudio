namespace NightTales.Application.Abstractions.Audit;

/// <summary>
/// The write side of <c>audit_logs</c> (09 README BD15): the pipeline's <c>AuditBehavior</c> and every
/// non-Mediator writer — auth handlers, Worker jobs, CLI verbs — call it.
/// </summary>
/// <remarks>
/// <para>
/// The implementation only adds the row to the scope's context. It never saves and never commits: the
/// caller's transaction does, so a change and its audit row commit or roll back together (09 README D5)
/// and an audit write in a scope without a transaction is a programming error that fails loudly
/// (<see cref="InvalidOperationException"/>) instead of silently writing outside the change it records.
/// </para>
/// <para>
/// A caller outside the Mediator pipeline opens the transaction itself:
/// <c>IUnitOfWork.BeginAsync</c> → <see cref="WriteAsync"/> → <c>IUnitOfWork.CommitAsync</c>.
/// </para>
/// </remarks>
public interface IAuditWriter
{
    /// <summary>
    /// Adds <paramref name="entry"/> to the ambient transaction. Never commits and never saves; the
    /// implementation throws when no transaction is open.
    /// </summary>
    /// <param name="entry">The row to record.</param>
    /// <param name="cancellationToken">The scope's cancellation token.</param>
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken);
}
