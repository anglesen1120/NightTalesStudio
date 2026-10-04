namespace NightTales.Application.Abstractions.Fetching;

/// <summary>
/// The wire body of <c>POST /internal/fetcher/v1/tasks/{taskId}/result</c> (02.5.2-T2 plan §5.2). Every member is
/// optional at bind time, because which ones are required depends on the task's kind and on <see cref="Status"/>;
/// <c>FetchTaskResultValidator</c> turns it into one of the <see cref="FetchTaskResult"/> cases or a 400.
/// </summary>
/// <param name="WorkerId">The fetcher instance that holds the lease.</param>
/// <param name="Status">Mandatory (T39): <c>done</c> or <c>failed</c>.</param>
/// <param name="HttpStatus">The last HTTP status the fetcher saw, when it saw one.</param>
/// <param name="FinalUrl">The URL after redirects (<c>fetch_source</c>, <c>done</c>).</param>
/// <param name="Title">The extracted title, when found.</param>
/// <param name="ContentType">The response content type.</param>
/// <param name="Text">The extracted article text.</param>
/// <param name="TextSha256">Lowercase hex SHA-256 of the UTF-8 <paramref name="Text"/>.</param>
/// <param name="ErrorCode">The D8 job code of a <c>failed</c> body.</param>
/// <param name="Candidates">The proposals of a <c>find_sources</c> <c>done</c> body.</param>
public sealed record FetchResultRequest(
    string? WorkerId,
    string? Status,
    int? HttpStatus,
    string? FinalUrl,
    string? Title,
    string? ContentType,
    string? Text,
    string? TextSha256,
    string? ErrorCode,
    IReadOnlyList<FetchCandidateRequest?>? Candidates);

/// <summary>One proposal of a <c>find_sources</c> result body.</summary>
/// <param name="Url">The proposed URL; an entry whose URL does not parse is dropped silently.</param>
/// <param name="Title">The proposed title.</param>
/// <param name="Site">The proposed site name.</param>
public sealed record FetchCandidateRequest(string? Url, string? Title, string? Site);

/// <summary>
/// A validated fetcher result (02.5.2-T2 Step 2): the union of a fetched text, a failure with a D8 code and a list of
/// proposed candidates.
/// </summary>
public abstract record FetchTaskResult;

/// <summary>A <c>fetch_source</c> task that produced text; every member is already validated and normalised.</summary>
/// <param name="HttpStatus">The HTTP status of the final response, when reported.</param>
/// <param name="FinalUrl">The normalised URL after redirects.</param>
/// <param name="Title">The cleaned title, or <see langword="null"/>.</param>
/// <param name="ContentType">The lowercase media type, without parameters.</param>
/// <param name="Text">The text exactly as posted.</param>
/// <param name="Sha256">The lowercase hex SHA-256 of the UTF-8 text, recomputed here.</param>
public sealed record FetchedResult(
    int? HttpStatus,
    string FinalUrl,
    string? Title,
    string ContentType,
    string Text,
    string Sha256) : FetchTaskResult;

/// <summary>A task the fetcher gave up on, with a code from the closed D8 set (an unknown code was already coerced).</summary>
/// <param name="ErrorCode">The D8 job code.</param>
/// <param name="HttpStatus">The last HTTP status, when reported.</param>
public sealed record FailedResult(string ErrorCode, int? HttpStatus) : FetchTaskResult;

/// <summary>The proposals of a <c>find_sources</c> task, URLs already parsed and normalised; invalid ones were dropped.</summary>
/// <param name="Candidates">At most 50 proposals.</param>
public sealed record CandidatesResult(IReadOnlyList<FetchCandidate> Candidates) : FetchTaskResult;

/// <summary>One accepted proposal.</summary>
/// <param name="Url">The normalised URL.</param>
/// <param name="Title">The cleaned title, or <see langword="null"/>.</param>
/// <param name="Site">The cleaned site name, or <see langword="null"/>.</param>
public sealed record FetchCandidate(string Url, string? Title, string? Site);
