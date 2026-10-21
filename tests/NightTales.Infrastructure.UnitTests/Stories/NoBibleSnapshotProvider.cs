using NightTales.Application.Features.SeriesBible.Abstractions;

namespace NightTales.Infrastructure.UnitTests.Stories;

/// <summary>
/// An <see cref="IBibleSnapshotProvider"/> for the story-generation harnesses that do not exercise the bible: every
/// series answers with no bible and no avoid-plots list, which is the same answer the real provider gives for a series
/// with an empty bible (02.6.3-T3).
/// </summary>
internal sealed class NoBibleSnapshotProvider : IBibleSnapshotProvider
{
    /// <inheritdoc />
    public ValueTask<BibleForWriter> GetForWriterAsync(Guid seriesId, CancellationToken ct) =>
        ValueTask.FromResult(new BibleForWriter(null, [], 0));
}
