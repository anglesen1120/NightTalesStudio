using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Messaging;

namespace NightTales.Application.Features.Research.Fetcher.Commands.HeartbeatFetchTask;

/// <summary>
/// <c>POST /internal/fetcher/v1/tasks/{taskId}/heartbeat</c> (02.5.2-T2, 02 D10): extend the lease of a task the
/// fetcher still holds.
/// </summary>
/// <param name="TaskId">The route value.</param>
/// <param name="WorkerId">The fetcher instance that holds the lease.</param>
[NotAudited("A machine-to-machine keep-alive every few seconds; it has no actor, and the task row records the lease.")]
[AllowAnonymousRequest]
public sealed record HeartbeatFetchTaskCommand(Guid TaskId, string? WorkerId) : ICommand<ErrorOr<DateTimeOffset>>;
