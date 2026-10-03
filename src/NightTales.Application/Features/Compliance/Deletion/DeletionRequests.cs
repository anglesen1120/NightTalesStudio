using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Compliance.Deletion;

namespace NightTales.Application.Features.Compliance.Deletion;

/// <summary>
/// <see cref="IDeletionRequests"/>: validates through the domain factory, checks target ownership, inserts with
/// <c>ON CONFLICT DO NOTHING</c> and re-reads the open row by the same four columns inside the caller's tenant
/// scope (13 README YR13), so a second revoke returns the first row and never extends the clock.
/// </summary>
public sealed class DeletionRequests(
    IAppDbContext database,
    IDeletionRequestWriter writer,
    IEnumerable<IDeletionTargetOwnership> ownership,
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
}
