using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories.ReadModels;
using NightTales.Domain.Episodes;
using EpisodeAggregate = NightTales.Domain.Episodes.Episode;

namespace NightTales.Application.Features.Stories.Queries.GetStoryVersion;

/// <summary>Reads one immutable story version, constrained by both episode and version id.</summary>
/// <param name="readDb">The tenant-filtered no-tracking read model.</param>
/// <param name="logger">Logs unreadable document ids only.</param>
public sealed class GetStoryVersionHandler(IReadDb readDb, ILogger<GetStoryVersionHandler> logger)
    : IQueryHandler<GetStoryVersionQuery, ErrorOr<StoryVersionDetailDto>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<StoryVersionDetailDto>> Handle(
        GetStoryVersionQuery query, CancellationToken cancellationToken)
    {
        var exists = await readDb.Query<EpisodeAggregate>().AsNoTracking()
            .AnyAsync(row => row.Id == query.EpisodeId && row.Status != EpisodeStatus.Cancelled, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
        {
            return Errors.NotFound();
        }

        var row = await StoryReadProjection.Versions(readDb, query.EpisodeId, query.VersionId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? Errors.NotFound() : StoryReadProjection.Detail(row, query.EpisodeId, logger);
    }
}
