using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using NightTales.Application.Abstractions.Fetching;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Research;

namespace NightTales.Application.Features.Research.Fetcher;

/// <summary>
/// Turns a fetcher's result body into a <see cref="FetchTaskResult"/> or a 400 (02.5.2-T2 Design items 2-4, T39). The
/// order of the checks is the contract: a text that is missing, then too long, then corrupt, then from a blocked
/// domain, then of an unsupported type. Lease ownership (item 1) is checked before this runs.
/// </summary>
/// <remarks>
/// Nothing here is ever stored or logged on failure: the errors name the rule, not the URL, the title or the text (D19).
/// A failure is a 400 that leaves the task <c>leased</c>; the lease expires and the sweeper decides what happens next.
/// </remarks>
public static class FetchTaskResultValidator
{
    /// <summary>The wire value of a completed body.</summary>
    public const string StatusDone = "done";

    /// <summary>The wire value of a failed body.</summary>
    public const string StatusFailed = "failed";

    private const int MaxHttpStatus = 599;

    private const int MinHttpStatus = 100;

    /// <summary>The D8 job codes a <c>fetch_source</c> body may report (02 D8).</summary>
    private static readonly HashSet<string> SourceJobCodes = new(StringComparer.Ordinal)
    {
        ErrorCodes.SourceUnreachable,
        ErrorCodes.SourceTooLarge,
        ErrorCodes.SourceBlockedByRobots,
        ErrorCodes.SourceUnsupportedType,
        ErrorCodes.SourceNoText,
        ErrorCodes.SourceDomainNotAllowed,
    };

    /// <summary>The media types the Api accepts as article text (defence in depth: the fetcher already checked).</summary>
    private static readonly HashSet<string> AcceptedMediaTypes = new(StringComparer.Ordinal)
    {
        "text/html",
        "text/plain",
    };

    /// <summary>Validates and normalises one result body.</summary>
    /// <param name="kind">The kind of the leased task.</param>
    /// <param name="body">The posted body.</param>
    /// <param name="limits">The caps.</param>
    /// <param name="psl">The public-suffix list that decides registrable domains.</param>
    /// <param name="blocklist">The blocked domains (02 D11).</param>
    /// <returns>The validated result, or the 400 to answer.</returns>
    public static ErrorOr<FetchTaskResult> Interpret(
        FetchTaskKind kind,
        FetchResultRequest body,
        FetchResultLimits limits,
        IPublicSuffixList psl,
        DomainBlocklist blocklist)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(psl);
        ArgumentNullException.ThrowIfNull(blocklist);

        if (body.Status is not (StatusDone or StatusFailed))
        {
            return FetcherErrors.Invalid("status", "status is required and must be 'done' or 'failed'.");
        }

        var done = body.Status == StatusDone;
        var httpStatus = body.HttpStatus is >= MinHttpStatus and <= MaxHttpStatus ? body.HttpStatus : null;

