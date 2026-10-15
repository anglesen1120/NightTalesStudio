using System.Globalization;
using NightTales.Application.Abstractions.Rendering;
using NightTales.RenderNode.Contract;

namespace NightTales.Infrastructure.Rendering;

/// <summary>
/// Maps one failed adapter call to the failure model the backend reasons about (09 README BD6, 05 README
/// D4/D5). This is the only place the adapter's status and code table lives, so a new adapter code is a
/// change here and nowhere else.
/// </summary>
/// <remarks>
/// <para>The table, in the order the mapper applies it:</para>
/// <list type="bullet">
/// <item><description>a transport error or a timeout is <see cref="RenderClientFailureKind.TransientFailure"/> with <c>render_node_error</c>;</description></item>
/// <item><description><c>adapter_capacity_full</c> (429) and <c>adapter_task_busy</c> (409 on a DELETE) are <see cref="RenderClientFailureKind.Deferred"/>, the first with the answer's <c>Retry-After</c>;</description></item>
/// <item><description><c>adapter_node_unhealthy</c> (503) is <see cref="RenderClientFailureKind.Deferred"/> after the configured health-poll interval;</description></item>
/// <item><description><c>adapter_task_unknown</c> (404) is <c>render_lost</c> when the task had been submitted, and <c>render_node_error</c> with an alert when it had not;</description></item>
/// <item><description><c>adapter_output_not_ready</c> (409) is <see cref="RenderClientFailureKind.OutputNotReady"/> and <c>adapter_output_absent</c> (404) is <see cref="RenderClientFailureKind.OutputAbsent"/>, neither of which fails the job;</description></item>
/// <item><description><c>adapter_contract_violation</c>, <c>adapter_idempotency_conflict</c>, <c>adapter_body_too_large</c>, <c>adapter_unauthorized</c> and <c>adapter_route_not_found</c> are terminal <c>render_node_error</c> failures that alert, because each one means a bug, a rotated key or a drifted contract;</description></item>
/// <item><description>any other 3xx status is terminal and alerting (a node must never bounce the Worker elsewhere);</description></item>
/// <item><description>a 429 without a known code is still deferred, because the status is the capacity signal;</description></item>
/// <item><description>a 5xx after the pipeline has exhausted its retries is <see cref="RenderClientFailureKind.TransientFailure"/> with <c>render_node_error</c> (this covers <c>adapter_internal_error</c> and a 5xx with an unknown code);</description></item>
/// <item><description>an unknown code, and an answer that carries none (a 200 with an unparsable body), are terminal <c>render_node_error</c> failures that alert; the unknown code itself becomes <see cref="RenderClientFailure.Detail"/>, and nothing else is ever copied from the adapter.</description></item>
/// </list>
/// </remarks>
public static class AdapterErrorMapper
{
    /// <summary>The wait a <c>adapter_node_unhealthy</c> answer deferred the work by when the caller passes no health-poll interval.</summary>
    public static readonly TimeSpan DefaultHealthPoll = TimeSpan.FromSeconds(30);

    /// <summary>The wait a deferred answer without a usable <c>Retry-After</c> defers the work by.</summary>
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>The ceiling a deferred answer's <c>Retry-After</c> is clamped to (05 D4: ≤ 300 s).</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(300);

    /// <summary>Maps one failed call.</summary>
    /// <param name="error">What the HTTP layer saw; never a body and never a node secret.</param>
    /// <param name="taskSubmitted">
    /// Whether this call concerns a task the node accepted: a 404 <c>adapter_task_unknown</c> on a submitted
    /// task is a lost render (<c>render_lost</c>), on anything else it is a node fault (05 D5).
    /// </param>
    /// <param name="healthPoll">
    /// The configured <c>RetryNodes:HealthPollSeconds</c> the work waits after <c>adapter_node_unhealthy</c>;
    /// <see cref="DefaultHealthPoll"/> when the caller passes none.
    /// </param>
    /// <returns>The failure the caller switches on.</returns>
    public static RenderClientFailure Map(RenderClientError error, bool taskSubmitted, TimeSpan? healthPoll = null)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (error.IsTransport || error.IsTimeout)
        {
            return Transient();
        }

