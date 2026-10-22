using ErrorOr;
using Mediator;
using NightTales.Application.Features.Autopilot.Dtos;

namespace NightTales.Application.Features.Autopilot.Queries.ListAutopilotRuns;

/// <summary>
/// <c>GET /api/v1/series/{id}/autopilot/runs?cursor=&amp;limit=</c> (02.8.3 plan §4 UC7): one keyset page of the
/// series' runs, newest slot first.
/// </summary>
/// <remarks>
/// <para>
/// The validation is the handler's, not a validator's, exactly as <c>ListSeriesQuery</c> does it: this request folder
/// owns one request, one handler and the DTOs, and the two failures the wire can carry — a limit outside
/// <see cref="MinLimit"/>…<see cref="MaxLimit"/> and a cursor that is not one of ours — answer 400
/// <c>validation_failed</c> with the field the client must fix (<c>limit</c> / <c>cursor</c>).
/// </para>
/// <para>
/// <see cref="DefaultLimit"/> is the D14 default of the other lists (20): task §Routes freezes only <c>limit ≤ 100</c>,
/// and a page of twenty runs covers a series' ready buffer several times over.
/// </para>
/// </remarks>
/// <param name="SeriesId">The series whose runs are listed; a foreign or missing one is a 404.</param>
/// <param name="Cursor">The opaque <c>nextCursor</c> of the previous page, or <see langword="null"/>.</param>
/// <param name="Limit">The page size; the endpoint passes <see langword="null"/> when the query string omits it.</param>
public sealed record ListAutopilotRunsQuery(
    Guid SeriesId,
    string? Cursor,
    int? Limit) : IQuery<ErrorOr<AutopilotRunPageDto>>
{
    /// <summary>The page size when <c>?limit=</c> is absent (09 README D14's default).</summary>
    public const int DefaultLimit = 20;

    /// <summary>The smallest accepted page size.</summary>
    public const int MinLimit = 1;

    /// <summary>The largest accepted page size; above it the request is a 400 (task §Routes: <c>limit ≤ 100</c>).</summary>
    public const int MaxLimit = 100;
}
