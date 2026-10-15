namespace NightTales.Application.Abstractions.Rendering;

/// <summary>
/// The test-only fake of <see cref="IRecentMusicQuery"/>: no series has a recent pick, so the selector's choice
/// depends on the episode id alone. It is not registered in any composition root; <c>RecentMusicQuery</c> is.
/// </summary>
public sealed class NullRecentMusicQuery : IRecentMusicQuery
{
    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetRecentTrackIdsAsync(Guid seriesId, int take, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
