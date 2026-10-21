using ErrorOr;
using NightTales.Application.Common.Errors;

namespace NightTales.Application.Features.Research.Fetcher;

/// <summary>
/// The errors of the fetcher's internal API (02.5.2-T2). <c>fetch_task_not_found</c> and <c>fetch_lease_lost</c> are
/// internal-only codes (T43): they never reach a user and never land in <c>story_sources.error_code</c>.
/// </summary>
public static class FetcherErrors
{
    /// <summary>404: the task does not exist (any longer).</summary>
    public static Error TaskNotFound { get; } =
        Error.NotFound(ErrorCodes.FetchTaskNotFound, "The fetch task does not exist.");

    /// <summary>409: the caller does not hold a live lease on the task.</summary>
    public static Error LeaseLost { get; } =
        Error.Conflict(ErrorCodes.FetchLeaseLost, "The lease on the fetch task is no longer held by this worker.");

    /// <summary>400: the task kind has no consumer for its result yet.</summary>
    public static Error KindUnsupported { get; } =
        Error.Validation(ErrorCodes.FetchTaskKindUnsupported, "No consumer accepts the result of this task kind yet.");

    /// <summary>A 400 <c>validation_failed</c> on one body member.</summary>
    /// <param name="field">The camelCase member name.</param>
    /// <param name="description">What is wrong.</param>
    public static Error Invalid(string field, string description) =>
        Error.Validation(
            ErrorCodes.ValidationFailed,
            description,
            new Dictionary<string, object> { [ErrorMetadata.Field] = field });

    /// <summary>A 400 carrying one of the D8 job codes (<c>source_no_text</c>, <c>source_too_large</c>, ...).</summary>
    /// <param name="code">The D8 code.</param>
    /// <param name="description">What is wrong; never the text or the URL.</param>
    public static Error Job(string code, string description) => Error.Validation(code, description);
}
