namespace NightTales.Application.Features.Autopilot.Dtos;

/// <summary>
/// One keyset page of a series' autopilot runs (02.8.3-T2 task §Routes: <c>{ items, nextCursor }</c>, plan §4 UC7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Not <c>Page&lt;T&gt;</c>.</b> The frozen body of this route is a page plus the optional
/// <c>calibrationReport</c> of 02.8.2-T3 (§Routes: "no separate route"), so the shape is its own record rather than
/// the shared <c>Page&lt;T&gt;</c> of D14. The report itself is not part of this build — 02.8.2-T3 has not landed
/// its <c>GateCalibrationReportDto</c>/<c>GateCalibrationStatus</c> types, so T2 omits the member entirely (recorded as
/// a gap in the delivery) and leaves the DTO the room to carry it without reshaping the page.
/// </para>
/// <para>
/// <b>The ordering is <c>(slot_at DESC, id DESC)</c></b> and <see cref="NextCursor"/> is the opaque base64url
/// <c>Cursor</c> of the last row of the page: the next request continues strictly after that pair, so a run inserted
/// between two requests can neither duplicate nor skip one (09 README D14).
/// </para>
/// </remarks>
/// <param name="Items">The page's runs, newest slot first.</param>
/// <param name="NextCursor">The cursor of the last row, or <see langword="null"/> when this is the last page.</param>
public sealed record AutopilotRunPageDto(
    IReadOnlyList<AutopilotRunDto> Items,
    string? NextCursor);
