namespace NightTales.Domain.Stories;

/// <summary>
/// Who approved a story version (02.3.2-T1); carried by the approval event and audit projection, never story text.
/// </summary>
public enum ApprovalSource
{
    /// <summary>The owner approved the story.</summary>
    User,

    /// <summary>Autopilot approved the story under the series authorization.</summary>
    Autopilot
}
