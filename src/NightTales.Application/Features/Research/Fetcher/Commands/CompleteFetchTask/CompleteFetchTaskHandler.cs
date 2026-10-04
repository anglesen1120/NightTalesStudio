using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Fetching;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Telemetry;
using NightTales.Domain.Common;
using NightTales.Domain.Research;

namespace NightTales.Application.Features.Research.Fetcher.Commands.CompleteFetchTask;

/// <summary>
/// Accepts a fetcher's result (02.5.2-T2 Design, in the contract's order): the lease ownership (<c>FOR UPDATE</c> on
/// the task row, <c>409 fetch_lease_lost</c>), then the body (<see cref="FetchTaskResultValidator"/>, 400 and the task
/// stays leased), then the object write, then — in the one transaction — the source and task transitions and the
/// <c>SourceFetched</c> outbox row.
/// </summary>
/// <remarks>
/// The object is written <em>before</em> the row update: a crash between the two leaves an orphan object under
/// <c>research/</c> (13.3's sweep) and never a row whose <c>extracted_text_key</c> points at nothing. The key is
/// content-addressed, so a retry after a failed commit writes the same object again.
/// </remarks>
/// <param name="store">The queue-claim store.</param>
/// <param name="db">The tracked write model, pinned to the task's owner by the host.</param>
/// <param name="texts">The extracted-text object store.</param>
/// <param name="clock">The command's clock reading.</param>
/// <param name="limits">The body caps.</param>
/// <param name="psl">The public-suffix list.</param>
/// <param name="blocklist">The blocked domains.</param>
/// <param name="metrics">The custom instruments.</param>
/// <param name="gauge">The <c>fetch_leases_active</c> gauge.</param>
public sealed class CompleteFetchTaskHandler(
    IFetchTaskLeaseStore store,
    IAppDbContext db,
    IExtractedTextStore texts,
    IClock clock,
    FetchResultLimits limits,
    IPublicSuffixList psl,
    DomainBlocklist blocklist,
    NightTalesMetrics metrics,
    FetchLeaseGauge gauge)
    : ICommandHandler<CompleteFetchTaskCommand, ErrorOr<FetchResultAck>>
{
    private const string StatusDone = "done";

    private const string StatusFailed = "failed";

    /// <inheritdoc />
    public async ValueTask<ErrorOr<FetchResultAck>> Handle(
        CompleteFetchTaskCommand command,
        CancellationToken cancellationToken)
    {
        var claimed = await store
            .ClaimForResultAsync(command.TaskId, command.Body.WorkerId!, cancellationToken)
            .ConfigureAwait(false);
        if (claimed.IsError)
        {
            return claimed.Errors;
        }

        var task = claimed.Value;
        var interpreted = FetchTaskResultValidator.Interpret(task.Kind, command.Body, limits, psl, blocklist);
        if (interpreted.IsError)
        {
            return interpreted.Errors;
        }

        if (task.Kind != FetchTaskKind.FetchSource || task.SourceId is not { } sourceId)
        {
            // [DECISION 02.5.2-T2] the finder result is validated (50 cap, invalid URLs dropped) but nothing consumes the
            // candidates before 02.7.2-T3 ships source_candidates; the task stays leased and is swept.
            return FetcherErrors.KindUnsupported;
        }

        var source = await db.StorySources
            .SingleOrDefaultAsync(candidate => candidate.Id == sourceId, cancellationToken)
            .ConfigureAwait(false);
        var fetchTask = await db.FetchTasks
            .SingleOrDefaultAsync(candidate => candidate.Id == task.TaskId, cancellationToken)
            .ConfigureAwait(false);
        if (source is null || fetchTask is null)
        {
            return FetcherErrors.TaskNotFound;
        }

        if (source.Status != StorySourceStatus.Pending)
        {
            return FetcherErrors.LeaseLost;
        }

        var now = clock.UtcNow;
        var leasedBefore = await store.CountLeasedAsync(cancellationToken).ConfigureAwait(false);
        string metricStatus;
        string? metricCode = null;

        switch (interpreted.Value)
        {
            case FetchedResult fetched:
                {
                    var key = await texts
                        .WriteAsync(task.UserId, task.EpisodeId, source.Id, fetched.Sha256, fetched.Text, cancellationToken)
                        .ConfigureAwait(false);
                    if (key.IsError)
                    {
                        return key.Errors;
                    }

                    source.MarkFetched(
                        fetched.FinalUrl,
                        fetched.Title,
                        fetched.ContentType,
                        fetched.Sha256,
                        key.Value,
                        fetched.Text.Length,
                        now,
                        fetched.HttpStatus);
                    fetchTask.Complete(now);
                    metricStatus = StatusDone;
                    break;
                }

            case FailedResult failed:
                source.MarkFailed(failed.ErrorCode, failed.HttpStatus, now);
                fetchTask.Fail(failed.ErrorCode, now);
                metricStatus = StatusFailed;
                metricCode = failed.ErrorCode;
                break;

            default:
                return FetcherErrors.KindUnsupported;
        }

        var episode = await db.Episodes
            .SingleOrDefaultAsync(candidate => candidate.Id == task.EpisodeId, cancellationToken)
            .ConfigureAwait(false);
        episode?.RaiseSourceFetched(source.Id, now);

        metrics.RecordFetchTask(metricStatus, metricCode);
        if (task.LeasedAt is { } leasedAt)
        {
            // Lease to result, never created to result: the histogram must not measure queue time (plan §7.4).
            metrics.RecordFetchDuration(Math.Max(0d, (now - leasedAt).TotalSeconds));
        }

        gauge.Set(leasedBefore - 1);

        return new FetchResultAck(task.TaskId, StatusDone);
    }
}
