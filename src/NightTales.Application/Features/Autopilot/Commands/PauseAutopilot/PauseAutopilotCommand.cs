using ErrorOr;
using Mediator;

namespace NightTales.Application.Features.Autopilot.Commands.PauseAutopilot;

/// <summary>
/// <c>POST /api/v1/series/{id}/autopilot/pause</c> (02.8.3 plan §4 UC5, §3.6 row 15): the owner holds the series, so
/// no new run of it starts until it is resumed.
/// </summary>
/// <remarks>
/// <para>
/// <b>The only error is 404.</b> §3.6 row 15 is always allowed — pausing is the brake, and a series that is already
/// paused, switched off or archived answers 204 like any other (the series row's own rules make the second call a
/// no-op). A caller who cannot see the series gets 404 from the tenant-filtered lookup, never 403 (09 README D10).
/// </para>
/// <para>
/// <b>The note is not stored.</b> The wire carries <c>{ note? }</c> so the owner's reason can be shown in the UI later;
/// no column of <c>series</c> or <c>autopilot_runs</c> holds it, and D8 forbids free text in an audit entry, so the
/// command accepts it and writes nothing. 02.8.3-T4 (the pause triggers, spend caps and alerts) owns whatever the note
/// becomes.
/// </para>
/// </remarks>
/// <param name="SeriesId">The series to hold.</param>
/// <param name="Note">The owner's optional note; accepted and not stored (see the remarks).</param>
public sealed record PauseAutopilotCommand(
    Guid SeriesId,
    string? Note) : ICommand<ErrorOr<Success>>;
