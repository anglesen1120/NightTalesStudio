using System.Diagnostics.CodeAnalysis;

namespace NightTales.Application.Abstractions;

/// <summary>
/// The Worker's way to run a unit of work as one user (09 README D10): <see cref="For"/> pins the
/// whole DI scope to <paramref name="userId"/> as <see cref="NightTales.Domain.Common.ActorType.System"/>
/// <em>before</em> any <c>AppDbContext</c> is resolved, so the tenant filter compares against that
/// user and nothing wider.
/// </summary>
/// <remarks>
/// There is deliberately no "run unfiltered" mode: without a call to <see cref="For"/>, the scope's
/// <see cref="ICurrentUser.Id"/> is <c>null</c> and every user-owned table reads as empty. A second
/// call in one scope, a <see cref="Guid.Empty"/> id and a call after the scope already created a
/// <c>DbContext</c> all throw, so a job that forgets its user fails loudly instead of leaking rows.
/// 02's <c>SystemContext.For(run.UserId)</c> reads as a call on the injected instance.
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1716:Identifiers should not match keywords",
    Justification = "'For' is the member name the 09 README D10 contract fixes, and 02/06 already call ISystemContext.For(run.UserId); renaming it would break both.")]
public interface ISystemContext
{
    /// <summary>Runs the rest of this scope as <paramref name="userId"/>.</summary>
    /// <param name="userId">The owner of the work; never <see cref="Guid.Empty"/>.</param>
    /// <returns>
    /// A handle that releases the scope back to anonymous when it is disposed. Disposing does not make a
    /// second <see cref="For"/> legal: a scope accepts exactly one call, so a later one throws whether the
    /// earlier handle was disposed or not.
    /// </returns>
    IDisposable For(Guid userId);
}
