using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Usage;
using NightTales.Domain.Common;
using NightTales.Domain.Usage;

namespace NightTales.Infrastructure.Persistence.Usage;

/// <summary>
/// The only writer of <c>usage_records</c> (02 README D14). Every call opens its <b>own</b> scope, pins
/// it to the run's owner and saves one row: the row is committed by that scope's context, independent of
/// any transaction of the calling job, so a job that fails or is cancelled still keeps its cost.
/// </summary>
/// <param name="scopeFactory">Creates the fresh scope per call.</param>
/// <param name="catalog">Resolves the run's price version.</param>
/// <param name="options">The long-context threshold.</param>
/// <param name="clock">The row's creation instant.</param>
public sealed class UsageMeter(
    IServiceScopeFactory scopeFactory,
    IPriceCatalog catalog,
    IOptions<PricingOptions> options,
    IClock clock) : IUsageMeter
{
    /// <inheritdoc />
    public async Task RecordAsync(UsageItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);

        var operation = ParseOperation(item.Operation);
        var cost = await CostOfAsync(item, ct).ConfigureAwait(false);

        // Not the caller's scope: a new one, so the context and its transaction belong to this write alone.
        await using var scope = scopeFactory.CreateAsyncScope();
        using var actor = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(item.UserId);
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();

        db.UsageRecords.Add(UsageRecord.Record(
            item.UserId,
            operation,
            item.Provider,
            cost,
            clock.UtcNow,
            episodeId: item.EpisodeId,
            seriesId: item.SeriesId,
            generationRunId: item.GenerationRunId,
            reservationId: item.ReservationId,
            model: item.Model,
            inputTokens: item.InputTokens ?? 0,
            cachedInputTokens: item.CachedInputTokens ?? 0,
            outputTokens: item.OutputTokens ?? 0,
            estimated: item.Estimated || item.InputTokens is null,
            priceVersion: int.Parse(item.PriceVersion, CultureInfo.InvariantCulture),
            providerRequestId: item.ProviderRequestId));

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task<decimal> CostOfAsync(UsageItem item, CancellationToken ct)
    {
        if (item.InputTokens is null || item.Estimated)
        {
            // No usage reported: the D1 bound, never 0 and never null — an unmetered call must not look free.
            return item.CostUsd
                ?? throw new ArgumentException(
                    "An estimated usage item must carry the D1 bound as CostUsd.", nameof(item));
        }

        if (item.CostUsd is { } given)
        {
            return given;
        }

        var version = await catalog.GetAsync(int.Parse(item.PriceVersion, CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        var rates = version.ParseRates().RatesFor(item.Model);

        return TextCostCalculator.Compute(
            rates,
            item.InputTokens,
            item.CachedInputTokens,
            item.CacheWriteTokens,
            item.OutputTokens,
            options.Value.SurchargeThresholdTokens)
            ?? throw new InvalidOperationException("The calculator returned no cost for a call with usage.");
    }

    private static UsageOperation ParseOperation(string operation)
    {
        foreach (var candidate in Enum.GetValues<UsageOperation>())
        {
            if (string.Equals(JsonNamingPolicy.SnakeCaseLower.ConvertName(candidate.ToString()), operation, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        throw new ArgumentException($"Unknown usage operation '{operation}'.", nameof(operation));
    }
}
