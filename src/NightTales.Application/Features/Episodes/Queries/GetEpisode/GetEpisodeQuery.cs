using ErrorOr;
using Mediator;

namespace NightTales.Application.Features.Episodes.Queries.GetEpisode;

/// <summary>
/// <c>GET /api/v1/episodes/{id}</c> (09.5.1 plan §4 UC3, §5): one of the caller's episodes, cancelled
/// ones included in the 404 because the read path hides them (F14).
/// </summary>
/// <remarks>
/// 09.5.3-T1 extends the returned <see cref="EpisodeDto"/> with <c>nextActions</c>,
/// <c>currentStory</c> and <c>generation</c> (02 CR-7); this task pins the basic members.
/// </remarks>
/// <param name="Id">The episode to read; another tenant's id answers the same 404 as a missing one (D10).</param>
public sealed record GetEpisodeQuery(Guid Id) : IQuery<ErrorOr<EpisodeDto>>;
