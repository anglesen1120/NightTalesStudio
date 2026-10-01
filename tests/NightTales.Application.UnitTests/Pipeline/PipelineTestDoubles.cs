using ErrorOr;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Idempotency;
using NightTales.Application.Abstractions.Persistence;

namespace NightTales.Application.UnitTests.Pipeline;

/// <summary>
/// The <see cref="IUnitOfWork"/> of the unit pipeline: it opens nothing, so a probe command that runs
/// the real behaviour chain never needs a database.
/// </summary>
public sealed class PassThroughUnitOfWork : IUnitOfWork
{
    /// <summary>How many times a probe opened a transaction; the behaviour always opens exactly one.</summary>
    public int Begins { get; private set; }

    /// <summary>How many times a probe committed.</summary>
    public int Commits { get; private set; }

    /// <summary>How many times a probe rolled back.</summary>
    public int Rollbacks { get; private set; }

    /// <inheritdoc />
    public Task BeginAsync(CancellationToken cancellationToken)
    {
        Begins++;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ErrorOr<Success>> CommitAsync(CancellationToken cancellationToken)
    {
        Commits++;
        return Task.FromResult<ErrorOr<Success>>(Result.Success);
    }

    /// <inheritdoc />
    public Task RollbackAsync(CancellationToken cancellationToken)
    {
        Rollbacks++;
        return Task.CompletedTask;
    }
}

/// <summary>
/// The <see cref="IUnitOfWork"/> of the rollback-masking probe: its rollback fails, so a behaviour that
/// lets that secondary failure escape replaces the handler's error result with it.
/// </summary>
public sealed class FailingRollbackUnitOfWork : IUnitOfWork
{
    /// <summary>How many times a probe rolled back; the behaviour always rolls an error result back.</summary>
    public int Rollbacks { get; private set; }

    /// <inheritdoc />
    public Task BeginAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<ErrorOr<Success>> CommitAsync(CancellationToken cancellationToken) =>
        Task.FromResult<ErrorOr<Success>>(Result.Success);

    /// <inheritdoc />
    public Task RollbackAsync(CancellationToken cancellationToken)
    {
        Rollbacks++;

        throw new InvalidOperationException("probe rollback failure (09.1.3-T2)");
    }
}

/// <summary>
/// The <see cref="IIdempotencyStore"/> of the unit pipeline. Every probe message there carries no
/// idempotency key, so a call means the behaviour ran when it should have passed through — the fake
/// fails the test instead of silently succeeding.
/// </summary>
public sealed class KeylessIdempotencyStore : IIdempotencyStore
{
    /// <inheritdoc />
    public Task<IdempotencyClaim> ClaimAsync(
        Guid userId,
        string key,
        string requestHash,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A probe message without an idempotency key claimed one.");

    /// <inheritdoc />
    public Task CompleteAsync(
        Guid claimId,
        string responseType,
        string responseJson,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A probe message without an idempotency key stored a response.");
}

/// <summary>
/// The <see cref="IAuditWriter"/> of the unit pipeline (09.3.6-T1). No probe message of this assembly
/// carries <c>[Audited]</c>, so a call means slot 6 wrote a row it should not have — the fake fails the
/// test instead of silently accepting it. The behaviour itself is tested against a recording writer in
/// <c>AuditBehaviorTests</c>.
/// </summary>
public sealed class UnusedAuditWriter : IAuditWriter
{
    /// <inheritdoc />
    public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A probe message without [Audited] wrote an audit row.");
}
