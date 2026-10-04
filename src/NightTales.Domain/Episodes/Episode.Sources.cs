using NightTales.Domain.Research.Events;

namespace NightTales.Domain.Episodes;

public sealed partial class Episode
{
    /// <summary>
    /// Records that a source of this episode was removed (02.5.1 plan §5.5): raises <see cref="SourceRemoved"/> on
    /// the aggregate, so the outbox row commits in the transaction that deletes the source row and the Worker can
    /// send <c>EpisodeUpdated</c>. The source is a child entity and has no events of its own.
    /// </summary>
    /// <param name="sourceId">The removed source.</param>
    /// <param name="now">The command's clock reading.</param>
    public void RaiseSourceRemoved(Guid sourceId, DateTimeOffset now) =>
        Raise(new SourceRemoved(sourceId, Id, UserId, now));

    /// <summary>
    /// Records that the fetcher's result for a url source was accepted (02.5.2-T2, plan §5.5): raises
    /// <see cref="SourceFetched"/> on the aggregate, so the outbox row commits in the transaction that stores the text
    /// and the Worker can send <c>EpisodeUpdated</c>. It is raised for a failed fetch too: the UI refetches the list
    /// either way, and the event carries ids only (D19).
    /// </summary>
    /// <param name="sourceId">The source the result belongs to.</param>
    /// <param name="now">The command's clock reading.</param>
    public void RaiseSourceFetched(Guid sourceId, DateTimeOffset now) =>
        Raise(new SourceFetched(sourceId, Id, UserId, now));
}
