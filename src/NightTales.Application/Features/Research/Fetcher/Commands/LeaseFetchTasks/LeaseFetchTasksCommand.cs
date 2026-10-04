using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Fetching;
using NightTales.Application.Abstractions.Messaging;

namespace NightTales.Application.Features.Research.Fetcher.Commands.LeaseFetchTasks;

/// <summary>
/// <c>POST /internal/fetcher/v1/tasks/lease</c> (02.5.2-T2, 02 D10): hand the fetcher up to <paramref name="Max"/>
/// queued tasks, at most 4 leased platform-wide and one per registrable domain.
/// </summary>
/// <remarks>
/// The caller is the fetcher host, authenticated by the internal listener's token scheme, not a user: the message is
/// anonymous in the user sense and carries no tenant.
/// </remarks>
/// <param name="WorkerId">The fetcher instance that takes the lease.</param>
/// <param name="Max">How many tasks it wants, 1 to 4.</param>
[NotAudited("A machine-to-machine queue claim every few seconds; it has no actor, and the task row records the lease.")]
[AllowAnonymousRequest]
public sealed record LeaseFetchTasksCommand(string? WorkerId, int Max) : ICommand<ErrorOr<IReadOnlyList<LeaseDto>>>
{
    /// <summary>The most tasks one call may ask for; a bigger value is a 400 so a buggy client is visible.</summary>
    public const int MaxBatch = 4;
}
