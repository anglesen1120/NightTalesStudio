using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Series;

// The feature namespace `…Features.Series` shadows the aggregate's name inside this folder, so the
// entity is aliased (09.4.1-T1).
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.Features.Series.Queries.GetSeries;

/// <summary>
/// <c>GET /api/v1/series/{id}</c> (09.4.1 plan §4 U4): one projection through the tenant-filtered read
/// model, so another user's series is a 404 and never a 403 (09 README D10).
/// </summary>
/// <remarks>
/// The ETag is built from the row's <c>xmin</c>, which the D8 convention maps to
/// <c>Series.Version</c>: a read returns the value that is current <em>now</em>, which is exactly what
/// <c>If-Match</c> on the next write must compare against.
/// </remarks>
/// <param name="readDb">The no-tracking read model; the D10 tenant filter applies to it.</param>
public sealed class GetSeriesHandler(IReadDb readDb) : IQueryHandler<GetSeriesQuery, ErrorOr<SeriesDto>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<SeriesDto>> Handle(GetSeriesQuery query, CancellationToken cancellationToken)
    {
        var row = await readDb.Query<SeriesAggregate>()
            .Where(series => series.Id == query.Id)
            .Select(series => new Row(
                series.Id,
                series.Title,
                series.Topic,
                series.Language,
                series.Aspect,
                series.VoiceId,
                series.StyleTemplateId,
                series.StoryPreset,
                series.AutopilotEnabled,
                series.ArchivedAt,
                series.CreatedAt,
                series.UpdatedAt,
                series.Version))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return row is null
            ? Errors.NotFound()
            : new SeriesDto(
                row.Id,
                row.Title,
                row.Topic,
                row.Language,
                row.Aspect,
                row.VoiceId,
                row.StyleTemplateId,
                row.StoryPreset,
                row.AutopilotEnabled,
                row.ArchivedAt,
                row.CreatedAt,
                row.UpdatedAt,
                ETags.Series(row.Version));
    }

    /// <summary>The projected row, including the concurrency token the ETag is built from.</summary>
    private sealed record Row(
        Guid Id,
        string Title,
        Topic Topic,
        Language Language,
        Aspect Aspect,
        string VoiceId,
        Guid StyleTemplateId,
        string? StoryPreset,
        bool AutopilotEnabled,
        DateTimeOffset? ArchivedAt,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        uint Version);
}
