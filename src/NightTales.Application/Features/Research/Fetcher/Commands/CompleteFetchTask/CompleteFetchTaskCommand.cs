using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Fetching;

namespace NightTales.Application.Features.Research.Fetcher.Commands.CompleteFetchTask;

/// <summary>The <c>200</c> body of the result route (02.5.2-T2 plan §5.2): the task and its terminal status.</summary>
/// <param name="TaskId">The completed task.</param>
/// <param name="Status">Always <c>done</c>: a <c>failed</c> fetcher body is also a completed task as far as the lease is concerned.</param>
public sealed record FetchResultAck(Guid TaskId, string Status);

/// <summary>
/// <c>POST /internal/fetcher/v1/tasks/{taskId}/result</c> (02.5.2-T2, 02 D10): the fetcher's outcome for a leased task.
/// </summary>
/// <remarks>
/// The Api host runs this command pinned to the task's owner (<c>ISystemContext.For</c>, in a scope of its own), so the
/// tenant filter applies to the source, the task and the episode it touches. The audit trail is the source row itself
/// (<c>status</c>, <c>error_code</c>, <c>fetched_at</c>) and the <c>source_fetched.v1</c> outbox row.
/// </remarks>
/// <param name="TaskId">The route value.</param>
/// <param name="Body">The posted body; its <c>workerId</c> must hold the lease.</param>
[NotAudited("A machine-to-machine result; the story_sources row and the source_fetched.v1 outbox row record the outcome (02.5.2-T2).")]
public sealed record CompleteFetchTaskCommand(Guid TaskId, FetchResultRequest Body) : ICommand<ErrorOr<FetchResultAck>>;
