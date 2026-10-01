using ErrorOr;
using Mediator;
using NightTales.Application.Common.Paging;

namespace NightTales.Application.Features.Series.Queries.ListSeries;

/// <summary>
/// <c>GET /api/v1/series?cursor=&amp;limit=&amp;includeArchived=</c> (09.4.1 plan §4 U5, 09 README D14):
/// one keyset page, newest first.
/// </summary>
/// <remarks>
/// <para>
/// The validation is the handler's and not a validator's, because this request folder owns exactly one
/// request, one handler and one DTO (09.4.1 plan §Design): the limit's 1–100 range and the cursor's
/// encoding answer 400 <c>validation_failed</c> with the field the client must fix
/// (<c>limit</c> / <c>cursor</c>).
/// </para>
/// <para>
/// Archived series are excluded unless the client asks for them: the row stays readable through
/// <c>GET /series/{id}</c> and its episodes stay untouched (00 §6).
/// </para>
/// </remarks>
/// <param name="Cursor">The opaque <c>nextCursor</c> of the previous page, or <see langword="null"/>.</param>
/// <param name="Limit">The page size; the endpoint defaults an absent value to <see cref="DefaultLimit"/>.</param>
/// <param name="IncludeArchived">Whether archived series are listed too.</param>
public sealed record ListSeriesQuery(
    string? Cursor,
    int Limit,
    bool IncludeArchived = false) : IQuery<ErrorOr<Page<SeriesListItemDto>>>
{
    /// <summary>The page size when <c>?limit=</c> is absent (09 README D14).</summary>
    public const int DefaultLimit = 20;

    /// <summary>The largest accepted page size; above it the request is a 400 (09 README D14).</summary>
    public const int MaxLimit = 100;
}
