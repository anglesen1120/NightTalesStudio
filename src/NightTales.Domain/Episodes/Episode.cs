using NightTales.Domain.Common;

namespace NightTales.Domain.Episodes;

/// <summary>
/// An episode: the central aggregate (00 overview §6 <c>episodes</c>). This is the shell 09.2.2-T1 owns
/// — the columns of plan §3; the behaviour (<c>Create</c>, <c>Cancel</c> and the partial files of 02)
/// is 09.5.1-T1's, 02's and 06's.
/// </summary>
public sealed class Episode : AggregateRoot, IUserOwned, IAuditable
{
    private Episode()
    {
    }

    public Guid UserId { get; private set; }

    public Guid SeriesId { get; private set; }

    public string? Title { get; private set; }

    public string Brief { get; private set; } = string.Empty;

    public LengthTier LengthTier { get; private set; }

    public EpisodeStatus Status { get; private set; }

    public Guid? CurrentStoryVersionId { get; private set; }

    public Guid? ApprovedStoryVersionId { get; private set; }

    public Guid? FinalVideoAssetId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
}
