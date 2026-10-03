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

    /// <summary>A note is empty, blank or longer than the cap.</summary>
    public static Error NoteInvalid(int maxChars) => Error.Validation(
        "validation_failed",
        $"The note must hold 1 to {maxChars} characters.",
        new Dictionary<string, object> { ["field"] = "noteText" });
}
