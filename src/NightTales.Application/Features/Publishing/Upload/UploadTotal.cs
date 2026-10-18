using ErrorOr;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Abstractions.Storage;

namespace NightTales.Application.Features.Publishing.Upload;

/// <summary>
/// Resolves the one total a resumable upload runs with (06.2.3-T1, CR-18): the asset row's <c>size_bytes</c>,
/// cross-checked against the length the blob store reports for the object the caller just opened.
/// </summary>
/// <remarks>
/// <para>
/// The row is the authority: <c>assets.size_bytes</c> is what the uploader, the quota accounting and the publication
/// row agree on, and <see cref="BlobObject.Length"/> is nullable — a store that reports none must not block the
/// upload. A reported length that <em>disagrees</em> with it means the object was replaced or truncated under the row,
/// so no session is created and the answer is <c>youtube_upload_failed</c> (the caller fails the publication instead
/// of publishing a half-length video, which a lost byte cannot repair).
/// </para>
/// <para>
/// Public because this solution has no <c>InternalsVisibleTo</c> and the unit tests exercise it.
/// </para>
/// </remarks>
public static class UploadTotal
{
    /// <summary>The upload's total in bytes, or <c>youtube_upload_failed</c> before any Google call.</summary>
    /// <param name="assetSizeBytes">The asset row's <c>size_bytes</c>, which must be positive.</param>
    /// <param name="blob">The opened object; only its reported <see cref="BlobObject.Length"/> is read.</param>
    /// <returns>The total to hand to <c>IYouTubeClient.StartUploadAsync</c>.</returns>
    public static ErrorOr<long> Resolve(long assetSizeBytes, BlobObject blob)
    {
        ArgumentNullException.ThrowIfNull(blob);

        if (assetSizeBytes <= 0)
        {
            return Failure("The asset's size in bytes must be positive before an upload session is created.");
        }

        if (blob.Length is { } reportedLength && reportedLength != assetSizeBytes)
        {
            return Failure(
                "The stored object's length disagrees with the asset's size in bytes, so no upload session was created.");
        }

        return assetSizeBytes;
    }

    /// <summary>The fixed failure both branches answer: no value of the row or the object ever reaches the text.</summary>
    private static Error Failure(string description) =>
        Error.Failure(
            YouTubeErrorCodes.UploadFailed,
            description,
            new YouTubeFailure(null, null, false).ToMetadata());
}
