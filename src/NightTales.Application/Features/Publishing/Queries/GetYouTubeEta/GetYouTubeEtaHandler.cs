using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Publishing;
using NightTales.Application.Publishing.Quota;
using NightTales.Domain.Common;
using NightTales.Domain.Publishing;

namespace NightTales.Application.Features.Publishing.Queries.GetYouTubeEta;

/// <summary>
/// Answers the YouTube ETA from the Pacific day row (06.3.1-T1). With no row yet the full
/// <c>YouTube:Quota:UnitsPerDay</c> is available, because the ledger seeds the row on the first reserve.
/// </summary>
public sealed class GetYouTubeEtaHandler(
    IReadDb readDb,
    IClock clock,
    YouTubeOptions options) : IQueryHandler<GetYouTubeEtaQuery, ErrorOr<YouTubeEtaDto>>
{
    private const string YouTubePlatform = "youtube";

    public async ValueTask<ErrorOr<YouTubeEtaDto>> Handle(GetYouTubeEtaQuery query, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var day = PacificQuotaDay.Day(now);

        var row = await readDb.Query<PlatformQuotaUsage>()
            .Where(usage => usage.Platform == YouTubePlatform && usage.QuotaDay == day)
            .Select(usage => new { usage.UnitsUsed, usage.UnitsLimit })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return new YouTubeEtaDto(options.Quota.UnitsPerDay, null);
        }

        var available = Math.Max(0, row.UnitsLimit - row.UnitsUsed);
        return new YouTubeEtaDto(available, available > 0 ? null : PacificQuotaDay.NextResetAt(now));
    }
}
