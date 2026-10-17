namespace NightTales.Domain.Autopilot.Ideas;

/// <summary>
/// The lifecycle of one episode idea (02.8.1 plan §3.1, <c>episode_ideas.status</c>), stored as
/// snake_case text: <c>proposed</c>, <c>picked</c>, <c>rejected</c>, <c>used</c>.
/// </summary>
/// <remarks>
/// The 02.8.1 plan's state machine: a run persists every surviving candidate as <see cref="Proposed"/>,
/// scoring marks the winner <see cref="Picked"/> and the rest <see cref="Rejected"/> (02.8.1-T2), and
/// 02.8.3 marks the picked idea <see cref="Used"/> once the episode exists. A rejected idea is terminal:
/// it is never picked or used. The stored text is the EF <c>EnumToSnakeCaseStringConverter</c> form, so the
/// member names carry exactly the wire values (a status check constraint is added with the table).
/// </remarks>
public enum IdeaStatus
{
    /// <summary><c>proposed</c>: the candidate a run stored and nothing has decided about yet (02.8.1-T1).</summary>
    Proposed,

    /// <summary><c>picked</c>: the winner the scorer chose for the run (02.8.1-T2).</summary>
    Picked,

    /// <summary><c>rejected</c>: lost the ranking or was blocked, with <c>rejected_reason</c> set (02.8.1-T2).</summary>
    Rejected,

    /// <summary><c>used</c>: the picked idea became an episode, so <c>episode_id</c> is set (02.8.3-T1).</summary>
    Used,
}
