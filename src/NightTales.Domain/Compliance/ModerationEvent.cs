using NightTales.Domain.Common;

namespace NightTales.Domain.Compliance;

/// <summary>
/// One moderation decision that touched content (00 overview §6 <c>moderation_events</c>). Append-only,
/// so it stops at <see cref="ICreatedAt"/>. The excerpt is the only content-bearing column (the logger
/// never writes story text, D19).
/// </summary>
public sealed class ModerationEvent : Entity, IUserOwned, ICreatedAt
{
    /// <summary>The longest excerpt a moderation event may store (00 overview §6).</summary>
    public const int ExcerptMaxLength = 300;

    private ModerationEvent()
    {
    }

    public Guid UserId { get; private set; }

    public Guid? EpisodeId { get; private set; }

    public ModerationSource Source { get; private set; }

    public string Stage { get; private set; } = string.Empty;

    public List<string> Categories { get; private set; } = [];

    public string? Excerpt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Records one moderation decision (02.4.1-T1 calls this). <paramref name="excerpt"/> is truncated
    /// to <see cref="ExcerptMaxLength"/> characters, so the column limit is never the failure mode.
    /// </summary>
    public static ModerationEvent Record(
        Guid userId,
        ModerationSource source,
        string stage,
        IReadOnlyCollection<string> categories,
        DateTimeOffset now,
        Guid? episodeId = null,
        string? excerpt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);

        var text = string.IsNullOrEmpty(excerpt)
            ? null
            : excerpt.Length <= ExcerptMaxLength
                ? excerpt
                : excerpt[..ExcerptMaxLength];

        return new ModerationEvent
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            EpisodeId = episodeId,
            Source = source,
            Stage = stage,
            Categories = [.. categories],
            Excerpt = text,
            CreatedAt = now,
        };
    }
}
