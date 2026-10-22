using ErrorOr;

namespace NightTales.Application.Abstractions.Publishing;

/// <summary>
/// What one raise decided: the incident the caller must hold against (<paramref name="IncidentId"/>
/// is the open row, whether this call raised it or met it), and how many live connections the broken
/// client affects.
/// </summary>
/// <param name="IncidentId">The <c>platform_incidents</c> row that is open for the client after the call.</param>
/// <param name="IsNew">
/// <c>true</c> only for the call that actually inserted the row — the one that also wrote the
/// <c>platform_incident_raised.v1</c> outbox row and the <c>publishing.incident_raised</c> audit row.
/// </param>
/// <param name="AffectedConnections">The client's <c>active</c> connections at raise time (D13's payload field).</param>
public sealed record PlatformIncidentRaiseResult(Guid IncidentId, bool IsNew, int AffectedConnections);

/// <summary>
/// What one clear decided.
/// </summary>
/// <param name="IncidentId">The incident the call addressed.</param>
/// <param name="AlreadyCleared">
/// <c>true</c> when the incident was already cleared, so the call wrote nothing: no outbox row, no audit
/// row and no reschedule (idempotent repeat).
/// </param>
/// <param name="PublicationsRescheduled">
/// How many held publications this call woke. Always <c>0</c> in 06.2.3-T2: the per-incident sweep belongs
/// to 06.2.3-T3's clear command (YR24 — holds are lazy and per row, there is no raise-time or clear-time
/// batch sweep in this task).
/// </param>
/// <param name="Remaining">
/// <c>true</c> when 06.2.3-T3's sweep hit <c>MaxBatches</c> or a batch failed, so the incident is
/// intentionally left open. Always <c>false</c> while this task is the implementation.
/// </param>
public sealed record PlatformIncidentClearResult(
    Guid IncidentId,
    bool AlreadyCleared,
    int PublicationsRescheduled,
    bool Remaining = false);

/// <summary>
/// The <c>platform_incidents</c> table and its two events (06 README D14, plan §3: CR-22 moves the table, the entity
/// and this port to 06.2.3-T2; 06.2.3-T3 keeps the classifier, the sweep, the CLI verb and the alert).
/// </summary>
/// <remarks>
/// <para>
/// <b>The store is the only writer of the incident rows (YR25).</b> <see cref="RaiseAsync"/> writes the incident row,
/// its <c>platform_incident_raised.v1</c> outbox row and its <c>publishing.incident_raised</c> audit row (category
/// <c>support</c>, visibility <c>internal</c>, <c>subject_user_id</c> <c>null</c>); <see cref="ClearAsync"/> writes the
/// cleared columns and <c>platform_incident_cleared.v1</c>. The clear's audit row
/// (<c>publishing.incident_cleared</c>) is 06.2.3-T3's clear command's, because only its commit knows how many
/// publications the reschedule sweep woke (<c>data.rescheduled</c>).
/// </para>
/// <para>
/// <b>No transaction of its own.</b> Both writes enlist in the caller's ambient <c>IUnitOfWork</c> — the upload job's
/// transaction on the upload path, 06.4.2-T2's on the token-health path — so the incident, its outbox row, its audit
/// row and (on the upload path) <c>Publication.HoldForIncident</c> commit or roll back together: a crash can never
/// leave a publication held without its incident, or an incident without its audit/outbox pair.
/// <c>IAuditWriter.WriteAsync</c> refuses a scope without a transaction, which is the guard that enforces it.
/// </para>
/// <para>
/// <b>Idempotent raise.</b> The insert is <c>INSERT … ON CONFLICT DO NOTHING RETURNING id</c> against the partial
/// unique index <c>ux_platform_incidents_open</c>, so fifty publications refused in one storm produce one row, one
/// outbox event, one audit row and one alert; every other call answers the open row with <see cref="PlatformIncidentRaiseResult.IsNew"/>
/// <c>false</c> and writes nothing.
/// </para>
/// <para>
/// <b>Holds are the caller's.</b> This port never touches <c>publications</c>: the caller holds the one row it already
/// locks (<c>Publication.HoldForIncident(incidentId, nextAttemptAt)</c> with the id this port returned), and any other
/// publication of the client is held lazily, the next time its own run finds the incident open via
/// <see cref="HasOpenIncidentAsync"/> (YR24).
/// </para>
/// <para>
/// <b>No secrets.</b> A platform, a client id, Google's error code, an operator name and a note; never a token, a
/// session URI, a response body or a publication's title.
/// </para>
/// </remarks>
public interface IPlatformIncidentStore
{
    /// <summary>
    /// Raises (or meets) the open incident of one OAuth client, writing the outbox and audit rows only for the call
    /// that inserted the row.
    /// </summary>
    /// <param name="platform">The platform's wire value (<c>youtube</c>).</param>
    /// <param name="oauthClientId">The client Google refused (<c>invalid_client</c>/<c>unauthorized_client</c>).</param>
    /// <param name="errorCode">Google's refusal code, stored as-is in <c>error_code</c>.</param>
    /// <param name="now">The failure's instant, from the caller's clock.</param>
    /// <param name="ct">Cancels the statements.</param>
    /// <returns>
    /// The open incident, whether this call raised it, and the client's live connection count — or the database
    /// failure (never a domain refusal: raising is the safe answer to a broken client).
    /// </returns>
    Task<ErrorOr<PlatformIncidentRaiseResult>> RaiseAsync(
        string platform,
        string oauthClientId,
        string errorCode,
        DateTimeOffset now,
        CancellationToken ct);

