using ErrorOr;
using Mediator;
using NightTales.Application.Features.Autopilot.Dtos;

namespace NightTales.Application.Features.Autopilot.Queries.GetAutopilot;

/// <summary>
/// <c>GET /api/v1/series/{id}/autopilot?locale=</c> (02.8.3-T2 plan §4 UC1, task step 3): everything the autopilot
/// page shows for one series — the settings, the pause state, the live authorization, the text the owner may accept
/// now, whether the owner must accept again, the channel, and the next three slots.
/// </summary>
/// <remarks>
/// <para>
/// <b>A read, not a decision.</b> Nothing here revokes, rewrites a pointer or advances a run: the covered-field
/// comparison that decides <see cref="AutopilotDto.RequiredAuthorization"/> runs without its write half, which is
/// what lets the page poll this route (contract D-2).
/// </para>
/// <para>
/// <c>locale</c> is optional and is not validated into an error: an absent or unknown value renders the text in the
/// series' own content language, which is the only locale the owner is guaranteed to read. The route's
/// <c>Accept-Language</c>-style negotiation belongs to the client; echoing back which locale was used is
/// <see cref="AuthorizationTextDto.Locale"/>'s job.
/// </para>
/// </remarks>
/// <param name="Id">The series to describe.</param>
/// <param name="Locale">The preferred render locale (<c>vi</c>/<c>en</c>), or <see langword="null"/> for the series' own.</param>
public sealed record GetAutopilotQuery(Guid Id, string? Locale) : IQuery<ErrorOr<AutopilotDto>>;
