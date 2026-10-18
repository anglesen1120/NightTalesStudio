namespace NightTales.Application.Abstractions.Etags;

/// <summary>
/// Reads one episode's current ETag (09 README D15) for a caller that has no request to take it from: the
/// Worker's <c>VideoReRenderHandler</c> starts a render from an outbox delivery, so it reads the episode's
/// <c>xmin</c> at send time and passes it as the render's precondition.
/// </summary>
/// <remarks>
/// <para>
/// The value cannot travel in the event payload: <c>xmin</c> is assigned by PostgreSQL at commit, so the
/// rejection that scheduled the re-render is itself what bumped it, and the tag the rejecting request was
/// accepted under is stale by the time this handler runs — a render refuses a precondition it cannot match.
/// The send-time read therefore supplies the current tag. The read happens milliseconds before the send in the
/// same handler, so it is not what detects "the episode moved under us": only an interleaving write landing in
/// that sub-millisecond window makes the render's <c>If-Match</c> stale, and that is the narrow case whose
/// retryable 412 the dispatcher backs off on instead of a lost action.
/// </para>
/// <para>
/// The port exists because its caller is Application code that has no request to take a tag from, while the
/// Api's <c>PostCommitETagReader</c> lives in the Api layer, which Application cannot reference.
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
