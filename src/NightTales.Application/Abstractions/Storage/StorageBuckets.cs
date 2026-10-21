namespace NightTales.Application.Abstractions.Storage;

/// <summary>
/// The bucket names of one environment (11 D15): <c>nt-media-&lt;env&gt;</c> for every
/// <c>assets</c> row and every render output, <c>nt-public-&lt;env&gt;</c> for published previews and
/// <c>nt-music-&lt;env&gt;</c> for the 05 music library.
/// </summary>
/// <remarks>
/// The names carry the environment because staging and production share one SeaweedFS deployment per
/// environment, and a dev process must never be able to write a production bucket.
/// </remarks>
public sealed class StorageBuckets
{
    /// <summary>The bucket of every episode asset (09.6.3-T2's <c>media/…</c> keys).</summary>
    public string Media { get; set; } = "nt-media-dev";

    /// <summary>The bucket of published previews.</summary>
    public string Public { get; set; } = "nt-public-dev";

    /// <summary>The bucket of the music library (05).</summary>
    public string Music { get; set; } = "nt-music-dev";
}
