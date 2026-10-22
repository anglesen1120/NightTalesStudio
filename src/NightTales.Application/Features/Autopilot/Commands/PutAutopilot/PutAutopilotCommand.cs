
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Domain.Audit;

namespace NightTales.Application.Features.Autopilot.Commands.PutAutopilot;

/// <summary>
/// <c>PUT /api/v1/series/{id}/autopilot</c> (02.8.3-T2 plan §4 UC2, task step 4): store the settings document and
/// switch autopilot on or off under the checks of plan §3.7.
/// </summary>
/// <remarks>
/// <para>
/// <b>One route, four behaviours, and the order they are checked in.</b> Off on an enabled series revokes with
/// <c>disabled</c> and needs no re-auth. On from off runs the full ladder — legal acceptance, re-auth, the current
/// authorization, the channel — because it is the click that authorizes future uploads without per-video consent.
/// Settings-only applies the document and runs the covered-field diff, revoking and holding the runs when a covered
/// field moved (plan §3.7); it is deliberately <em>not</em> a 409, because the owner is editing the very settings the
/// text covers. That holds with the switch in either position: <c>enabled = false</c> sent to a series that is already
/// off is the same document edit, so it writes no switch-off row and never answers as a second disable.
/// </para>
/// <para>
/// <b><c>[NotAudited]</c> with a written reason, not <c>[Audited]</c>.</b> The action depends on the request:
/// <c>autopilot.enabled</c>, <c>autopilot.disabled</c>, and beside it <c>autopilot.authorization_revoked</c> when a
/// covered field or the disable revoked the row. One attribute on the class cannot express three actions, so the
/// handler writes the rows itself through <see cref="IAuditWriter"/>, inside the same transaction — the shape
/// <c>CancelPublicationByLinkHandler</c> uses.
/// </para>
/// </remarks>
/// <param name="Id">The series to change.</param>
/// <param name="IfMatch">The <c>If-Match</c> tag the client read (<c>"sa-&lt;xmin&gt;"</c>).</param>
/// <param name="Enabled">Whether autopilot should be on when the command returns.</param>
/// <param name="SettingsJson">
/// The settings document exactly as the client wrote it; <c>AutopilotSettingsValidator</c> owns its shape and reports
/// every failure keyed by JSON path (400 <c>autopilot_settings_invalid</c>).
/// </param>
[NotAudited("The action depends on the request (enable, disable, or a covered-field revoke), so the handler writes those rows itself through IAuditWriter in the same transaction (plan §4).")]
public sealed record PutAutopilotCommand(Guid Id, string IfMatch, bool Enabled, string SettingsJson)
    : ICommand<ErrorOr<Success>>;

/// <summary>
/// The audit <c>data</c> keys of the three autopilot rows this command can write (plan §4): <c>autopilot.enabled</c>
/// carries <c>seriesId, settingsHash, authorizationId, authorizationVersion</c>, <c>autopilot.disabled</c> carries
/// <c>seriesId, revokedAuthorizationId, reason</c>, and <c>autopilot.authorization_revoked</c> carries
/// <c>seriesId, authorizationId, reason</c>.
/// </summary>
/// <remarks>
/// <b>A digest, never the document.</b> The enable row names the settings document by the SHA-256 of the canonical
/// text it stored, so the row proves which document the consent was given for without copying it into the audit log
/// (contract D-8).
/// </remarks>
public static class PutAutopilotAuditKeys
{
    /// <summary>The audit action of plan §4 for a successful enable.</summary>
    public const string EnabledAction = "autopilot.enabled";

    /// <summary>The audit action of plan §4 for a successful disable.</summary>
    public const string DisabledAction = "autopilot.disabled";

    /// <summary>The audit action of plan §4 for an automatic revocation.</summary>
    public const string RevokedAction = "autopilot.authorization_revoked";

    /// <summary>The entity kind the rows name.</summary>
    public const string EntityType = "series";

    /// <summary>The <c>data</c> member naming the series.</summary>
    public const string SeriesId = "seriesId";

    /// <summary>The <c>data</c> member naming the authorization row the row is about.</summary>
    public const string AuthorizationId = "authorizationId";

    /// <summary>The <c>data</c> member naming the revoked row (plan §4: <c>revokedAuthorizationId</c>).</summary>
    public const string RevokedAuthorizationId = "revokedAuthorizationId";

    /// <summary>The <c>data</c> member carrying the accepted version (plan §4: <c>authorizationVersion</c>).</summary>
    public const string AuthorizationVersion = "authorizationVersion";

    /// <summary>The <c>data</c> member carrying the SHA-256 of the stored settings document, lowercase hex.</summary>
    public const string SettingsHash = "settingsHash";

    /// <summary>The <c>data</c> member carrying the revoke reason slug.</summary>
    public const string Reason = "reason";
}

/// <summary>
/// The category and visibility every autopilot audit row carries: publishing, owner-visible (D13, plan §4).
/// </summary>
public static class AutopilotAuditScope
{
    /// <summary>The audit category of the autopilot actions.</summary>
    public const Category Category = Category.Publishing;

    /// <summary>The visibility of the autopilot actions.</summary>
    public const Visibility Visibility = Visibility.User;
}
