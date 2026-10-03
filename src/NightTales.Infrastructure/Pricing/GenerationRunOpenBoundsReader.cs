using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Usage;
using NightTales.Domain.Stories.Generation;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Pricing;

/// <summary>
/// The open bounds of phase P (02.2.2-T2): the summed <c>worst_case_usd</c> of every <c>queued</c> or
/// <c>running</c> generation run of every user. It reads on the read model in its own transaction and returns
/// one number, never a row, which is why it is on the tenant-filter bypass allow-list (09 README D10).
/// </summary>
/// <param name="readDb">The read model.</param>
public sealed class GenerationRunOpenBoundsReader(IReadDb readDb) : IOpenBoundsReader
{
    /// <inheritdoc />
    public async ValueTask<decimal> SumOpenWorstCaseUsdAsync(CancellationToken ct)
    {
        var total = await readDb
            .Query<GenerationRun>()
            .IgnoreQueryFilters([TenantFilterConvention.FilterName])
            .Where(run => run.Status == GenerationRunStatus.Queued || run.Status == GenerationRunStatus.Running)
            .SumAsync(run => (decimal?)run.WorstCaseUsd, ct)
            .ConfigureAwait(false);

        return total ?? 0m;
    }
}
