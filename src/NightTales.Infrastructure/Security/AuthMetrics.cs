using System.Diagnostics.Metrics;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The auth counters of 09.3.2-T1 (Design "Metrics/hooks", exported by 09.9.3-T1):
/// <c>auth_login_total{result}</c> and <c>auth_refresh_total{result}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton, like every meter: one instrument per process, so a scrape never sees two
/// series for one counter. The <c>result</c> tag carries the wire code the caller received
/// (<c>ok</c>, <c>invalid_credentials</c>, <c>refresh_token_reused</c>, <c>session_revoked</c>, …) —
/// always a value from the closed ErrorCatalogue vocabulary, never a message, an address or a token.
/// </para>
/// <para>
/// The meter name follows the platform's <c>NightTales.*</c> convention, so 09.9.3-T1's exporter and the
/// tests that listen on it address it the same way.
/// </para>
/// </remarks>
public sealed class AuthMetrics : IDisposable
{
    /// <summary>The meter every counter of this task publishes on.</summary>
    public const string MeterName = "NightTales.Auth";

    /// <summary>The login counter; its <c>result</c> tag is the answer the caller got.</summary>
    public const string LoginCounterName = "auth_login_total";

    /// <summary>The refresh counter; its <c>result</c> tag is the answer the caller got.</summary>
    public const string RefreshCounterName = "auth_refresh_total";

    /// <summary>The tag carrying the outcome code.</summary>
    public const string ResultTagName = "result";

    /// <summary>The tag carrying the action-token kind (<c>password_reset</c>, …).</summary>
    public const string KindTagName = "kind";

    /// <summary>
    /// The single-use action-token counter of 09.3.3-T1: <c>auth_action_token_total{kind,result}</c>. Its
    /// <c>result</c> is a closed value (<c>queued</c>, <c>enqueue_failed</c>, <c>skipped</c>) — never a
    /// token, an address or an id.
    /// </summary>
    public const string ActionTokenCounterName = "auth_action_token_total";

    /// <summary>
    /// The password-reset flow counter of 09.3.3-T1: <c>auth_password_reset_total{result}</c> with
    /// <c>result</c> ∈ {<c>ok</c>, <c>failed</c>, <c>notice_failed</c>}.
    /// </summary>
    public const string PasswordResetCounterName = "auth_password_reset_total";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _logins;
    private readonly Counter<long> _refreshes;
    private readonly Counter<long> _actionTokens;
    private readonly Counter<long> _passwordResets;

    /// <summary>Creates the meter and its counters.</summary>
    public AuthMetrics()
    {
        _logins = _meter.CreateCounter<long>(LoginCounterName, "1", "Login attempts by outcome code.");
        _refreshes = _meter.CreateCounter<long>(RefreshCounterName, "1", "Refresh attempts by outcome code.");
        _actionTokens = _meter.CreateCounter<long>(ActionTokenCounterName, "1", "Action-token issues by kind and result.");
        _passwordResets = _meter.CreateCounter<long>(PasswordResetCounterName, "1", "Password-reset flow outcomes.");
    }

    /// <summary>Counts one finished login attempt.</summary>
    /// <param name="result">The wire code of the answer.</param>
    public void Login(string result) => Record(_logins, result);

    /// <summary>Counts one finished refresh attempt.</summary>
    /// <param name="result">The wire code of the answer.</param>
    public void Refresh(string result) => Record(_refreshes, result);

    /// <summary>Counts one action-token issue or request outcome (09.3.3-T1).</summary>
    /// <param name="kind">The <c>UserActionKind</c> wire value.</param>
    /// <param name="result">The closed outcome value.</param>
    public void ActionToken(string kind, string result) => _actionTokens.Add(
        1,
        new KeyValuePair<string, object?>(KindTagName, kind),
        new KeyValuePair<string, object?>(ResultTagName, result));

    /// <summary>Counts one password-reset outcome (09.3.3-T1).</summary>
    /// <param name="result">The closed outcome value.</param>
    public void PasswordReset(string result) => Record(_passwordResets, result);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();

    private static void Record(Counter<long> counter, string result) =>
        counter.Add(1, new KeyValuePair<string, object?>(ResultTagName, result));
}
