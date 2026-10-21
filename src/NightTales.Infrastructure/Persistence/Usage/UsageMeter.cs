using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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
/// <remarks>
/// The settlement (price lookup and save) runs on its own short deadline, never on the caller's token: a
/// cancelled job already paid for the call, so the write must not be cancelled with it. A call that was
/// paid for but cannot be priced (the provider miscounted <c>cached &gt; input</c>, or the model is not in
/// the run's price version) is still written, <c>estimated = true</c>, at the bound the caller supplied
/// as <see cref="UsageItem.CostUsd"/>.
/// </remarks>
/// <param name="scopeFactory">Creates the fresh scope per call.</param>
/// <param name="catalog">Resolves the run's price version.</param>
/// <param name="clock">The row's creation instant.</param>
public sealed class UsageMeter(
    IServiceScopeFactory scopeFactory,
    IPriceCatalog catalog,
    IClock clock) : IUsageMeter
{
    /// <summary>How long the settlement of one row may take, independent of the caller's token.</summary>
    public static readonly TimeSpan SettlementTimeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public async Task RecordAsync(UsageItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);

        var operation = ParseOperation(item.Operation);
        var priceVersion = Validate(item, operation);

        // The caller's token ends here: the call is already paid for.
        using var settlement = new CancellationTokenSource(SettlementTimeout);
        var settle = settlement.Token;

        var (cost, estimated) = await CostOfAsync(item, operation, priceVersion, settle).ConfigureAwait(false);

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
            estimated: estimated,
            priceVersion: priceVersion,
            providerRequestId: item.ProviderRequestId));

        await db.SaveChangesAsync(settle).ConfigureAwait(false);
    }

    /// <summary>Rejects a malformed item before anything is priced or written; returns the parsed price version.</summary>
    private static int Validate(UsageItem item, UsageOperation operation)
    {
        if (!int.TryParse(item.PriceVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var priceVersion) || priceVersion <= 0)
        {
            throw new ArgumentException($"PriceVersion '{item.PriceVersion}' is not a positive price_versions number.", nameof(item));
        }

        if (item.InputTokens < 0 || item.CachedInputTokens < 0 || item.CacheWriteTokens < 0 || item.OutputTokens < 0)
        {
            throw new ArgumentException("Token counts cannot be negative.", nameof(item));
        }

        var estimated = item.Estimated || item.InputTokens is null;

        // The moderation endpoint is free and reports no usage (D12): it is the one estimated item whose bound is 0.
        if (estimated && operation != UsageOperation.Moderation && item.CostUsd is not > 0m)
        {
            throw new ArgumentException("An estimated usage item must carry the D1 bound (> 0) as CostUsd.", nameof(item));
        }

        if (item.CostUsd < 0m)
        {
            throw new ArgumentException("CostUsd cannot be negative.", nameof(item));
        }

        return priceVersion;
    }

    private async Task<(decimal Cost, bool Estimated)> CostOfAsync(UsageItem item, UsageOperation operation, int priceVersion, CancellationToken ct)
    {
        if (operation == UsageOperation.Moderation)
        {
            // Free endpoint, no usage in the response: always cost 0, estimated (D12).
            return (0m, true);
        }

        if (item.InputTokens is null || item.Estimated)
        {
            // No usage reported: the D1 bound, never 0 and never null - an unmetered call must not look free.
            return (item.CostUsd!.Value, true);
        }

        var version = await catalog.GetAsync(priceVersion, ct).ConfigureAwait(false);
        var document = version.ParseRates();

        // A provider miscount or an unpriced model after the money was spent: the call still gets its row, at the
        // caller's bound and flagged estimated. Without a bound there is nothing honest to write, so it throws.
        var miscounted = (item.CachedInputTokens ?? 0) > item.InputTokens;
        if (miscounted || !document.Text.TryGetValue(item.Model, out var rates))
        {
            return item.CostUsd is > 0m
                ? (item.CostUsd.Value, true)
                : miscounted
                    ? throw new ArgumentException("cached_input_tokens exceeds input_tokens and no CostUsd bound was supplied.", nameof(item))
                    : throw new UnknownModelPricingException(item.Model);
        }

        var cost = TextCostCalculator.Compute(
            rates,
            item.InputTokens,
            item.CachedInputTokens,
            item.CacheWriteTokens,
            item.OutputTokens,
            document.SurchargeThresholdTokens)
            ?? throw new InvalidOperationException("The calculator returned no cost for a call with usage.");

        return (cost, false);
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
