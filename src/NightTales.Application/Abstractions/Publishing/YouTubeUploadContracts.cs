using System.Text;
using ErrorOr;

namespace NightTales.Application.Abstractions.Publishing;

/// <summary>
/// The snippet and status of one <c>videos.insert</c> (06.2.3-T1, 06 README D9). The record does not enforce
/// <see cref="ContainsSyntheticMedia"/>: the upload job asserts it is <see langword="true"/> (D6), and the client
/// sends the value it is given.
/// </summary>
/// <remarks>
/// A video title and description are user content: the printed form carries the lengths only, so a log line,
/// an exception message or a debugger string never shows them (06 README D17).
/// </remarks>
/// <param name="Title">The video title; never logged.</param>
/// <param name="Description">The video description; never logged.</param>
/// <param name="Tags">The tags (may be empty).</param>
/// <param name="PrivacyStatus">The wire value of <c>status.privacyStatus</c> (<c>private</c>, <c>unlisted</c>, <c>public</c>).</param>
/// <param name="PublishAt">The scheduled publish time, or <see langword="null"/> to omit <c>status.publishAt</c>.</param>
/// <param name="ContainsSyntheticMedia">The <c>status.containsSyntheticMedia</c> disclosure (always <see langword="true"/> for this product).</param>
/// <param name="SelfDeclaredMadeForKids">The <c>status.selfDeclaredMadeForKids</c> declaration.</param>
/// <param name="Length">The object's total size in bytes; must equal the <c>length</c> passed to <c>StartUploadAsync</c>.</param>
/// <param name="MimeType">The object's content type (the <c>X-Upload-Content-Type</c> of the session request).</param>
public sealed record VideoInsertRequest(
    string Title,
    string Description,
    IReadOnlyList<string> Tags,
    string PrivacyStatus,
    DateTimeOffset? PublishAt,
    bool ContainsSyntheticMedia,
    bool SelfDeclaredMadeForKids,
    long Length,
    string MimeType)
{
    /// <summary>Prints the non-content members only; the title, description and tags never reach a log line (D17).</summary>
    /// <param name="builder">The record's string builder.</param>
    /// <returns><see langword="true"/> when members were appended.</returns>
    private bool PrintMembers(StringBuilder builder)
    {
        builder
            .Append("TitleLength = ").Append(Title.Length)
            .Append(", DescriptionLength = ").Append(Description.Length)
            .Append(", TagCount = ").Append(Tags.Count)
            .Append(", PrivacyStatus = ").Append(PrivacyStatus)
            .Append(", HasPublishAt = ").Append(PublishAt is not null)
            .Append(", Length = ").Append(Length);

        return true;
    }
}

/// <summary>The completed upload: the id Google assigned to the new video.</summary>
/// <param name="VideoId">The YouTube video id.</param>
public sealed record UploadResult(string VideoId);

/// <summary>What a session holds, as the probe saw it (G7).</summary>
public abstract record UploadProbe
{
    /// <summary>Google has the whole object and answered the video id.</summary>
    /// <param name="VideoId">The YouTube video id.</param>
    public sealed record Complete(string VideoId) : UploadProbe;

    /// <summary>
    /// Google holds the bytes <c>0 .. NextByte - 1</c>; <see cref="NextByte"/> is the first byte it still needs
    /// (the <c>Range</c> end plus one, so it is exclusive of the chunk that already arrived).
    /// </summary>
    /// <param name="NextByte">The offset to resume from.</param>
#pragma warning disable CA1716 // The name is the contract of 06.2.3-T1 (G7, task file `UploadProbe.Partial`); 06.2.3-T2 and the tests consume it.
    public sealed record Partial(long NextByte) : UploadProbe;
#pragma warning restore CA1716

    /// <summary>The session no longer exists (404); the caller creates a new one.</summary>
    public sealed record Expired : UploadProbe;
}

