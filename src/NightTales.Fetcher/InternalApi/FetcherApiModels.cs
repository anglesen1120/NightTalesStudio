using ErrorOr;

namespace NightTales.Fetcher.InternalApi;

/// <summary>One leased task (the Api's <c>LeaseDto</c>, 02.5.2-T2 frozen hand-off). A null member is omitted on the wire.</summary>
/// <param name="TaskId">The task id.</param>
/// <param name="Kind"><c>fetch_source</c> or <c>find_sources</c>.</param>
/// <param name="Url">The source URL of a <c>fetch_source</c> task.</param>
/// <param name="Attempt">The 1-based attempt number.</param>
/// <param name="LeaseUntil">When the lease ends unless heartbeated.</param>
/// <param name="UserAgent">The user agent the Api advertises for the fetch.</param>
/// <param name="FinderRequest">The search brief of a <c>find_sources</c> task.</param>
public sealed record LeasedTask(
    Guid TaskId,
    string Kind,
    string? Url,
    int Attempt,
    DateTimeOffset LeaseUntil,
    string? UserAgent,
    FinderRequest? FinderRequest);

/// <summary>The brief of a <c>find_sources</c> task.</summary>
/// <param name="Topic">The snake_case topic.</param>
/// <param name="Language">The language code.</param>
/// <param name="SeriesTitle">The series title.</param>
/// <param name="Brief">The free-text brief.</param>
/// <param name="AllowList">The allowed domains.</param>
/// <param name="MaxCandidates">The most candidates wanted.</param>
public sealed record FinderRequest(
    string? Topic,
    string? Language,
    string? SeriesTitle,
    string? Brief,
    IReadOnlyList<string>? AllowList,
    int MaxCandidates);

/// <summary>One candidate source a <c>find_sources</c> result carries.</summary>
/// <param name="Url">The candidate URL.</param>
/// <param name="Title">The candidate title.</param>
/// <param name="Site">The site name.</param>
public sealed record ResultCandidate(string Url, string? Title, string? Site);

/// <summary>The body of <c>POST /{taskId}/result</c>; null members are omitted on the wire.</summary>
/// <param name="WorkerId">The worker holding the lease.</param>
/// <param name="Status"><c>done</c> or <c>failed</c>.</param>
/// <param name="HttpStatus">The final HTTP status, when known.</param>
/// <param name="FinalUrl">The URL after redirects.</param>
/// <param name="Title">The page title.</param>
/// <param name="ContentType">The lowercase media type.</param>
/// <param name="Text">The extracted text.</param>
/// <param name="TextSha256">Lowercase hex SHA-256 of the UTF-8 text.</param>
/// <param name="ErrorCode">The D8 code of a failed fetch.</param>
/// <param name="Candidates">The candidates of a <c>find_sources</c> result.</param>
public sealed record FetchResultBody(
    string WorkerId,
    string Status,
    int? HttpStatus = null,
    string? FinalUrl = null,
    string? Title = null,
    string? ContentType = null,
    string? Text = null,
    string? TextSha256 = null,
    string? ErrorCode = null,
    IReadOnlyList<ResultCandidate>? Candidates = null);

/// <summary>The internal API surface the loop and the heartbeat use; the client implements it, tests fake it.</summary>
public interface IFetcherApi
{
    /// <summary>Leases up to <paramref name="max"/> tasks for <paramref name="workerId"/>.</summary>
    ValueTask<ErrorOr<IReadOnlyList<LeasedTask>>> LeaseAsync(string workerId, int max, CancellationToken ct);

    /// <summary>Extends the lease of <paramref name="taskId"/>.</summary>
    ValueTask<ErrorOr<Success>> HeartbeatAsync(Guid taskId, string workerId, CancellationToken ct);

    /// <summary>Posts the result of <paramref name="taskId"/>; only a transient failure is retried (3 attempts in all).</summary>
    ValueTask<ErrorOr<Success>> PostResultAsync(Guid taskId, FetchResultBody body, CancellationToken ct);
}

/// <summary>The typed errors of the internal API client (02.5.2-T3 step 3).</summary>
public static class FetcherApiErrors
{
    /// <summary>The catalogued code of a lost lease (409).</summary>
    public const string LeaseLostCode = "fetch_lease_lost";

    /// <summary>The catalogued code of a missing task (404).</summary>
    public const string TaskNotFoundCode = "fetch_task_not_found";

    /// <summary>The code of a 401 answer.</summary>
    public const string UnauthorizedCode = "fetcher_unauthorized";

    /// <summary>The code of a 5xx, timeout or network failure.</summary>
    public const string UnavailableCode = "fetcher_api_unavailable";

    /// <summary>The metadata key that says whether an unavailable answer is worth retrying.</summary>
    public const string RetryableKey = "retryable";

    /// <summary>409 <c>fetch_lease_lost</c>: the worker no longer holds a live lease.</summary>
    public static Error LeaseLost { get; } = Error.Conflict(LeaseLostCode, "The lease is lost.");

    /// <summary>404 <c>fetch_task_not_found</c>: the task is gone.</summary>
    public static Error TaskNotFound { get; } = Error.NotFound(TaskNotFoundCode, "The task no longer exists.");

    /// <summary>401: the token was refused; fatal.</summary>
    public static Error Unauthorized { get; } = Error.Unauthorized(UnauthorizedCode, "The internal API refused the fetcher token.");

    /// <summary>A 400 answer carrying the problem <c>code</c> (a validation failure is deterministic: never retried).</summary>
    /// <param name="code">The <c>code</c> member of the problem body, or <c>validation_failed</c>.</param>
    public static Error ValidationFailed(string code) => Error.Validation(code, "The internal API rejected the request.");

    /// <summary>A 5xx, timeout or network failure (<paramref name="retryable"/>), or another unexpected status (not retryable).</summary>
    /// <param name="retryable">Whether a result POST may be retried.</param>
    public static Error Unavailable(bool retryable = true) => Error.Unexpected(
        UnavailableCode,
        "The internal API is unavailable.",
        new Dictionary<string, object> { [RetryableKey] = retryable });

    /// <summary>Whether <paramref name="error"/> is a transient, retryable <see cref="Unavailable"/>.</summary>
    /// <param name="error">The error to test.</param>
    public static bool IsRetryable(Error error) =>
        error.Code == UnavailableCode
        && (error.Metadata is null || !error.Metadata.TryGetValue(RetryableKey, out var value) || value is true);
}
