namespace NightTales.Application.Features.Autopilot.Dtos;

/// <summary>
/// One autopilot run as <c>GET /series/{id}/autopilot/runs</c> returns it (02.8.3-T2 task §DTOs, plan §4 UC7): the
/// row's position in the state machine, what it stopped for, and the rows it produced.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every enum-shaped member is its wire value, not the member name.</b> <see cref="State"/> is
/// <c>planned</c>/<c>awaiting_authorization</c>/<c>published</c>/…, <see cref="Outcome"/> is
/// <c>gate_failed</c>/<c>paused</c>/…, <see cref="PauseReason"/> is one of plan §3.6's snake_case triggers (including
/// the one exception, <c>autopilot_channel_unsupported</c>), <see cref="WaitingFor"/> is
/// <c>fact_sheet</c>/<c>sources</c>/<c>story</c>/<c>video</c>, and <see cref="Step"/> is the idempotency step's
/// name. They are carried as strings because the column, the <c>CHECK</c> constraints and the web page all compare
/// against that one spelled form.
/// </para>
/// <para>
/// <b>The two stop columns answer two questions</b> (plan §3.5 rule 1): <see cref="Outcome"/> is set whenever the run
/// stopped — terminal, paused, waiting for the owner, waiting for an authorization — and <see cref="FinishedAt"/>
/// only in a terminal state. A client that wants "done" filters <see cref="FinishedAt"/>, one that wants "why did it
/// stop" reads <see cref="Outcome"/>.
/// </para>
/// <para>
/// <b>No row text, no cost, no authorization text.</b> Ids only (D19/D22): the idea, episode and publication the run
/// produced are named by id, and <see cref="Gates"/> is the summary of <see cref="GateSummaryDto"/>.
/// </para>
/// </remarks>
/// <param name="Id">The <c>autopilot_runs.id</c>.</param>
/// <param name="SlotAt">The slot's intended publish instant (plan §3.2), UTC.</param>
/// <param name="StartAt">The computed earliest safe start of the slot (plan §3.4).</param>
/// <param name="State">The live position's wire value.</param>
/// <param name="Outcome">Why the run stopped, or <see langword="null"/> while it is still running.</param>
/// <param name="PauseReason">The plan §3.6 trigger that held the run, or <see langword="null"/>.</param>
/// <param name="Step">The last completed idempotency step, or <see langword="null"/> before the first one.</param>
/// <param name="Attempt">Story generations consumed (0…2).</param>
/// <param name="RenderAttempts">Renders consumed (0…1).</param>
/// <param name="WaitingFor">Why the owner is needed while the run waits, or <see langword="null"/>.</param>
/// <param name="IdeaId">The idea the run picked, or <see langword="null"/>.</param>
/// <param name="EpisodeId">The episode the run produced, or <see langword="null"/>.</param>
/// <param name="PublicationId">The publication the run created, or <see langword="null"/>.</param>
/// <param name="Gates">The gates' summary, or <see langword="null"/> when the run carries no <c>gates.v1</c> document.</param>
/// <param name="AuthorizationId">The authorization the run relied on when it was planned, or <see langword="null"/>.</param>
/// <param name="FinishedAt">When the run reached a terminal state, or <see langword="null"/>.</param>
/// <param name="LastTransitionAt">When the state last changed.</param>
public sealed record AutopilotRunDto(
    Guid Id,
    DateTimeOffset SlotAt,
    DateTimeOffset StartAt,
    string State,
    string? Outcome,
    string? PauseReason,
    string? Step,
    int Attempt,
    int RenderAttempts,
    string? WaitingFor,
    Guid? IdeaId,
    Guid? EpisodeId,
    Guid? PublicationId,
    GateSummaryDto? Gates,
    Guid? AuthorizationId,
    DateTimeOffset? FinishedAt,
    DateTimeOffset LastTransitionAt);
