using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Compliance.Deletion;

namespace NightTales.Infrastructure.Compliance.Deletion.Handlers;

/// <summary>
/// The <c>episode</c> scope (13.3.1 plan §3.1, 02 CR-22): deletes every object of the episode — the keys of its
/// <c>assets</c> rows plus everything under <c>research/{userId}/{episodeId}/</c> (the extracted-text objects, which
/// have no row of their own to enumerate) — then removes the episode row so its versions, runs, assets and
/// moderation events cascade. <c>usage_records</c> survive (their <c>episode_id</c> is set null, 13 §5.4). It never
/// writes <c>assets.deleted_at</c>.
/// </summary>
/// <remarks>
/// Idempotent: a missing key is a successful delete, and an episode already gone is a no-op that still sweeps the
/// prefix, so a re-run after a crash or a restore ends in the same state with zero further deletes.
/// </remarks>
public sealed partial class EpisodeDeletionScopeHandler(
    IAppDbContext database,
    IBlobStore blobs,
    IOptions<StorageSettings> storage,
    ILogger<EpisodeDeletionScopeHandler> logger) : IDeletionScopeHandler
{
    /// <summary>How many swept keys are buffered before they are deleted.</summary>
    public const int SweepChunkSize = 500;

    /// <summary>The key prefix of the extracted-text objects of an episode.</summary>
    public static string ResearchPrefix(Guid userId, Guid episodeId) => $"research/{userId:D}/{episodeId:D}/";

    /// <inheritdoc />
    public DeletionScope Scope => DeletionScope.Episode;

    /// <inheritdoc />
    public async Task<ErrorOr<DeletionResult>> ExecuteAsync(DeletionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var episodeId = request.TargetId ?? throw new InvalidOperationException("An episode request carries a target id.");
        var settings = storage.Value;

        var assetKeys = await database.Assets
            .Where(asset => asset.EpisodeId == episodeId)
            .Select(asset => new { asset.Kind, asset.StorageKey })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var objectsDeleted = 0;

        foreach (var asset in assetKeys)
        {
            var deleted = await blobs
                .DeleteAsync(BlobLocator.BucketFor(asset.Kind, settings), asset.StorageKey, cancellationToken)
                .ConfigureAwait(false);

            if (deleted.IsError)
            {
                return deleted.Errors;
            }

            objectsDeleted++;
        }

        // PERF-2: the sweep deletes in bounded chunks as the listing streams, so memory stays O(SweepChunkSize) however
        // many objects the prefix holds. A key listed after an earlier chunk's delete is unaffected (S3 continuation
        // tokens page by key order), and a key already removed above is skipped.
        var assetKeySet = assetKeys.Select(asset => asset.StorageKey).ToHashSet(StringComparer.Ordinal);
        var chunk = new List<string>(SweepChunkSize);

        var enumerator = blobs
            .ListKeysAsync(settings.Buckets.Media, ResearchPrefix(request.UserId, episodeId), cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        try
        {
            while (true)
            {
                bool hasNext;

                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    var failedType = exception.GetType().Name;
                    LogListFailed(failedType, request.Id);

                    return Error.Failure(code: ErrorCodes.ServiceUnavailable, description: "The object store could not be listed.");
                }

                if (hasNext && !assetKeySet.Contains(enumerator.Current))
                {
                    chunk.Add(enumerator.Current);
                }

                if (chunk.Count >= SweepChunkSize || (!hasNext && chunk.Count > 0))
                {
                    foreach (var key in chunk)
                    {
                        var deleted = await blobs.DeleteAsync(settings.Buckets.Media, key, cancellationToken).ConfigureAwait(false);
                        if (deleted.IsError)
                        {
                            return deleted.Errors;
                        }

                        objectsDeleted++;
                    }

                    chunk.Clear();
                }

                if (!hasNext)
                {
                    break;
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        var rows = await database.Episodes
            .Where(episode => episode.Id == episodeId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        if (rows == 0 && objectsDeleted == 0)
        {
            LogNothingLeft(request.Id);
        }

        return new DeletionResult(objectsDeleted, rows);
    }

    [LoggerMessage(EventId = 4320, Level = LogLevel.Warning, Message = "data_deletion_list_failed exception={ExceptionType} requestId={RequestId}")]
    private partial void LogListFailed(string exceptionType, Guid requestId);

    [LoggerMessage(EventId = 4321, Level = LogLevel.Information, Message = "data_deletion_episode_already_gone requestId={RequestId}")]
    private partial void LogNothingLeft(Guid requestId);
}
