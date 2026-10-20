using System.Globalization;
using System.Text.Json.Serialization;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Features.Publishing.Dtos;
using NightTales.Domain.Audit;

namespace NightTales.Application.Features.Publishing.Commands.ConsentPublication;

/// <summary>
/// <c>POST /api/v1/publications/{id}/consent</c> (06.2.2-T1 task §Design, 06 README D7): records the owner's
/// consent on an <c>awaiting_consent</c> publication, which moves the row to <c>scheduled</c>, moves its episode
/// to <c>scheduled</c> in the same transaction and lets the outbox carry <c>publication_updated.v1</c> to the
/// Worker (D13). The handler never schedules the upload job itself (YR3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two guards on one header.</b> <see cref="IfMatch"/> is the ETag the client read (<c>"pu-&lt;xmin&gt;"</c>);
/// the route's <c>RequireIfMatch()</c> answers 428 when it is absent, and the handler compares it after loading the
/// row so a stale value answers 412 <c>publication_conflict</c> — with the current tag reachable to the route —
/// instead of writing a lost update (09 README D15).
/// </para>
/// <para>
/// <b>Why the repeat is a success.</b> A retried request after a lost response carries the pre-consent tag, which
/// no longer names the row; the handler therefore answers the identical request with the same DTO <em>before</em>
/// comparing the header, and writes nothing: no second audit row, no second outbox row, no episode move
/// (06 README D7, <c>Publication.IsUserConsentedAs</c>).
/// </para>
/// <para>
/// The audit row's <c>data</c> (ids and the text version only, D17) is known only after the handler ran, so the
/// handler stores it in <see cref="Outcome"/> and <see cref="ToAuditData"/> projects it — the same shape
/// <c>CreatePublicationCommand</c> uses. A response with no outcome (a refusal, or the idempotent repeat that
/// changed nothing) writes no row.
/// </para>
/// </remarks>
/// <param name="PublicationId">The publication (<c>{id}</c>); another owner's id takes the same 404 an unknown one gets.</param>
/// <param name="ConsentTextVersion">The version of the consent text the client showed (<c>pc.v1</c>); it must equal <c>YouTube:ConsentTextVersion</c>.</param>
/// <param name="IfMatch">The <c>If-Match</c> header value — the ETag the client read.</param>
[Audited("publishing.consented", Category.Publishing, Visibility.User)]
public sealed record ConsentPublicationCommand(
    Guid PublicationId,
    string ConsentTextVersion,
    string IfMatch) : ICommand<ErrorOr<PublicationDto>>, IAuditData
{
    /// <summary>
    /// What the handler wrote, for the audit projection: the ids and the text version the owner consented to, and
    /// nothing the owner typed (06 README D17 — never a title, a description or a channel name).
    /// </summary>
    /// <remarks>
    /// Excluded from serialization so a command logged or echoed back never carries it: it is a handler output,
    /// not part of the request. It stays <see langword="null"/> on every refusal and on the idempotent repeat.
    /// </remarks>
    [JsonIgnore]
    public PublicationConsentedOutcome? Outcome { get; set; }

    /// <inheritdoc />
    public AuditData? ToAuditData() => Outcome is null
        ? null
        : new AuditData(
            SubjectUserId: null,
            EntityType: ConsentOutcome.SubjectType,
            EntityId: Outcome.PublicationId,
            Summary: new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [ConsentOutcome.PublicationIdKey] = Outcome.PublicationId.ToString("D", CultureInfo.InvariantCulture),
                [ConsentOutcome.EpisodeIdKey] = Outcome.EpisodeId.ToString("D", CultureInfo.InvariantCulture),
                [ConsentOutcome.ConsentTextVersionKey] = Outcome.ConsentTextVersion,
                [ConsentOutcome.SourceKey] = ConsentOutcome.SourceUser,
            });
}

/// <summary>
/// The ids a consent wrote, as the audit row and the id-only log line report them (06 README D17).
/// </summary>
/// <param name="PublicationId">The consented <c>publications.id</c>.</param>
/// <param name="EpisodeId">The episode the publication belongs to.</param>
/// <param name="ConsentTextVersion">The version of the consent text the owner consented to (<c>pc.v1</c>).</param>
public sealed record PublicationConsentedOutcome(Guid PublicationId, Guid EpisodeId, string ConsentTextVersion);

/// <summary>
/// The frozen spellings of the consented-publication audit projection (09.3.6-T2 keeps the audit <c>data</c> keys
/// stable; 06.2.2-T1 owns these five, 06.2.2-T3 reuses the action for the autopilot job's sibling).
/// </summary>
internal static class ConsentOutcome
{
    /// <summary>The <c>audit_logs.subject_type</c> of a publication row.</summary>
    internal const string SubjectType = "publication";

    /// <summary>The audit <c>data</c> key of the consented publication id.</summary>
    internal const string PublicationIdKey = "publicationId";

    /// <summary>The audit <c>data</c> key of the episode the publication belongs to.</summary>
    internal const string EpisodeIdKey = "episodeId";

    /// <summary>The audit <c>data</c> key of the consent text version (<c>pc.v1</c>).</summary>
    internal const string ConsentTextVersionKey = "consentTextVersion";

    /// <summary>The audit <c>data</c> key of who consented (D17: <c>user</c> here, <c>autopilot</c> in 06.2.2-T3).</summary>
    internal const string SourceKey = "source";

    /// <summary>The <see cref="SourceKey"/> value of this route: the owner consented in the app (D7).</summary>
    internal const string SourceUser = "user";
}
