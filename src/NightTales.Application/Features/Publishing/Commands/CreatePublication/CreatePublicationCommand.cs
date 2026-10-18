using System.Globalization;
using System.Text.Json.Serialization;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Features.Publishing.Dtos;
using NightTales.Domain.Audit;

namespace NightTales.Application.Features.Publishing.Commands.CreatePublication;

/// <summary>
/// <c>POST /api/v1/episodes/{id}/publications</c> (06.2.1-T1 task §Application, 06 README D6): turns a
/// <c>ready</c> episode and an <c>active</c> connection into an <c>awaiting_consent</c> publication whose
/// metadata already satisfies YouTube's limits.
/// </summary>
/// <remarks>
/// <para>
/// One command serves both callers of C1: the owner's route and 02.8.3's in-process autopilot run. That is why
/// <see cref="AutopilotAuthorizationId"/> is part of the command but <b>not</b> of the public request body —
/// only the autopilot path can set it, and it is what makes the created row carry a veto window.
/// </para>
/// <para>
/// The audit row's <c>data</c> (ids only, 06 README D17) is known only after the handler ran, so the handler
/// stores it in <see cref="Outcome"/> and <see cref="ToAuditData"/> projects it — the same shape
/// <c>ApproveFactSheetCommand</c> uses. A response with no outcome (a refused create) writes no row.
/// </para>
/// <para>
/// <see cref="Privacy"/> is the <b>submitted</b> value: the handler stores the effective one, which on a
/// pre-audit Google client is <c>private</c> and the submitted value travels in the row's
/// <c>platform_payload</c> (06 README D6, YR2).
/// </para>
/// </remarks>
/// <param name="EpisodeId">The episode the publication belongs to (<c>{id}</c>).</param>
/// <param name="ChannelConnectionId">The connection to upload through; must be one of the caller's, and <c>active</c>.</param>
/// <param name="Title">The YouTube title, 1–100 characters.</param>
/// <param name="Description">The submitted description; the handler appends the music attribution before it counts bytes.</param>
/// <param name="Tags">The submitted tags, at most 15, each at most 30 characters.</param>
/// <param name="Privacy">The submitted privacy (<c>public</c>, <c>unlisted</c>, <c>private</c>).</param>
/// <param name="PublishAt">The scheduled instant, or <see langword="null"/> for an immediate upload.</param>
/// <param name="AutopilotAuthorizationId">The authorization 02.8.3 creates under, or <see langword="null"/> for a manual create.</param>
[Audited("publishing.publication_created", Category.Publishing, Visibility.User)]
public sealed record CreatePublicationCommand(
    Guid EpisodeId,
    Guid ChannelConnectionId,
    string Title,
    string Description,
    IReadOnlyList<string> Tags,
    string Privacy,
    DateTimeOffset? PublishAt,
    Guid? AutopilotAuthorizationId) : ICommand<ErrorOr<PublicationDto>>, IAuditData
{
    /// <summary>
    /// What the handler wrote, for the audit projection: the three ids the row ties together and nothing the
    /// owner typed (06 README D17 — never a title, a description or a channel name).
    /// </summary>
    /// <remarks>
    /// Excluded from serialization so a command logged or echoed back never carries it: it is a handler
    /// output, not part of the request.
    /// </remarks>
    [JsonIgnore]
    public PublicationCreatedOutcome? Outcome { get; set; }

    /// <inheritdoc />
    public AuditData? ToAuditData() => Outcome is null
        ? null
        : new AuditData(
            SubjectUserId: null,
            EntityType: PublicationOutcome.SubjectType,
            EntityId: Outcome.PublicationId,
            Summary: new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [PublicationOutcome.PublicationIdKey] = Outcome.PublicationId.ToString("D", CultureInfo.InvariantCulture),
                [PublicationOutcome.EpisodeIdKey] = Outcome.EpisodeId.ToString("D", CultureInfo.InvariantCulture),
                [PublicationOutcome.ChannelConnectionIdKey] = Outcome.ChannelConnectionId.ToString("D", CultureInfo.InvariantCulture),
            });
}

/// <summary>
/// The ids a created publication wrote, as the audit row and the id-only log line report them
/// (06 README D17).
/// </summary>
/// <param name="PublicationId">The new <c>publications.id</c>.</param>
/// <param name="EpisodeId">The episode the publication belongs to.</param>
/// <param name="ChannelConnectionId">The connection it will upload through.</param>
public sealed record PublicationCreatedOutcome(Guid PublicationId, Guid EpisodeId, Guid ChannelConnectionId);

/// <summary>
/// The frozen spellings of the created-publication audit projection and its log line (09.3.6-T2 keeps the
/// audit <c>data</c> keys stable; 06.2.1-T1 owns these three).
/// </summary>
internal static class PublicationOutcome
{
    /// <summary>The <c>audit_logs.subject_type</c> of a publication row.</summary>
    internal const string SubjectType = "publication";

    /// <summary>The audit <c>data</c> key of the new publication id.</summary>
    internal const string PublicationIdKey = "publication_id";

    /// <summary>The audit <c>data</c> key of the episode the publication belongs to.</summary>
    internal const string EpisodeIdKey = "episode_id";

    /// <summary>The audit <c>data</c> key of the connection the publication uploads through.</summary>
    internal const string ChannelConnectionIdKey = "channel_connection_id";
}
