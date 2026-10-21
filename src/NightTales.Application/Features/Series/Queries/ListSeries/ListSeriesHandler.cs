using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common;
using NightTales.Application.Common.Errors;
using NightTales.Application.Common.Paging;
using NightTales.Domain.Common;
using NightTales.Domain.Series;

// The feature namespace `…Features.Series` shadows the aggregate's name inside this folder, so the
// entity is aliased (09.4.1-T1).
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.Features.Series.Queries.ListSeries;

/// <summary>
/// <c>GET /api/v1/series</c> (09.4.1 plan §4 U5, Design): the keyset page
/// <c>WHERE (created_at, id) &lt; (@c, @i) ORDER BY created_at DESC, id DESC LIMIT @limit + 1</c>, whose
/// extra row is what tells the handler whether a next page exists.
/// </summary>
/// <remarks>
/// <para>
/// Reading one row more than the page is cheaper and steadier than a second count query: if the extra
/// row arrives, <c>nextCursor</c> is the cursor of the last row of the page, so the next request
/// continues strictly after it. A row inserted between the two requests therefore appears in neither,
/// nor twice — the ordering pair is the key, so no <c>OFFSET</c> drift is possible
/// (09 README D14).
/// </para>
/// <para>
/// The comparison is the row-value comparison PostgreSQL optimizes against
/// <c>ix_series_user_id_created_at_id</c> (the index of 09.2.2-T1, descending on the same pair), and it
/// is written out because LINQ has no tuple comparison.
/// </para>
/// </remarks>
/// <param name="readDb">The no-tracking read model; the D10 tenant filter applies to it.</param>
public sealed class ListSeriesHandler(IReadDb readDb)
    : IQueryHandler<ListSeriesQuery, ErrorOr<Page<SeriesListItemDto>>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<Page<SeriesListItemDto>>> Handle(
        ListSeriesQuery query,
        CancellationToken cancellationToken)
    {
        if (query.Limit is < 1 or > ListSeriesQuery.MaxLimit)
        {
            return Errors.ValidationFailed(
                "limit",
                $"The page size must be between 1 and {ListSeriesQuery.MaxLimit}.");
        }

        var rows = readDb.Query<SeriesAggregate>();
        if (!query.IncludeArchived)
        {
            rows = rows.Where(series => series.ArchivedAt == null);
        }

        if (query.Cursor is { } rawCursor)
        {
            if (!Cursor.TryDecode(rawCursor, out var cursor))
            {
                return Errors.ValidationFailed("cursor", "The cursor is not a valid page cursor.");
            }

            var beforeCreatedAt = cursor.CreatedAt;
            var beforeId = cursor.Id;

            // A row-value comparison, not `created_at < @c OR (created_at = @c AND id < @i)`: Npgsql
            // translates the tuple comparison to `(created_at, id) < (@c, @i)`, which is a single range
            // the (user_id, created_at DESC, id DESC) index can seek, while the OR form is a bitmap of
            // two scans (PERF-01). The ordering below is unchanged, so the keyset stays total.
            rows = rows.Where(series => ValueTuple.Create(series.CreatedAt, series.Id)
                .CompareTo(ValueTuple.Create(beforeCreatedAt, beforeId)) < 0);
        }

        var page = await rows
            .OrderByDescending(series => series.CreatedAt)
            .ThenByDescending(series => series.Id)
            .Take(query.Limit + 1)
            .Select(series => new Row(
                series.Id,
                series.Title,
                series.Topic,
                series.Language,
                series.Aspect,
                series.VoiceId,
                series.StyleTemplateId,
                series.AutopilotEnabled,
                series.ArchivedAt,
                series.CreatedAt,
                series.UpdatedAt,
                series.Version))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = page
            .Take(query.Limit)
            .Select(row => new SeriesListItemDto(
                row.Id,
                row.Title,
                row.Topic,
                row.Language,
                row.Aspect,
                row.VoiceId,
                row.StyleTemplateId,
                row.AutopilotEnabled,
                row.ArchivedAt,
                row.CreatedAt,
                row.UpdatedAt,
                ETags.Series(row.Version)))
            .ToList();

        var nextCursor = page.Count > query.Limit && items.Count > 0
            ? new Cursor(items[^1].CreatedAt, items[^1].Id).Encode()
            : null;

        return new Page<SeriesListItemDto>(items, nextCursor);
    }

    /// <summary>The projected row, including the concurrency token each item's ETag is built from.</summary>
    private sealed record Row(
        Guid Id,
        string Title,
        Topic Topic,
        Language Language,
        Aspect Aspect,
        string VoiceId,
        Guid StyleTemplateId,
        bool AutopilotEnabled,
        DateTimeOffset? ArchivedAt,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        uint Version);
}
