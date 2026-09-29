using NightTales.Domain.Common;

namespace NightTales.Domain.Series.Autopilot;

/// <summary>
/// One accepted version of the autopilot authorization text of a series (00 overview §6
/// <c>series_autopilot_authorizations</c>). Shell owned by 09.2.2-T1; the behaviour is 02.8.3-T2's.
/// </summary>
public sealed class SeriesAutopilotAuthorization : Entity, IUserOwned, IAuditable
{
    private SeriesAutopilotAuthorization()
    {
    }

    public Guid UserId { get; private set; }

    public Guid SeriesId { get; private set; }

    public int Version { get; private set; }

    public string TextVersion { get; private set; } = string.Empty;

    /// <summary>SHA-256 hex of the rendered text (<c>char(64)</c>).</summary>
    public string TextHash { get; private set; } = string.Empty;

    public Topic Topic { get; private set; }

    /// <summary>Placeholder FK to <c>channel_connections</c>, whose constraint 06 adds.</summary>
    public Guid? ChannelConnectionId { get; private set; }

    public string Cadence { get; private set; } = string.Empty;

    public PrivacyStatus PrivacyStatus { get; private set; }

    public DateTimeOffset AcceptedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
}
