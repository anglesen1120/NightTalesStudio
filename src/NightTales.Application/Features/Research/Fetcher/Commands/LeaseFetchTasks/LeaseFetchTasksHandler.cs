using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Fetching;

namespace NightTales.Application.Features.Research.Fetcher.Commands.LeaseFetchTasks;

/// <summary>
/// Leases tasks through <see cref="IFetchTaskLeaseStore"/> (02.5.2-T2): the platform advisory lock, the caps and the
/// <c>FOR UPDATE SKIP LOCKED</c> claim all live in the store's one transaction. The <c>fetch_leases_active</c> gauge is
/// exported by the Worker's sweeper only, so the Api never reports a stale value.
/// </summary>
/// <param name="store">The queue-claim store.</param>
public sealed class LeaseFetchTasksHandler(IFetchTaskLeaseStore store)
    : ICommandHandler<LeaseFetchTasksCommand, ErrorOr<IReadOnlyList<LeaseDto>>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<IReadOnlyList<LeaseDto>>> Handle(
        LeaseFetchTasksCommand command,
        CancellationToken cancellationToken)
    {
        return await store.LeaseAsync(command.WorkerId!, command.Max, cancellationToken).ConfigureAwait(false);
    }
}
