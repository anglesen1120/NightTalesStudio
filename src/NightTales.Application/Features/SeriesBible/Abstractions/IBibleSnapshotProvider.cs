using NightTales.Domain.Series.Bible;

namespace NightTales.Application.Features.SeriesBible.Abstractions;

/// <summary>
/// Reads the series bible as the story writer and the reviewer see it (02.6.3-T3, D4, D5, D6). The generation pipeline
/// calls it once per run, so the writer, the review and any revision share one snapshot.
/// </summary>
public interface IBibleSnapshotProvider
{
    /// <summary>Reads the bible snapshot and the avoid-plots list of one series.</summary>
    /// <param name="seriesId">The series.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>
    /// The writer's view. <c>Bible</c> is <see langword="null"/> when the series has no bible row or the bible holds no
    /// persona, characters, places, rules or summaries.
    /// </returns>
    /// <exception cref="BibleSnapshotTooLargeException">The bible is still over the budget after every truncation step.</exception>
    ValueTask<BibleForWriter> GetForWriterAsync(Guid seriesId, CancellationToken ct);
}

/// <summary>The bible as one generation run uses it.</summary>
/// <param name="Bible">The truncated snapshot for the writer and the reviewer, or <see langword="null"/> when the bible is empty.</param>
/// <param name="AvoidPlots">The one-line plot summaries the writer must not repeat; empty when there is no bible.</param>
/// <param name="Version">The bible content version, or 0 when there is no bible.</param>
public sealed record BibleForWriter(BibleSnapshot? Bible, IReadOnlyList<string> AvoidPlots, int Version);
