using NightTales.Domain.Common;

namespace NightTales.Domain.Series;

/// <summary>
/// A series: one channel's consistent style (00 overview §6 <c>series</c>). This is the shell 09.2.2-T1
/// owns — the columns of plan §3; the behaviour (<c>Create</c>, <c>Update</c>, <c>Archive</c>) is
/// 09.4.1-T1's.
/// </summary>
public sealed class Series : AggregateRoot, IUserOwned, IAuditable
{
    private Series()
    {
    }

    public Guid UserId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public Topic Topic { get; private set; }

    public Language Language { get; private set; }

    public Aspect Aspect { get; private set; }

    /// <summary>FK <c>voices</c> (the 04 catalog).</summary>
    public string VoiceId { get; private set; } = string.Empty;

    /// <summary>FK <c>style_templates</c>.</summary>
    public Guid StyleTemplateId { get; private set; }

    public string? StoryPreset { get; private set; }

    /// <summary>Placeholder FK to <c>channel_connections</c>, whose constraint 06 adds.</summary>
    public Guid? DefaultChannelId { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    public bool AutopilotEnabled { get; private set; }

    /// <summary>The <c>autopilot_settings</c> JSON document (02.8.3-T1 owns its shape).</summary>
    public string? AutopilotSettings { get; private set; }

    public Guid? CurrentAutopilotAuthorizationId { get; private set; }

    public DateTimeOffset? AutopilotPausedAt { get; private set; }

    public string? AutopilotPauseReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
}
