using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Domain.Audit;

namespace NightTales.Application.Features.Episodes.Commands.DeleteEpisode;

/// <summary>
/// <c>DELETE /api/v1/episodes/{id}</c> (09.5.1 plan §4 UC4, §5): cancel one of the caller's episodes.
/// </summary>
/// <remarks>
/// <para>
/// The delete is a soft cancel (F14): the row stays, every read hides it (404) and the assets are
/// scheduled for the 09.7.3 sweep through <c>episode_cancelled.v1</c>. A physical delete is 13.3.1's.
/// </para>
/// <para>
/// <see cref="IfMatch"/> is <em>optional</em> on this route ([DECISION] 09.5.1 plan §Notes): when the
/// client sends the tag it read, a stale one answers 412 <c>episode_conflict</c> before the aggregate
/// sees the cancel; when it sends none, the cancel proceeds.
/// </para>
/// </remarks>
/// <param name="Id">The episode to cancel.</param>
/// <param name="IfMatch">
/// The <c>If-Match</c> header exactly as received, or <see langword="null"/> when the header is absent
/// (an empty or whitespace value is still "present" and is compared, CR-1).
/// </param>
[Audited("episode.deleted", Category.Episodes, Visibility.User)]
public sealed record DeleteEpisodeCommand(Guid Id, string? IfMatch) : ICommand<ErrorOr<Success>>, IAuditData
{
    /// <summary>
    /// The row's projection (09 README BD15): the cancelled episode, the caller as the subject, and no
    /// summary — the action name says everything that changed.
    /// </summary>
    /// <returns>The projection of this command's audit row.</returns>
    public AuditData ToAuditData() => new(null, "episode", Id, AuditData.NoSummary);
}
