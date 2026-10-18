using System.Data.Common;
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

    /// <summary>PostgreSQL's SQLSTATE for a unique-constraint violation.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>The key Npgsql records the refused constraint under in <see cref="Exception.Data"/>.</summary>
    private const string ConstraintNameKey = "ConstraintName";

    /// <summary>The unique index of <c>assets.storage_key</c>, whose refusal is a delivery that stored the key
    /// first (AC8).</summary>
    private const string StorageKeyIndex = "ix_assets_storage_key";

    /// <summary>
    /// The budget of a call this job holds a timeout of its own for: the settlement (which must still be
    /// attempted when the job's own token is already cancelled) and the best-effort rollbacks and blob deletes.
    /// </summary>
    private static readonly TimeSpan BestEffortTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The writer of the stored verdict: the node contract's own options, so the document is the exact
    /// <c>render-quality.v1</c> its readers expect.</summary>
    private static readonly JsonSerializerOptions ReportJson = new(RenderNodeJson.Strict);

    /// <summary>
    /// What one run of the ingest addresses: the scope's context, the media store and the two object keys BD7
    /// fixes for the row. Set once at the start of <see cref="RunAsync"/> so every ending can take the row lock,
    /// re-read the committed status and clear both objects best-effort without threading five values through every
    /// helper (S2, S3, C5, C6). A scoped job instance runs one <see cref="RunAsync"/> at a time, so this is
    /// per-run state, not shared state.
    /// </summary>
    private IngestRun? _run;

    /// <summary>The state of the run in progress.</summary>
    private IngestRun Run =>
        _run ?? throw new InvalidOperationException("The ingest run state was not initialised by RunAsync.");

    /// <summary>What one run addresses: the context, the store and the bucket, and the row's two object keys.</summary>
    /// <param name="Db">The tenant-scoped context the run reads and writes through.</param>
    /// <param name="Blobs">The media blob store.</param>
    /// <param name="Bucket">The media bucket the two keys live in.</param>
    /// <param name="VideoKey">The row's video object key (BD7).</param>
    /// <param name="SubtitlesKey">The row's subtitle object key (BD7).</param>
    private sealed record IngestRun(
        IAppDbContext Db,
        IBlobStore Blobs,
        string Bucket,
        string VideoKey,
        string SubtitlesKey);

    /// <summary>
    /// An attempt's own typed failure: the ones this job raises itself, built from the job id and a catalogue
    /// code — never from a raw provider or I/O message. It is the <em>only</em> exception a retried attempt
    /// rethrows unchanged, because it is the only one whose text is safe for Hangfire to persist and show in the
    /// Api's dashboard (S5). Everything else is logged in full and replaced by a message carrying its type name
    /// alone (N5): a framework <see cref="InvalidOperationException"/> such as <see cref="ObjectDisposedException"/>,
    /// a provider failure wrapped by the execution strategy, or the destination's own I/O exception all used to
    /// travel unchanged and leak their message and inner exceptions.
    /// </summary>
    /// <param name="message">The job's own message: ids and codes, never a raw exception's text.</param>
    private sealed class IngestAttemptException(string message) : InvalidOperationException(message);

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
        var bucket = BlobLocator.BucketFor(AssetKind.Video, storage);
        var videoKey = Key(job, "video", VideoExtension);
        var subtitlesKey = Key(job, "subtitles", SubtitlesExtension);

        // The two keys BD7 fixes for this row, so every ending below can clear both objects best-effort (S3):
        // a node error or a crash must not leave a paid render unreferenced in the media bucket.
        _run = new IngestRun(db, blobs, bucket, videoKey, subtitlesKey);

        if (IsTerminal(job.Status))
        {
            var terminal = job.Status.ToString();
            LogTerminalNoOp(job.Id, terminal);

            // A row the watchdog or a cancel ended while an attempt was still uploading keeps that attempt's two
            // objects, so a delivery that finds it terminal clears them best-effort (S3). A row that *succeeded*
            // keeps them: a concurrent success stored these exact keys (C5).
            if (job.Status is RenderJobStatus.Failed or RenderJobStatus.Cancelled)
            {
                await DeleteRunBlobsAsync(job.Id).ConfigureAwait(false);
            }

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

                throw new IngestAttemptException(
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
                attempt.IsLastAttempt,
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

            // WT55, SubmitRenderJob's own first step: the final transaction opens on the row lock, so two
            // deliveries of this job that both passed the entry guards serialise here instead of one losing at the
            // insert. The winner commits; this one re-reads the committed row below.
            await db.LockRenderJobForUpdateAsync(job.Id, cancellationToken).ConfigureAwait(false);

            // Read as committed (no identity resolution, the poller's own shape): a delivery that committed while
            // this attempt was downloading has already stored the verdict, the assets and the settlement, so this
            // run is the logged no-op of AC8 — no second asset, no second settle, no second event (D7).
            var committed = await db.RenderJobs
                .AsNoTracking()
                .Where(candidate => candidate.Id == job.Id)
                .Select(candidate => (RenderJobStatus?)candidate.Status)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (committed is null)
            {
                LogJobMissing(job.Id);
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return;
            }

            if (IsTerminal(committed.Value))
            {
                var stored = committed.Value.ToString();

                LogTerminalNoOp(job.Id, stored);
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                // The committed verdict is another delivery's: a success owns these two keys, so they stay, while
                // a row the watchdog or a cancel ended keeps nothing of this attempt (C5/S3).
                if (committed.Value is RenderJobStatus.Failed or RenderJobStatus.Cancelled)
                {
                    await DeleteRunBlobsAsync(job.Id).ConfigureAwait(false);
                }

                return;
            }

            // The ingest leg — a download plus up to two uploads — can take minutes, so the instant the output
            // became durable is read here, inside the final transaction, instead of reusing the entry reading:
            // FinishedAt, the assets' CreatedAt and retention, the audit row and the event's occurredAt must all
            // agree with the moment the bytes were stored, not with the moment the job was picked up (C4).
            var finishedAt = clock.UtcNow;

            var images = await hooks.MergeImagesAsync(job, body, cancellationToken).ConfigureAwait(false);
            var merged = report with { Images = images };

            var reported = job.RecordQualityReport(JsonSerializer.Serialize(merged, ReportJson));
            if (reported.IsError)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                throw new IngestAttemptException(
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

                    throw new IngestAttemptException(
                        $"Failing job '{job.Id}' as render_quality_failed failed ('{failed.FirstError.Code}').");
                }

                await CommitAsync(unitOfWork, job.Id, cancellationToken).ConfigureAwait(false);

                LogQualityFailed(job.Id, failed.Value.Final, failed.Value.RetryCreated);

                metrics.ObserveDuration(stage, RenderMetrics.OutcomeFailed, clock.UtcNow - startedAt);

                await DeleteRunBlobsAsync(job.Id).ConfigureAwait(false);

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
                finishedAt,
                cancellationToken).ConfigureAwait(false);

            if (videoAsset.IsError)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                throw new IngestAttemptException(
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
                    finishedAt,
                    cancellationToken).ConfigureAwait(false);

                if (subtitleAsset.IsError)
                {
                    await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    throw new IngestAttemptException(
                        $"Storing the subtitle asset of job '{job.Id}' failed ('{subtitleAsset.FirstError.Code}').");
                }
            }

            var attached = episode.AttachFinalVideo(videoAsset.Value, finishedAt);
            if (attached.IsError)
            {
                // The episode moved out of `rendering` behind this attempt (a cancel, an owner action): the output
                // is real but there is no episode to attach it to, so the attempt ends through the recorder. The
                // staged rows — the two assets and the verdict — must not survive into that ending, so this
                // attempt's unsaved changes are dropped and the row is re-read as committed first: the same two
                // steps the catch block below takes (C2). Without them the recorder's own SaveChanges would
                // insert asset rows whose objects were just deleted and a passing verdict on a failed row.
                LogEpisodeStateRefused(job.Id, job.EpisodeId, attached.FirstError.Code);

                DiscardPendingChanges(db);
                await RollbackQuietlyAsync(unitOfWork, job.Id).ConfigureAwait(false);

                var current = await db.RenderJobs
                    .SingleOrDefaultAsync(candidate => candidate.Id == job.Id, cancellationToken)
                    .ConfigureAwait(false);

                if (current is null)
                {
                    LogJobMissing(job.Id);

                    return;
                }

                if (IsTerminal(current.Status))
                {
                    var stored = current.Status.ToString();

                    LogTerminalNoOp(job.Id, stored);

                    // A row another delivery already ended keeps its objects only when that ending was a success.
                    if (current.Status is RenderJobStatus.Failed or RenderJobStatus.Cancelled)
                    {
                        await DeleteRunBlobsAsync(job.Id).ConfigureAwait(false);
                    }

                    return;
                }

                await RetryOrFailAsync(
                    current,
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

            var reviewed = episode.RenderSucceeded(finishedAt);
            if (reviewed.IsError)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                throw new IngestAttemptException(
                    $"Moving episode '{job.EpisodeId}' to video review failed ('{reviewed.FirstError.Code}').");
            }

            var succeeded = job.Succeed(finishedAt);
            if (succeeded.IsError)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                throw new IngestAttemptException(
                    $"Succeeding job '{job.Id}' failed ('{succeeded.FirstError.Code}').");
            }

            await hooks.RecordKeptVideoAsync(job, body, cancellationToken).ConfigureAwait(false);

            var raised = job.RaiseSucceeded();
            if (raised.IsError)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                throw new IngestAttemptException(
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
                        OccurredAt: finishedAt),
                    cancellationToken)
                .ConfigureAwait(false);

            // Exactly once, by reservation id, as the last statement before the commit: the settlement is not part
            // of this unit of work, so issuing it any earlier would let the kept-video hook, the event, the audit
            // row or the commit fail after a settled reservation — the rollback then refunds a reservation the
            // ledger already settled (D5/C3). Here only the commit itself can fail after it.
            await SettleAsync(ledger, job, cancellationToken).ConfigureAwait(false);

            try
            {
                await CommitAsync(unitOfWork, job.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException exception) when (IsStorageKeyViolation(exception))
            {
                // AC8's insert-time backstop: an assets row already names this key, so a delivery the row lock
                // above did not serialise won the insert. That delivery owns the verdict, the settle and the
                // event, so this attempt only drops its own writes.
                await RollbackQuietlyAsync(unitOfWork, job.Id).ConfigureAwait(false);
                DiscardPendingChanges(db);

                var stored = await db.RenderJobs
                    .AsNoTracking()
                    .Where(candidate => candidate.Id == job.Id)
                    .Select(candidate => (RenderJobStatus?)candidate.Status)
                    .SingleOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (stored is RenderJobStatus.Succeeded)
                {
                    LogConcurrentNoOp(job.Id, videoKey, StorageKeyIndex);

                    return;
                }

                throw;
            }

            LogIngested(job.Id, job.EpisodeId, videoAsset.Value, downloaded.Value.Bytes);

            metrics.ObserveDuration(stage, RenderMetrics.OutcomeSucceeded, clock.UtcNow - startedAt);

            // Step 10. Only now, with the verdict committed, is the adapter asked to release the task; a failure
            // here never fails an ingest whose output is already durable, and it deletes no blob.
            await DeleteAdapterTaskAsync(client, node, taskId, job.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Every outcome of the download, the verification, the blob PUTs and the final transaction ends here
            // (D3, H1, WT52): the attempt's own writes are dropped first — a transaction of this scope is either
            // rolled back now or by the scope's disposal, so nothing it staged can be committed by the terminal
            // write below — and the row is then handed back to the scheduler, or, on the attempt that has none
            // left, ended here, because a row left `ingesting` with no claim on it is exactly the stranding WT52
            // forbids. An `OperationCanceledException` this job's own token did not ask for is the destination's
            // (the download client rethrows it unchanged); one the token did ask for is the host shutting down and
            // is never swallowed.
            var destination = IsDestinationFailure(exception, cancellationToken);

            DiscardPendingChanges(db);
            await RollbackQuietlyAsync(unitOfWork, job.Id).ConfigureAwait(false);

            if (!attempt.IsLastAttempt)
            {
                // The scheduler downloads the output again, so the exception travels — but never its raw message:
                // an IO or storage exception can carry the spool path, the blob endpoint or the bucket, and
                // Hangfire persists the message and the stack in its state table, which the Api's dashboard shows
                // (S5). Only the type name travels; the job's own typed failures already carry ids and codes, so
                // they — and only they — are rethrown unchanged (N5: a framework InvalidOperationException such as
                // ObjectDisposedException, or an execution-strategy-wrapped provider failure, is not the job's own
                // and must not travel as if it were).
                if (exception is IngestAttemptException)
                {
                    throw;
                }

                // Everything else is logged once, in full, before its message is replaced: the wrapper below
                // carries neither the message nor an inner exception, so without this line a bug such as a
                // NullReferenceException or a storage fault would show nothing but its type name for every attempt
                // (N4). Logs are not the Hangfire state table, so the original exception object — its stack
                // included — is safe here.
                LogAttemptFailed(job.Id, stage, exception.GetType().Name, exception);

                throw new InvalidOperationException(
                    $"The ingest of job '{job.Id}' failed at stage '{stage}' ({exception.GetType().Name}); "
                    + "the job's next attempt downloads the output again.");
            }

            LogAttemptExhausted(job.Id, destination ? "destination" : "unexpected", exception.GetType().Name);

            // The committed row, read after this attempt's writes were dropped: the attempt may in fact have
            // committed (a failure after the commit) or another delivery may have ended the row while this one
            // ran, and a terminal row — whose output is durable — is left exactly as it is (D7).
            var current = await db.RenderJobs
                .SingleOrDefaultAsync(candidate => candidate.Id == job.Id, cancellationToken)
                .ConfigureAwait(false);

            if (current is null)
            {
                LogJobMissing(job.Id);

                return;
            }

            if (IsTerminal(current.Status))
            {
                var stored = current.Status.ToString();

                LogTerminalNoOp(job.Id, stored);

                // A row another delivery already ended keeps its objects only when that ending was a success (C5).
                if (current.Status is RenderJobStatus.Failed or RenderJobStatus.Cancelled)
                {
                    await DeleteRunBlobsAsync(job.Id).ConfigureAwait(false);
                }

                return;
            }

            await RetryOrFailAsync(
                current,
                RenderOutcomeCodes.RenderOutputInvalid,
                stage,
                startedAt,
                now,
                lastAttempt: true,
                recorder,
                metrics,
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // The host is shutting down: this attempt's staged writes are dropped and the cancellation travels
            // unchanged so the scheduler re-runs the job. Nothing is deleted and the row is not read (N2/N3).
            // A delete decision here could only come from an *unlocked* status read, and that read is wrong for
            // the one case that matters: a duplicate delivery B that has already PUT these exact BD7 keys and is
            // inside its final transaction (but has not committed) still reads `ingesting`, so deleting the
            // objects would leave B's committed success verdict pointing at nothing — silently, and for good.
            // Nothing this attempt wrote was committed (its transaction is gone), the keys are content-addressed
            // so the next attempt overwrites the same objects, and an ending that never runs again is the
            // failed/cancelled sweep's job (09.7.3-T1). The status read that used to sit here was also unbudgeted
            // and unguarded: with a slow or failing database it could replace the `OperationCanceledException`
            // Hangfire needs to requeue the job, and on the last attempt nothing would then end the row at all
            // (the WT52 stranding).
            DiscardPendingChanges(db);
            await RollbackQuietlyAsync(unitOfWork, job.Id).ConfigureAwait(false);

            throw;
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
            throw new IngestAttemptException(
                $"Committing the ingest of job '{renderJobId}' failed ('{committed.FirstError.Code}').");
        }
    }

    /// <summary>Rolls back the scope's transaction and swallows a secondary failure: the failure the caller is
    /// already handling is the one that has to travel.</summary>
    /// <param name="unitOfWork">The unit of work of this scope (no transaction open is a no-op).</param>
    /// <param name="renderJobId">The row the transaction belongs to (for the log line).</param>
    /// <returns>A task that completes once the rollback was attempted.</returns>
    private async Task RollbackQuietlyAsync(IUnitOfWork unitOfWork, Guid renderJobId)
    {
        using var budget = new CancellationTokenSource(BestEffortTimeout);

        try
        {
            await unitOfWork.RollbackAsync(budget.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || budget.IsCancellationRequested)
        {
            // A transaction that cannot roll back is the scope's disposal's business (it rolls back an abandoned
            // one), and the attempt's failure is what the caller must see.
            LogRollbackSkipped(renderJobId, exception.GetType().Name);
        }
    }

    /// <summary>
    /// Drops every unsaved change of this scope's context, so the committed row can be read back and the terminal
    /// write is the only change left to save (the <c>GenerateFactSheetPipeline</c>/
    /// <c>StoryGenerationProgress.DiscardPendingChanges</c> precedent).
    /// </summary>
    /// <param name="db">The scope's context.</param>
    private static void DiscardPendingChanges(IAppDbContext db)
    {
        if (db is DbContext context)
        {
            context.ChangeTracker.Clear();
        }
    }

    /// <summary>Whether a row has already ended: a terminal row is never moved again by a delivery of this job.</summary>
    /// <param name="status">The row's status.</param>
    /// <returns><c>true</c> for <c>succeeded</c>, <c>failed</c> and <c>cancelled</c>.</returns>
    private static bool IsTerminal(RenderJobStatus status) =>
        status is RenderJobStatus.Succeeded or RenderJobStatus.Failed or RenderJobStatus.Cancelled;

    /// <summary>
    /// Whether the commit was refused by the <c>assets</c> storage-key index — another delivery stored this
    /// attempt's key first. The Application layer may not reference Npgsql, so the provider's error is read
    /// through the BCL only (<see cref="DbException.SqlState"/> and the constraint name the provider records in
    /// <see cref="Exception.Data"/>, the <c>FactSheetFlushConflict</c>/<c>BibleProposalRunConflict</c> shape).
    /// </summary>
    /// <param name="exception">The commit's <see cref="DbUpdateException"/>.</param>
    /// <returns><c>true</c> when that index was the one refused.</returns>
    private static bool IsStorageKeyViolation(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException { SqlState: UniqueViolation } database
                && database.Data[ConstraintNameKey] is string constraint
                && string.Equals(constraint, StorageKeyIndex, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Ends the attempt inside the job, in its own transaction, through the recorder (the D5 code, the retry
    /// decision, one refund by reservation id, the audit row and <c>render_failed.v1</c> at once).
    /// </summary>
    /// <remarks>
    /// The failure path opens on the same row lock and committed-status re-read as the success path (S2): a
    /// delivery that decides to fail while another delivery already committed — Hangfire redelivers a job whose
    /// download outlived the invisibility timeout — must not refund a reservation that delivery settled, nor end a
    /// row it already ended. A terminal row is therefore a logged no-op here; a row this attempt is the first to
    /// end has both of its deterministic objects cleared best-effort once the verdict committed (S3, C5).
    /// </remarks>
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
    /// <returns>A task that completes once the row is terminal, or once it is found already terminal.</returns>
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
        var db = Run.Db;

        await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        await db.LockRenderJobForUpdateAsync(job.Id, cancellationToken).ConfigureAwait(false);

        var committed = await db.RenderJobs
            .AsNoTracking()
            .Where(candidate => candidate.Id == job.Id)
            .Select(candidate => (RenderJobStatus?)candidate.Status)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (committed is null)
        {
            LogJobMissing(job.Id);
            await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return;
        }

        if (IsTerminal(committed.Value))
        {
            var stored = committed.Value.ToString();

            LogTerminalNoOp(job.Id, stored);
            await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

            // A row another delivery already ended keeps its objects only when that ending was a success (C5).
            if (committed.Value is RenderJobStatus.Failed or RenderJobStatus.Cancelled)
            {
                await DeleteRunBlobsAsync(job.Id).ConfigureAwait(false);
            }

            return;
        }

        var failed = await recorder
            .FailAsync(job, code, outcomeRetryable: null, cancellationToken)
            .ConfigureAwait(false);

        if (failed.IsError)
        {
            await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

            throw new IngestAttemptException(
                $"Ending job '{job.Id}' as '{code}' failed ('{failed.FirstError.Code}').");
        }

        await CommitAsync(unitOfWork, job.Id, cancellationToken).ConfigureAwait(false);

        LogFailed(job.Id, code, failed.Value.Final, failed.Value.RetryCreated);

        metrics.ObserveDuration(stage, metricOutcome, now - startedAt);

        if (failed.Value.RetryCreated)
        {
            metrics.CountRetry(code);
        }

        // The row is terminal and committed, so nothing owns these two objects: clear them best-effort (S3/C5).
        await DeleteRunBlobsAsync(job.Id).ConfigureAwait(false);
    }

    /// <summary>
    /// Throws when the job still has an attempt left (the scheduler downloads again from scratch) and ends the row
    /// in-job on the last one (WT52: nothing may be left <c>ingesting</c> with no claim).
    /// </summary>
    /// <remarks>
    /// A row the owner already asked to cancel never waits for another attempt (WT51): the terminal recorder's
    /// verdict is final whatever the code says, so the attempt is ended here rather than spending the rest of the
    /// scheduler's budget of downloads on a render the owner cancelled.
    /// </remarks>
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
        if (!lastAttempt && job.CancelRequestedAt is null)
        {
            throw new IngestAttemptException(
                $"The output of job '{job.Id}' could not be stored ('{code}'); the job's next attempt downloads it again.");
        }

        // A rethrown attempt keeps its keys (the next attempt overwrites them, BD7); the ending itself clears
        // both of them best-effort inside FailAsync (S3/C5).
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

    /// <summary>Classifies a failed status call: the deferred and not-ready kinds are retried until the scheduler's
    /// last attempt, the rest end the attempt with the code the failure carries (or the D5 default of its kind).</summary>
    /// <param name="error">The mapped failure of the status call.</param>
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
    private async Task StatusFailedAsync(
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

        if (failure is { Kind: RenderClientFailureKind.Deferred or RenderClientFailureKind.OutputNotReady })
        {
            if (!lastAttempt)
            {
                throw new IngestAttemptException(
                    $"The status call of job '{job.Id}' was '{failure.Kind}'; the next attempt reads it again.");
            }

            // IJobAttempt's rule, and WT52's: the scheduler has no attempt left, so the row cannot wait for
            // another read — the attempt ends here (as the node error this path's other failures carry) rather
            // than leaving the row `ingesting` with no claim on it.
            LogAttemptExhausted(job.Id, "status", failure.Kind.ToString());
        }

        var code = failure switch
        {
            // A deferred/not-ready read that has run out of the scheduler's attempts carries no catalogued D5 code
            // of its own (its code is an adapter detail, never render_jobs.error_code - 05 H4), so the verdict is
            // the node error this path's other failures carry.
            { Kind: RenderClientFailureKind.Deferred or RenderClientFailureKind.OutputNotReady } => RenderOutcomeCodes.RenderNodeError,
            { OutcomeCode: { } carried } => carried,
            { Kind: RenderClientFailureKind.TerminalFailure } => RenderOutcomeCodes.RenderOutputInvalid,
            _ => RenderOutcomeCodes.RenderNodeError,
        };

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

            if (!lastAttempt)
            {
                LogDownloadRetried(job.Id, kind);

                throw new IngestAttemptException(
                    $"Downloading the output of job '{job.Id}' failed as '{failure.Kind}'; the next attempt takes it again.");
            }

            // IJobAttempt's rule, and WT52's: the scheduler has no attempt left, so the row cannot be handed back
            // for another download — the attempt ends below (as the invalid output this path's other failures
            // carry) rather than leaving the row `ingesting` with no claim on it.
            LogAttemptExhausted(job.Id, "download", kind);
        }

        var code = failure switch
        {
            // A retried kind that has run out of the scheduler's attempts (the branch above threw for the others)
            // carries no catalogued D5 code of its own - its code is an adapter detail, never
            // render_jobs.error_code (05 H4) - so the verdict is the D5 code this path's other failures use.
            { Kind: RenderClientFailureKind.TransientFailure } => RenderOutcomeCodes.RenderNodeError,
            { Kind: RenderClientFailureKind.Deferred or RenderClientFailureKind.OutputNotReady } => RenderOutcomeCodes.RenderOutputInvalid,
            { OutcomeCode: { } carried } => carried,
            _ => RenderOutcomeCodes.RenderOutputInvalid,
        };

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

    /// <summary>Settles the reservation of a stored render, once, by reservation id, with a bounded budget of its
    /// own on top of the caller's token: a failure is never swallowed, because the row this job is about to commit
    /// is exactly what a swallow would buy (D5).</summary>
    /// <remarks>
    /// <c>SettleAsync</c> is not the refund of a terminal transition: it has no documented idempotence per
    /// reservation and it is not part of this job's unit of work, so its failure travels (AC3): the attempt's
    /// transaction rolls back, the row is not committed <c>succeeded</c>, and the attempt is either retried or
    /// ended on its last one — never a settled-short success. It is called as the last statement before the
    /// commit (C3), so no other step of the success path can fail after a settled reservation — only the commit
    /// itself, whose rollback then ends the attempt through the failure recorder.
    /// <para>
    /// The budget is a <em>linked</em> one with <see cref="BestEffortTimeout"/> as its cap: it bounds the call when
    /// nothing else does, and it is deliberately not independent of the caller (N9) — a job token the host has
    /// already cancelled cancels the settlement immediately, and the resulting
    /// <see cref="OperationCanceledException"/> aborts the attempt before the commit, which is the same safe
    /// outcome as a refusal. Nothing needs the settlement to survive a shutdown: the next attempt settles again.
    /// </para>
    /// </remarks>
    /// <param name="ledger">The credit ledger.</param>
    /// <param name="job">The row whose reservation is settled.</param>
    /// <param name="cancellationToken">The job's token, linked into the settlement's own budget.</param>
    /// <returns>A task that completes once the ledger answered.</returns>
    /// <exception cref="InvalidOperationException">The ledger refused the settlement; the attempt must not
    /// commit.</exception>
    private async Task SettleAsync(ICreditLedger ledger, RenderJob job, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(BestEffortTimeout);

        var settled = await ledger
            .SettleAsync(new ReservationId(job.ReservationId), budget.Token)
            .ConfigureAwait(false);

        if (settled.IsError)
        {
            LogSettleFailed(job.Id, settled.FirstError.Code);

            throw new IngestAttemptException(
                $"Settling the reservation of job '{job.Id}' failed ('{settled.FirstError.Code}'); the ingest is not committed.");
        }
    }

    /// <summary>Deletes objects that no committed row owns, best effort: the verdict that just committed must not
    /// be undone by a bucket that answers late. The budget is this helper's own, because an ending can be running
    /// precisely because the job's token was cancelled (S3).</summary>
    /// <param name="blobs">The blob store.</param>
    /// <param name="bucket">The media bucket.</param>
    /// <param name="keys">The keys to delete.</param>
    /// <param name="renderJobId">The row the keys belong to (for the log line).</param>
    /// <returns>A task that completes once every delete was attempted.</returns>
    private async Task DeleteBlobsAsync(
        IBlobStore blobs,
        string bucket,
        IReadOnlyList<string> keys,
        Guid renderJobId)
    {
        foreach (var key in keys)
        {
            using var budget = new CancellationTokenSource(BestEffortTimeout);

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

    /// <summary>
    /// Clears both deterministic objects of this row (BD7: the video key and the subtitle key), best effort.
    /// Every ending that does not keep the output goes through here, not only the keys this attempt happened to
    /// have PUT: a node error, a refused attach or a crash must not leave a paid render unreferenced in the media
    /// bucket, and the delete must not depend on a token a shutdown already cancelled (S3, C5).
    /// </summary>
    /// <param name="renderJobId">The row the keys belong to (for the log line).</param>
    /// <returns>A task that completes once both deletes were attempted.</returns>
    private Task DeleteRunBlobsAsync(Guid renderJobId) =>
        _run is { } run
            ? DeleteBlobsAsync(run.Blobs, run.Bucket, [run.VideoKey, run.SubtitlesKey], renderJobId)
            : Task.CompletedTask;

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
        if (download.Bytes == 0)
        {
            return ZeroBytes;
        }

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

    /// <summary>
    /// Whether an exception came from the destination rather than from the node: the spool refused a write, or a
    /// cancellation this job's own token did not ask for (H1, 09.6.2-T1's hand-over — the download client rethrows a
    /// destination exception unchanged, so the ingest is where it is recognised). Everything else keeps travelling
    /// to the scheduler as the bug it is.
    /// </summary>
    /// <param name="exception">The exception this attempt threw.</param>
    /// <param name="cancellationToken">The job's own token.</param>
    /// <returns>Whether this is the destination's own failure.</returns>
    private static bool IsDestinationFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is IOException
        || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    /// <summary>The reason of a downloaded output that served no bytes at all (S6: a zero-byte video is a
    /// failed render, not a stored asset).</summary>
    private const string ZeroBytes = "bytes_zero";

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

    [LoggerMessage(EventId = 6336, Level = LogLevel.Error, Message = "render_ingest_store_failed renderJobId={RenderJobId} storageKey={StorageKey} reason={Reason}")]
    private partial void LogStoreFailed(Guid renderJobId, string storageKey, string reason);

    [LoggerMessage(EventId = 6337, Level = LogLevel.Information, Message = "render_ingest_download_retried renderJobId={RenderJobId} kind={Kind}")]
    private partial void LogDownloadRetried(Guid renderJobId, string kind);

    [LoggerMessage(EventId = 6338, Level = LogLevel.Warning, Message = "render_ingest_status_failed renderJobId={RenderJobId} code={Code}")]
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

    [LoggerMessage(EventId = 6339, Level = LogLevel.Error, Message = "render_ingest_episode_missing renderJobId={RenderJobId} episodeId={EpisodeId}")]
    private partial void LogEpisodeMissing(Guid renderJobId, Guid episodeId);

    [LoggerMessage(EventId = 6359, Level = LogLevel.Warning, Message = "render_ingest_episode_state_refused renderJobId={RenderJobId} episodeId={EpisodeId} code={Code}")]
    private partial void LogEpisodeStateRefused(Guid renderJobId, Guid episodeId, string code);

    [LoggerMessage(EventId = 6333, Level = LogLevel.Warning, Message = "render_ingest_attempt_exhausted renderJobId={RenderJobId} operation={Operation} kind={Kind}")]
    private partial void LogAttemptExhausted(Guid renderJobId, string operation, string kind);

    [LoggerMessage(EventId = 6334, Level = LogLevel.Information, Message = "render_ingest_concurrent_noop renderJobId={RenderJobId} storageKey={StorageKey} constraint={Constraint}")]
    private partial void LogConcurrentNoOp(Guid renderJobId, string storageKey, string constraint);

    [LoggerMessage(EventId = 6335, Level = LogLevel.Warning, Message = "render_ingest_rollback_skipped renderJobId={RenderJobId} reason={Reason}")]
    private partial void LogRollbackSkipped(Guid renderJobId, string reason);

    /// <summary>
    /// A retried attempt's failure, in full, <em>before</em> the wrapper that carries its type name alone is
    /// thrown (N4). The exception object travels with it, so the original stack survives in the logs; the
    /// message itself stays ids-only, and the raw text is the log line's business, never the wrapper's (S5).
    /// </summary>
    /// <param name="renderJobId">The row of the attempt.</param>
    /// <param name="stage">The histogram stage the failure happened in.</param>
    /// <param name="kind">The exception's type name.</param>
    /// <param name="exception">The failure itself, stack included.</param>
    [LoggerMessage(EventId = 6374, Level = LogLevel.Warning, Message = "render_ingest_attempt_failed renderJobId={RenderJobId} stage={Stage} kind={Kind}")]
    private partial void LogAttemptFailed(Guid renderJobId, string stage, string kind, Exception exception);
}