        return (kind, done) switch
        {
            (FetchTaskKind.FetchSource, true) => Fetched(body, httpStatus, limits, psl, blocklist),
            (FetchTaskKind.FetchSource, false) => new FailedResult(CoerceSourceCode(body.ErrorCode), httpStatus),
            (FetchTaskKind.FindSources, true) => Candidates(body, psl),
            _ => new FailedResult(ErrorCodes.FindSourcesGenerationFailed, httpStatus),
        };
    }

    private static ErrorOr<FetchTaskResult> Fetched(
        FetchResultRequest body,
        int? httpStatus,
        FetchResultLimits limits,
        IPublicSuffixList psl,
        DomainBlocklist blocklist)
    {
        var text = body.Text;
        if (string.IsNullOrEmpty(text))
        {
            return FetcherErrors.Job(ErrorCodes.SourceNoText, "The result carries no text.");
        }

        if (text.Length > limits.MaxTextChars)
        {
            return FetcherErrors.Job(ErrorCodes.SourceTooLarge, "The text exceeds the character cap.");
        }

        // A lone surrogate has no UTF-8 form, so the hash below would be over a replacement and a stored object would
        // not round-trip; a NUL or another control character cannot be stored in the text columns downstream.
        if (!IsWellFormed(text) || HasDisallowedControl(text))
        {
            return FetcherErrors.Job(ErrorCodes.SourceNoText, "The text is not valid UTF-8 text.");
        }

        var sha256 = HashUtf8(text);
        if (!string.Equals(body.TextSha256, sha256, StringComparison.OrdinalIgnoreCase))
        {
            return FetcherErrors.Job(ErrorCodes.SourceNoText, "The text hash does not match the text.");
        }

        var parsed = SourceUrl.Parse(body.FinalUrl, psl);
        if (parsed.IsError || blocklist.IsBlocked(parsed.Value.RegistrableDomain))
        {
            return FetcherErrors.Job(ErrorCodes.SourceDomainNotAllowed, "The final URL is not an allowed source.");
        }

        var mediaType = MediaTypeOf(body.ContentType);
        if (mediaType is null || !AcceptedMediaTypes.Contains(mediaType))
        {
            return FetcherErrors.Job(ErrorCodes.SourceUnsupportedType, "The content type is not accepted.");
        }

        return new FetchedResult(httpStatus, parsed.Value.Value, CleanLabel(body.Title), mediaType, text, sha256);
    }

    private static ErrorOr<FetchTaskResult> Candidates(FetchResultRequest body, IPublicSuffixList psl)
    {
        if (body.Candidates is null)
        {
            return FetcherErrors.Invalid("candidates", "candidates is required for a finished find_sources result.");
        }

        if (body.Candidates.Count > FetchResultLimits.MaxCandidates)
        {
            return FetcherErrors.Invalid(
                "candidates",
                $"candidates must not carry more than {FetchResultLimits.MaxCandidates} entries.");
        }

        var accepted = new List<FetchCandidate>(body.Candidates.Count);
        foreach (var candidate in body.Candidates)
        {
            // An entry whose URL fails SourceUrl.Parse is dropped silently (T39 item 4): the user re-adds it through
            // 02.5.1, which validates again anyway.
            if (candidate is null || SourceUrl.Parse(candidate.Url, psl) is not { IsError: false } parsed)
            {
                continue;
            }

            accepted.Add(new FetchCandidate(parsed.Value.Value, CleanLabel(candidate.Title), CleanLabel(candidate.Site)));
        }

        return new CandidatesResult(accepted);
    }

    /// <summary>An unknown code on a failed body is stored as <c>source_unreachable</c>, never as itself (T39).</summary>
    private static string CoerceSourceCode(string? code) =>
        code is not null && SourceJobCodes.Contains(code) ? code : ErrorCodes.SourceUnreachable;

    private static string? MediaTypeOf(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var separator = contentType.IndexOf(';', StringComparison.Ordinal);
        var mediaType = (separator < 0 ? contentType : contentType[..separator]).Trim().ToLowerInvariant();

        return mediaType.Length == 0 ? null : mediaType;
    }

    /// <summary>
    /// A title or site name without control, format (bidi, zero-width, BOM), line/paragraph separator, private-use or
    /// unassigned characters, with whitespace runs collapsed, trimmed and capped; <see langword="null"/> when nothing is left.
    /// </summary>
    private static string? CleanLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !IsWellFormed(value))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(value.Length, FetchResultLimits.MaxLabelLength));
        foreach (var rune in value.EnumerateRunes())
        {
            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.PrivateUse:
                case UnicodeCategory.OtherNotAssigned:
                    continue;
            }

            if (Rune.IsWhiteSpace(rune))
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }

                continue;
            }

            builder.Append(rune.ToString());
        }

        var cleaned = builder.ToString().Trim();
        if (cleaned.Length > FetchResultLimits.MaxLabelLength)
        {
            var cut = FetchResultLimits.MaxLabelLength;
            if (char.IsHighSurrogate(cleaned[cut - 1]))
            {
                cut--;
            }

            cleaned = cleaned[..cut];
        }

        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>The lowercase hex SHA-256 of the UTF-8 form of a well-formed <paramref name="text"/>, without a full temporary array of the text.</summary>
    private static string HashUtf8(string text)
    {
        const int ChunkChars = 16 * 1024;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var encoder = Encoding.UTF8.GetEncoder();
        var buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(ChunkChars));
        try
        {
            for (var offset = 0; offset < text.Length; offset += ChunkChars)
            {
                var count = Math.Min(ChunkChars, text.Length - offset);
                var flush = offset + count >= text.Length;
                var written = encoder.GetBytes(text.AsSpan(offset, count), buffer, flush);
                hash.AppendData(buffer, 0, written);
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsWellFormed(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                index++;
            }
            else if (char.IsSurrogate(text[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>NUL, the other C0 controls except tab, line feed and carriage return, and DEL.</summary>
    private static bool HasDisallowedControl(string text)
    {
        foreach (var character in text)
        {
            if ((character < ' ' && character is not ('\t' or '\n' or '\r')) || character == '\u007f')
            {
                return true;
            }
        }

        return false;
    }
}