        switch (error.AdapterCode)
        {
            case AdapterErrorCodes.CapacityFull:
                return Deferred(RetryAfterOf(error));

            case AdapterErrorCodes.NodeUnhealthy:
                return Deferred(healthPoll ?? DefaultHealthPoll);

            case AdapterErrorCodes.TaskUnknown:
                return taskSubmitted
                    ? Terminal(RenderOutcomeCodes.RenderLost, alert: false)
                    : Terminal(RenderOutcomeCodes.RenderNodeError, alert: true);

            case AdapterErrorCodes.OutputNotReady:
                return new RenderClientFailure(
                    RenderClientFailureKind.OutputNotReady, OutcomeCode: null, Detail: null, Alert: false, RetryAfter: null);

            case AdapterErrorCodes.OutputAbsent:
                return new RenderClientFailure(
                    RenderClientFailureKind.OutputAbsent, OutcomeCode: null, Detail: null, Alert: false, RetryAfter: null);

            case AdapterErrorCodes.TaskBusy:
                return Deferred(DefaultRetryAfter);

            case AdapterErrorCodes.ContractViolation:
            case AdapterErrorCodes.IdempotencyConflict:
            case AdapterErrorCodes.BodyTooLarge:
            case AdapterErrorCodes.Unauthorized:
            case AdapterErrorCodes.RouteNotFound:
                return Terminal(RenderOutcomeCodes.RenderNodeError, alert: true);
        }

        if (error.HttpStatus is { } status)
        {
            if (status is >= 300 and < 400)
            {
                // Redirects are off on both clients; an answer that still is one is a node misconfiguration,
                // and following it would let the node move the Worker to another host (05 D4).
                return Terminal(RenderOutcomeCodes.RenderNodeError, alert: true);
            }

            if (status == 429)
            {
                return Deferred(RetryAfterOf(error));
            }

            if (status >= 500)
            {
                return Transient();
            }
        }

        // An unknown code fails closed and alerts: swallowing it would hide a drifted contract, and the code
        // is safe to keep (it matches the adapter's ^[a-z_]+$ rule and is never raw MPT text). An answer with
        // no code at all — a 200 the client could not parse — lands here too, with no detail.
        return error.AdapterCode is { Length: > 0 } unknownCode
            ? new RenderClientFailure(
                RenderClientFailureKind.TerminalFailure, RenderOutcomeCodes.RenderNodeError, unknownCode, Alert: true, RetryAfter: null)
            : Terminal(RenderOutcomeCodes.RenderNodeError, alert: true);
    }

    /// <summary>
    /// The failure of an output whose bytes contradict what the task promised (09 README BD6): a size cap that
    /// was exceeded, a content type that is not the kind's, and any text, length or hash anomaly the download
    /// detects. Terminal with <c>render_output_invalid</c> and no alert — the node's own quality report is what
    /// an operator reads, not this.
    /// </summary>
    /// <param name="detail">A short machine-readable reason (a code, never adapter text and never a body excerpt).</param>
    /// <returns>The failure the download client returns.</returns>
    public static RenderClientFailure OutputInvalid(string? detail) =>
        new(
            RenderClientFailureKind.TerminalFailure,
            RenderOutcomeCodes.RenderOutputInvalid,
            detail,
            Alert: false,
            RetryAfter: null);

    /// <summary>
    /// Parses the adapter's <c>Retry-After</c> header: the integer seconds of 11 T6, and nothing else. A
    /// missing, empty, negative or unparsable value (including the HTTP-date form) answers <c>null</c>, which
    /// <see cref="Map"/> then turns into <see cref="DefaultRetryAfter"/> — an unreadable header may never
    /// become a zero wait.
    /// </summary>
    /// <param name="headerValue">The raw header value, or <c>null</c> when the answer carried none.</param>
    /// <returns>The delay in seconds, or <c>null</c> when the header cannot be used as one.</returns>
    public static TimeSpan? ParseRetryAfter(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return null;
        }

        return int.TryParse(headerValue.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            && seconds >= 0
                ? TimeSpan.FromSeconds(seconds)
                : null;
    }

    /// <summary>A deferred failure with the given wait.</summary>
    private static RenderClientFailure Deferred(TimeSpan retryAfter) =>
        new(RenderClientFailureKind.Deferred, OutcomeCode: null, Detail: null, Alert: false, RetryAfter: retryAfter);

    /// <summary>A transient node failure: retryable, no alert.</summary>
    private static RenderClientFailure Transient() =>
        new(
            RenderClientFailureKind.TransientFailure, RenderOutcomeCodes.RenderNodeError, Detail: null, Alert: false, RetryAfter: null);

    /// <summary>A terminal failure with an outcome code and no detail.</summary>
    private static RenderClientFailure Terminal(string outcomeCode, bool alert) =>
        new(
            RenderClientFailureKind.TerminalFailure,
            OutcomeCode: outcomeCode,
            Detail: null,
            Alert: alert,
            RetryAfter: null);

    /// <summary>
    /// The wait a deferred answer asks for: the header's value, <see cref="DefaultRetryAfter"/> when there is
    /// none, clamped into <c>[0, 300 s]</c> (05 D4's edge case — a header may not be believed without bound, and
    /// a negative one is not a zero wait either).
    /// </summary>
    private static TimeSpan RetryAfterOf(RenderClientError error)
    {
        var value = error.RetryAfterHeader ?? DefaultRetryAfter;

        if (value < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return value > MaxRetryAfter ? MaxRetryAfter : value;
    }
}
