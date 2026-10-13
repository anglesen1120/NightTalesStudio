using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Application.Features.Research.Jobs;

/// <summary>
/// The Hangfire job of one <c>fact_sheet</c> run (02.5.3 plan §7): <c>default</c> queue, <b>retry 0</b>
/// (D7/F23: transient failures are retried inside the call by the resilience layer, a crash is recovered by
/// <c>StaleGenerationSweeperJob</c>), enqueued by <see cref="GenerateFactSheetJobHandler"/>. The run id is the only
/// data argument (ids-only rule, D19).
/// </summary>
/// <remarks>
/// The run's owner is not known from the id, so the job resolves it through <see cref="ISystemWorkQuery"/>, then runs
/// the pipeline in a scope pinned to that user with <see cref="ISystemContext.For(Guid)"/>: every query of the run is
/// tenant-filtered like the Api's, with no <c>IgnoreQueryFilters</c>.
/// </remarks>
/// <param name="systemWork">The cross-tenant lookup that resolves the run's owner.</param>
/// <param name="scopes">The scope factory the pinned pipeline is resolved from.</param>
/// <param name="logger">The logger.</param>
[JobPolicy(Queue = JobQueue.Default, Retries = 0)]
public sealed partial class GenerateFactSheetJob(
    ISystemWorkQuery systemWork,
    IServiceScopeFactory scopes,
    ILogger<GenerateFactSheetJob> logger) : IBackgroundJob
{
    /// <summary>Runs the fact-sheet generation of <paramref name="generationRunId"/>.</summary>
    /// <param name="generationRunId">The run (the only parameter besides the token).</param>
    /// <param name="cancellationToken">Hangfire's shutdown token.</param>
    /// <returns>A task that completes when the run reached a status, or was left to another actor.</returns>
    public async Task RunAsync(Guid generationRunId, CancellationToken cancellationToken)
    {
        Guid? owner = null;

        await foreach (var (_, userId) in systemWork
            .PendingAsync<GenerationRun>(run => run.Id == generationRunId, 1, cancellationToken)
            .ConfigureAwait(false))
        {
            owner = userId;
        }

        if (owner is not { } ownerId)
        {
            LogRunMissing(generationRunId);
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(ownerId);

        var pipeline = scope.ServiceProvider.GetRequiredService<GenerateFactSheetPipeline>();

        await pipeline.RunAsync(generationRunId, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 6161,
        Level = LogLevel.Warning,
        Message = "generate_fact_sheet_job_unowned_run runId={RunId}")]
    private partial void LogRunMissing(Guid runId);
}
