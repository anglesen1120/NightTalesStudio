using NightTales.Domain.Series;

namespace NightTales.Application.Rendering.Quality;

/// <summary>
/// Everything the verdict needs that is <em>not</em> in the measured report (05 README D10): the render
/// request's own expectations. They come from <c>render_jobs.params</c> (<c>render-params.v1</c>, 05
/// D11) through the 09.6.3-T2 ingest.
/// </summary>
/// <param name="Aspect">The series' aspect; it decides the width/height pair <c>resolution</c> requires.</param>
/// <param name="SubtitlesEnabled">
/// Whether subtitles were requested. When they were not, both subtitle checks are skipped
/// (<c>pass = true</c>, <c>value = null</c>) instead of failing a render that was never supposed to
/// carry them.
/// </param>
/// <param name="MusicSelected">
/// Whether a music track was selected (<c>render-params.v1.music != null</c>, T12); <c>false</c> raises
/// the advisory <c>music_none</c> hint.
/// </param>
public sealed record RenderExpectations(Aspect Aspect, bool SubtitlesEnabled, bool MusicSelected);
