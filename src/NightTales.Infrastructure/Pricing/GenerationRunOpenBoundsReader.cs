using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Usage;
using NightTales.Domain.Rendering;
using NightTales.Domain.Stories.Generation;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Pricing;

/// <summary>
/// The open bounds of phase P (02.2.2-T2, extended by 09.6.3-T1): the summed <c>worst_case_usd</c> of every
/// <c>queued</c> or <c>running</c> generation run plus every <c>queued</c>, <c>submitted</c> or <c>running</c> render
/// job, of every user. It reads on the read model in its own transaction and returns one number, never a row, which
/// is why it is on the tenant-filter bypass allow-list (09 README D10).
/// </summary>
/// <remarks>
/// <para>
/// The two status sets differ on purpose. A render job in <c>ingesting</c> is <em>not</em> open: the adapter work is
/// done, the images are already billed and recorded, and the remaining download settles the existing reservation
/// rather than buying anything new. Every terminal state (<c>succeeded</c>, <c>failed</c>, <c>cancelled</c>) is
/// settled or refunded and therefore not open either. A generation run has no such stage, so its set stays
/// <c>queued</c>/<c>running</c> as 02.2.2-T2 wrote it.
/// </para>
/// <para>
/// The platform-wide cap is what this reader serves, so it deliberately spans tenants:
/// <see cref="IBudgetGuard.CheckAsync"/> compares the total against the day and month caps, and the caller excludes
/// its own row with <c>excludingRunId</c> where that row was already inserted in the same transaction (02 D1,
/// 09.6.3-T1's retry path).
/// </para>
/// </remarks>
/// <param name="readDb">The read model.</param>
public sealed class GenerationRunOpenBoundsReader(IReadDb readDb) : IOpenBoundsReader
{
    /// <inheritdoc />
    public async ValueTask<decimal> SumOpenWorstCaseUsdAsync(CancellationToken ct, Guid? excludingRunId = null)
    {
        var runs = readDb
            .Query<GenerationRun>()
            .IgnoreQueryFilters([TenantFilterConvention.FilterName])
            .Where(run => run.Status == GenerationRunStatus.Queued || run.Status == GenerationRunStatus.Running);

        // The jobs of the render side, in the three states that are still open: `ingesting` is excluded because the
        // adapter work is done and the images are already billed, and every terminal state is settled or refunded.
        var jobs = readDb
            .Query<RenderJob>()
            .IgnoreQueryFilters([TenantFilterConvention.FilterName])
            .Where(job => job.Status == RenderJobStatus.Queued
                || job.Status == RenderJobStatus.Submitted
                || job.Status == RenderJobStatus.Running);

        // `excludingRunId` is the caller's own row — a generation run id on the generation paths, a render job id on
        // the render paths — which it inserted in this very transaction, so it must not count itself twice. The id is
        // matched in both tables: every id is a UUIDv7 from one generator, so a value excludes the one row it names.
        if (excludingRunId is { } own)
        {
            runs = runs.Where(run => run.Id != own);
            jobs = jobs.Where(job => job.Id != own);
        }

        var runsTotal = await runs
            .SumAsync(run => (decimal?)run.WorstCaseUsd, ct)
            .ConfigureAwait(false);
        var jobsTotal = await jobs
            .SumAsync(job => (decimal?)job.WorstCaseUsd, ct)
            .ConfigureAwait(false);

        return (runsTotal ?? 0m) + (jobsTotal ?? 0m);
    }
}
