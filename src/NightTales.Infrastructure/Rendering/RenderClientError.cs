namespace NightTales.Infrastructure.Rendering;

/// <summary>
/// What one failed adapter call looks like once the HTTP layer has read it and before it is mapped
/// (09 README BD6): the status, the envelope's <c>error.code</c> and <c>Retry-After</c> the adapter sent,
/// and whether the call never produced a response at all.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately narrow: it carries no body, no adapter message and no request data, so the mapper it feeds
/// (<see cref="AdapterErrorMapper"/>) has nothing raw to copy into a failure and the node's key and base URL
/// can never travel with it (09 README BD16).
/// </para>
/// <para>
/// <see cref="HttpStatus"/> is <c>null</c> when no response arrived (transport error, timeout).
/// <see cref="AdapterCode"/> is <c>null</c> when the answer carried no parsable envelope, which the mapper
/// treats as its own fault case rather than as a transient one.
/// </para>
/// </remarks>
/// <param name="HttpStatus">The response status, or <c>null</c> when the call produced no response.</param>
/// <param name="AdapterCode">The envelope's <c>error.code</c>, or <c>null</c> when the answer had none.</param>
/// <param name="RetryAfterHeader">The parsed <c>Retry-After</c> header, or <c>null</c> when absent or unparsable
/// (see <see cref="AdapterErrorMapper.ParseRetryAfter"/>).</param>
/// <param name="IsTransport">Whether the call failed before a response arrived (DNS, refused connection, reset).</param>
/// <param name="IsTimeout">Whether the call ran into a timeout, attempt or total.</param>
public sealed record RenderClientError(
    int? HttpStatus,
    string? AdapterCode,
    TimeSpan? RetryAfterHeader,
    bool IsTransport,
    bool IsTimeout)
{
    /// <summary>A call that failed before a response arrived (no status, no envelope).</summary>
    /// <returns>The error to map.</returns>
    public static RenderClientError Transport() => new(null, null, null, IsTransport: true, IsTimeout: false);

    /// <summary>A call that ran into a timeout (no status, no envelope).</summary>
    /// <returns>The error to map.</returns>
    public static RenderClientError Timeout() => new(null, null, null, IsTransport: false, IsTimeout: true);

    /// <summary>A call whose response was not an accepted 2xx body.</summary>
    /// <param name="status">The response status.</param>
    /// <param name="code">The envelope's <c>error.code</c>, when the body carried a parsable envelope.</param>
    /// <param name="retryAfter">The parsed <c>Retry-After</c> header, when the answer had one.</param>
    /// <returns>The error to map.</returns>
    public static RenderClientError Status(int status, string? code = null, TimeSpan? retryAfter = null) =>
        new(status, code, retryAfter, IsTransport: false, IsTimeout: false);
}
