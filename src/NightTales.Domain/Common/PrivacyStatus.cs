namespace NightTales.Domain.Common;

/// <summary>
/// Intended privacy of a publication, as stated in an autopilot authorization
/// (<c>series_autopilot_authorizations.privacy_status</c>) and on a publication row.
/// </summary>
public enum PrivacyStatus
{
    Private,
    Unlisted,
    Public,
}
