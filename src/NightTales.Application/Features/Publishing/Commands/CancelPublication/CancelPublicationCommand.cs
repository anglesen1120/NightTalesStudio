using System.Globalization;
using System.Text.Json.Serialization;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Domain.Audit;

namespace NightTales.Application.Features.Publishing.Commands.CancelPublication;

/// <summary>
/// <c>DELETE /api/v1/publications/{id}</c> (06.2.6-T1 §Design, 06 README D7): cancels a publication that has not
/// started uploading, so its pending upload never runs and any quota unit reserved for it is given back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two guards on one header.</b> <see cref="IfMatch"/> is the ETag the client read (<c>"pu-&lt;xmin&gt;"</c>);
/// the route's <c>RequireIfMatch()</c> answers 428 when it is absent, and the handler compares it after loading the
/// row so a stale value answers 412 <c>publication_conflict</c> — with the current tag reachable to the route —
/// instead of cancelling a row the client has not seen the current state of (06 README D7, 09 README D15).
/// </para>
/// <para>
/// <b>Why the repeat is a success.</b> A <c>DELETE</c> retried after a lost response carries the pre-cancel tag,
/// which no longer names the row; the handler therefore answers an already-<c>cancelled</c> row with the same 204
/// <em>before</em> comparing the header, and writes nothing: no second audit row, no second outbox row, no second
/// quota release (<see cref="NightTales.Domain.Publishing.Publication.Cancel"/>'s idempotent arm).
/// </para>
/// <para>
/// <b>Ids only.</b> The audit row's <c>data</c> (D17) is the publication, its episode and the status the row left;
/// the title, the description and the channel display name are never part of it, and the log line spells the same
/// three values. The command carries no metadata of its own for that reason.
/// </para>
/// </remarks>
/// <param name="PublicationId">The publication (<c>{id}</c>); another owner's id takes the same 404 an unknown one gets.</param>
/// <param name="IfMatch">The <c>If-Match</c> header value — the ETag the client read.</param>
[Audited("publishing.cancelled", Category.Publishing, Visibility.User)]
public sealed record CancelPublicationCommand(
    Guid PublicationId,
    string IfMatch) : ICommand<ErrorOr<Deleted>>, IAuditData
{
    /// <summary>
    /// What this call cancelled, for the audit projection: the ids and the status the row left — never anything the
    /// owner typed (06 README D17).
    /// </summary>
    /// <remarks>
    /// Excluded from serialization so a command logged or echoed back never carries it: it is a handler output, not
    /// part of the request. It stays <see langword="null"/> on every refusal, on the idempotent repeat (the row was
    /// already <c>cancelled</c>, so this call changed nothing) and when the quota release fails and rolls the
    /// cancel back, so one cancel writes exactly one audit row.
    /// </remarks>
    [JsonIgnore]
    public PublicationCancelledOutcome? Outcome { get; set; }

    /// <inheritdoc />
    public AuditData? ToAuditData() => Outcome is null
        ? null
        : new AuditData(
            SubjectUserId: null,
            EntityType: CancelAuditKeys.EntityType,
            EntityId: Outcome.PublicationId,
            Summary: new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [CancelAuditKeys.PublicationIdKey] = Outcome.PublicationId.ToString("D", CultureInfo.InvariantCulture),
                [CancelAuditKeys.EpisodeIdKey] = Outcome.EpisodeId.ToString("D", CultureInfo.InvariantCulture),
                [CancelAuditKeys.PreviousStatusKey] = Outcome.PreviousStatus,
                // The actor is the step's, not a hard-coded `user`: the disconnect reuses the same transition with its
                // own value (D17, STATUS S72-4).
                [CancelAuditKeys.ActorKey] = Outcome.Actor,
            });
}

/// <summary>
/// The ids a cancel wrote, as the audit row and the id-only log line report them (06 README D17).
/// </summary>
/// <param name="PublicationId">The cancelled <c>publications.id</c>.</param>
/// <param name="EpisodeId">The episode the publication belongs to.</param>
/// <param name="PreviousStatus">
/// The status wire value the row left (<c>awaiting_consent</c> or <c>scheduled</c>), read before the transition —
/// the only history the row keeps, because the state itself is overwritten.
/// </param>
/// <param name="Actor">
/// Who cancelled, as the frozen <see cref="CancelAuditKeys.ActorKey"/> value: the owner through this route, or
/// <c>system:disconnect</c> when 06.4.1-T1's disconnect cascades over the row (D17, STATUS S72-4).
/// </param>
public sealed record PublicationCancelledOutcome(
    Guid PublicationId,
    Guid EpisodeId,
    string PreviousStatus,
    string Actor);

/// <summary>
/// The frozen spellings of the cancelled-publication audit projection (09.3.6-T2 keeps the audit <c>data</c> keys
/// stable; 06.2.6-T1 owns these four, and 06.4.1-T1's disconnect reuses the same action).
/// </summary>
internal static class CancelAuditKeys
{
    /// <summary>
    /// The <c>audit_logs.subject_type</c> of a publication row — the same value
    /// <c>ConsentOutcome.SubjectType</c> spells, because one entity has one audit subject type.
    /// </summary>
    internal const string EntityType = "publication";

    /// <summary>The audit <c>data</c> key of the cancelled publication id.</summary>
    internal const string PublicationIdKey = "publicationId";

    /// <summary>The audit <c>data</c> key of the episode the publication belongs to.</summary>
    internal const string EpisodeIdKey = "episodeId";

    /// <summary>The audit <c>data</c> key of the status the row left.</summary>
    internal const string PreviousStatusKey = "previousStatus";

    /// <summary>
    /// The audit <c>data</c> key of who cancelled (D17: <c>user</c> here, <c>system:disconnect</c> in 06.4.1-T1 and
    /// <c>system:link</c> in 06.2.2-T2).
    /// </summary>
    internal const string ActorKey = "actor";

    /// <summary>The <see cref="ActorKey"/> value of this route: the owner cancelled in the app (D7).</summary>
    internal const string ActorUser = "user";

    /// <summary>
    /// The <see cref="ActorKey"/> value 06.4.1-T1's disconnect passes when it cancels every open publication of a
    /// channel: nobody clicked cancel on these rows, the channel's disconnect did (D17, STATUS S72-4).
    /// </summary>
    internal const string ActorDisconnect = "system:disconnect";

    /// <summary>
    /// The <see cref="ActorKey"/> value 06.2.2-T2's cancel-by-link route passes: the owner clicked the single-use
    /// link of the veto notice, with no session (D7, D17, STATUS S72-4).
    /// </summary>
    internal const string ActorLink = "system:link";
}
