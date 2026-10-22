using NightTales.Domain.Common;

namespace NightTales.Domain.Publishing;

/// <summary>
/// One <c>platform_incidents</c> row (06.2.3-T2, 06 README D14/YR4): an OAuth client the platform refused with
/// <c>invalid_client</c>/<c>unauthorized_client</c>, and therefore the reason every publication pinned to that client
/// is held instead of being retried. The row is platform-wide — it carries no <c>user_id</c>, is not
/// <see cref="IUserOwned"/> and has no tenant filter.
/// </summary>
/// <remarks>
/// <para>
/// <b>One open incident per client.</b> The partial unique index <c>ux_platform_incidents_open</c>
/// (<c>WHERE cleared_at IS NULL</c>) is the arbiter of "already raised": the store inserts with
/// <c>ON CONFLICT … DO NOTHING</c>, so a storm of refusals from every publication of one client produces one row,
/// one <c>platform_incident_raised.v1</c> outbox event and one audit row (YR25: <c>IPlatformIncidentStore</c> is the
/// only writer of those two, never a caller). A cleared row leaves the index, so the same client can raise a new
/// incident afterwards.
/// </para>
/// <para>
/// <b>Columns of a shell.</b> Every write is set-based SQL driven by
/// <c>NightTales.Infrastructure.Publishing.PlatformIncidentStore</c> — the insert of <see cref="Raise"/> (which
/// decides "new or already open" atomically) and the clear — so this type holds the row's shape and the invariants
/// the factory can check, not the statements; the same posture as <see cref="PlatformQuotaUsage"/>. Reads
/// (<c>HasOpenIncidentAsync</c>) go through the <c>PlatformIncidents</c> DbSet, which is why the type is a mapped
/// entity and the columns are exact (plan §3.3: no extra column, no soft-delete column, no row version).
/// </para>
/// <para>
/// <b>Nothing secret, nothing user-facing.</b> The row holds ids, a Google <c>error</c> code and the operator's note;
/// it never holds a token, a session URI or a publication's title, and <see cref="Note"/> is written by the operator
/// on clear only (06.2.3-T3's CLI verb, <c>--note</c>).
/// </para>
/// </remarks>
public sealed class PlatformIncident : Entity
{
    private PlatformIncident()
    {
    }

    /// <summary>
    /// The platform whose client broke, as its wire value (<c>youtube</c>; 06 README D3) — the spelling the port's
    /// <c>platform</c> argument and the outbox payload use, so the column, the payload and the C# member cannot drift.
    /// </summary>
    public string Platform { get; private set; } = string.Empty;

    /// <summary>
    /// The OAuth client (Google <c>client_id</c>) that refused the grant. Every connection pinned to it
    /// (<see cref="ChannelConnection.OAuthClientId"/>) is affected; connections of another client are not.
    /// </summary>
    public string OAuthClientId { get; private set; } = string.Empty;

    /// <summary>
    /// The refusal that raised the incident — Google's own code (<c>invalid_client</c>, <c>unauthorized_client</c>),
    /// never a token, a URI or a response body.
    /// </summary>
    public string ErrorCode { get; private set; } = string.Empty;

    /// <summary>When the first refusal was raised, from the caller's clock; a repeat raise keeps the first instant.</summary>
    public DateTimeOffset RaisedAt { get; private set; }

    /// <summary>
    /// When an operator cleared the incident (<c>nil</c> while it is open — that is the index's predicate); written
    /// only by 06.2.3-T3's clear command through <c>IPlatformIncidentStore.ClearAsync</c>.
    /// </summary>
    public DateTimeOffset? ClearedAt { get; private set; }

    /// <summary>
    /// The operator who cleared the incident (YR16: the person, never a fixed string); <c>nil</c> while it is open.
    /// </summary>
    public string? ClearedBy { get; private set; }

    /// <summary>The operator's note from the clear command (YR16: <c>--note</c> is required); <c>nil</c> while open.</summary>
    public string? Note { get; private set; }

    /// <summary>Whether the incident blocks its client: exactly <c>cleared_at IS NULL</c>, which the index also holds.</summary>
    public bool IsOpen => ClearedAt is null;

    /// <summary>
    /// Builds the row the store inserts for one refused OAuth client. The caller mints the UUIDv7 id (the store needs
    /// it <em>before</em> the insert, because <c>ON CONFLICT DO NOTHING RETURNING id</c> is what tells it whether this
    /// call raised the incident or met an already open one), so the factory pins the id instead of inventing one.
    /// </summary>
    /// <param name="id">The UUIDv7 the caller minted for this attempt; a repeat raise mints its own and loses the race.</param>
    /// <param name="platform">The platform's wire value (<c>youtube</c>); required.</param>
    /// <param name="oauthClientId">The refused client; required.</param>
    /// <param name="errorCode">Google's refusal code; required.</param>
    /// <param name="now">The failure's instant, from the caller's clock: this row's <c>raised_at</c>.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is not a UUIDv7, or a platform, client or code value is blank.
    /// </exception>
    public static PlatformIncident Raise(
        Guid id,
        string platform,
        string oauthClientId,
        string errorCode,
        DateTimeOffset now)
    {
        if (!IsUuidV7(id))
        {
            throw new ArgumentException(
                "An incident id must be a UUIDv7 minted by the raising caller (09 README D3).",
                nameof(id));
        }

        RequireText(platform, nameof(platform));
        RequireText(oauthClientId, nameof(oauthClientId));
        RequireText(errorCode, nameof(errorCode));

        return new PlatformIncident
        {
            Id = id,
            Platform = platform,
            OAuthClientId = oauthClientId,
            ErrorCode = errorCode,
            RaisedAt = now,
        };
    }

    /// <summary>The UUIDv7 shape every domain id has (09 README D3): version nibble 7 and the RFC 4122 variant.</summary>
    private static bool IsUuidV7(Guid id) => id.Version == 7;

    /// <summary>A value the row cannot hold blank: the store's insert would store an unusable incident.</summary>
    private static void RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The incident column cannot be blank.", parameterName);
        }
    }
}