/// <summary>One video's processing state from <c>videos.list?part=status</c>.</summary>
/// <param name="VideoId">The video id.</param>
/// <param name="UploadStatus">Google's <c>status.uploadStatus</c> (<c>uploaded</c>, <c>processed</c>, <c>failed</c>, <c>rejected</c>, <c>deleted</c>).</param>
/// <param name="FailureReason">Google's <c>status.failureReason</c> when <c>uploadStatus</c> is <c>failed</c>.</param>
/// <param name="RejectionReason">Google's <c>status.rejectionReason</c> when <c>uploadStatus</c> is <c>rejected</c>.</param>
/// <param name="PrivacyStatus">Google's <c>status.privacyStatus</c>.</param>
public sealed record YouTubeVideoStatus(
    string VideoId,
    string UploadStatus,
    string? FailureReason,
    string? RejectionReason,
    string? PrivacyStatus);

/// <summary>
/// The classifier's input (06.2.3-T3): what Google's error body and the transport said, carried on the
/// <see cref="Error.Metadata"/> of every failure the upload and refresh members return. Only the sanitised reason
/// travels, never a body (06 README D17).
/// </summary>
/// <param name="GoogleReason">Google's machine <c>reason</c> (e.g. <c>quotaExceeded</c>), sanitised, or <see langword="null"/>.</param>
/// <param name="HttpStatus">The HTTP status Google answered, or <see langword="null"/> when none arrived.</param>
/// <param name="IsTransport">Whether no answer arrived or it was a 5xx / unreadable (the job retries).</param>
public sealed record YouTubeFailure(string? GoogleReason, int? HttpStatus, bool IsTransport)
{
    /// <summary>Metadata key of <see cref="GoogleReason"/>.</summary>
    public const string ReasonKey = "google_reason";

    /// <summary>Metadata key of <see cref="HttpStatus"/>.</summary>
    public const string StatusKey = "http_status";

    /// <summary>Metadata key of <see cref="IsTransport"/>.</summary>
    public const string TransportKey = "is_transport";

    /// <summary>The longest reason this layer keeps; an untrusted body must not stretch a log line or a column.</summary>
    public const int MaxReasonLength = 64;

    /// <summary>Builds the <see cref="Error.Metadata"/> dictionary for this failure.</summary>
    /// <returns>A new dictionary with the members that are set.</returns>
    public Dictionary<string, object> ToMetadata()
    {
        var metadata = new Dictionary<string, object> { [TransportKey] = IsTransport };

        if (GoogleReason is not null)
        {
            metadata[ReasonKey] = GoogleReason;
        }

        if (HttpStatus is { } status)
        {
            metadata[StatusKey] = status;
        }

        return metadata;
    }

    /// <summary>Reads a failure back from an error the client returned.</summary>
    /// <param name="error">The error.</param>
    /// <returns>The failure, or <see langword="null"/> when the error carries no YouTube metadata.</returns>
    public static YouTubeFailure? From(Error error)
    {
        if (error.Metadata is not { } metadata || !metadata.TryGetValue(TransportKey, out var transport) || transport is not bool isTransport)
        {
            return null;
        }

        return new YouTubeFailure(
            metadata.TryGetValue(ReasonKey, out var reason) ? reason as string : null,
            metadata.TryGetValue(StatusKey, out var status) && status is int code ? code : null,
            isTransport);
    }

    /// <summary>
    /// Keeps a Google <c>reason</c> only when it is a short identifier (<c>[A-Za-z0-9_.-]</c>, at most
    /// <see cref="MaxReasonLength"/> characters); anything else is dropped to <see langword="null"/>.
    /// </summary>
    /// <param name="reason">The reason as parsed from an untrusted body.</param>
    /// <returns>The reason, or <see langword="null"/>.</returns>
    public static string? SanitizeReason(string? reason)
    {
        if (string.IsNullOrEmpty(reason) || reason.Length > MaxReasonLength)
        {
            return null;
        }

        foreach (var c in reason)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
            {
                return null;
            }
        }

        return reason;
    }
}
