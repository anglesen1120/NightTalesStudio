using System.Text.Json.Serialization;

namespace NightTales.Domain.Rendering;

/// <summary>
/// The <c>music</c> block of <c>render-params.v1</c> (05 README D8/D11); the parent property is <c>null</c> when
/// the selector picked nothing.
/// </summary>
/// <param name="TrackId">The catalog track id.</param>
/// <param name="BgmVolume">The gain frozen at render time.</param>
/// <param name="Attribution">The track's catalog attribution frozen at render time (06.2.1-T1 reads it from here).</param>
public sealed record MusicParams(
    [property: JsonPropertyName("track_id")][property: JsonRequired] string TrackId,
    [property: JsonPropertyName("bgm_volume")][property: JsonRequired] decimal BgmVolume,
    [property: JsonPropertyName("attribution")][property: JsonRequired] string? Attribution);
