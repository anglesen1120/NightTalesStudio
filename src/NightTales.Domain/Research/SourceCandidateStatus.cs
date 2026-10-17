namespace NightTales.Domain.Research;

/// <summary>
/// The lifecycle of one <see cref="SourceCandidate"/> (02.7.2 plan §3): a find-sources run proposes it, and the
/// user either adds it as a <see cref="StorySource"/> or dismisses it. Stored as snake_case text with a CHECK
/// (09 README D8), so the wire value of the list endpoint is the same word.
/// </summary>
public enum SourceCandidateStatus
{
    /// <summary>Proposed by a run and not acted on yet; the only status that can be added or dismissed.</summary>
    Proposed = 0,

    /// <summary>Added to the episode through the normal 02.5.1 source path; <see cref="SourceCandidate.SourceId"/> is set.</summary>
    Added = 1,

    /// <summary>The user dismissed it; the row stays as the record of what was proposed.</summary>
    Dismissed = 2,
}
