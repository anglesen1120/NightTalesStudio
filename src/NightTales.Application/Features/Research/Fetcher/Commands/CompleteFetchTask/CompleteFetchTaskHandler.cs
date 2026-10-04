using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Fetching;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Common;
using NightTales.Domain.Research;

namespace NightTales.Application.Features.Research.Fetcher.Commands.CompleteFetchTask;

/// <summary>
/// Accepts a fetcher's result (02.5.2-T2 Design, in the contract's order): the lease ownership (<c>FOR UPDATE</c> on
/// the task row, <c>409 fetch_lease_lost</c>), then the body (<see cref="FetchTaskResultValidator"/>, 400 and the task
/// stays leased), then — in the one transaction — the source and task transitions and the <c>SourceFetched</c> outbox
/// row.
/// </summary>
/// <remarks>
/// <para>
/// No object-store I/O runs here (PERF-1): the host writes the content-addressed text object <em>before</em> this
/// command's transaction opens, with a deadline of its own, and passes the key in
/// <see cref="CompleteFetchTaskCommand.StoredTextKey"/>. The <c>fetch_tasks</c> row lock is therefore held only for
/// the row updates. A lease lost after the write leaves an orphan object under <c>research/</c> (13.3's sweep) and
/// answers 409; a row never points at a missing object.
/// </para>
/// <para>
/// The instruments are not touched here either (QA-9): the readings travel on <see cref="FetchResultAck.Telemetry"/>
/// and the host emits them once the commit has succeeded. The duration is the claim's database <c>now()</c> minus the
/// database <c>leased_at</c>, one clock.
/// </para>
/// </remarks>
/// <param name="store">The queue-claim store.</param>
/// <param name="db">The tracked write model, pinned to the task's owner by the host.</param>
/// <param name="clock">The command's clock reading.</param>
/// <param name="limits">The body caps.</param>
/// <param name="psl">The public-suffix list.</param>
/// <param name="blocklist">The blocked domains.</param>
public sealed class CompleteFetchTaskHandler(
    IFetchTaskLeaseStore store,
    IAppDbContext db,
    IClock clock,
    FetchResultLimits limits,
    IPublicSuffixList psl,
    DomainBlocklist blocklist)
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

        string metricStatus;
        string? metricCode = null;

        switch (interpreted.Value)
        {
            case FetchedResult fetched:
                {
                    if (command.StoredTextKey is not { Length: > 0 } storedKey)
                    {
                        throw new InvalidOperationException(
                            "A done fetch_source result must carry the key of the text the host wrote before the transaction.");
                    }

                    var key = storedKey;

                    source.MarkFetched(
                        fetched.FinalUrl,
                        fetched.Title,
                        fetched.ContentType,
                        fetched.Sha256,
                        key,
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

        var telemetry = new FetchResultTelemetry(
            metricStatus,
            metricCode,
            // Lease to result, never created to result (plan §7.4); leased_at and the claim's now() are both the
            // database clock, so host clock skew cannot show up in the histogram (QA-9).
            task.LeasedAt is { } leasedAt ? Math.Max(0d, (task.DbNow - leasedAt).TotalSeconds) : null);

        // Emitted by the host after the unit of work commits, never here: a failed commit counts nothing.
        return new FetchResultAck(task.TaskId, StatusDone) { Telemetry = telemetry };
    }
}
