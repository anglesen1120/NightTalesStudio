using NightTales.Domain.Compliance.Deletion;

namespace NightTales.Application.Features.Compliance.Deletion;

/// <summary>
/// The conflict-tolerant insert behind <see cref="IDeletionRequests"/>: <c>INSERT … ON CONFLICT (user_id, scope,
/// target_id, reason) WHERE completed_at IS NULL DO NOTHING</c>, which EF cannot express.
/// </summary>
public interface IDeletionRequestWriter
{
    /// <summary>Inserts <paramref name="request"/> in the ambient transaction; <c>false</c> when an open twin exists.</summary>
    Task<bool> InsertIfAbsentAsync(DeletionRequest request, CancellationToken cancellationToken);
}
