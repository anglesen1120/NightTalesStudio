using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;

namespace NightTales.Application.Features.Platform.Flags.Queries;

/// <summary>
/// The one query behind <c>nts-worker flags list</c> (09 README BD13, 09.9.2-T2 §Operator CLI): every
/// flag, ordered by key, projected to the two fields the listing prints.
/// </summary>
/// <remarks>
/// One query, no tracking, no <c>Include</c> and no <c>rules</c> column: the set is closed and seeded
/// (09.2.5-T1), and P answers from <c>enabled</c> only (BD13/WT13).
/// </remarks>
/// <param name="database">The write model, which owns the flag configuration the read service reads.</param>
public sealed class ListFeatureFlagsHandler(IAppDbContext database)
    : IQueryHandler<ListFeatureFlagsQuery, ErrorOr<IReadOnlyList<FeatureFlagListItemDto>>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<IReadOnlyList<FeatureFlagListItemDto>>> Handle(
        ListFeatureFlagsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return await database.FeatureFlags
            .AsNoTracking()
            .OrderBy(flag => flag.Key)
            .Select(flag => new FeatureFlagListItemDto(flag.Key, flag.Enabled))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
