using NightTales.Domain.Common;

namespace NightTales.Domain.Publishing;

/// <summary>
/// A publication's state changed (06 README D13): the outbox stores it as <c>publication_updated.v1</c> beside
/// the row in the same unit of work, with ids, the status and the error code only — never a title, a description
/// or a channel display name (06 README D17).
/// </summary>
/// <remarks>
/// <para>
/// It is the single event of the aggregate: the Worker's handler re-reads the row and schedules the upload
/// (06 YR3), and <c>IRealtimeNotifier</c> forwards the ids <c>{ publicationId, episodeId }</c> with no data
/// (06 README F13). The transitions that do not change what a consumer must do — <c>StartUpload</c> and its
/// upload-resume no-op (06 YR18) — raise nothing.
/// </para>
/// <para>
/// <see cref="OccurredAt"/> is part of the record because every <see cref="IDomainEvent"/> carries it, and the
/// outbox writes it to the message row; it is not part of the D13 payload contract.
/// </para>
/// </remarks>
/// <param name="PublicationId">The publication the change belongs to.</param>
/// <param name="EpisodeId">The episode it publishes; the Worker's handler resolves the job parameters through it.</param>
/// <param name="UserId">The owner the message runs as (the outbox row's <c>user_id</c> as well).</param>
/// <param name="Status">The state the row is in after the change; stored as its snake_case wire value.</param>
/// <param name="ErrorCode">
/// The catalogued reason of a <c>failed</c> publication, or <see langword="null"/> in every other state.
/// </param>
/// <param name="OccurredAt">When the aggregate raised it, from the command's clock.</param>
[OutboxEvent("publication_updated.v1")]
public sealed record PublicationUpdated(
    Guid PublicationId,
    Guid EpisodeId,
    Guid UserId,
    PublicationStatus Status,
    string? ErrorCode,
    DateTimeOffset OccurredAt) : IDomainEvent;
