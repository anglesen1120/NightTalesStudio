using ErrorOr;

namespace NightTales.Domain.Research;

/// <summary>
/// The typed errors of the source rules (02 README D8). The code constants equal
/// <c>NightTales.Application.Common.Errors.ErrorCodes</c>, which the Domain may not reference; the Domain unit test
/// <c>SourceErrors_Codes_MatchTheErrorCatalogue</c> keeps the two equal.
/// </summary>
public static class SourceErrors
{
    /// <summary>The catalogued code of an unusable source URL (400).</summary>
    public const string SourceUrlInvalidCode = "source_url_invalid";

    /// <summary>The catalogued code of a blocked source domain (400).</summary>
    public const string SourceDomainNotAllowedCode = "source_domain_not_allowed";

    /// <summary>The user-facing explanation that accompanies <see cref="SourceDomainNotAllowedCode"/> (02.5.1 T1 Design step 3).</summary>
    public const string DomainNotAllowedMessage =
        "Paste an article or record page; video and social platforms cannot be used as sources.";

    /// <summary>The URL is not an http(s) DNS-named page the platform may fetch.</summary>
    public static Error SourceUrlInvalid { get; } = Error.Validation(
        SourceUrlInvalidCode,
        "The source URL must be an http or https address of a public web page.");

    /// <summary>The domain is on the blocklist (02 D11).</summary>
    public static Error SourceDomainNotAllowed { get; } = Error.Validation(
        SourceDomainNotAllowedCode,
        DomainNotAllowedMessage);

    /// <summary>The catalogued code of a host that did not answer (02 D8).</summary>
    public const string SourceUnreachableCode = "source_unreachable";

    /// <summary>The catalogued code of a body over the size cap (02 D8).</summary>
    public const string SourceTooLargeCode = "source_too_large";

    /// <summary>The catalogued code of a page robots.txt forbids (02 D8).</summary>
    public const string SourceBlockedByRobotsCode = "source_blocked_by_robots";

    /// <summary>The catalogued code of an unsupported content type (02 D8).</summary>
    public const string SourceUnsupportedTypeCode = "source_unsupported_type";

    /// <summary>The catalogued code of a page without extractable text (02 D8).</summary>
    public const string SourceNoTextCode = "source_no_text";

    /// <summary>The host did not answer, answered with an error status, or the fetch ran out of budget (02 D8, D11).</summary>
    public static Error SourceUnreachable { get; } = Error.Failure(
        SourceUnreachableCode,
        "The source could not be reached.");

    /// <summary>The decompressed body, the text or the page structure exceeds the cap (02 D8, D11).</summary>
    public static Error SourceTooLarge { get; } = Error.Failure(
        SourceTooLargeCode,
        "The source is too large.");

    /// <summary>robots.txt forbids the fetch, or could not be read (02 D8, D11).</summary>
    public static Error SourceBlockedByRobots { get; } = Error.Failure(
        SourceBlockedByRobotsCode,
        "The site's robots.txt does not allow this page to be read.");

    /// <summary>The response is not text/html, application/xhtml+xml or text/plain, or its bytes are binary (02 D8, D11).</summary>
    public static Error SourceUnsupportedType { get; } = Error.Failure(
        SourceUnsupportedTypeCode,
        "The source is not a text page.");

    /// <summary>No article text could be extracted (02 D8).</summary>
    public static Error SourceNoText { get; } = Error.Failure(
        SourceNoTextCode,
        "No readable text was found on the source page.");

    /// <summary>
    /// <see cref="SourceUnreachable"/> carrying the HTTP status the host answered with (<c>httpStatus</c> metadata).
    /// </summary>
    /// <param name="httpStatus">The status of the 4xx/5xx answer.</param>
    public static Error SourceUnreachableWithStatus(int httpStatus) => Error.Failure(
        SourceUnreachableCode,
        SourceUnreachable.Description,
        new Dictionary<string, object> { [HttpStatusMetadataKey] = httpStatus });

    /// <summary>The metadata key under which a fetch error carries the HTTP status.</summary>
    public const string HttpStatusMetadataKey = "httpStatus";

    /// <summary>A note is empty, blank or longer than the cap.</summary>
    public static Error NoteInvalid(int maxChars) => Error.Validation(
        "validation_failed",
        $"The note must hold 1 to {maxChars} characters.",
        new Dictionary<string, object> { ["field"] = "noteText" });
}
