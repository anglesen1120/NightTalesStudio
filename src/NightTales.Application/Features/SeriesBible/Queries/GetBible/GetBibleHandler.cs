using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Series.Bible;

// The feature namespace `…Features.SeriesBible` shadows the Domain entity's name inside this folder (the
// same trap 09.4.1-T1 documents for the series aggregate), so both entities are aliased.
using SeriesAggregate = NightTales.Domain.Series.Series;
using SeriesBibleEntity = NightTales.Domain.Series.Bible.SeriesBible;

namespace NightTales.Application.Features.SeriesBible.Queries.GetBible;

/// <summary>
/// <c>GET /api/v1/series/{id}/bible</c> (02.6.3-T1, plan §4 U1): the series' one bible row — the version
/// and content the next save replaces, the <c>"sb-&lt;version&gt;"</c> ETag that save must send back, and
/// the pending proposal when there is one.
/// </summary>
/// <remarks>
/// The read model is untracked and read-only, so this route never creates the row it does not find (the
/// CQRS rule of plan §4 U1). Two failures are distinguished without naming a status: an unknown or another
/// user's series — the tenant filter hides both — is 404, while a series of the caller's whose bible row
/// is missing is the data-integrity 500 <c>series_bible_missing</c>, counted on
/// <see cref="SeriesBibleMetrics"/> and never repaired here.
/// </remarks>
/// <param name="readDb">The read model.</param>
/// <param name="metrics">The feature's instruments, counted when a row is missing.</param>
public sealed class GetBibleHandler(IReadDb readDb, SeriesBibleMetrics metrics)
    : IQueryHandler<GetBibleQuery, ErrorOr<BibleDto>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<BibleDto>> Handle(
        GetBibleQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var row = await readDb.Query<SeriesBibleEntity>()
            .Where(bible => bible.SeriesId == query.SeriesId)
            .Select(bible => new BibleRow(
                bible.Version,
                bible.Content,
                bible.ProposedContent,
                bible.ProposedFromEpisodeId))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            var known = await readDb.Query<SeriesAggregate>()
                .AnyAsync(series => series.Id == query.SeriesId, cancellationToken)
                .ConfigureAwait(false);
            if (!known)
            {
                return Errors.NotFound();
            }

            metrics.Missing();
            return BibleErrors.Missing();
        }

        return new BibleDto(
            query.SeriesId,
            row.Version,
            BibleEtags.For(row.Version),
            BibleContentV1.FromDomain(row.Content),
            row.ProposedContent is null
                ? null
                : BibleProposalDto.FromDomain(row.ProposedContent, row.ProposedFromEpisodeId));
    }

    /// <summary>The projected bible row: the content and the pending proposal, without tracking.</summary>
    private sealed record BibleRow(
        int Version,
        SeriesBibleContent Content,
        BibleProposal? ProposedContent,
        Guid? ProposedFromEpisodeId);
}
