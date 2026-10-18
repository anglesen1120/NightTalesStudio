using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Rendering;
using NightTales.Application.Rendering.Quality;
using NightTales.Domain.Assets;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using NightTales.Domain.Rendering;
using NightTales.Domain.Rendering.Subtitles;
using NightTales.Domain.Series;
using NightTales.RenderNode.Contract;

namespace NightTales.Application.Features.Rendering.Jobs;

/// <summary>
/// Turns a succeeded adapter task into durable, reviewed video (09.6.3-T2, BD7, WT45/WT52/WT56): the output is
/// streamed into a private temp file and verified against the node's own <c>output.video</c> facts, the video and
/// the subtitle track are written to the media bucket, the <c>assets</c> rows and <c>final_video_asset_id</c> are
/// created idempotently, the <c>quality_report</c> verdict is stored, the episode moves to <c>video_review</c>,
/// <c>RenderSucceeded</c> is raised and only then is the adapter asked to release the task.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two short transactions, and never one long one (WT45).</b> Step 2 stamps the row <c>ingesting</c> and
/// releases the poller in its own transaction <em>before</em> the first byte is downloaded, so a crash between the
/// download and the commit leaves a row that no tick will claim (a retry resumes it and writes once). The final
/// transaction holds the <c>assets</c> rows, the verdict, the episode, the row, the reservation's settlement, the
/// outbox event and the audit row; the download and the bucket PUTs stay outside it, because the node can take
/// minutes and a stored object cannot be rolled back.
/// </para>
/// <para>
/// <b>The download is never buffered.</b> Each output streams into its own <see cref="IngestTempFile"/> through
/// <see cref="IRenderDownloadClient"/> and is uploaded from that file, so the Worker copies bytes and never holds a
/// video in memory, and both objects carry a SHA-256 compared with the one the node declared for them (H1: the
/// destination keeps partial data on every error, so nothing is committed until the bytes are verified).
/// </para>
/// <para>
/// <b>At-least-once.</b> The job is delivered again after a crash, so its first steps are guards: a row that is
/// already terminal, or whose video object is already recorded, is a logged no-op — no second download, no second
/// blob variant, no second <c>assets</c> row, no second outbox event. The keys are fixed by the render job id
/// (BD7/BD10), so even a retry that re-uploads overwrites the same object instead of adding a variant.
/// </para>
/// <para>
/// <b>Retryable versus terminal.</b> An output that does not verify, a cap breach, a store that is down or a node
/// that cannot answer yet is thrown, so the scheduler runs the job again and the download starts from scratch. On
/// the last allowed attempt it must not be thrown: the row would sit <c>ingesting</c> with no claim left, so the
/// ingest ends it itself through <see cref="IRenderFailureRecorder"/> — the D5 code, the retry decision, one refund
/// by reservation id, the audit row and <c>render_failed.v1</c> ([RULED — README §B10 WT52/WT28]). A verdict that
/// fails the quality policy is never retried (BD7's table) and a row the owner asked to cancel is final whatever
/// the code says (WT51): both are the recorder's decisions, not this job's.
/// </para>
/// <para>
/// <b>Tenant scope.</b> The row is resolved through the allow-listed <see cref="ISystemWorkQuery"/> read and
/// everything else runs under <c>ISystemContext.For(userId)</c>, so every read and write is the owner's (D10) and
/// the keys embed the user id (BD10). Nothing here logs a node URL, a key's contents, a byte of media or a blob
/// URL: an id, a key, a hash, a byte count and a code only.
/// </para>
/// </remarks>
/// <param name="systemWork">The allow-listed cross-user read that resolves the row's owner (D10).</param>
/// <param name="scopes">Creates the per-job scope the pinned services are resolved from.</param>
/// <param name="logger">The Worker's logger; every line carries ids, hashes and codes only.</param>
[JobPolicy(Queue = JobQueue.Render, Retries = 5)]
public sealed partial class IngestRenderOutputJob(
    ISystemWorkQuery systemWork,
    IServiceScopeFactory scopes,
    ILogger<IngestRenderOutputJob> logger) : IBackgroundJob
{
    /// <summary>The media type the node serves the assembled video as (05 D4).</summary>
    public const string VideoContentType = "video/mp4";

    /// <summary>The media type the node serves the subtitle track as (05 D4).</summary>
    public const string SubtitlesContentType = "application/x-subrip";

    /// <summary>The audit action of a stored render (step 8).</summary>
    public const string SucceededAction = "render.succeeded";

    /// <summary>The file extension of the video spool.</summary>
    private const string VideoExtension = "mp4";

    /// <summary>The file extension of the subtitle spool.</summary>
    private const string SubtitlesExtension = "srt";

    /// <summary>The cleanup step of the post-commit adapter call.</summary>
    private const string DeleteOperation = "delete";

    /// <summary>The adapter code of "the task is still busy", the one failure the cleanup retries once.</summary>
    private const string BusyCode = "adapter_task_busy";

    /// <summary>The catalogued code of a <c>storage_key</c> that already holds different bytes.</summary>
    private const string StorageKeyConflictCode = "asset_storage_key_conflict";

    /// <summary>
    /// The budget of a best-effort call that must not fail the ingest (the settlement, a cleanup delete): the
    /// job's own token may already be cancelled, and the ledger of a terminal transition is not skippable.
    /// </summary>
    private static readonly TimeSpan BestEffortTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The writer of the stored verdict: the node contract's own options, so the document is the exact
    /// <c>render-quality.v1</c> its readers expect.</summary>
    private static readonly JsonSerializerOptions ReportJson = new(RenderNodeJson.Strict);

    /// <summary>Runs the ingest of <paramref name="renderJobId"/>.</summary>
    /// <param name="renderJobId">The <c>render_jobs</c> row to ingest (the only argument, D12).</param>
    /// <param name="cancellationToken">The scheduler's token for this attempt.</param>
    /// <returns>A task that completes when the row is stored, ended, or left for another attempt.</returns>
    /// <exception cref="InvalidOperationException">
    /// The output could not be obtained, verified or stored while the job still has attempts left, or a deadline
    /// of this job's own graph was violated.
    /// </exception>
    public async Task RunAsync(Guid renderJobId, CancellationToken cancellationToken)
    {
        var ownerId = await ResolveOwnerAsync(renderJobId, cancellationToken).ConfigureAwait(false);
        if (ownerId is not { } owner)
        {
            LogJobMissing(renderJobId);

            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(owner);

        // Resolved only after For(): the tenant filter of the context is fixed at construction (D10).
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<IAppDbContext>();
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        var clock = services.GetRequiredService<IClock>();
        var render = services.GetRequiredService<RenderOptions>();
        var options = services.GetRequiredService<RenderIngestOptions>();
        var attempt = services.GetRequiredService<IJobAttempt>();
        var metrics = services.GetRequiredService<RenderMetrics>();
        var client = services.GetRequiredService<IRenderClient>();
        var downloads = services.GetRequiredService<IRenderDownloadClient>();
        var blobs = services.GetRequiredService<IBlobStore>();
        var storage = services.GetRequiredService<StorageSettings>();
        var policy = services.GetRequiredService<RenderQualityPolicy>();
        var recorder = services.GetRequiredService<IRenderFailureRecorder>();
        var hooks = services.GetRequiredService<IRenderTerminalHooks>();
        var ledger = services.GetRequiredService<ICreditLedger>();
        var audit = services.GetRequiredService<IAuditWriter>();

        var job = await db.RenderJobs
            .SingleOrDefaultAsync(candidate => candidate.Id == renderJobId, cancellationToken)
            .ConfigureAwait(false);

        if (job is null)
        {
            LogJobMissing(renderJobId);

            return;
        }

        // Step 1. At-least-once delivery: a row whose verdict is written never moves again, so a second run of
        // this job is a logged no-op (AC8).
        if (job.Status is RenderJobStatus.Succeeded or RenderJobStatus.Failed or RenderJobStatus.Cancelled)
        {
            var terminal = job.Status.ToString();
            LogTerminalNoOp(job.Id, terminal);

            return;
        }

        // A row the poller never handed over is not this job's: `queued` has no adapter task at all and the two
        // in-flight states belong to the poller's tick, which is what stamps `ingesting`.
        if (job.Status is RenderJobStatus.Queued)
        {
            var waiting = job.Status.ToString();
            LogNotIngesting(job.Id, waiting);

            return;
        }

        var bucket = BlobLocator.BucketFor(AssetKind.Video, storage);
        var videoKey = Key(job, "video", VideoExtension);
        var subtitlesKey = Key(job, "subtitles", SubtitlesExtension);

        if (await db.Assets
                .AnyAsync(asset => asset.StorageKey == videoKey, cancellationToken)
                .ConfigureAwait(false))
        {
            // The video of this row is already stored, so this delivery lost the race with one that committed:
            // the ingest is done and this run must not write a second asset or upload a second object.
            LogAlreadyIngested(job.Id, videoKey);

            return;
        }

        var now = clock.UtcNow;
        var startedAt = job.StartedAt ?? job.SubmittedAt ?? now;
        var stage = RenderStageWire.ForMetric(job.Stage);

        // Step 2 (WT45). The row leaves the poller's claim in its own short transaction, before a single byte is
        // downloaded: a tick that re-claimed it could move the row while the output is streamed. The row is
        // already `ingesting` when the poller enqueued this job, so on a re-run this writes nothing — but it runs
        // through the same beginning-committed-before-the-download order the first run proves.
        if (job.Status != RenderJobStatus.Ingesting)
        {
            await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

            var stamped = job.MarkIngesting(now);
            if (stamped.IsError)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                throw new InvalidOperationException(
                    $"Moving job '{job.Id}' to ingesting failed ('{stamped.FirstError.Code}').");
            }

            await CommitAsync(unitOfWork, job.Id, cancellationToken).ConfigureAwait(false);
        }

        // Only the poller's MarkSubmitted writes both together, so a row without them was written by hand:
        // nothing can be downloaded and the row must not stay claimed for a tick that will never come, so it ends
        // here through the recorder instead of stranding (WT52).
        if (job.AdapterTaskId is not { Length: > 0 } taskId || job.WorkerNodeId is not { } nodeId)
        {
            LogNotSubmitted(job.Id);

            await FailAsync(
                job,
                RenderOutcomeCodes.RenderNodeError,
                RenderMetrics.OutcomeFailed,
                stage,
                startedAt,
                now,
                recorder,
                metrics,
                unitOfWork,
                cancellationToken).ConfigureAwait(false);

            return;
        }

        var node = await db.WorkerNodes
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == nodeId, cancellationToken)
            .ConfigureAwait(false);

        if (node is null)
        {
            LogNodeMissing(job.Id, nodeId);

            await FailAsync(
                job,
                RenderOutcomeCodes.RenderNodeError,
                RenderMetrics.OutcomeFailed,
                stage,
                startedAt,
                now,
                recorder,
                metrics,
                unitOfWork,
                cancellationToken).ConfigureAwait(false);

            return;
        }

        // Step 4. The node's own answer. The poller stored the terminal transition from this body, and the ingest
        // re-reads it because the facts the download is verified against (`output.video.bytes`/`sha256`) live in
        // the body and are deliberately not copied into the row.
        var status = await client.GetStatusAsync(node, taskId, cancellationToken).ConfigureAwait(false);
        if (status.IsError)
        {
            await StatusFailedAsync(
                status.FirstError,
                job,
                stage,
                startedAt,
                now,
                recorder,
                metrics,
                unitOfWork,
                cancellationToken).ConfigureAwait(false);

            return;
        }

        var body = status.Value;

        if (!string.Equals(
                body.TaskId.ToString("D", CultureInfo.InvariantCulture),
                taskId,
                StringComparison.Ordinal))
        {
            // A body about another task is a task this row no longer owns: a lost render, which the retry policy
            // owns (the poller's H2 rule, applied to the ingest's own read).
            LogTaskMismatch(job.Id, taskId, body.TaskId);

            await FailAsync(
                job,
                RenderOutcomeCodes.RenderLost,
                RenderMetrics.OutcomeFailed,
                stage,
                startedAt,
                now,
                recorder,
                metrics,
                unitOfWork,
                cancellationToken).ConfigureAwait(false);

            return;
        }

        if (!string.Equals(body.State, RenderStates.Succeeded, StringComparison.Ordinal) || body.Output is not { } output)
        {
            // The row is `ingesting` because the poller read `succeeded`; a body without that state or without an
            // `output` block carries no output this ingest can store (05 D10, H1).
            LogOutputMissing(job.Id, body.State);

            await RetryOrFailAsync(
                job,
                RenderOutcomeCodes.RenderOutputInvalid,
                stage,
                startedAt,
                now,
                attempt.IsLastAttempt,
                recorder,
                metrics,
                unitOfWork,
                cancellationToken).ConfigureAwait(false);

            return;
        }

        var written = new List<string>(2);
        string? subtitlesSha = null;
        long subtitlesBytes = 0;
        IngestTempFile? video = null;
        IngestTempFile? subtitles = null;

        try
        {
            video = IngestTempFile.Create(options.TempDirectory, job.Id, VideoExtension);

            var downloaded = await downloads
                .DownloadAsync(node, taskId, RenderOutputKind.Video, video.Stream, render.MaxVideoBytes, cancellationToken)
                .ConfigureAwait(false);

            if (downloaded.IsError)
            {
                await DownloadFailedAsync(
                    downloaded.FirstError,
                    job,
                    stage,
                    startedAt,
                    now,
                    attempt.IsLastAttempt,
                    recorder,
                    metrics,
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false);

                return;
            }

            if (Invalid(downloaded.Value, output.Video, VideoContentType) is { } videoProblem)
            {
                LogOutputInvalid(job.Id, nameof(RenderOutputKind.Video), videoProblem);

                await RetryOrFailAsync(
                    job,
                    RenderOutcomeCodes.RenderOutputInvalid,
                    stage,
                    startedAt,
                    now,
                    attempt.IsLastAttempt,
                    recorder,
                    metrics,
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false);

                return;
            }

            // The subtitle track is optional on both ends: the node may name none, and a node that has none
            // answers `adapter_output_absent`, which leaves the video alone (05 D10) and is never a failure.
            if (output.Subtitles is { } declaredTrack)
            {
                subtitles = IngestTempFile.Create(options.TempDirectory, job.Id, SubtitlesExtension);

                var track = await downloads
                    .DownloadAsync(
                        node,
                        taskId,
                        RenderOutputKind.Subtitles,
                        subtitles.Stream,
                        render.MaxSubtitleBytes,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (track.IsError)
                {
                    if (FailureOf(track.FirstError) is { Kind: RenderClientFailureKind.OutputAbsent })
                    {
                        LogSubtitlesAbsent(job.Id);

                        subtitles.Dispose();
                        subtitles = null;
                    }
                    else
                    {
                        await DownloadFailedAsync(
                            track.FirstError,
                            job,
                            stage,
                            startedAt,
                            now,
                            attempt.IsLastAttempt,
                            recorder,
                            metrics,
                            unitOfWork,
                            cancellationToken).ConfigureAwait(false);

                        return;
                    }
                }
                else if (Invalid(track.Value, declaredTrack, SubtitlesContentType) is { } trackProblem)
                {
                    LogOutputInvalid(job.Id, nameof(RenderOutputKind.Subtitles), trackProblem);

                    await RetryOrFailAsync(
                        job,
                        RenderOutcomeCodes.RenderOutputInvalid,
                        stage,
                        startedAt,
                        now,
                        attempt.IsLastAttempt,
                        recorder,
                        metrics,
                        unitOfWork,
                        cancellationToken).ConfigureAwait(false);

                    return;
                }
                else
                {
                    subtitlesSha = track.Value.Sha256;
                    subtitlesBytes = track.Value.Bytes;
                }
            }

            // Step 6. The objects, outside any transaction: the keys are fixed by the render job id, so a retry
            // overwrites the same two keys instead of adding a variant (BD7/BD10).
            var storedVideo = await blobs
                .PutAsync(bucket, videoKey, video.Rewind(), VideoContentType, cancellationToken)
                .ConfigureAwait(false);

            if (storedVideo.IsError || !HashMatches(storedVideo.Value.Sha256, downloaded.Value.Sha256))
            {
                LogStoreFailed(job.Id, videoKey, storedVideo.IsError ? storedVideo.FirstError.Code : Sha256Mismatch);

                await RetryOrFailAsync(
                    job,
                    RenderOutcomeCodes.RenderOutputInvalid,
                    stage,
                    startedAt,
                    now,
                    attempt.IsLastAttempt,
                    recorder,
                    metrics,
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false);

                return;
            }

            written.Add(videoKey);

            if (subtitles is not null && subtitlesSha is { } trackSha)
            {
                var storedTrack = await blobs
                    .PutAsync(bucket, subtitlesKey, subtitles.Rewind(), SubtitlesContentType, cancellationToken)
                    .ConfigureAwait(false);

                if (storedTrack.IsError || !HashMatches(storedTrack.Value.Sha256, trackSha))
                {
                    LogStoreFailed(job.Id, subtitlesKey, storedTrack.IsError ? storedTrack.FirstError.Code : Sha256Mismatch);

                    await RetryOrFailAsync(
                        job,
                        RenderOutcomeCodes.RenderOutputInvalid,
                        stage,
                        startedAt,
                        now,
                        attempt.IsLastAttempt,
                        recorder,
                        metrics,
                        unitOfWork,
                        cancellationToken).ConfigureAwait(false);

                    return;
                }

                written.Add(subtitlesKey);
            }

            // Steps 7-9. One short transaction decides the attempt: the verdict (with the 03.4.2-T1 `images` block
            // merged inside it), the `assets` rows, the episode, the reservation, the row, the outbox event and the
            // audit row commit together, or nothing does.
            var report = RenderQualityEvaluator.Evaluate(CheckOf(body.Check), ExpectationsOf(job), policy);

            var episode = await db.Episodes
                .SingleOrDefaultAsync(candidate => candidate.Id == job.EpisodeId, cancellationToken)
                .ConfigureAwait(false);

            if (episode is null)
            {
                LogEpisodeMissing(job.Id, job.EpisodeId);

                await DeleteBlobsAsync(blobs, bucket, written, job.Id, cancellationToken).ConfigureAwait(false);

                await RetryOrFailAsync(
                    job,
                    RenderOutcomeCodes.RenderNodeError,
                    stage,
                    startedAt,
                    now,
                    attempt.IsLastAttempt,
                    recorder,
                    metrics,
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false);

                return;
            }

            await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

            var images = await hooks.MergeImagesAsync(job, body, cancellationToken).ConfigureAwait(false);
            var merged = report with { Images = images };

            var reported = job.RecordQualityReport(JsonSerializer.Serialize(merged, ReportJson));
            if (reported.IsError)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                throw new InvalidOperationException(
                    $"Recording the verdict of job '{job.Id}' failed ('{reported.FirstError.Code}').");
            }

            if (!merged.Pass)
            {
                // 05 D10/D7: a failing verdict is stored with the row that ends here and no asset is kept, so the
                // failure path is this same transaction — the recorder needs one open, and the report must commit
                // with the row it belongs to. The code is never retried (BD7's table) and a cancel-requested row is
                // final whatever the code says (WT51); both are the Recorder's decisions.
                var failed = await recorder
                    .FailAsync(job, RenderOutcomeCodes.RenderQualityFailed, outcomeRetryable: null, cancellationToken)
                    .ConfigureAwait(false);

                if (failed.IsError)
                {
                    await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    throw new InvalidOperationException(
                        $"Failing job '{job.Id}' as render_quality_failed failed ('{failed.FirstError.Code}').");
                }

                await CommitAsync(unitOfWork, job.Id, cancellationToken).ConfigureAwait(false);

                LogQualityFailed(job.Id, failed.Value.Final, failed.Value.RetryCreated);

                metrics.ObserveDuration(stage, RenderMetrics.OutcomeFailed, clock.UtcNow - startedAt);

                await DeleteBlobsAsync(blobs, bucket, written, job.Id, cancellationToken).ConfigureAwait(false);

                return;
            }

            var videoAsset = await AssetAsync(
                db,
                videoKey,
                downloaded.Value.Sha256,
                AssetKind.Video,
                VideoContentType,
                downloaded.Value.Bytes,
                DurationOf(output.NarrationSeconds),
                job,
                options,
                now,
                cancellationToken).ConfigureAwait(false);

            if (videoAsset.IsError)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                await DeleteBlobsAsync(blobs, bucket, written, job.Id, cancellationToken).ConfigureAwait(false);

                throw new InvalidOperationException(
                    $"Storing the video asset of job '{job.Id}' failed ('{videoAsset.FirstError.Code}').");
            }

            if (subtitlesSha is { } subtitleHash)
            {
                var subtitleAsset = await AssetAsync(
                    db,
                    subtitlesKey,
                    subtitleHash,
                    AssetKind.Subtitle,
                    SubtitlesContentType,
                    subtitlesBytes,
                    null,
                    job,
                    options,
                    now,
                    cancellationToken).ConfigureAwait(false);

                if (subtitleAsset.IsError)
                {
                    await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    await DeleteBlobsAsync(blobs, bucket, written, job.Id, cancellationToken).ConfigureAwait(false);

                    throw new InvalidOperationException(
                        $"Storing the subtitle asset of job '{job.Id}' failed ('{subtitleAsset.FirstError.Code}').");
                }
            }

            var attached = episode.AttachFinalVideo(videoAsset.Value, now);
            if (attached.IsError)
            {
                // The episode moved out of `rendering` behind this attempt (a cancel, an owner action): the output
                // is real but there is no episode to attach it to, so the attempt ends through the recorder.
                LogEpisodeStateRefused(job.Id, job.EpisodeId, attached.FirstError.Code);

                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                await DeleteBlobsAsync(blobs, bucket, written, job.Id, cancellationToken).ConfigureAwait(false);

                await RetryOrFailAsync(
                    job,
                    RenderOutcomeCodes.RenderNodeError,
                    stage,
                    startedAt,
                    now,
                    attempt.IsLastAttempt,
                    recorder,
                    metrics,
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false);

                return;
            }

            var reviewed = episode.RenderSucceeded(now);
            if (reviewed.IsError)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                throw new InvalidOperationException(
                    $"Moving episode '{job.EpisodeId}' to video review failed ('{reviewed.FirstError.Code}').");
            }

            // Exactly once, by reservation id, before the row becomes terminal — the same order the failure
            // recorder uses for its refund (09.6.3-T1).
            await SettleAsync(ledger, job, cancellationToken).ConfigureAwait(false);

            var succeeded = job.Succeed(now);
            if (succeeded.IsError)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                throw new InvalidOperationException(
                    $"Succeeding job '{job.Id}' failed ('{succeeded.FirstError.Code}').");
            }

            await hooks.RecordKeptVideoAsync(job, body, cancellationToken).ConfigureAwait(false);

            var raised = job.RaiseSucceeded();
            if (raised.IsError)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                throw new InvalidOperationException(
                    $"Raising render_succeeded.v1 for job '{job.Id}' failed ('{raised.FirstError.Code}').");
            }

            await audit
                .WriteAsync(
                    new AuditEntry(
                        ActorUserId: null,
                        ActorType.System,
                        SubjectUserId: job.UserId,
                        Action: SucceededAction,
                        Category.Episodes,
                        Visibility.User,
                        EntityType: "render_job",
                        EntityId: job.Id,
                        DataJson: AuditData(job, videoAsset.Value, downloaded.Value.Sha256),
                        Ip: (IPAddress?)null,
                        UserAgent: null,
                        ActorLabel: null,
                        OccurredAt: now),
                    cancellationToken)
                .ConfigureAwait(false);

            await CommitAsync(unitOfWork, job.Id, cancellationToken).ConfigureAwait(false);

            LogIngested(job.Id, job.EpisodeId, videoAsset.Value, downloaded.Value.Bytes);

            metrics.ObserveDuration(stage, RenderMetrics.OutcomeSucceeded, clock.UtcNow - startedAt);

            // Step 10. Only now, with the verdict committed, is the adapter asked to release the task; a failure
            // here never fails an ingest whose output is already durable, and it deletes no blob.
            await DeleteAdapterTaskAsync(client, node, taskId, job.Id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            video?.Dispose();
            subtitles?.Dispose();
        }
    }

    /// <summary>Resolves the owner of the row through the allow-listed cross-user read (D10).</summary>
    /// <param name="renderJobId">The row to resolve.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The owner, or <c>null</c> when no row has that id.</returns>
    private async Task<Guid?> ResolveOwnerAsync(Guid renderJobId, CancellationToken cancellationToken)
    {
        await foreach (var row in systemWork
                           .PendingAsync<RenderJob>(job => job.Id == renderJobId, batch: 1, cancellationToken)
                           .ConfigureAwait(false))
        {
            return row.UserId;
        }

        return null;
    }

    /// <summary>Commits the work of one transaction, or throws so the attempt is not silently half-applied.</summary>
    /// <param name="unitOfWork">The open transaction.</param>
    /// <param name="renderJobId">The row the transaction belongs to (for the message).</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A task that completes once the work is committed.</returns>
    private static async Task CommitAsync(IUnitOfWork unitOfWork, Guid renderJobId, CancellationToken cancellationToken)
    {
        var committed = await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (committed.IsError)
        {
            throw new InvalidOperationException(
                $"Committing the ingest of job '{renderJobId}' failed ('{committed.FirstError.Code}').");
        }
    }

    /// <summary>
    /// Ends the attempt inside the job, in its own transaction, through the recorder (the D5 code, the retry
    /// decision, one refund by reservation id, the audit row and <c>render_failed.v1</c> at once).
    /// </summary>
    /// <param name="job">The tracked row.</param>
    /// <param name="code">The D5 outcome code the attempt failed with.</param>
    /// <param name="metricOutcome">The <c>render_duration_seconds</c> outcome tag.</param>
    /// <param name="stage">The <c>stage</c> tag of the histogram.</param>
    /// <param name="startedAt">When the attempt started.</param>
    /// <param name="now">The clock reading the terminal transition is stamped with.</param>
    /// <param name="recorder">The terminal-failure recorder of 09.6.3-T1.</param>
    /// <param name="metrics">The render instruments.</param>
    /// <param name="unitOfWork">The unit of work the recorder writes in.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A task that completes once the row is terminal.</returns>
    private async Task FailAsync(
        RenderJob job,
        string code,
        string metricOutcome,
        string stage,
        DateTimeOffset startedAt,
        DateTimeOffset now,
        IRenderFailureRecorder recorder,
        RenderMetrics metrics,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        var failed = await recorder
            .FailAsync(job, code, outcomeRetryable: null, cancellationToken)
            .ConfigureAwait(false);

        if (failed.IsError)
        {
            await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

            throw new InvalidOperationException(
                $"Ending job '{job.Id}' as '{code}' failed ('{failed.FirstError.Code}').");
        }

        await CommitAsync(unitOfWork, job.Id, cancellationToken).ConfigureAwait(false);

        LogFailed(job.Id, code, failed.Value.Final, failed.Value.RetryCreated);

        metrics.ObserveDuration(stage, metricOutcome, now - startedAt);

        if (failed.Value.RetryCreated)
        {
            metrics.CountRetry(code);
        }
    }

    /// <summary>
    /// Throws when the job still has an attempt left (the scheduler downloads again from scratch) and ends the row
    /// in-job on the last one (WT52: nothing may be left <c>ingesting</c> with no claim).
    /// </summary>
    /// <param name="job">The tracked row.</param>
    /// <param name="code">The D5 outcome code of the failure.</param>
    /// <param name="stage">The <c>stage</c> tag of the histogram.</param>
    /// <param name="startedAt">When the attempt started.</param>
    /// <param name="now">The clock reading of this attempt.</param>
    /// <param name="lastAttempt">Whether the scheduler has no attempt left (the attempt port's verdict).</param>
    /// <param name="recorder">The terminal-failure recorder.</param>
    /// <param name="metrics">The render instruments.</param>
    /// <param name="unitOfWork">The unit of work the recorder writes in.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A task that completes when the row is either terminal or left for the next attempt.</returns>
    private async Task RetryOrFailAsync(
        RenderJob job,
        string code,
        string stage,
        DateTimeOffset startedAt,
        DateTimeOffset now,
        bool lastAttempt,
        IRenderFailureRecorder recorder,
        RenderMetrics metrics,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        if (!lastAttempt)
        {
            throw new InvalidOperationException(
                $"The output of job '{job.Id}' could not be stored ('{code}'); the job's next attempt downloads it again.");
        }

        await FailAsync(
            job,
            code,
            RenderMetrics.OutcomeFailed,
            stage,
            startedAt,
            now,
            recorder,
            metrics,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Classifies a failed status call: the deferred and not-ready kinds are retried, the rest end the
    /// attempt with the code the failure carries (or the D5 default of its kind).</summary>
    /// <param name="error">The mapped failure of the status call.</param>
    /// <param name="job">The tracked row.</param>
    /// <param name="stage">The <c>stage</c> tag of the histogram.</param>
    /// <param name="startedAt">When the attempt started.</param>
    /// <param name="now">The clock reading of this attempt.</param>
    /// <param name="recorder">The terminal-failure recorder.</param>
    /// <param name="metrics">The render instruments.</param>
    /// <param name="unitOfWork">The unit of work the recorder writes in.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A task that completes once the attempt is ended or thrown.</returns>
    private async Task StatusFailedAsync(
        Error error,
        RenderJob job,
        string stage,
        DateTimeOffset startedAt,
        DateTimeOffset now,
        IRenderFailureRecorder recorder,
        RenderMetrics metrics,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        var failure = FailureOf(error);

        if (failure is { Kind: RenderClientFailureKind.Deferred or RenderClientFailureKind.OutputNotReady })
        {
            throw new InvalidOperationException(
                $"The status call of job '{job.Id}' was '{failure.Kind}'; the next attempt reads it again.");
        }

        var code = failure?.OutcomeCode
                   ?? (failure is { Kind: RenderClientFailureKind.TerminalFailure }
                       ? RenderOutcomeCodes.RenderOutputInvalid
                       : RenderOutcomeCodes.RenderNodeError);

        LogStatusFailed(job.Id, code);

        await FailAsync(
            job,
            code,
            RenderMetrics.OutcomeFailed,
            stage,
            startedAt,
            now,
            recorder,
            metrics,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Classifies a failed download: the transient, deferred and not-ready kinds are retried, the rest end
    /// the attempt with the code the failure carries (or <c>render_output_invalid</c>).</summary>
    /// <param name="error">The mapped failure of the download.</param>
    /// <param name="job">The tracked row.</param>
    /// <param name="stage">The <c>stage</c> tag of the histogram.</param>
    /// <param name="startedAt">When the attempt started.</param>
    /// <param name="now">The clock reading of this attempt.</param>
    /// <param name="lastAttempt">Whether the scheduler has no attempt left.</param>
    /// <param name="recorder">The terminal-failure recorder.</param>
    /// <param name="metrics">The render instruments.</param>
    /// <param name="unitOfWork">The unit of work the recorder writes in.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A task that completes once the attempt is ended or thrown.</returns>
    private async Task DownloadFailedAsync(
        Error error,
        RenderJob job,
        string stage,
        DateTimeOffset startedAt,
        DateTimeOffset now,
        bool lastAttempt,
        IRenderFailureRecorder recorder,
        RenderMetrics metrics,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        var failure = FailureOf(error);

        if (failure is { Kind: RenderClientFailureKind.TransientFailure or RenderClientFailureKind.Deferred or RenderClientFailureKind.OutputNotReady })
        {
            var kind = failure.Kind.ToString();
            LogDownloadRetried(job.Id, kind);

            throw new InvalidOperationException(
                $"Downloading the output of job '{job.Id}' failed as '{failure.Kind}'; the next attempt takes it again.");
        }

        var code = failure?.OutcomeCode ?? RenderOutcomeCodes.RenderOutputInvalid;

        LogOutputInvalid(job.Id, "download", code);

        await RetryOrFailAsync(
            job,
            code,
            stage,
            startedAt,
            now,
            lastAttempt,
            recorder,
            metrics,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Settles the reservation of a stored render, once, by reservation id and with the ledger's own
    /// budget (the recorder's refund pattern).</summary>
    /// <param name="ledger">The credit ledger.</param>
    /// <param name="job">The row whose reservation is settled.</param>
    /// <param name="cancellationToken">The job's token; a cancelled one still gets the settlement.</param>
    /// <returns>A task that completes once the ledger answered or the budget ran out.</returns>
    private async Task SettleAsync(ICreditLedger ledger, RenderJob job, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(BestEffortTimeout);

        try
        {
            var settled = await ledger
                .SettleAsync(new ReservationId(job.ReservationId), budget.Token)
                .ConfigureAwait(false);

            if (settled.IsError)
            {
                LogSettleFailed(job.Id, settled.FirstError.Code);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || budget.IsCancellationRequested)
        {
            LogSettleFailed(job.Id, exception.GetType().Name);
        }
    }

    /// <summary>Deletes the objects this attempt uploaded before it turned out not to keep them, best effort: the
    /// verdict that just committed must not be undone by a bucket that answers late.</summary>
    /// <param name="blobs">The blob store.</param>
    /// <param name="bucket">The media bucket.</param>
    /// <param name="keys">The keys uploaded in this attempt.</param>
    /// <param name="renderJobId">The row the keys belong to (for the log line).</param>
    /// <param name="cancellationToken">The job's token.</param>
    /// <returns>A task that completes once every delete was attempted.</returns>
    private async Task DeleteBlobsAsync(
        IBlobStore blobs,
        string bucket,
        IReadOnlyList<string> keys,
        Guid renderJobId,
        CancellationToken cancellationToken)
    {
        foreach (var key in keys)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(BestEffortTimeout);

            try
            {
                var deleted = await blobs.DeleteAsync(bucket, key, budget.Token).ConfigureAwait(false);

                if (deleted.IsError)
                {
                    LogBlobDeleteSkipped(renderJobId, key, deleted.FirstError.Code);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException || budget.IsCancellationRequested)
            {
                LogBlobDeleteSkipped(renderJobId, key, exception.GetType().Name);
            }
        }
    }

    /// <summary>Asks the adapter to release a task whose output is committed. A failure — including the one retry
    /// of <c>adapter_task_busy</c> — is logged and never fails the ingest and never deletes a blob (step 10).</summary>
    /// <param name="client">The render client.</param>
    /// <param name="node">The node the task was submitted to.</param>
    /// <param name="taskId">The adapter's task id.</param>
    /// <param name="renderJobId">The row the task belongs to.</param>
    /// <param name="cancellationToken">The job's token.</param>
    /// <returns>A task that completes once the cleanup was attempted.</returns>
    private async Task DeleteAdapterTaskAsync(
        IRenderClient client,
        WorkerNode node,
        string taskId,
        Guid renderJobId,
        CancellationToken cancellationToken)
    {
        var deleted = await BestEffortAsync(
                renderJobId,
                DeleteOperation,
                () => client.DeleteAsync(node, taskId, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);

        if (deleted is { IsError: false })
        {
            return;
        }

        if (deleted.FirstError.Code == BusyCode)
        {
            // The adapter is still finishing the task it already handed over: one more ask, and no more (BD7).
            LogCleanupRetried(renderJobId, BusyCode);

            var retried = await BestEffortAsync(
                    renderJobId,
                    DeleteOperation,
                    () => client.DeleteAsync(node, taskId, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);

            if (retried is { IsError: false })
            {
                return;
            }

            LogCleanupSkipped(renderJobId, DeleteOperation, retried.FirstError.Code);
        }
        else
        {
            LogCleanupSkipped(renderJobId, DeleteOperation, deleted.FirstError.Code);
        }
    }

    /// <summary>Runs a post-commit adapter call that must not fail the ingest, with its own budget.</summary>
    /// <param name="renderJobId">The row the call belongs to.</param>
    /// <param name="operation">The operation name for the log line.</param>
    /// <param name="call">The call itself.</param>
    /// <param name="cancellationToken">The job's token.</param>
    /// <returns>The call's typed answer.</returns>
    private async Task<ErrorOr<Success>> BestEffortAsync(
        Guid renderJobId,
        string operation,
        Func<Task<ErrorOr<Success>>> call,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(BestEffortTimeout);

        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || budget.IsCancellationRequested)
        {
            LogCleanupSkipped(renderJobId, operation, exception.GetType().Name);

            return Error.Failure("render_cleanup_failed", "The adapter cleanup did not answer.");
        }
    }

    /// <summary>Finds or records one asset row of this attempt: the row of a key that already holds the same bytes
    /// is reused (a re-run writes no duplicate), and a key that holds different bytes is a conflict to fail loudly.
    /// </summary>
    /// <param name="db">The tenant-scoped context.</param>
    /// <param name="storageKey">The object key the row names.</param>
    /// <param name="sha256">The verified hash of the bytes under that key.</param>
    /// <param name="kind">Video or subtitle.</param>
    /// <param name="contentType">The stored content type.</param>
    /// <param name="sizeBytes">The verified byte count.</param>
    /// <param name="durationSec">The length in seconds, when the adapter reported one.</param>
    /// <param name="job">The row being ingested.</param>
    /// <param name="options">The retention of an approved-later asset (<c>Assets:UnapprovedRetentionDays</c>).</param>
    /// <param name="now">The Worker's clock reading.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The row's id, or the typed error that refused it.</returns>
    private static async Task<ErrorOr<Guid>> AssetAsync(
        IAppDbContext db,
        string storageKey,
        string sha256,
        AssetKind kind,
        string contentType,
        long sizeBytes,
        decimal? durationSec,
        RenderJob job,
        RenderIngestOptions options,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await db.Assets
            .SingleOrDefaultAsync(asset => asset.StorageKey == storageKey, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            if (!HashMatches(existing.Sha256, sha256))
            {
                return Error.Conflict(
                    StorageKeyConflictCode,
                    "An assets row already names this storage key with different bytes.");
            }

            return existing.Id;
        }

        var created = Asset.Create(
            Guid.CreateVersion7(),
            job.UserId,
            job.EpisodeId,
            kind,
            storageKey,
            contentType,
            sizeBytes,
            durationSec,
            sha256,
            now + options.UnapprovedRetention,
            now);

        if (created.IsError)
        {
            return created.Errors;
        }

        db.Assets.Add(created.Value);

        return created.Value.Id;
    }

    /// <summary>The object key BD7 fixes for one output of a render (BD10's naming).</summary>
    /// <param name="job">The row being ingested.</param>
    /// <param name="name">The file name of the output.</param>
    /// <param name="extension">Its extension.</param>
    /// <returns>The key inside the media bucket.</returns>
    private static string Key(RenderJob job, string name, string extension) =>
        $"media/{job.UserId:D}/{job.EpisodeId:D}/{job.Id:D}/{name}.{extension}";

    /// <summary>Compares two SHA-256 values the way the contract spells them (lowercase hex, ordinal).</summary>
    /// <param name="left">One hash.</param>
    /// <param name="right">The other.</param>
    /// <returns><c>true</c> when they are the same value.</returns>
    private static bool HashMatches(string? left, string? right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    /// <summary>
    /// Verifies one downloaded output against the node's own report: the byte count, the SHA-256 and the media
    /// type (the node's header may carry parameters, so only the type before <c>;</c> is compared).
    /// </summary>
    /// <param name="download">What the client measured.</param>
    /// <param name="declared">What the node declared.</param>
    /// <param name="expectedContentType">The media type this output must be served as.</param>
    /// <returns><c>null</c> when everything matches, otherwise the reason code (never media content).</returns>
    private static string? Invalid(DownloadResult download, RenderFileV1 declared, string expectedContentType)
    {
        if (download.Bytes != declared.Bytes)
        {
            return BytesMismatch;
        }

        if (!HashMatches(download.Sha256, declared.Sha256))
        {
            return Sha256Mismatch;
        }

        if (!MediaTypeMatches(download.ContentType, expectedContentType))
        {
            return ContentTypeMismatch;
        }

        return null;
    }

    /// <summary>Compares a served media type with an expected one, ignoring its parameters and casing.</summary>
    /// <param name="reported">The media type the node served.</param>
    /// <param name="expected">The media type this output must be.</param>
    /// <returns><c>true</c> when they are the same type.</returns>
    private static bool MediaTypeMatches(string? reported, string expected)
    {
        if (string.IsNullOrWhiteSpace(reported))
        {
            return false;
        }

        var separator = reported.IndexOf(';', StringComparison.Ordinal);
        var mediaType = separator < 0 ? reported : reported[..separator];

        return string.Equals(mediaType.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads the measured <c>render-check.v1</c> of a status body, treating a block the contract refuses
    /// as absent so the evaluator fails closed (05 D10).</summary>
    /// <param name="check">The by-reference block of the status.</param>
    /// <returns>The parsed check, or <c>null</c>.</returns>
    private static RenderCheckV1? CheckOf(JsonElement? check)
    {
        if (check is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        try
        {
            return element.Deserialize<RenderCheckV1>(RenderNodeJson.Strict);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The expectations the verdict needs and the row carries (<c>render-params.v1</c>, 05 D11).</summary>
    /// <param name="job">The row being ingested.</param>
    /// <returns>The aspect, whether subtitles were requested and whether a track was selected.</returns>
    private static RenderExpectations ExpectationsOf(RenderJob job) => new(
        string.Equals(job.Params.Aspect, SubtitlePresetCatalog.PortraitAspect, StringComparison.Ordinal)
            ? Aspect.Portrait
            : Aspect.Landscape,
        job.Params.Subtitles.Enabled,
        job.Params.Music is not null);

    /// <summary>The video's length in seconds when the adapter reported one the column can hold.</summary>
    /// <param name="seconds">The adapter's <c>narration_seconds</c>.</param>
    /// <returns>The length, or <c>null</c>.</returns>
    private static decimal? DurationOf(double seconds) =>
        double.IsFinite(seconds) && seconds > 0 ? (decimal)seconds : null;

    /// <summary>The audit row's <c>data</c>: ids and the verified hash, never a byte of media (BD16).</summary>
    /// <param name="job">The row that succeeded.</param>
    /// <param name="videoAssetId">The stored video row.</param>
    /// <param name="sha256">The verified hash of the stored video.</param>
    /// <returns>The serialised <c>data</c> object.</returns>
    private static string AuditData(RenderJob job, Guid videoAssetId, string sha256)
    {
        var data = new JsonObject
        {
            ["episodeId"] = job.EpisodeId.ToString("D", CultureInfo.InvariantCulture),
            ["renderJobId"] = job.Id.ToString("D", CultureInfo.InvariantCulture),
            ["videoAssetId"] = videoAssetId.ToString("D", CultureInfo.InvariantCulture),
            ["videoSha256"] = sha256,
        };

        return data.ToJsonString();
    }

    /// <summary>Maps a typed error back to the render failure that travelled inside it.</summary>
    /// <param name="error">The error the client returned.</param>
    /// <returns>The failure, or <c>null</c> for an error no client mapped.</returns>
    private static RenderClientFailure? FailureOf(Error error) =>
        RenderClientErrors.TryGetFailure(error, out var failure) ? failure : null;

    /// <summary>The reason of a downloaded output whose byte count is not the one the node declared.</summary>
    private const string BytesMismatch = "bytes_mismatch";

    /// <summary>The reason of a downloaded output whose SHA-256 is not the one the node declared (H1).</summary>
    private const string Sha256Mismatch = "sha256_mismatch";

    /// <summary>The reason of an output served as a media type the contract does not allow.</summary>
    private const string ContentTypeMismatch = "content_type_mismatch";

    [LoggerMessage(EventId = 6300, Level = LogLevel.Information, Message = "render_ingest_job_missing renderJobId={RenderJobId}")]
    private partial void LogJobMissing(Guid renderJobId);

    [LoggerMessage(EventId = 6301, Level = LogLevel.Information, Message = "render_ingest_terminal_noop renderJobId={RenderJobId} status={Status}")]
    private partial void LogTerminalNoOp(Guid renderJobId, string status);

    [LoggerMessage(EventId = 6302, Level = LogLevel.Information, Message = "render_ingest_not_handed_over renderJobId={RenderJobId} status={Status}")]
    private partial void LogNotIngesting(Guid renderJobId, string status);

    [LoggerMessage(EventId = 6303, Level = LogLevel.Information, Message = "render_ingest_already_stored renderJobId={RenderJobId} storageKey={StorageKey}")]
    private partial void LogAlreadyIngested(Guid renderJobId, string storageKey);

    [LoggerMessage(EventId = 6304, Level = LogLevel.Warning, Message = "render_ingest_not_submitted renderJobId={RenderJobId}")]
    private partial void LogNotSubmitted(Guid renderJobId);

    [LoggerMessage(EventId = 6305, Level = LogLevel.Warning, Message = "render_ingest_node_missing renderJobId={RenderJobId} nodeId={NodeId}")]
    private partial void LogNodeMissing(Guid renderJobId, Guid nodeId);

    [LoggerMessage(EventId = 6306, Level = LogLevel.Warning, Message = "render_ingest_task_mismatch renderJobId={RenderJobId} expectedTaskId={ExpectedTaskId} reportedTaskId={ReportedTaskId}")]
    private partial void LogTaskMismatch(Guid renderJobId, string expectedTaskId, Guid reportedTaskId);

    [LoggerMessage(EventId = 6307, Level = LogLevel.Warning, Message = "render_ingest_output_missing renderJobId={RenderJobId} state={State}")]
    private partial void LogOutputMissing(Guid renderJobId, string state);

    [LoggerMessage(EventId = 6308, Level = LogLevel.Warning, Message = "render_ingest_output_invalid renderJobId={RenderJobId} output={Output} reason={Reason}")]
    private partial void LogOutputInvalid(Guid renderJobId, string output, string reason);

    [LoggerMessage(EventId = 6309, Level = LogLevel.Information, Message = "render_ingest_subtitles_absent renderJobId={RenderJobId}")]
    private partial void LogSubtitlesAbsent(Guid renderJobId);

    [LoggerMessage(EventId = 6310, Level = LogLevel.Error, Message = "render_ingest_store_failed renderJobId={RenderJobId} storageKey={StorageKey} reason={Reason}")]
    private partial void LogStoreFailed(Guid renderJobId, string storageKey, string reason);

    [LoggerMessage(EventId = 6311, Level = LogLevel.Information, Message = "render_ingest_download_retried renderJobId={RenderJobId} kind={Kind}")]
    private partial void LogDownloadRetried(Guid renderJobId, string kind);

    [LoggerMessage(EventId = 6312, Level = LogLevel.Warning, Message = "render_ingest_status_failed renderJobId={RenderJobId} code={Code}")]
    private partial void LogStatusFailed(Guid renderJobId, string code);

    [LoggerMessage(EventId = 6313, Level = LogLevel.Warning, Message = "render_ingest_failed renderJobId={RenderJobId} code={Code} final={Final} retryCreated={RetryCreated}")]
    private partial void LogFailed(Guid renderJobId, string code, bool final, bool retryCreated);

    [LoggerMessage(EventId = 6314, Level = LogLevel.Warning, Message = "render_ingest_quality_failed renderJobId={RenderJobId} final={Final} retryCreated={RetryCreated}")]
    private partial void LogQualityFailed(Guid renderJobId, bool final, bool retryCreated);

    [LoggerMessage(EventId = 6315, Level = LogLevel.Information, Message = "render_ingest_stored renderJobId={RenderJobId} episodeId={EpisodeId} videoAssetId={VideoAssetId} bytes={Bytes}")]
    private partial void LogIngested(Guid renderJobId, Guid episodeId, Guid videoAssetId, long bytes);

    [LoggerMessage(EventId = 6316, Level = LogLevel.Error, Message = "render_ingest_settle_failed renderJobId={RenderJobId} reason={Reason}")]
    private partial void LogSettleFailed(Guid renderJobId, string reason);

    [LoggerMessage(EventId = 6317, Level = LogLevel.Warning, Message = "render_ingest_blob_delete_skipped renderJobId={RenderJobId} storageKey={StorageKey} reason={Reason}")]
    private partial void LogBlobDeleteSkipped(Guid renderJobId, string storageKey, string reason);

    [LoggerMessage(EventId = 6318, Level = LogLevel.Information, Message = "render_ingest_cleanup_retried renderJobId={RenderJobId} code={Code}")]
    private partial void LogCleanupRetried(Guid renderJobId, string code);

    [LoggerMessage(EventId = 6319, Level = LogLevel.Warning, Message = "render_ingest_cleanup_skipped renderJobId={RenderJobId} operation={Operation} reason={Reason}")]
    private partial void LogCleanupSkipped(Guid renderJobId, string operation, string reason);

    [LoggerMessage(EventId = 6320, Level = LogLevel.Error, Message = "render_ingest_episode_missing renderJobId={RenderJobId} episodeId={EpisodeId}")]
    private partial void LogEpisodeMissing(Guid renderJobId, Guid episodeId);

    [LoggerMessage(EventId = 6321, Level = LogLevel.Warning, Message = "render_ingest_episode_state_refused renderJobId={RenderJobId} episodeId={EpisodeId} code={Code}")]
    private partial void LogEpisodeStateRefused(Guid renderJobId, Guid episodeId, string code);
}
