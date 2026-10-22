using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;

namespace NightTales.Application.Features.Autopilot.Commands.ResumeAutopilot;

/// <summary>
/// <c>POST /api/v1/series/{id}/autopilot/resume</c> (02.8.3 plan §4 UC6, §3.6 "Resume"): the owner lifts the pause, so
/// the next tick plans slots again and the held runs continue where they stood.
/// </summary>
/// <remarks>
/// The documented refusals are plan §4 UC6's: 409 <c>autopilot_authorization_required</c> when the series has no live
/// authorization, 409 <c>channel_reauth_required</c> when that authorization's connection is no longer
/// <c>active</c>, and — once 02.8.3-T4 lands the trigger ladder — 409 <c>autopilot_pause_reason_active</c> when a
/// §3.6 trigger still holds.
/// </remarks>
/// <param name="SeriesId">The series to resume.</param>
[NotAudited("02.8.3-T4 owns the autopilot.resumed row, whose data carries previousPauseReason from its trigger re-check; this route only clears the pause fields and T4 writes the row.")]
public sealed record ResumeAutopilotCommand(Guid SeriesId) : ICommand<ErrorOr<Success>>;
