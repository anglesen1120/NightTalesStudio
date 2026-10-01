using ErrorOr;
using Mediator;

namespace NightTales.Application.Features.Series.Queries.GetSeries;

/// <summary>
/// <c>GET /api/v1/series/{id}</c> (09.4.1 plan §4 U4): read one series, archived or not.
/// </summary>
/// <remarks>
/// The read is not filtered on <c>archived_at</c>: the series itself stays readable after
/// <c>DELETE</c>, which is what "its episodes stay accessible" needs — the list hides it, this returns
/// it with <c>archivedAt</c> set (09 README D14/D16).
/// </remarks>
/// <param name="Id">The series to read; another user's id answers 404, like a missing one.</param>
public sealed record GetSeriesQuery(Guid Id) : IQuery<ErrorOr<SeriesDto>>;
