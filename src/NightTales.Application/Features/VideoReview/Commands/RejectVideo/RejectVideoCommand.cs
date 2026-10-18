using System.Globalization;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Messaging;
using NightTales.Domain.Audit;
using NightTales.Domain.Episodes;

namespace NightTales.Application.Features.VideoReview.Commands.RejectVideo;

/// <summary>
/// Rejects the reviewed video (09.7 plan §4 UC2/UC3, 00 §7, WT3): <c>video_review → story_review</c> when the
/// story needs another edit, or <c>video_review → story_approved</c> when the approved story is to be rendered
/// again.
/// </summary>
/// <remarks>
/// <para>
/// The rejection itself starts nothing: it stores the reason, moves the state and raises
/// <c>video_rejected.v1</c>. The Worker's <c>VideoReRenderHandler</c> sends the render in its own scope, so a
/// render error (a flagged keyword, the spend cap, a busy node) can never roll the rejection back — the
/// episode simply stays in the state the caller chose (WT3).
/// </para>
/// <para>
/// <see cref="IdempotencyKey"/> is the D6 key of the reject route: the success value
/// (<see cref="VideoReviewResult"/>) is stored per <c>(user, key)</c>, so a repeated POST replays the answer it
/// already gave and raises no second <c>VideoRejected</c> — which is what keeps one client action from
/// reserving a re-render twice.
/// </para>
/// <para>
/// The audit row's <c>data</c> is this command's own projection (BD15/BD16): ids, the target, the resulting
/// status and whether a reason was sent — never the reason's text or a storage key.
/// </para>
/// </remarks>
/// <param name="EpisodeId">The episode under review.</param>
/// <param name="Reason">The owner's reason, 1–500 characters after trimming.</param>
/// <param name="Target">Where the rejection sends the episode.</param>
/// <param name="IfMatch">
/// The <c>If-Match</c> header of an HTTP caller, or <see langword="null"/> for an in-process one.
/// </param>
/// <param name="IdempotencyKey">The <c>Idempotency-Key</c> header value.</param>
[Audited("episode.video_rejected", Category.Episodes, Visibility.User)]
public sealed record RejectVideoCommand(
    Guid EpisodeId,
    string Reason,
    RejectTarget Target,
    string? IfMatch = null,
    string? IdempotencyKey = null)
    : ICommand<ErrorOr<VideoReviewResult>>, IIdempotentCommand, IAuditData
{
    /// <inheritdoc />
    string? IIdempotentCommand.IdempotencyKey => IdempotencyKey;

    /// <inheritdoc />
    public AuditData? ToAuditData() =>
        new(
            null,
            "episode",
            EpisodeId,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["episodeId"] = EpisodeId.ToString("D", CultureInfo.InvariantCulture),
                ["target"] = Target.ToWireName(),
                ["status"] = Target.SuccessorStatus(),
                ["reasonPresent"] = true,
            });
}
