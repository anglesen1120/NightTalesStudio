using ErrorOr;
using Mediator;

namespace NightTales.Application.Features.SeriesBible.Queries.GetBible;

/// <summary>
/// <c>GET /api/v1/series/{id}/bible</c> (02.6.3-T1, plan §4 U1): the series' one bible row.
/// </summary>
/// <remarks>
/// The result is an <c>ErrorOr</c> like every other query of this layer, so the endpoint answers the two
/// failures without the handler naming a status: 404 for an unknown or another user's series, 500
/// <c>series_bible_missing</c> for an existing series whose row is missing.
/// </remarks>
/// <param name="SeriesId">The series whose bible is read.</param>
public sealed record GetBibleQuery(Guid SeriesId) : IQuery<ErrorOr<BibleDto>>;
