namespace NightTales.Application.Features.Autopilot.Dtos;

/// <summary>
/// The <c>POST /api/v1/series/{id}/autopilot/authorization</c> success body (201): which revision became current and
/// when the owner accepted it.
/// </summary>
/// <param name="Version">The 1-based version of the row the accept created (<c>previous + 1</c>).</param>
/// <param name="AcceptedAt">The acceptance instant, from the command's clock.</param>
public sealed record AcceptedAuthorizationDto(int Version, DateTimeOffset AcceptedAt);
