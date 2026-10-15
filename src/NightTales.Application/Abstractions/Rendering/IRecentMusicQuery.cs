namespace NightTales.Application.Abstractions.Rendering;

/// <summary>
/// The read seam behind the selector's avoid-recent rule (05 README D8, 05.3.2-T3): the track ids a series
/// used most recently, so <c>MusicSelector.Pick</c> does not repeat a track back to back.
/// </summary>
/// <remarks>
/// The contract is the last <paramref name="take"/> values of <c>render_jobs.params-&gt;'music'-&gt;&gt;'track_id'</c>
/// of the series, newest first, ignoring rows without a <c>music</c> object:
/// <code>
/// select params-&gt;'music'-&gt;&gt;'track_id' as track_id from render_jobs
/// where series_id = @series_id and params-&gt;'music' is not null
/// order by created_at desc limit @take;
/// </code>
/// <c>render_jobs</c> is created by 09.6.1. The registered implementation is <c>RecentMusicQuery</c> (Infrastructure,
/// 09.6.1-T1; the raw <c>SqlQuery&lt;string&gt;</c> shape is its call), registered by <c>AddPersistence</c>; it replaces
/// the <see cref="NullRecentMusicQuery"/> that was registered before 09.6.1-T1.
/// <para>
/// Tenant isolation (05.3.2-T3 gate SEC-5): <c>render_jobs</c> is tenant-owned while <c>music_tracks</c> is not,
/// so the implementation MUST read through the tenant-filtered <c>RenderJobs</c> set, or a raw query MUST add a
/// <c>user_id = @current_user</c> predicate from <c>ICurrentUser</c>; the SQL above omits it only as a sketch.
/// </para>
/// </remarks>
public interface IRecentMusicQuery
{
    /// <summary>The series' latest picked track ids, newest first, at most <paramref name="take"/>.</summary>
    /// <param name="seriesId">The series.</param>
    /// <param name="take">How many ids to return (<c>Music:AvoidRecent</c>, default 3).</param>
    /// <param name="ct">The cancellation token.</param>
    Task<IReadOnlyList<string>> GetRecentTrackIdsAsync(Guid seriesId, int take, CancellationToken ct);
}
