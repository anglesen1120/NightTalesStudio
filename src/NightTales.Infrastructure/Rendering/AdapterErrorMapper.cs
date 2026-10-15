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
/// <para>The table, in the order the mapper applies it — the status class decides first, so a code can never
/// pull a 5xx into an output kind or a 4xx into a lost render:</para>
/// <list type="bullet">
/// <item><description>a transport error or a timeout is <see cref="RenderClientFailureKind.TransientFailure"/> with <c>render_node_error</c>;</description></item>
/// <item><description>any 3xx status is terminal and alerting (a node must never bounce the Worker elsewhere), whatever code the body carries;</description></item>
/// <item><description>a code is honoured only on the status the adapter table documents for it: <c>adapter_capacity_full</c> (429), <c>adapter_node_unhealthy</c> (503, deferred by the configured health-poll), <c>adapter_task_unknown</c> (404), <c>adapter_output_not_ready</c> (409), <c>adapter_output_absent</c> (404), <c>adapter_task_busy</c> (409), and the bug/drift codes <c>adapter_contract_violation</c> (400), <c>adapter_idempotency_conflict</c> (409), <c>adapter_body_too_large</c> (413), <c>adapter_unauthorized</c> (401) and <c>adapter_route_not_found</c> (404), which are terminal <c>render_node_error</c> failures that alert;</description></item>
/// <item><description>a code sent on any other status is not a row of the table and falls to the status class below;</description></item>
/// <item><description>a 408, or a 5xx after the pipeline has exhausted its retries, is <see cref="RenderClientFailureKind.TransientFailure"/> with <c>render_node_error</c> (this covers <c>adapter_internal_error</c>, a 5xx with an unknown code, and a known code sent on a 5xx);</description></item>
/// <item><description>a 429 without a usable code is still deferred, because the status is the capacity signal;</description></item>
/// <item><description>every other answer — an unknown code, a known code on the wrong status, or an answer that carries none (a 200 with an unparsable body) — fails closed: terminal <c>render_node_error</c> with an alert. The code itself becomes <see cref="RenderClientFailure.Detail"/> only when <see cref="IsSafeCode"/> accepts it, and nothing else is ever copied from the adapter.</description></item>
/// </list>
/// </remarks>
public static class AdapterErrorMapper
{
    /// <summary>The wait a <c>adapter_node_unhealthy</c> answer deferred the work by when the caller passes no health-poll interval.</summary>
    public static readonly TimeSpan DefaultHealthPoll = TimeSpan.FromSeconds(30);

    /// <summary>The wait a deferred answer without a usable <c>Retry-After</c> defers the work by.</summary>
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>The floor a deferred answer's <c>Retry-After</c> is raised to: a header below one second (an explicit 0 included) may never become a zero wait.</summary>
    public static readonly TimeSpan MinRetryAfter = TimeSpan.FromSeconds(1);

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

        var status = error.HttpStatus;

        // A redirect is a node misconfiguration and following it would let the node move the Worker to
        // another host (05 D4); the status decides this whatever code the body carries.
        if (status is >= 300 and < 400)
        {
            return Terminal(RenderOutcomeCodes.RenderNodeError, alert: true);
        }

        // The code table, honoured only on the status 05.4.1-T2 §1 documents for each code. A code arriving
        // on any other status — a 5xx that claims output_absent, a 404 that claims capacity_full — is not a
        // row of the table and falls through to the status class below, so a 5xx can never become an output
        // kind or a lost render.
        switch (error.AdapterCode)
        {
            case AdapterErrorCodes.CapacityFull when status == 429:
                return Deferred(RetryAfterOf(error));

            case AdapterErrorCodes.NodeUnhealthy when status == 503:
                return Deferred(healthPoll ?? DefaultHealthPoll);

            case AdapterErrorCodes.TaskUnknown when status == 404:
                return taskSubmitted
                    ? Terminal(RenderOutcomeCodes.RenderLost, alert: false)
                    : Terminal(RenderOutcomeCodes.RenderNodeError, alert: true);

            case AdapterErrorCodes.OutputNotReady when status == 409:
                return new RenderClientFailure(
                    RenderClientFailureKind.OutputNotReady, OutcomeCode: null, Detail: null, Alert: false, RetryAfter: null);

            case AdapterErrorCodes.OutputAbsent when status == 404:
                return new RenderClientFailure(
                    RenderClientFailureKind.OutputAbsent, OutcomeCode: null, Detail: null, Alert: false, RetryAfter: null);

            case AdapterErrorCodes.TaskBusy when status == 409:
                return Deferred(DefaultRetryAfter);

            case AdapterErrorCodes.ContractViolation when status == 400:
            case AdapterErrorCodes.IdempotencyConflict when status == 409:
            case AdapterErrorCodes.BodyTooLarge when status == 413:
            case AdapterErrorCodes.Unauthorized when status == 401:
            case AdapterErrorCodes.RouteNotFound when status == 404:
                return Terminal(RenderOutcomeCodes.RenderNodeError, alert: true);
        }

