using System.Buffers;
using ErrorOr;
using NightTales.Application.Abstractions.Publishing;

namespace NightTales.Application.Features.Publishing.Upload;

/// <summary>
/// Positions a caller's stream at the byte offset a resumed resumable upload continues from (06.2.3-T1, G7):
/// 06.2.3-T2 opens the asset through <c>IBlobStore.OpenReadAsync</c>, calls this before
/// <c>IYouTubeClient.ResumeUploadAsync</c>, and the client's first <c>Content-Range</c> starts exactly where the
/// stream now stands.
/// </summary>
/// <remarks>
/// <para>
/// A seekable stream is positioned with <see cref="Stream.Seek"/> and <em>no</em> read at all. An S3 response body is
/// not reliably seekable (CR-17 asks for a ranged read to remove this path), so a non-seekable stream is advanced by
/// discarding <c>offset</c>-worth of bytes through one rented <see cref="DiscardBufferBytes"/> buffer: memory stays
/// constant whatever the offset, and no buffer is ever sized from it. A stream that ends before the offset means the
/// stored object shrank after the session was created, so the answer is <c>youtube_session_expired</c> — the caller
/// creates a new session — instead of resuming from a byte the session does not expect.
/// </para>
/// <para>
/// Public because this solution has no <c>InternalsVisibleTo</c> and the unit tests exercise it (the
/// <c>BibleEtags</c> precedent).
/// </para>
/// </remarks>
public static class UploadStreamPositioning
{
    /// <summary>The one buffer the discard path rents, in bytes (256 KiB, the chunk granularity of G7).</summary>
    public const int DiscardBufferBytes = 256 * 1024;

    /// <summary>
    /// Advances <paramref name="stream"/> so its next byte read is <paramref name="offset"/>, or answers
    /// <c>youtube_session_expired</c> when the stream ends first.
    /// </summary>
    /// <param name="stream">The object's read stream; owned by the caller, never disposed here.</param>
    /// <param name="offset">The next byte the session still needs, as the probe answered it (&gt;= 0).</param>
    /// <param name="cancellationToken">The job's token; a cancellation is rethrown, never mapped to an error.</param>
    /// <returns>Success once the stream stands at <paramref name="offset"/>.</returns>
    public static async Task<ErrorOr<Success>> EnsureAtAsync(
        Stream stream,
        long offset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();

        if (offset < 0)
        {
            return Failure(
                YouTubeErrorCodes.UploadFailed,
                "The upload's resume offset must not be negative.");
        }

        if (offset == 0)
        {
            return Result.Success;
        }

        if (stream.CanSeek)
        {
            stream.Seek(offset, SeekOrigin.Begin);
            return Result.Success;
        }

        return await DiscardAsync(stream, offset, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Consumes <paramref name="offset"/> bytes off a stream that cannot seek, one 256 KiB read at a time.</summary>
    private static async Task<ErrorOr<Success>> DiscardAsync(
        Stream stream,
        long offset,
        CancellationToken cancellationToken)
    {
        // Rented once per call and returned in `finally`. The slice is capped at DiscardBufferBytes because the pool
        // may hand back a larger array, and the loop never asks the stream for more than that slice whatever the
        // offset is, so a 2 GB resume costs the same 256 KiB of memory as a 256 KiB one.
        var buffer = ArrayPool<byte>.Shared.Rent(DiscardBufferBytes);

        try
        {
            var remaining = offset;

            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var read = await stream
                    .ReadAsync(buffer.AsMemory(0, (int)Math.Min(DiscardBufferBytes, remaining)), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    return Failure(
                        YouTubeErrorCodes.SessionExpired,
                        "The stored object ended before the upload's resume offset, so the session can no longer continue.");
                }

                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Result.Success;
    }

    /// <summary>The fixed failure every branch answers: one of the client's internal codes plus the empty failure.</summary>
    private static Error Failure(string code, string description) =>
        Error.Failure(code, description, new YouTubeFailure(null, null, false).ToMetadata());
}
