using ErrorOr;
using NightTales.Domain.Usage;

namespace NightTales.Application.Abstractions.Usage;

/// <summary>The immutable <c>price_versions</c> rows (02 README D14).</summary>
public interface IPriceCatalog
{
    /// <summary>
    /// The newest version with <c>active_from &lt;= queuedAt</c>: a run queued before a re-price keeps the old rates.
    /// </summary>
    /// <param name="queuedAt">The run's queue time.</param>
    /// <param name="ct">Cancels the lookup.</param>
    /// <returns>The version, or the typed error <c>price_catalog_empty</c> when none is active at <paramref name="queuedAt"/> (02 T111).</returns>
    Task<ErrorOr<PriceVersion>> GetActiveAsync(DateTimeOffset queuedAt, CancellationToken ct);

    /// <summary>One version by number; a historical <c>usage_records.price_version</c> always resolves.</summary>
    /// <param name="version">The version number.</param>
    /// <param name="ct">Cancels the lookup.</param>
    /// <exception cref="KeyNotFoundException">No such version.</exception>
    Task<PriceVersion> GetAsync(int version, CancellationToken ct);
}
