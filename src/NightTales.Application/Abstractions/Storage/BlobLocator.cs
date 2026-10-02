using NightTales.Domain.Assets;

namespace NightTales.Application.Abstractions.Storage;

/// <summary>
/// The one place that maps an <c>assets</c> row to its bucket (09.7 plan §3, BD10 leaves the bucket to
/// the caller): every row — a rendered video, a subtitle, a thumbnail — lives in the <em>media</em>
/// bucket, because that is where 09.6.3-T2 writes its <c>media/…</c> keys.
/// </summary>
/// <remarks>
/// The switch is exhaustive over <see cref="AssetKind"/> on purpose: a new kind fails the build here
/// rather than silently landing in the wrong bucket. Kinds that are not <c>assets</c> rows (published
/// previews in <c>nt-public-&lt;env&gt;</c>, the 05 music library) resolve their bucket through
/// <see cref="StorageSettings.Buckets"/> directly.
/// </remarks>
public static class BlobLocator
{
    /// <summary>Resolves the bucket every kind of asset row is stored in.</summary>
    /// <param name="kind">The kind of the asset row.</param>
    /// <param name="settings">The environment's storage settings.</param>
    /// <returns>The bucket name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is <see langword="null"/>.</exception>
    public static string BucketFor(AssetKind kind, StorageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return kind switch
        {
            AssetKind.Video => settings.Buckets.Media,
            AssetKind.Thumbnail => settings.Buckets.Media,
            AssetKind.Audio => settings.Buckets.Media,
            AssetKind.Subtitle => settings.Buckets.Media,
            AssetKind.Image => settings.Buckets.Media,
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Unknown asset kind; add its bucket to BlobLocator."),
        };
    }
}
