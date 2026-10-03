using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ErrorOr;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;

namespace NightTales.Domain.Research;

/// <summary>
/// One source of an episode (00 §6 <c>story_sources</c>, 02.5.1 plan §3.1): a pasted URL the fetcher downloads, or
/// the user's own note. It is a child of the episode aggregate and is only ever reached through an episode id.
/// </summary>
public sealed partial class StorySource : Entity, IUserOwned
{
    /// <summary>The longest note, after trimming (00 §6 <c>note_text</c> CHECK).</summary>
    public const int NoteMaxChars = 3_000;

    private StorySource()
    {
    }

    public Guid UserId { get; private set; }

    public Guid EpisodeId { get; private set; }

    public StorySourceKind Kind { get; private set; }

    /// <summary>The normalised URL (the pasted one is only in the request); <see langword="null"/> for a note.</summary>
    public string? Url { get; private set; }

    public string? NormalizedUrl { get; private set; }

    public string? RegistrableDomain { get; private set; }

    /// <summary>The note text, 1 to <see cref="NoteMaxChars"/> characters; <see langword="null"/> for a URL source.</summary>
    public string? NoteText { get; private set; }

    public StorySourceStatus Status { get; private set; }

    public string? ErrorCode { get; private set; }

    public int? HttpStatus { get; private set; }

    public string? ContentType { get; private set; }

    /// <summary>64 lowercase hex characters; what 02.5.4-T1 counts distinct values of (F13).</summary>
    public string? ContentSha256 { get; private set; }

    /// <summary>The object-storage key of the extracted text, never a URL; <see langword="null"/> for a note.</summary>
    public string? ExtractedTextKey { get; private set; }

    public int? TextChars { get; private set; }

    public string? FinalUrl { get; private set; }

    public string? Title { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? FetchedAt { get; private set; }

    /// <summary>Creates a <see cref="StorySourceKind.Url"/> source in <see cref="StorySourceStatus.Pending"/>.</summary>
    /// <param name="id">The UUIDv7 id.</param>
    /// <param name="userId">The owner of the episode.</param>
    /// <param name="episodeId">The episode.</param>
    /// <param name="url">The parsed, normalised URL.</param>
    /// <param name="now">The command's clock reading.</param>
    public static ErrorOr<StorySource> CreateUrl(Guid id, Guid userId, Guid episodeId, SourceUrl url, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (string.IsNullOrEmpty(url.Value) || string.IsNullOrEmpty(url.RegistrableDomain))
        {
            return SourceErrors.SourceUrlInvalid;
        }

        return new StorySource
        {
            Id = id,
            UserId = userId,
            EpisodeId = episodeId,
            Kind = StorySourceKind.Url,
            Url = url.Value,
            NormalizedUrl = url.Value,
            RegistrableDomain = url.RegistrableDomain,
            Status = StorySourceStatus.Pending,
            CreatedAt = now,
        };
    }

    /// <summary>
    /// Creates a <see cref="StorySourceKind.UserNote"/> source, already <see cref="StorySourceStatus.Fetched"/>: a note
    /// has no fetch to wait for, so every source reads the same way (02.5.1 T1 [DECISION], plan §4 UC2).
    /// </summary>
    /// <param name="id">The UUIDv7 id.</param>
    /// <param name="userId">The owner of the episode.</param>
    /// <param name="episodeId">The episode.</param>
    /// <param name="noteText">The note; trimmed here, 1 to <see cref="NoteMaxChars"/> characters.</param>
    /// <param name="now">The command's clock reading.</param>
    public static ErrorOr<StorySource> CreateNote(Guid id, Guid userId, Guid episodeId, string? noteText, DateTimeOffset now)
    {
        var text = noteText?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > NoteMaxChars)
        {
            return SourceErrors.NoteInvalid(NoteMaxChars);
        }

        return new StorySource
        {
            Id = id,
            UserId = userId,
            EpisodeId = episodeId,
            Kind = StorySourceKind.UserNote,
            NoteText = text,
            Status = StorySourceStatus.Fetched,
            ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.Normalize(NormalizationForm.FormC)))),
            TextChars = text.Length,
            CreatedAt = now,
            FetchedAt = now,
        };
    }

    /// <summary>The fetch produced valid text (02.5.2-T2): <c>Pending</c> to <c>Fetched</c>.</summary>
    /// <param name="finalUrl">The URL after redirects.</param>
    /// <param name="title">The extracted title, if any.</param>
    /// <param name="contentType">The response content type.</param>
    /// <param name="sha256">The 64-character lowercase hex SHA-256 of the text.</param>
    /// <param name="textKey">The object-storage key of the stored text.</param>
    /// <param name="textChars">The text length, at least 0.</param>
    /// <param name="at">When the Api accepted the result.</param>
    public void MarkFetched(
        string finalUrl,
        string? title,
        string contentType,
        string sha256,
        string textKey,
        int textChars,
        DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(textKey);
        if (sha256 is null || !LowerHex64().IsMatch(sha256))
        {
            throw new ArgumentException("The content hash must be 64 lowercase hex characters.", nameof(sha256));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(textChars);
        RequirePending();

        Status = StorySourceStatus.Fetched;
        FinalUrl = finalUrl;
        Title = title;
        ContentType = contentType;
        ContentSha256 = sha256;
        ExtractedTextKey = textKey;
        TextChars = textChars;
        ErrorCode = null;
        FetchedAt = at;
    }

    /// <summary>The fetch ended with a D8 job outcome code: <c>Pending</c> to <c>Failed</c>.</summary>
    /// <param name="errorCode">The job outcome code.</param>
    /// <param name="httpStatus">The last HTTP status, if any.</param>
    /// <param name="at">When the failure was recorded.</param>
    public void MarkFailed(string errorCode, int? httpStatus, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        RequirePending();

        Status = StorySourceStatus.Failed;
        ErrorCode = errorCode;
        HttpStatus = httpStatus;
        FetchedAt = at;
    }

    /// <summary>A redirect hop hit the blocklist: <c>Pending</c> to <c>Rejected</c>.</summary>
    /// <param name="errorCode">The job outcome code.</param>
    public void MarkRejected(string errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        RequirePending();

        Status = StorySourceStatus.Rejected;
        ErrorCode = errorCode;
    }

    /// <summary>Puts a <see cref="StorySourceStatus.Failed"/> source back to <see cref="StorySourceStatus.Pending"/> for a new fetch.</summary>
    /// <param name="now">The command's clock reading; the retry does not move <c>created_at</c>.</param>
    /// <returns><c>Success</c>, or 409 <see cref="Episode.InvalidStateCode"/> when the source is not <c>Failed</c>.</returns>
    public ErrorOr<Success> Retry(DateTimeOffset now)
    {
        _ = now;
        if (Status != StorySourceStatus.Failed)
        {
            return Error.Conflict(Episode.InvalidStateCode, "Only a failed source can be retried.");
        }

        Status = StorySourceStatus.Pending;
        ErrorCode = null;
        HttpStatus = null;
        FetchedAt = null;

        return Result.Success;
    }

    private void RequirePending()
    {
        if (Status != StorySourceStatus.Pending)
        {
            throw new InvalidOperationException($"A source in status '{Status}' has no fetch in flight.");
        }
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex LowerHex64();
}
