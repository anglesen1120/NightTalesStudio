namespace NightTales.Application.Features.Autopilot.Dtos;

/// <summary>
/// The gate verdicts of one autopilot run as <c>GET /series/{id}/autopilot/runs</c> summarises them (02.8.3 plan §4
/// UC7: <c>gateResults (summary: mode, story.pass, video.pass, failures[])</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>A summary, never the document.</b> The stored <c>gates.v1</c> document (02.8.2's <c>GateResultWriter</c>) is
/// the run's audit record: it carries the per-check evidence, the profile name and the ids of the story version,
/// render job and asset. The run list answers the four questions the UI actually asks — did each gate run in
/// <c>live</c> or <c>dry_run</c> mode, did it pass, and why did it not — and nothing else, so the list stays small
/// and a check's evidence never reaches a page that is polled.
/// </para>
/// <para>
/// <b>The members are wire values and nullable on purpose.</b> <see cref="Mode"/> is <c>live</c>/<c>dry_run</c>
/// (plan §3.8) and <see cref="StoryPass"/>/<see cref="VideoPass"/> are <see langword="null"/> when that gate has not
/// evaluated the run yet — the story verdict and the video verdict arrive hours apart in one document. A run with no
/// document at all answers <see langword="null"/> for the whole value, never an error (task §Failure handling).
/// </para>
/// <para>
/// <b>One <c>failures</c> list.</b> The document keeps each gate's failures inside its own block
/// (<c>story.failures</c>, <c>video.failures</c>); this summary concatenates them in document order, story first,
/// without duplicates, because the UI's run log shows one reason list and the codes are unique per run.
/// </para>
/// </remarks>
/// <param name="Mode">The gate mode the run was evaluated in: <c>live</c> or <c>dry_run</c>.</param>
/// <param name="StoryPass">Whether the story gate passed, or <see langword="null"/> when it has not evaluated yet.</param>
/// <param name="VideoPass">Whether the video gate passed, or <see langword="null"/> when it has not evaluated yet.</param>
/// <param name="Failures">The failure codes of both gates, story first, without duplicates.</param>
public sealed record GateSummaryDto(
    string? Mode,
    bool? StoryPass,
    bool? VideoPass,
    IReadOnlyList<string> Failures);
