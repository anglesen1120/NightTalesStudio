namespace NightTales.Application.Abstractions.Rendering;

/// <summary>
/// The two downloadable outputs of one render task (05 README D4): each maps to its own route and its own
/// byte cap, and each is optional on a finished task, so the caller asks for them separately.
/// </summary>
public enum RenderOutputKind
{
    /// <summary><c>GET /v1/renders/{task_id}/video</c> — the assembled MP4, capped by <c>Render:MaxVideoBytes</c>.</summary>
    Video,

    /// <summary>
    /// <c>GET /v1/renders/{task_id}/subtitles</c> — the subtitle track (<c>application/x-subrip</c>), capped
    /// by <c>Render:MaxSubtitleBytes</c>. A node that has none answers <c>adapter_output_absent</c>, which is
    /// not a job failure (05 D10).
    /// </summary>
    Subtitles,
}
