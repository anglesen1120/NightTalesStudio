using NightTales.Domain.Common;

namespace NightTales.Domain.Research;

/// <summary>
/// One url-source creation of a user (02 README F15/D8, CR-31): the append-only record the rolling 24 h cap
/// counts. It outlives the <see cref="StorySource"/> it was written for, so deleting a source does not free quota.
/// </summary>
/// <remarks>
/// Privacy-minimal on purpose (02 D13/D19): no episode, source, URL or note reference — only who and when. A row
/// older than the window is dead weight and may be purged by a retention job (none is needed for correctness).
/// </remarks>
public sealed class SourceCreation : Entity, IUserOwned
{
    private SourceCreation()
    {
    }

    public Guid UserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Records one url-source creation (an add, or a retry that re-applies the cap).</summary>
    /// <param name="id">The UUIDv7 id.</param>
    /// <param name="userId">The owner.</param>
    /// <param name="now">The command's clock reading.</param>
    public static SourceCreation Record(Guid id, Guid userId, DateTimeOffset now) =>
        new() { Id = id, UserId = userId, CreatedAt = now };
}
