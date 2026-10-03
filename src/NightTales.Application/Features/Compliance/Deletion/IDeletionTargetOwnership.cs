using NightTales.Domain.Compliance.Deletion;

namespace NightTales.Application.Features.Compliance.Deletion;

/// <summary>
/// Answers whether a target id belongs to a user, for one scope. The scope's owner registers it next to its
/// <see cref="IDeletionScopeHandler"/> (06.4.x for <c>channel</c>); <c>CreateAsync</c> refuses a scope that needs a
/// target and has no registered check rather than create a request nobody verified.
/// </summary>
public interface IDeletionTargetOwnership
{
    /// <summary>The scope this check covers.</summary>
    DeletionScope Scope { get; }

    /// <summary>Whether <paramref name="targetId"/> is <paramref name="userId"/>'s.</summary>
    Task<bool> OwnsAsync(Guid userId, Guid targetId, CancellationToken cancellationToken);
}
