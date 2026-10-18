using System.Globalization;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Domain.Audit;

namespace NightTales.Application.Features.VideoReview.Commands.ApproveVideo;

/// <summary>
/// Accepts the reviewed video (09.7 plan §4 UC1, 00 §7): <c>video_review → ready</c>, with the episode's live
/// assets re-stamped for the approved retention window (WT56).
/// </summary>
/// <remarks>
/// <para>
/// One command serves both callers of BD1: the owner's
/// <c>POST /api/v1/episodes/{id}/video/approve</c> passes the <c>If-Match</c> header it read, and 02.8.2-T1's
/// autopilot gate sends it in process as <c>ApproveVideoCommand(episodeId, "autopilot")</c>. A
/// <see langword="null"/> <see cref="IfMatch"/> therefore means "the caller is not HTTP": only the stale-tag
/// comparison is skipped, never the state check.
/// </para>
/// <para>
/// The audit row's <c>data</c> is this command's own projection, never its request (BD15/BD16): ids, the
/// resulting status and whether a note was sent — never the note's text.
/// </para>
/// </remarks>
/// <param name="EpisodeId">The episode under review.</param>
/// <param name="Note">
/// The optional note the acceptance carries (≤ 500 characters); the autopilot passes <c>"autopilot"</c>.
/// </param>
/// <param name="IfMatch">
/// The <c>If-Match</c> header of an HTTP caller, or <see langword="null"/> for an in-process one.
/// </param>
[Audited("episode.video_approved", Category.Episodes, Visibility.User)]
public sealed record ApproveVideoCommand(Guid EpisodeId, string? Note, string? IfMatch = null)
    : ICommand<ErrorOr<Success>>, IAuditData
{
    /// <inheritdoc />
    public AuditData? ToAuditData() =>
        new(
            null,
            "episode",
            EpisodeId,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["episodeId"] = EpisodeId.ToString("D", CultureInfo.InvariantCulture),
                ["status"] = "ready",
                ["notePresent"] = Note is not null,
            });
}
