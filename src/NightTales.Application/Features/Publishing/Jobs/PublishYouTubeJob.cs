using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Features.Publishing.Upload;
using NightTales.Domain.Publishing;

namespace NightTales.Application.Features.Publishing.Jobs;

/// <summary>
/// Uploads one publication to YouTube (06.2.3-T2 plan §Design "Job and command", 06 README D12): <c>publish</c> queue,
/// five retries, one id argument, tenant scope pinned from the row's owner before anything else is resolved.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ids only.</b> The queue payload carries the publication id and nothing else, so no title, description, channel
/// identity or token can reach Hangfire's own storage (D12, D17). The owner is resolved through the allow-listed
/// cross-user read and the run happens in a scope pinned with <c>ISystemContext.For(owner)</c>, so every read the
/// handler makes is the owner's own.
/// </para>
/// <para>
/// <b>What is swallowed and what is retried.</b> Every <c>ErrorOr</c> the handler answers is a verdict it already wrote
/// down — a deleted row, a disabled flag, a missing consent, a spent quota, a failed upload — and must never be retried
/// five times. The one statement that means "run me again" is <see cref="PublishAttemptException"/> (Google answered
/// nothing, or the session has to be re-probed), which is rethrown with its code-only message so Hangfire's attempt
/// counter decides; anything else is logged by type and replaced by a code-only exception, because a provider message
/// can carry a session URI or a row's values.
/// </para>
/// </remarks>
/// <param name="systemWork">The allow-listed read that resolves the row's owner (09 README D10).</param>
/// <param name="scopes">Creates the per-attempt scope the pinned services are resolved from.</param>
/// <param name="logger">The Worker's logger; ids, codes and outcomes only.</param>
[JobPolicy(Queue = JobQueue.Publish, Retries = 5)]
public sealed partial class PublishYouTubeJob(
    ISystemWorkQuery systemWork,
    IServiceScopeFactory scopes,
    ILogger<PublishYouTubeJob> logger) : IBackgroundJob
{
    /// <summary>Runs one upload attempt for <paramref name="publicationId"/>.</summary>
    /// <param name="publicationId">The <c>publications</c> row to upload (the only argument, D12).</param>
    /// <param name="cancellationToken">The scheduler's token for this attempt.</param>
    /// <returns>A task that completes when the attempt ended with a verdict, or throws to ask for another attempt.</returns>
    /// <exception cref="PublishAttemptException">
    /// The attempt failed in a way another run may fix (a transport failure, a session that needs a fresh probe).
    /// </exception>
    public async Task RunAsync(Guid publicationId, CancellationToken cancellationToken)
    {
        Guid? owner = null;

        await foreach (var (_, userId) in systemWork
            .PendingAsync<Publication>(publication => publication.Id == publicationId, 1, cancellationToken)
            .ConfigureAwait(false))
        {
            owner = userId;
        }

        if (owner is not { } ownerId)
        {
            // A row that is gone (or belongs to nobody this port can see) has no upload left to run.
            LogPublicationMissing(publicationId);

            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(ownerId);

        // Resolved after For(): the tenant filter of the context is fixed at construction (D10). The handler is called
        // directly instead of through ISender so AuthorizationBehavior (deny-by-default, built for HTTP callers) never
        // sees a job-dispatched message (`BibleProposalHandoffJob` precedent, 09 README D10).
        var handler = scope.ServiceProvider.GetRequiredService<PublishYouTubeHandler>();

        try
        {
            var result = await handler
                .Handle(new PublishYouTubeCommand(publicationId), cancellationToken)
                .ConfigureAwait(false);

            if (result.IsError)
            {
                LogDecided(publicationId, result.FirstError.Code);

                return;
            }

            var outcome = result.Value.ToString();
            LogOutcome(publicationId, outcome);
        }
        catch (OperationCanceledException)
        {
            // A shutting-down Worker is not a verdict: the row keeps `uploading` and the next delivery resumes it.
            throw;
        }
        catch (PublishAttemptException retry)
        {
            LogAttemptFailed(publicationId, retry.Code, exceptionType: null);

            throw;
        }
        catch (Exception exception)
        {
            // The type only: a provider or HttpClient message can carry a request URI (the session URI is a capability)
            // or a row's values, and the wrapped exception would put it in Hangfire's dashboard (06 README D17).
            LogAttemptFailed(publicationId, PublishAttemptException.InfrastructureCode, exception.GetType().FullName);

            throw new PublishAttemptException(PublishAttemptException.InfrastructureCode);
        }
    }

    [LoggerMessage(
        EventId = 6637,
        Level = LogLevel.Warning,
        Message = "publish_youtube_job_publication_missing publicationId={PublicationId}")]
    private partial void LogPublicationMissing(Guid publicationId);

    [LoggerMessage(
        EventId = 6638,
        Level = LogLevel.Information,
        Message = "publish_youtube_job_decided publicationId={PublicationId} code={Code}")]
    private partial void LogDecided(Guid publicationId, string code);

    [LoggerMessage(
        EventId = 6639,
        Level = LogLevel.Information,
        Message = "publish_youtube_job_outcome publicationId={PublicationId} outcome={Outcome}")]
    private partial void LogOutcome(Guid publicationId, string outcome);

    [LoggerMessage(
        EventId = 6640,
        Level = LogLevel.Warning,
        Message = "publish_youtube_job_attempt_failed publicationId={PublicationId} code={Code} exceptionType={ExceptionType}")]
    private partial void LogAttemptFailed(Guid publicationId, string code, string? exceptionType);
}
