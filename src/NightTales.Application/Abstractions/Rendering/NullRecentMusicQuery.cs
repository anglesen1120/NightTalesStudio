namespace NightTales.Application.Abstractions.Rendering;

/// <summary>
/// The <see cref="IRecentMusicQuery"/> until 09.6.1-T1 creates <c>render_jobs</c>: no series has a recent
/// pick, so the selector's choice depends on the episode id alone.
/// </summary>
public sealed class NullRecentMusicQuery : IRecentMusicQuery
{
    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetRecentTrackIdsAsync(Guid seriesId, int take, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
