using NightTales.Domain.Common;

namespace NightTales.Domain.Stories;

/// <summary>
/// One immutable story version of an episode (00 overview §6 <c>story_versions</c>). This is the shell
/// 09.2.2-T1 owns — the base columns of plan §3; 02.2.2-T1 adds its own columns and the behaviour.
/// Append-only, so it stops at <see cref="ICreatedAt"/>.
/// </summary>
public sealed class StoryVersion : Entity, IUserOwned, ICreatedAt
{
    private readonly List<string> _keywords = [];

    private StoryVersion()
    {
    }

    public Guid UserId { get; private set; }

    public Guid EpisodeId { get; private set; }

    public int Version { get; private set; }

    public StoryVersionSource Source { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string Body { get; private set; } = string.Empty;

    /// <summary>
    /// The keywords extracted for this version (plan §3 <c>keywords text[]</c>). The row is append-only,
    /// so the list is read-only: only the factory writes <c>_keywords</c>, which EF maps through field
    /// access. The view is built per access because EF replaces the field when it materializes a row.
    /// </summary>
    public IReadOnlyList<string> Keywords => _keywords.AsReadOnly();

    public int WordCount { get; private set; }

    public string? Model { get; private set; }

    public string? PromptVersion { get; private set; }

    /// <summary>The <c>content</c> JSON document (<c>story.v1</c>, plus the backend-built fact sources).</summary>
    public string Content { get; private set; } = string.Empty;

    /// <summary>The <c>audit_report</c> JSON document (<c>audit.v1</c>, 02 D22).</summary>
    public string? AuditReport { get; private set; }

    public decimal? SimilarityScore { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
