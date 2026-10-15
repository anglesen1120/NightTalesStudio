using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Features.SeriesBible.Abstractions;
using NightTales.Domain.Series.Bible;

using SeriesBibleEntity = NightTales.Domain.Series.Bible.SeriesBible;

namespace NightTales.Infrastructure.Bible;

/// <summary>
/// The writer's read of a series bible (02.6.3-T3). It reads the one tenant-filtered row through <see cref="IReadDb"/>,
/// truncates it with <see cref="BibleSnapshotBuilder"/> and derives the avoid-plots list with
/// <see cref="AvoidPlotsBuilder"/>. The read is untracked and never writes; the bible text is never logged (D19).
/// </summary>
public sealed class BibleSnapshotProvider : IBibleSnapshotProvider
{
    private readonly IReadDb _readDb;
    private readonly BibleSnapshotLimits _limits;
    private readonly BibleAvoidPlotsOptions _avoidPlots;

    /// <summary>Creates the provider over the tenant-filtered read model and the bible options.</summary>
    /// <param name="readDb">The read model; its tenant filter scopes the bible row to the caller.</param>
    /// <param name="options">The bible options; the writer budget and the avoid-plots limits come from here.</param>
    public BibleSnapshotProvider(IReadDb readDb, IOptions<BibleOptions> options)
    {
        _readDb = readDb;
        var prompt = options.Value.Prompt;
        _limits = new BibleSnapshotLimits(
            prompt.MaxSummaries,
            prompt.MaxChars,
            prompt.MaxDescriptionChars,
            prompt.MaxVoiceNotesChars,
            prompt.KeepFullSummaryCount,
            prompt.MaxArrayEntries);
        _avoidPlots = options.Value.AvoidPlots;
    }

    /// <inheritdoc />
    public async ValueTask<BibleForWriter> GetForWriterAsync(Guid seriesId, CancellationToken ct)
    {
        var row = await _readDb.Query<SeriesBibleEntity>()
            .Where(bible => bible.SeriesId == seriesId)
            .Select(bible => new { bible.Version, bible.Content })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (row is null || IsEmpty(row.Content))
        {
            return new BibleForWriter(null, [], 0);
        }

        var snapshot = BibleSnapshotBuilder.Build(row.Content, row.Version, _limits);
        var avoid = AvoidPlotsBuilder.Build(snapshot.EpisodeSummaries, _avoidPlots.Max, _avoidPlots.MaxCharsPerEntry);
        return new BibleForWriter(snapshot, avoid, row.Version);
    }

    /// <summary>A bible with no persona and no arrays is treated as no bible at all (02.6.3-T3).</summary>
    private static bool IsEmpty(SeriesBibleContent content) =>
        string.IsNullOrWhiteSpace(content.Narrator.Persona)
        && content.RecurringCharacters.Count == 0
        && content.Places.Count == 0
        && content.Rules.Count == 0
        && content.EpisodeSummaries.Count == 0;
}
