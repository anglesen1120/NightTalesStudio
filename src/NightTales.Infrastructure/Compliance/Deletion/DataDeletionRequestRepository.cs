using Microsoft.EntityFrameworkCore;
using NightTales.Application.Features.Compliance.Deletion;
using NightTales.Domain.Compliance.Deletion;
using NightTales.Infrastructure.Persistence;

namespace NightTales.Infrastructure.Compliance.Deletion;

/// <summary>
/// The conflict-tolerant insert of <see cref="IDeletionRequests"/> (13.3.1 plan §3.2). It runs on the caller's scope
/// and therefore inside the caller's transaction; it never calls <c>IgnoreQueryFilters</c> (09.1.2-T1:43).
/// </summary>
public sealed class DataDeletionRequestRepository(AppDbContext database) : IDeletionRequestWriter
{
    /// <inheritdoc />
    public async Task<bool> InsertIfAbsentAsync(DeletionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scope = request.Scope.ToWire();
        var reason = request.Reason.ToWire();
        var status = "pending";

        var inserted = await database.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO data_deletion_requests (id, user_id, scope, target_id, reason, status, requested_at, due_at, attempts)
            VALUES ({request.Id}, {request.UserId}, {scope}, {request.TargetId}, {reason}, {status},
                    {request.RequestedAt}, {request.DueAt}, 0)
            ON CONFLICT (user_id, scope, target_id, reason) WHERE completed_at IS NULL DO NOTHING
            """,
            cancellationToken).ConfigureAwait(false);

        return inserted == 1;
    }
}
