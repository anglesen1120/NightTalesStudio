namespace NightTales.Application.Features.Rendering.Commands.RenderEpisode;

/// <summary>
/// The <c>202</c> body of <c>POST /api/v1/episodes/{id}/render</c> (09 README BD5): the job the request
/// queued, and the URL of the <c>GET /render-jobs/{id}</c> the client polls from the <c>Location</c> header.
/// </summary>
/// <remarks>
/// A render is queued, not executed: the platform returns as soon as the job row and its outbox row are
/// committed, and the progress lives on the job (<c>stage</c>/<c>percent</c>) — never on this body.
/// </remarks>
/// <param name="RenderJobId">The queued job; the endpoint answers <c>Location: /render-jobs/{id}</c>.</param>
/// <param name="Status">The job's status; <c>queued</c> at insert (09.6.3-T1 advances it).</param>
public sealed record RenderJobAcceptedDto(Guid RenderJobId, string Status);
