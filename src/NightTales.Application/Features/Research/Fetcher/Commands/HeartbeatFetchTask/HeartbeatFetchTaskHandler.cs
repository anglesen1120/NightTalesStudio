using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Fetching;

namespace NightTales.Application.Features.Research.Fetcher.Commands.HeartbeatFetchTask;

/// <summary>
/// Extends the lease through the store's one conditional <c>UPDATE</c> (02.5.2-T2): it changes the row only while the
/// worker owns a live lease, so a heartbeat can never resurrect a lease the sweeper has already requeued.
/// </summary>
/// <param name="store">The queue-claim store.</param>
public sealed class HeartbeatFetchTaskHandler(IFetchTaskLeaseStore store)
    : ICommandHandler<HeartbeatFetchTaskCommand, ErrorOr<DateTimeOffset>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<DateTimeOffset>> Handle(
        HeartbeatFetchTaskCommand command,
        CancellationToken cancellationToken) =>
        await store.HeartbeatAsync(command.TaskId, command.WorkerId!, cancellationToken).ConfigureAwait(false);
}
