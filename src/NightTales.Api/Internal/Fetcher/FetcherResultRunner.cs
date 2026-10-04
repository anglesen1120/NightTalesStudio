using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Fetching;
using NightTales.Application.Features.Research.Fetcher;
using NightTales.Application.Features.Research.Fetcher.Commands.CompleteFetchTask;

namespace NightTales.Api.Internal.Fetcher;

/// <summary>
/// Runs <see cref="CompleteFetchTaskCommand"/> as the owner of the task (02.5.2-T2). The internal listener has no user,
/// but the command touches tenant-filtered rows (the source, the task, the episode), so it runs in a DI scope pinned to
/// the task's owner with <see cref="ISystemContext.For"/> — exactly how the outbox dispatcher runs a job on a user's
/// behalf (09 README D10).
/// </summary>
/// <remarks>
/// <see cref="ISystemContext.For"/> must run before any <c>DbContext</c> of its scope exists, so the owner is read in a
/// scope of its own first. The owner id is only a routing hint: the handler locks the task and refuses unless the
/// calling worker holds a live lease on it, so naming someone else's task does nothing.
/// </remarks>
/// <param name="scopes">The root scope factory.</param>
public sealed class FetcherResultRunner(IServiceScopeFactory scopes)
{
    /// <summary>Completes a task with the fetcher's result body.</summary>
    /// <param name="taskId">The task.</param>
    /// <param name="body">The posted body.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns>The acknowledgement, or the error to answer.</returns>
    public async Task<ErrorOr<FetchResultAck>> CompleteAsync(
        Guid taskId,
        FetchResultRequest body,
        CancellationToken cancellationToken)
    {
        Guid? owner;
        await using (var peek = scopes.CreateAsyncScope())
        {
            owner = await peek.ServiceProvider
                .GetRequiredService<IFetchTaskLeaseStore>()
                .FindOwnerAsync(taskId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (owner is not { } userId)
        {
            return FetcherErrors.TaskNotFound;
        }

        await using var scope = scopes.CreateAsyncScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(userId);

        return await scope.ServiceProvider
            .GetRequiredService<ISender>()
            .Send(new CompleteFetchTaskCommand(taskId, body), cancellationToken)
            .ConfigureAwait(false);
    }
}
