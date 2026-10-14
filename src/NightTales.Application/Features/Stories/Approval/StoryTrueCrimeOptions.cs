namespace NightTales.Application.Features.Stories.Approval;

/// <summary>
/// The true-crime approval settings of 02.3.3-T1 (02 README D8/D21): the attestation text version the gate
/// accepts, and the minimum number of distinct cited fetched sources (F13). Bound from
/// <see cref="SectionName"/> in the composition root, validated on start-up and registered as a plain
/// value, so the approval gate reads it without an <c>IOptions</c> dependency (same pattern as the
/// similarity settings).
/// </summary>
public sealed class StoryTrueCrimeOptions
{
    /// <summary>The configuration section the composition root binds.</summary>
    public const string SectionName = "Story:TrueCrime";

    /// <summary>
    /// The single attestation text version the gate accepts (02 README D14): a tick that names any other
    /// version is refused with <c>story_attestation_required</c>, so a text revision invalidates old ticks.
    /// </summary>
    public string AttestationVersion { get; set; } = "ta.v1";

    /// <summary>The minimum number of distinct qualifying sources a true-crime story needs (F13).</summary>
    public int MinCitedSources { get; set; } = 2;
}
