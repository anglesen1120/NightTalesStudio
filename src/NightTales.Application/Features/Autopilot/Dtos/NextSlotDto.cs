namespace NightTales.Application.Features.Autopilot.Dtos;

/// <summary>
/// One of the next planned slots <c>GET /series/{id}/autopilot</c> previews (02.8.3 plan §3.2): when the
/// episode would publish and when its run would start.
/// </summary>
/// <param name="SlotAt">The publish instant, UTC.</param>
/// <param name="StartAt">The instant the run becomes due, from <c>AutopilotTiming.Compute</c>.</param>
/// <param name="State">
/// What the run for that slot is doing, as the run table sees it: <c>planned</c>, <c>running</c>, the T1 machine
/// state slug (<c>waiting_owner</c>, <c>awaiting_authorization</c>, …) or <c>free</c> when no run is planned yet.
/// </param>
public sealed record NextSlotDto(DateTimeOffset SlotAt, DateTimeOffset StartAt, string State);
