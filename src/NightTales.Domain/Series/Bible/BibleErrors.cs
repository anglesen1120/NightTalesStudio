using ErrorOr;

namespace NightTales.Domain.Series.Bible;

/// <summary>
/// The typed errors of the series bible (02 README D8, D9). Each code constant equals the catalogue constant of
/// <c>NightTales.Application.Common.Errors.ErrorCodes</c>, which the Domain may not reference (ErrorOr is its
/// only package); the Domain unit test <c>BibleErrors_Codes_MatchTheErrorCatalogue</c> keeps the two equal, and
/// <c>NightTales.Api.Http.ErrorCatalogue</c> gives each code its status (400, 412, 409, 428, 500).
/// </summary>
public static class BibleErrors
{
    /// <summary>The catalogued code of "the body breaks a plan §3.2 rule" (400).</summary>
    public const string InvalidContentCode = "bible_invalid_content";

    /// <summary>The catalogued code of "an <c>If-Match</c> that no longer names the current version" (412).</summary>
    public const string ConflictCode = "bible_conflict";

    /// <summary>The catalogued code of "accept or dismiss without a pending proposal" (409).</summary>
    public const string ProposalNotPendingCode = "bible_proposal_not_pending";

    /// <summary>The catalogued code of "a D9 route called without <c>If-Match</c>" (428, 02 D9).</summary>
    public const string IfMatchRequiredCode = "if_match_required";

    /// <summary>The catalogued code of "the series exists but its bible row does not" (500, data drift).</summary>
    public const string MissingCode = "series_bible_missing";

    /// <summary>
    /// The metadata key under which <see cref="InvalidContent"/> carries its field list. It is not one of the
    /// <c>ErrorMetadata</c> keys the Api copies into the body: the writer reads it back in process through
    /// <see cref="TryGetFieldErrors"/> and builds the <c>errors</c> map itself (plan §5), so no client sees the
    /// key.
    /// </summary>
    private const string FieldErrorsKey = "bibleFieldErrors";

    /// <summary>
    /// The 400 of a body that breaks the plan §3.2 rules, carrying one <see cref="BibleFieldError"/> per
    /// violation so the Api can answer with the per-field <c>errors</c> map the UI shows (plan §5).
    /// </summary>
    /// <param name="errors">Every violation, in field order; never empty.</param>
    /// <returns>The validation error.</returns>
    /// <exception cref="ArgumentException"><paramref name="errors"/> is empty.</exception>
    public static Error InvalidContent(IReadOnlyList<BibleFieldError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (errors.Count == 0)
        {
            throw new ArgumentException(
                "A bible_invalid_content error must name at least one field.",
                nameof(errors));
        }

        return Error.Validation(
            InvalidContentCode,
            "The bible content is not valid.",
            new Dictionary<string, object>(StringComparer.Ordinal) { [FieldErrorsKey] = errors });
    }

    /// <summary>The 412 of a save whose <c>If-Match</c> no longer names the current version (D9).</summary>
    /// <returns>The conflict error.</returns>
    public static Error Conflict() => Error.Conflict(
        ConflictCode,
        "The bible changed since you read it. Reload it and save again.");

    /// <summary>The 409 of accept or dismiss without a pending proposal (plan §4 U3/U4).</summary>
    /// <returns>The conflict error.</returns>
    public static Error ProposalNotPending() => Error.Conflict(
        ProposalNotPendingCode,
        "No bible proposal is pending.");

    /// <summary>
    /// The 428 of a required <c>If-Match</c> that is absent. The same code the fact-sheet and story routes use
    /// (02 D9), so a client has one rule for every optimistic-concurrency route.
    /// </summary>
    /// <returns>The failure error.</returns>
    public static Error IfMatchRequired() => Error.Failure(
        IfMatchRequiredCode,
        "An If-Match header with the bible ETag you read is required.");

    /// <summary>
    /// The 500 of a series whose bible row is absent — the create hook never ran or the row was deleted. It is not
    /// auto-created by any read (plan §4 U1), because a silent create would hide the drift.
    /// </summary>
    /// <returns>The failure error.</returns>
    public static Error Missing() => Error.Failure(
        MissingCode,
        "This series has no bible row.");

    /// <summary>
    /// Reads the field list of an <see cref="InvalidContent"/> error back: <c>true</c> when the error carries the
    /// structured detail, with <paramref name="errors"/> set to it.
    /// </summary>
    /// <param name="error">The error to inspect; any other error yields <see langword="false"/>.</param>
    /// <param name="errors">The field list, or empty when the error carries none.</param>
    /// <returns>Whether the error is a bible validation failure.</returns>
    public static bool TryGetFieldErrors(Error error, out IReadOnlyList<BibleFieldError> errors)
    {
        if (error.Metadata is not null
            && error.Metadata.TryGetValue(FieldErrorsKey, out var value)
            && value is IReadOnlyList<BibleFieldError> fieldErrors)
        {
            errors = fieldErrors;
            return true;
        }

        errors = [];
        return false;
    }
}
