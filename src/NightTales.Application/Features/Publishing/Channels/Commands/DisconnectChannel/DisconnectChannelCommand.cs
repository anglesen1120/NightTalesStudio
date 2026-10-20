using System.Globalization;
using System.Text.Json.Serialization;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Domain.Audit;

namespace NightTales.Application.Features.Publishing.Channels.Commands.DisconnectChannel;

/// <summary>
/// <c>DELETE /api/v1/channels/{id}</c> (06.4.1-T1 §Design, 06 README D15/D17/D18): disconnects one connected channel —
/// the Google grant is revoked best effort, both stored tokens are wiped, every publication of the channel that has
/// not started uploading is cancelled and its quota unit is given back, and the channel-scope deletion request is
/// scheduled.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotent by contract (R3/S65-1).</b> Disconnecting an already <c>revoked</c> connection answers 204 again and
/// writes nothing else — no Google call, no second state change, no second outbox row, no audit row. The deletion
/// request is the one write the repeat may still make, and only when none is on record: the handler asks
/// <c>IDeletionRequests.ExistsAsync</c> first and calls the engine's idempotent create only when no
/// <c>in_app_revoke</c> row for that connection exists in <em>any</em> state, because the engine's open-row dedupe
/// cannot see a request it already completed.
/// </para>
/// <para>
/// <b>No If-Match and no idempotency key (D15).</b> The route carries neither: a disconnect is the owner's own
/// destructive intent on a row identified by the path, and the locked read — not a stale tag — is what decides.
/// </para>
/// <para>
/// <b>Ids only (D17/BD16).</b> The audit <c>data</c> carries the platform, the pinned OAuth client, Google's channel
/// id, the two cascade counts and which revoke answer Google gave. Never a token, never the channel title, never a
/// video title — the log line spells the same values. The command carries no metadata of its own for that reason.
/// </para>
/// </remarks>
/// <param name="ChannelId">The connection (<c>{id}</c>); another owner's id takes the same 404 an unknown one gets.</param>
[Audited("publishing.channel_disconnected", Category.Publishing, Visibility.User)]
public sealed record DisconnectChannelCommand(Guid ChannelId) : ICommand<ErrorOr<Success>>, IAuditData
{
    /// <summary>
    /// What this call disconnected, for the audit projection: the identity the row still carried, the cascade counts
    /// and Google's revoke answer.
    /// </summary>
    /// <remarks>
    /// Excluded from serialization so a command logged or echoed back never carries it: it is a handler output, not
    /// part of the request. It stays <see langword="null"/> on every refusal (404, a refused deletion request) so one
    /// disconnect writes exactly one audit row.
    /// </remarks>
    [JsonIgnore]
    public DisconnectedChannelOutcome? Outcome { get; set; }

    /// <inheritdoc />
    public AuditData? ToAuditData() => Outcome is null
        ? null
        : new AuditData(
            SubjectUserId: null,
            EntityType: DisconnectAuditKeys.EntityType,
            EntityId: Outcome.ConnectionId,
            Summary: new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [DisconnectAuditKeys.ConnectionIdKey] =
                    Outcome.ConnectionId.ToString("D", CultureInfo.InvariantCulture),
                [DisconnectAuditKeys.PlatformKey] = Outcome.Platform,
                [DisconnectAuditKeys.OAuthClientIdKey] = Outcome.OAuthClientId,
                [DisconnectAuditKeys.ExternalAccountIdKey] = Outcome.ExternalAccountId,
                [DisconnectAuditKeys.PublicationsCancelledKey] = Outcome.PublicationsCancelled,
                [DisconnectAuditKeys.QuotaUnitsReleasedKey] = Outcome.QuotaUnitsReleased,
                [DisconnectAuditKeys.GoogleRevokeKey] = Outcome.GoogleRevoke,
            });
}

/// <summary>
/// What a disconnect wrote, as the audit row and the id-only log line report it (06 README D17).
/// </summary>
/// <param name="ConnectionId">The disconnected <c>channel_connections.id</c>.</param>
/// <param name="Platform">The wire value of the platform the row published to (<c>youtube</c>).</param>
/// <param name="OAuthClientId">
/// The OAuth client the connection was pinned to (D5) — an identifier, never a secret: it is what lets an operator
/// see which client's grant was revoked.
/// </param>
/// <param name="ExternalAccountId">Google's opaque channel id, read before the transition.</param>
/// <param name="PublicationsCancelled">How many <c>awaiting_consent</c>/<c>scheduled</c> rows this call cancelled.</param>
/// <param name="QuotaUnitsReleased">How many reserved units the cascade gave back.</param>
/// <param name="GoogleRevoke">One of <see cref="DisconnectGoogleRevoke"/>.</param>
public sealed record DisconnectedChannelOutcome(
    Guid ConnectionId,
    string Platform,
    string OAuthClientId,
    string ExternalAccountId,
    int PublicationsCancelled,
    int QuotaUnitsReleased,
    string GoogleRevoke);

/// <summary>
/// The frozen spellings of the disconnect audit projection (09.3.6-T2 keeps the audit <c>data</c> keys stable).
/// </summary>
internal static class DisconnectAuditKeys
{
    /// <summary>
    /// The <c>audit_logs.subject_type</c> of a connection row — the same value the connect command spells, because one
    /// entity has one audit subject type.
    /// </summary>
    internal const string EntityType = "channel_connection";

    /// <summary>The audit <c>data</c> key of the disconnected connection.</summary>
    internal const string ConnectionIdKey = "channelId";

    /// <summary>The audit <c>data</c> key of the platform wire value.</summary>
    internal const string PlatformKey = "platform";

    /// <summary>The audit <c>data</c> key of the pinned OAuth client (D5).</summary>
    internal const string OAuthClientIdKey = "oauthClientId";

    /// <summary>The audit <c>data</c> key of Google's opaque channel id.</summary>
    internal const string ExternalAccountIdKey = "externalAccountId";

    /// <summary>The audit <c>data</c> key of the cascade's cancelled-publication count.</summary>
    internal const string PublicationsCancelledKey = "publicationsCancelled";

    /// <summary>The audit <c>data</c> key of the cascade's released quota units.</summary>
    internal const string QuotaUnitsReleasedKey = "quotaUnitsReleased";

    /// <summary>The audit <c>data</c> key of Google's revoke answer.</summary>
    internal const string GoogleRevokeKey = "googleRevoke";
}

/// <summary>The four <see cref="DisconnectAuditKeys.GoogleRevokeKey"/> values, frozen for the audit and the log line.</summary>
internal static class DisconnectGoogleRevoke
{
    /// <summary>Google accepted the revocation (<c>200</c>).</summary>
    internal const string Revoked = "revoked";

    /// <summary>Google answered the token is already gone (<c>400 invalid_token</c>): the grant is dead either way.</summary>
    internal const string AlreadyRevoked = "alreadyRevoked";

    /// <summary>
    /// The revoke was refused or ran out of retries: the metric was incremented, the line logged, and the disconnect
    /// continued — the tokens are wiped regardless (R10).
    /// </summary>
    internal const string Failed = "failed";

    /// <summary>
    /// No usable plaintext token was stored (an unreadable cipher, or a row that lost its pair): Google was not
    /// called, and the tokens are wiped regardless (R10).
    /// </summary>
    internal const string Skipped = "skipped";
}
