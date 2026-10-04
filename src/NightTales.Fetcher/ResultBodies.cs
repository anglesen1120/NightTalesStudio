using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using NightTales.Domain.Research;
using NightTales.Fetcher.Core.Fetching;
using NightTales.Fetcher.InternalApi;

namespace NightTales.Fetcher;

/// <summary>
/// Turns a fetch outcome into the result body of the Api's validator (02.5.2-T2 frozen hand-off). The text is never
/// changed: a body the validator would refuse becomes a <c>failed</c> body with the code the validator would have used.
/// </summary>
internal static class ResultBodies
{
    private static readonly HashSet<string> JobCodes = new(StringComparer.Ordinal)
    {
        SourceErrors.SourceUnreachableCode,
        SourceErrors.SourceTooLargeCode,
        SourceErrors.SourceBlockedByRobotsCode,
        SourceErrors.SourceUnsupportedTypeCode,
        SourceErrors.SourceNoTextCode,
        SourceErrors.SourceDomainNotAllowedCode,
    };

    /// <summary>The <c>done</c> body of <paramref name="source"/>, or the <c>failed</c> body of the first guard it trips.</summary>
    public static FetchResultBody Done(string workerId, FetchedSource source, FetcherOptions options)
    {
        var text = source.Text;
        if (string.IsNullOrEmpty(text) || !IsWellFormed(text))
        {
            return Failed(workerId, SourceErrors.SourceNoTextCode, source.HttpStatus);
        }

        if (text.Length > options.MaxTextChars)
        {
            return Failed(workerId, SourceErrors.SourceTooLargeCode, source.HttpStatus);
        }

        // Page metadata, not user text: a title that cannot be sent is left out rather than failing the fetch.
        var title = source.Title is { } candidate && IsWellFormed(candidate) ? candidate : null;
        return new FetchResultBody(
            workerId,
            "done",
            HttpStatus: ValidHttpStatus(source.HttpStatus),
            FinalUrl: source.FinalUrl,
            Title: title,
            ContentType: source.ContentType,
            Text: text,
            TextSha256: Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
    }

    /// <summary>The <c>failed</c> body of a D8 error from the fetcher; an unlisted code is reported as <c>source_unreachable</c>.</summary>
    public static FetchResultBody FromError(string workerId, Error error)
    {
        int? status = error.Metadata is not null
            && error.Metadata.TryGetValue(SourceErrors.HttpStatusMetadataKey, out var value)
            && value is int number
            ? number
            : null;
        return Failed(workerId, error.Code, status);
    }

    /// <summary>A <c>failed</c> body; <paramref name="code"/> outside the D8 job-code set becomes <c>source_unreachable</c>.</summary>
    public static FetchResultBody Failed(string workerId, string code, int? httpStatus = null) =>
        new(workerId, "failed", ValidHttpStatus(httpStatus), ErrorCode: JobCodes.Contains(code) ? code : SourceErrors.SourceUnreachableCode);

    /// <summary>The code a <c>failed</c> body carries, for logs and metrics.</summary>
    public static string CodeOf(FetchResultBody body) => body.ErrorCode ?? string.Empty;

    /// <summary>
    /// True when <paramref name="text"/> has no lone surrogate (it would make the body unbindable: 400 <c>validation_failed</c>),
    /// no NUL/C0 control other than TAB, LF and CR, and no DEL (the Api's validator answers <c>source_no_text</c>).
    /// </summary>
    public static bool IsWellFormed(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
                {
                    return false;
                }

                index++;
            }
            else if (char.IsLowSurrogate(character) || character == '\u007f' || (character < ' ' && character is not ('\t' or '\n' or '\r')))
            {
                return false;
            }
        }

        return true;
    }

    private static int? ValidHttpStatus(int? status) => status is >= 100 and <= 599 ? status : null;
}
