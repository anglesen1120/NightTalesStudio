using ErrorOr;
using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Fetching;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Research.Fetcher;
using NightTales.Application.Features.Research.Fetcher.Commands.CompleteFetchTask;
using NightTales.Application.Telemetry;
using NightTales.Domain.Research;

namespace NightTales.Api.Internal.Fetcher;

/// <summary>
/// Runs <see cref="CompleteFetchTaskCommand"/> as the owner of the task (02.5.2-T2). The internal listener has no user,
/// but the command touches tenant-filtered rows (the source, the task, the episode), so it runs in a DI scope pinned to
/// the task's owner with <see cref="ISystemContext.For"/> — exactly how the outbox dispatcher runs a job on a user's
/// behalf (09 README D10).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ISystemContext.For"/> must run before any <c>DbContext</c> of its scope exists, so the owner is read in a
/// scope of its own first. The owner id is only a routing hint: the handler locks the task and refuses unless the
/// calling worker holds a live lease on it, so naming someone else's task does nothing.
/// </para>
/// <para>
/// PERF-1: the text object is written here, <em>before</em> the command's transaction opens, so no object-store PUT
/// ever runs under the <c>fetch_tasks</c> row lock. Order: lease peek (no lock, 404/409) → body validation (400) →
/// object write under a deadline of its own → the command (lock, re-check, row updates, commit). The key is
/// content-addressed, so a retry rewrites the same object; a lease lost after the write leaves an orphan object
/// (task Failure handling) and answers 409.
/// </para>
/// <para>
/// QA-9: the instruments are emitted here, after the command returned success — that is, after the commit.
/// </para>
/// </remarks>
/// <param name="scopes">The root scope factory.</param>
public sealed class FetcherResultRunner(IServiceScopeFactory scopes)
{
    /// <summary>
    /// The deadline of the object write, independent of the request's abort token. Its expiry answers
    /// <c>503 service_unavailable</c> and leaves the task leased.
    /// </summary>
    public TimeSpan TextWriteDeadline { get; init; } = TimeSpan.FromSeconds(15);

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
        Guid userId;
        string? storedKey = null;

        await using (var peek = scopes.CreateAsyncScope())
        {
            var provider = peek.ServiceProvider;
            var leases = provider.GetRequiredService<IFetchTaskLeaseStore>();

            var owner = await leases.FindOwnerAsync(taskId, cancellationToken).ConfigureAwait(false);
            if (owner.IsError)
            {
                return owner.Errors;
            }

            userId = owner.Value;

            // A body the command's own validator rejects (a bad workerId) goes straight to the pipeline, which answers
            // the 400 before any lease is looked at, exactly as before.
            var command = new CompleteFetchTaskCommand(taskId, body);
            if (provider.GetRequiredService<IValidator<CompleteFetchTaskCommand>>().Validate(command).IsValid)
            {
                var claimed = await leases.PeekForResultAsync(taskId, body.WorkerId!, cancellationToken).ConfigureAwait(false);
                if (claimed.IsError)
                {
                    return claimed.Errors;
                }

                var peeked = claimed.Value;

                var parsed = FetchTaskResultValidator.Interpret(
                    peeked.Kind,
                    body,
                    provider.GetRequiredService<FetchResultLimits>(),
                    provider.GetRequiredService<IPublicSuffixList>(),
                    provider.GetRequiredService<DomainBlocklist>());
                if (parsed.IsError)
                {
                    return parsed.Errors;
                }

                var interpreted = parsed.Value;
                if (interpreted is FetchedResult fetched
                    && peeked.Kind == FetchTaskKind.FetchSource
                    && peeked.SourceId is { } sourceId)
                {
                    // The handler refuses a source that is no longer pending (409) or gone (404) after taking the
                    // lock; checking it here too keeps that refusal from costing an object write.
                    var sources = await provider.GetRequiredService<IAppDbContext>().Database
                        .SqlQuery<string>($"SELECT status AS \"Value\" FROM story_sources WHERE id = {sourceId}")
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (sources.Count == 0)
                    {
                        return FetcherErrors.TaskNotFound;
                    }

                    if (!string.Equals(sources[0], "pending", StringComparison.Ordinal))
                    {
                        return FetcherErrors.LeaseLost;
                    }

                    var key = await WriteTextAsync(
                        provider.GetRequiredService<IExtractedTextStore>(),
                        peeked,
                        sourceId,
                        fetched,
                        cancellationToken).ConfigureAwait(false);
                    if (key.IsError)
                    {
                        return key.Errors;
                    }

                    storedKey = key.Value;
                }
            }
        }

        await using var scope = scopes.CreateAsyncScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(userId);

        var result = await scope.ServiceProvider
            .GetRequiredService<ISender>()
            .Send(new CompleteFetchTaskCommand(taskId, body, storedKey), cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsError && result.Value.Telemetry is { } telemetry)
        {
            var provider = scope.ServiceProvider;
            provider.GetRequiredService<NightTalesMetrics>().RecordFetchTask(telemetry.Status, telemetry.ErrorCode);
            if (telemetry.DurationSeconds is { } seconds)
            {
                provider.GetRequiredService<NightTalesMetrics>().RecordFetchDuration(seconds);
            }
        }

        return result;
    }

    /// <summary>Writes the text object under <see cref="TextWriteDeadline"/>; the caller's own cancellation still propagates.</summary>
    private async Task<ErrorOr<string>> WriteTextAsync(
        IExtractedTextStore texts,
        ClaimedFetchTask task,
        Guid sourceId,
        FetchedResult fetched,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TextWriteDeadline);

        try
        {
            return await texts
                .WriteAsync(task.UserId, task.EpisodeId, sourceId, fetched.Sha256, fetched.Text, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our deadline, not the fetcher's: the documented 503, and the task stays leased for a retry.
            return Error.Failure(ErrorCodes.ServiceUnavailable, "The service is temporarily unavailable; retry.");
        }
    }
}
