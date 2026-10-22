namespace NightTales.Application.Features.Publishing.Upload;

/// <summary>
/// The one exception a retried upload attempt raises (06.2.3-T2 plan §Design "Job and command"): a transport-shaped
/// failure the row survives — Google answered nothing, or the session is gone — so the attempt ends without a verdict
/// and Hangfire's own retry counter decides how many more runs the upload gets.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an exception and not an error.</b> An <c>ErrorOr</c> answer is a decision the handler already wrote down, and
/// the job swallows every one of them (a deleted row must never retry five times). "Run me again" is the opposite
/// statement, so it travels the way the scheduler understands it — the ingest job's
/// <c>IngestAttemptException</c> precedent (09.6.3-T2, WT52).
/// </para>
/// <para>
/// <b>Its text is a code.</b> The message carries one of the closed <see cref="Codes"/> values and never a Google
/// reason, a session URI, a token or a title: the Api's Hangfire dashboard shows this exception, so the class exists
/// to keep provider text out of it (06 README D17). The original exception is deliberately not attached: HttpClient and
/// Npgsql messages can carry a request URI (the session URI is a capability) or a row's values, and the log line the
/// job writes already names the exception type for the operator.
/// </para>
/// </remarks>
public sealed class PublishAttemptException : Exception
{
    /// <summary>The code of an attempt that failed for a reason none of the platform's own codes describes.</summary>
    public const string InfrastructureCode = "publication_attempt_failed";

    /// <summary>The code of a retryable Google failure (no answer, or a session that has to be re-probed).</summary>
    public const string RetryCode = "publication_retry";

    /// <summary>Raises one attempt's failure.</summary>
    /// <param name="code">One of <see cref="Codes"/>.</param>
    public PublishAttemptException(string code)
        : base(code)
    {
        Code = code;
    }

    /// <summary>The catalogue code this attempt ended with; safe to log and to persist.</summary>
    public string Code { get; }

    /// <summary>Every value <see cref="Code"/> may hold on this path.</summary>
    public static IReadOnlyList<string> Codes { get; } = [RetryCode, InfrastructureCode];
}
