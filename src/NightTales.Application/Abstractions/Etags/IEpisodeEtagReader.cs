namespace NightTales.Application.Abstractions.Etags;

/// <summary>
/// Reads one episode's current ETag (09 README D15) for a caller that has no request to take it from: the
/// Worker's <c>VideoReRenderHandler</c> starts a render from an outbox delivery, so it reads the episode's
/// <c>xmin</c> at send time and passes it as the render's precondition.
/// </summary>
/// <remarks>
/// <para>
/// The value cannot travel in the event payload: <c>xmin</c> is assigned by PostgreSQL at commit, so the tag
/// a rejection was accepted under is already stale by the time the re-render runs, and a render refuses a
/// precondition it cannot match. Reading it at send time is what turns "the episode moved under us" into the
/// retryable 412 the dispatcher backs off on instead of a lost action.
/// </para>
/// <para>
/// The read is the D10 tenant-filtered one, so it can only ever see the caller's own episode: another
/// tenant's id, an unknown id and a deleted row are all <see langword="null"/> here (the render command then
/// answers the same 404 its own load would).
/// </para>
/// </remarks>
public interface IEpisodeEtagReader
{
    /// <summary>
    /// The episode's current ETag (<c>"ep-&lt;xmin&gt;"</c>), or <see langword="null"/> when the row is not
    /// visible to the caller.
    /// </summary>
    /// <param name="episodeId">The episode to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The strong ETag, quotes included, or <see langword="null"/>.</returns>
    ValueTask<string?> ForEpisodeAsync(Guid episodeId, CancellationToken cancellationToken);
}