    /// <summary>
    /// Reads the open incident of one client — the upload job's step-5 gate (plan §UC1).
    /// </summary>
    /// <param name="platform">The platform's wire value (<c>youtube</c>).</param>
    /// <param name="oauthClientId">The client pinned on the connection the job is about to use.</param>
    /// <param name="ct">Cancels the statement.</param>
    /// <returns>
    /// <c>null</c> when no incident is open (the job may proceed), otherwise that incident's id, which is what
    /// <c>Publication.HoldForIncident</c> stores in <c>platform_payload.heldByIncidentId</c>.
    /// </returns>
    Task<ErrorOr<Guid?>> HasOpenIncidentAsync(string platform, string oauthClientId, CancellationToken ct);

    /// <summary>
    /// Clears one incident for an operator: <c>cleared_at</c>, <c>cleared_by</c> and <c>note</c>, plus
    /// <c>platform_incident_cleared.v1</c>; the <c>publishing.incident_cleared</c> audit row is the caller's
    /// (06.2.3-T3's clear command), which is the only place that knows the rescheduled count.
    /// </summary>
    /// <remarks>
    /// The per-incident reschedule sweep is 06.2.3-T3's (its clear command loads the held publications as tracked
    /// entities and calls <c>Publication.ReleaseHold</c>): this member writes the incident's own columns and the outbox
    /// row, answers <see cref="PlatformIncidentClearResult.PublicationsRescheduled"/> <c>0</c>, and is idempotent — a
    /// repeat answers <see cref="PlatformIncidentClearResult.AlreadyCleared"/> <c>true</c> and writes nothing. The same
    /// command writes <c>publishing.incident_cleared</c> after its sweep, with the real count.
    /// </remarks>
    /// <param name="incidentId">The incident to clear.</param>
    /// <param name="operatorName">The person who cleared it (YR16: <c>cleared_by</c>, never a fixed string).</param>
    /// <param name="note">The operator's note (YR16: <c>--note</c> is required).</param>
    /// <param name="now">The clear's instant, from the caller's clock.</param>
    /// <param name="ct">Cancels the statements.</param>
    /// <returns>
    /// The clear result, <c>not_found</c> for an unknown id, or the database failure.
    /// </returns>
    Task<ErrorOr<PlatformIncidentClearResult>> ClearAsync(
        Guid incidentId,
        string operatorName,
        string note,
        DateTimeOffset now,
        CancellationToken ct);
}
