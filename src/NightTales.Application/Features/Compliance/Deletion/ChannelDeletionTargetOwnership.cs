using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Compliance.Deletion;

namespace NightTales.Application.Features.Compliance.Deletion;

/// <summary>
/// The <c>channel</c> ownership check (06.4.1-T1 §Design step 7): the connection exists for the caller's tenant and for
/// <c>userId</c>.
/// </summary>
/// <remarks>
/// The 13.3.1-T1 engine refuses to create a request for a scope that needs a target and has no registered check, so a
/// disconnect could not schedule its purge without this class. It is the channel scope's own half of the engine, next to
/// <see cref="EpisodeDeletionTargetOwnership"/>, and it reads the same tenant-filtered <c>channel_connections</c> the
/// disconnect just locked: another user's connection id is the 404 the route already answered, never a request created
/// for a row the caller does not own.
/// </remarks>
/// <param name="database">The tracked write model; the tenant filter narrows the read to the caller.</param>
public sealed class ChannelDeletionTargetOwnership(IAppDbContext database) : IDeletionTargetOwnership
{
    /// <inheritdoc />
    public DeletionScope Scope => DeletionScope.Channel;

    /// <inheritdoc />
    public Task<bool> OwnsAsync(Guid userId, Guid targetId, CancellationToken cancellationToken) =>
        database.ChannelConnections.AnyAsync(
            connection => connection.Id == targetId && connection.UserId == userId,
            cancellationToken);
}
