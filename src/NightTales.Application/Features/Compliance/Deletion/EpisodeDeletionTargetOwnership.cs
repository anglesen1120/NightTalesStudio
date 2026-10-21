using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Compliance.Deletion;

namespace NightTales.Application.Features.Compliance.Deletion;

/// <summary>The <c>episode</c> ownership check: the episode exists for the caller's tenant and for <c>userId</c>.</summary>
public sealed class EpisodeDeletionTargetOwnership(IAppDbContext database) : IDeletionTargetOwnership
{
    /// <inheritdoc />
    public DeletionScope Scope => DeletionScope.Episode;

    /// <inheritdoc />
    public Task<bool> OwnsAsync(Guid userId, Guid targetId, CancellationToken cancellationToken) =>
        database.Episodes.AnyAsync(episode => episode.Id == targetId && episode.UserId == userId, cancellationToken);
}
