using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Fetching;

namespace NightTales.Application.Features.Research.Fetcher.Commands.LeaseFetchTasks;

/// <summary>
/// Leases tasks through <see cref="IFetchTaskLeaseStore"/> (02.5.2-T2): the platform advisory lock, the caps and the
/// <c>FOR UPDATE SKIP LOCKED</c> claim all live in the store's one transaction; this handler only refreshes the
/// <c>fetch_leases_active</c> gauge from the exact count the store just produced.
/// </summary>
/// <param name="store">The queue-claim store.</param>
/// <param name="gauge">The gauge to refresh.</param>
public sealed class LeaseFetchTasksHandler(IFetchTaskLeaseStore store, FetchLeaseGauge gauge)
    : ICommandHandler<LeaseFetchTasksCommand, ErrorOr<IReadOnlyList<LeaseDto>>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<IReadOnlyList<LeaseDto>>> Handle(
        LeaseFetchTasksCommand command,
        CancellationToken cancellationToken)
    {
        var leased = await store.LeaseAsync(command.WorkerId!, command.Max, cancellationToken).ConfigureAwait(false);
        if (leased.IsError)
        {
            return leased.Errors;
        }

        // The advisory lock is still held, so the count is exact; a no-op lease (empty list) is a refresh too.
        gauge.Set(await store.CountLeasedAsync(cancellationToken).ConfigureAwait(false));

        return leased;
    }
}
