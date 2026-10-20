using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Compliance.Deletion;

namespace NightTales.Application.Features.Compliance.Deletion;

/// <summary>
/// <see cref="IDeletionRequests"/>: validates through the domain factory, checks target ownership, inserts with
/// <c>ON CONFLICT DO NOTHING</c> and re-reads the open row by the same four columns inside the caller's tenant
/// scope (13 README YR13), so a second revoke returns the first row and never extends the clock. It also answers the
/// cross-state existence read a repeat needs before it creates anything (06.4.1-T1 D4).
/// </summary>
public sealed class DeletionRequests(
    IAppDbContext database,
    IDeletionRequestWriter writer,
    IEnumerable<IDeletionTargetOwnership> ownership,
    ICurrentUser currentUser,
    IClock clock) : IDeletionRequests
{
    private const int MaxAttempts = 3;

    /// <inheritdoc />
    public async Task<ErrorOr<DeletionRequest>> CreateAsync(
        Guid userId,
        DeletionScope scope,
        Guid? targetId,
        DeletionReason reason,
        CancellationToken cancellationToken)
    {
        // The insert bypasses the tenant filter, so the ambient actor is checked here: a caller may only open a
        // request for its own user. Staff (Admin) and system (System) actors are exempt, because their
        // authorization stands upstream (13.3.1 plan UC1: 09.10.5-T1's admin delete, the job's pinned scopes);
        // an unpinned scope (Id null) keeps its behaviour, since its tenant-filtered re-read cannot see the row.
        if (currentUser.Actor == ActorType.User
            && currentUser.Id is { } caller
            && caller != userId)
        {
            return Errors.NotFound();
        }

        var created = DeletionRequest.Create(userId, scope, targetId, reason, clock.UtcNow);
        if (created.IsError)
        {
            return created.Errors;
        }

        if (targetId is { } target)
        {
            var check = ownership.FirstOrDefault(candidate => candidate.Scope == scope)
                ?? throw new InvalidOperationException(
                    $"No IDeletionTargetOwnership is registered for scope '{scope.ToWire()}'; its owner must register one.");

            if (!await check.OwnsAsync(userId, target, cancellationToken).ConfigureAwait(false))
            {
                return Errors.NotFound();
            }
        }

        // The open row can complete between the conflicting insert and the re-read, so the pair is retried.
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            await writer.InsertIfAbsentAsync(created.Value, cancellationToken).ConfigureAwait(false);

            var open = await database.DeletionRequests
                .AsNoTracking()
                .Where(request => request.UserId == userId
                    && request.Scope == scope
                    && request.TargetId == targetId
                    && request.Reason == reason
                    && request.CompletedAt == null)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (open is not null)
            {
                return open;
            }

            created = DeletionRequest.Create(userId, scope, targetId, reason, clock.UtcNow);
        }

        return Error.Conflict(code: ErrorCodes.ConcurrencyConflict, description: "The deletion request changed while it was created; retry.");
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(
        Guid userId,
        DeletionScope scope,
        Guid? targetId,
        DeletionReason reason,
        CancellationToken cancellationToken) =>
        // The tenant filter carries the same "a caller only sees its own user" rule as the re-read of CreateAsync, so
        // a foreign userId simply answers false here. Read-only: the row's status is deliberately not filtered, because
        // the caller asks whether the duty exists at all (a completed row means the purge already ran).
        database.DeletionRequests
            .AsNoTracking()
            .AnyAsync(
                request => request.UserId == userId
                    && request.Scope == scope
                    && request.TargetId == targetId
                    && request.Reason == reason,
                cancellationToken);
}
