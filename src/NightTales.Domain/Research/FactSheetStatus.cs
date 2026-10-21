namespace NightTales.Domain.Research;

/// <summary>
/// The lifecycle of one fact-sheet version (02.5.3 plan §3, D9/T54). A row is never edited in place: a new
/// <see cref="Draft"/> version is created and, when it is approved in the same transaction, the previous
/// <see cref="Approved"/> row of the episode is demoted to <see cref="Superseded"/>, so at most one row per
/// episode is <see cref="Approved"/>. The persisted/wire spelling is the snake_case of the member name.
/// </summary>
public enum FactSheetStatus
{
    /// <summary>The current, editable version.</summary>
    Draft = 0,

    /// <summary>The version the story writer reads; at most one per episode (T54).</summary>
    Approved = 1,

    /// <summary>A version replaced by a newer approved one; kept for history and never read by the writer.</summary>
    Superseded = 2,
}
