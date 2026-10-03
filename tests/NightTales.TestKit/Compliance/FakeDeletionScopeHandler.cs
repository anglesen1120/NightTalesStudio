using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ErrorOr;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Compliance.Deletion;

namespace NightTales.TestKit.Compliance;

/// <summary>One recorded handler call: which request, and which scope's context and user ran it.</summary>
/// <param name="RequestId">The executed request.</param>
/// <param name="ContextHash">The identity hash of the <c>IAppDbContext</c> the handler was resolved with.</param>
/// <param name="CurrentUserId">The user the scope was pinned to.</param>
public sealed record DeletionExecution(Guid RequestId, int ContextHash, Guid? CurrentUserId);

/// <summary>
/// The shared state of every <see cref="FakeDeletionScopeHandler"/> of a host: the calls it received and the
/// behaviour a test scripts. A singleton, because the handlers themselves are scoped (one per row).
/// </summary>
public sealed class DeletionHandlerProbe
{
    private readonly ConcurrentQueue<DeletionExecution> _executions = new();

    /// <summary>Every execution, in order.</summary>
    public IReadOnlyCollection<DeletionExecution> Executions => _executions;

    /// <summary>What a call returns; success with <c>(1, 1)</c> unless a test scripts otherwise.</summary>
    public Func<DeletionRequest, ErrorOr<DeletionResult>> Behavior { get; set; } = _ => new DeletionResult(1, 1);

    internal void Record(DeletionExecution execution) => _executions.Enqueue(execution);
}

/// <summary>A hand-written <see cref="IDeletionScopeHandler"/> (no mocking library) that records and scripts.</summary>
public sealed class FakeDeletionScopeHandler(
    DeletionScope scope,
    DeletionHandlerProbe probe,
    IAppDbContext database,
    ICurrentUser currentUser) : IDeletionScopeHandler
{
    /// <inheritdoc />
    public DeletionScope Scope => scope;

    /// <inheritdoc />
    public Task<ErrorOr<DeletionResult>> ExecuteAsync(DeletionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        probe.Record(new DeletionExecution(request.Id, RuntimeHelpers.GetHashCode(database), currentUser.Id));

        return Task.FromResult(probe.Behavior(request));
    }
}

/// <summary>A hand-written ownership check that accepts every target of its scope (the real one is the scope owner's).</summary>
public sealed class FakeDeletionTargetOwnership(DeletionScope scope) : NightTales.Application.Features.Compliance.Deletion.IDeletionTargetOwnership
{
    /// <inheritdoc />
    public DeletionScope Scope => scope;

    /// <inheritdoc />
    public Task<bool> OwnsAsync(Guid userId, Guid targetId, CancellationToken cancellationToken) => Task.FromResult(true);
}