        // The status class decides when no code matched: a 408 or a 5xx (after the pipeline exhausted its
        // retries) is transient, and a 429 without a usable code is still deferred because the status is
        // the capacity signal.
        if (status == 408 || status >= 500)
        {
            return Transient();
        }

        if (status == 429)
        {
            return Deferred(RetryAfterOf(error));
        }

        // Everything else fails closed and alerts: an unknown code, a known code on the wrong status, or an
        // answer with no code at all (a 200 the client could not parse). The code survives as Detail only
        // when it is safe — the mapper never copies raw adapter text into it (BD16).
        return error.AdapterCode is { Length: > 0 } unknownCode && IsSafeCode(unknownCode)
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

    /// <summary>
    /// Whether an adapter code is safe to keep as <see cref="RenderClientFailure.Detail"/> or to echo in a
    /// log line: exactly the adapter's own code shape, <c>^[a-z][a-z_]{0,63}$</c> (05.4.1-T2 §1). Any other
    /// text — upper case, digits, punctuation, a URL, a sentence, an embedded CR/LF — is not a code and must
    /// be dropped before a <see cref="RenderClientError"/> is built, so raw adapter text can never reach a
    /// job's <c>error_message</c> (BD16).
    /// </summary>
    /// <param name="code">The candidate code, or <c>null</c>.</param>
    /// <returns><c>true</c> when the text is a safe code.</returns>
    public static bool IsSafeCode(string? code)
    {
        if (string.IsNullOrEmpty(code) || code.Length > 64)
        {
            return false;
        }

        if (code[0] < 'a' || code[0] > 'z')
        {
            return false;
        }

        for (var i = 1; i < code.Length; i++)
        {
            var c = code[i];

            if (c != '_' && (c < 'a' || c > 'z'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The fixed <see cref="RenderClientFailure.Detail"/> of <see cref="Misconfigured"/>.</summary>
    public const string MisconfiguredDetail = "render_node_misconfigured";

    /// <summary>The fixed <see cref="RenderClientFailure.Detail"/> of <see cref="TlsFailure"/>.</summary>
    public const string TlsFailureDetail = "render_node_tls_failure";

    /// <summary>
    /// The failure of a node whose client could not be built at all: a missing certificate pin, a missing or
    /// malformed API key, or a base address that is not HTTPS. Terminal and alerting — the node row is wrong
    /// and only an operator can fix it (09 README BD6). Callers that fail before an HTTP call use this
    /// instead of inventing a code.
    /// </summary>
    /// <returns>The failure the caller returns.</returns>
    public static RenderClientFailure Misconfigured() =>
        new(
            RenderClientFailureKind.TerminalFailure,
            RenderOutcomeCodes.RenderNodeError,
            MisconfiguredDetail,
            Alert: true,
            RetryAfter: null);

    /// <summary>
    /// The failure of a node whose TLS handshake failed against the pinned certificate: the certificate it
    /// presented no longer matches <c>worker_nodes.tls_cert_sha256</c>. Terminal and alerting — a rotation or
    /// a man in the middle is an operator decision, never a retry (05 D4, D15 S1).
    /// </summary>
    /// <returns>The failure the caller returns.</returns>
    public static RenderClientFailure TlsFailure() =>
        new(
            RenderClientFailureKind.TerminalFailure,
            RenderOutcomeCodes.RenderNodeError,
            TlsFailureDetail,
            Alert: true,
            RetryAfter: null);

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
    /// none, raised to at least <see cref="MinRetryAfter"/> and clamped to <see cref="MaxRetryAfter"/>
    /// (05 D4's edge case — a header may not be believed without bound, and an explicit 0 or a sub-second
    /// value is a floor of one second, never a busy retry).
    /// </summary>
    private static TimeSpan RetryAfterOf(RenderClientError error)
    {
        var value = error.RetryAfterHeader ?? DefaultRetryAfter;

        if (value < MinRetryAfter)
        {
            return MinRetryAfter;
        }

        return value > MaxRetryAfter ? MaxRetryAfter : value;
    }
}
