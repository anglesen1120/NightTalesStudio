using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Flags;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Publishing;
using NightTales.Domain.Assets;
using NightTales.Domain.Common;
using NightTales.Domain.Publishing;

namespace NightTales.Application.Features.Publishing.Upload;

/// <summary>
/// Uploads one consented publication to YouTube exactly once (06.2.3-T2 plan §4 UC1, 06 README D9): it locks the row,
/// reconciles instead of re-uploading when the platform already holds the video, refuses any upload without a consent,
/// holds the row under an open platform incident, reserves one quota unit, refreshes the access token through the one
/// refresh-under-lock path, persists the encrypted resumable-session URI before the first byte, streams the MP4 from
/// object storage and maps every Google failure through <see cref="UploadFailureMap"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Short transactions, never one across a Google call.</b> The first commits the <c>scheduled → uploading</c> move,
/// the quota reservation and <c>platform_payload.quotaReservedDay</c> together, under the connection lock and then the
/// publication lock; the second claims the session create — <c>platform_payload.uploadCreateIssuedAt</c> plus a short
/// <c>uploadSessionCreateLeaseUntil</c> — and reads the metadata; the third commits the protected session URI (and
/// clears the claim) <em>before</em> the first byte leaves the process; the fourth commits the terminal state (or the
/// failure verdict) with the ledger release. No transaction is open while a Google call or the object stream runs: a
/// lock held across an HTTP call would pin a pooled connection for minutes, block a cancel on that row and orphan the
/// session a crash rolled back, and a stream cannot be rolled back ([RULED] 06.2.3 plan §3.2, STATUS S66-3/SEC-5,
/// S72-2/SEC-8, QA D3).
/// </para>
/// <para>
/// <b>The release decision is the row's, never the attempt's.</b> A create that reached Google may have been counted,
/// so the unit comes back only when this reservation never issued one (or the answer itself says the quota refused it).
/// That fact lives in <c>platform_payload.uploadCreateIssuedAt</c>, written before the call, so an attempt that merely
/// resumes a row an earlier run reserved still decides it correctly (YR19 corrected, QA D2).
/// </para>
/// <para>
/// <b>Exactly once.</b> <c>external_post_id</c> is the claim: a run that finds it reconciles through
/// <c>videos.list</c> and never calls <c>videos.insert</c> again, and the session URI is stored before the first byte so
/// a lost response resumes from the probe's offset instead of creating a second video (F02, YR27-1). A row that
/// already holds a reserved day skips the reservation, so a resumed run never spends a second unit (YR27-2), and only a
/// run that actually had to open a new session on an expired one reserves again (YR27-3).
/// </para>
/// <para>
/// <b>Tenant and secrets.</b> The job pins <c>ISystemContext.For(owner)</c> before this handler is resolved, so every
/// read is the owner's. The access token comes from <see cref="ITokenService"/> only; the session URI is protected with
/// <see cref="ITokenProtector"/> under <c>tokens.v1/{connectionId}</c> and never reaches a log line, an outbox payload,
/// an audit row or an exception message; no title, description or token is logged either (06 README D4, D17).
/// </para>
/// </remarks>
public sealed partial class PublishYouTubeHandler(
    IAppDbContext db,
    IUnitOfWork unitOfWork,
    IClock clock,
    ITokenService tokens,
    IChannelConnectionLocker connections,
    IPlatformIncidentStore incidents,
    IQuotaLedger quota,
    IYouTubeClient youTube,
    IBlobStore blobs,
    StorageSettings storage,
    ITokenProtector protector,
    IFeatureFlags flags,
    IJobAttempt attempt,
    YouTubeOptions options,
    ILogger<PublishYouTubeHandler> logger) : IRequestHandler<PublishYouTubeCommand, ErrorOr<UploadOutcome>>
{
    /// <summary>The feature flag that gates every upload (06 README §5: <c>youtube_publish</c> stays off by default).</summary>
    public const string FeatureKey = "youtube_publish";

    /// <summary>Google's <c>status.uploadStatus</c> for a video it refused after receiving it.</summary>
    private const string UploadStatusFailed = "failed";

    /// <summary>Google's <c>status.uploadStatus</c> for a video its policy rejected.</summary>
    private const string UploadStatusRejected = "rejected";

    /// <summary>Google's <c>status.uploadStatus</c> for a video the owner deleted again.</summary>
    private const string UploadStatusDeleted = "deleted";

    /// <summary>
    /// How many poll intervals a held row waits before it re-checks the open incident (YR24): the hold is a bounded lazy
    /// poll, never an immediate re-enqueue.
    /// </summary>
    private const int HoldPollIntervals = 5;

    /// <summary>The media bucket every video asset row lives in (<see cref="BlobLocator"/>).</summary>
    private string Bucket => BlobLocator.BucketFor(AssetKind.Video, storage);

    /// <summary>
    /// How long the poller may claim the row once YouTube is transcoding it (<c>YouTube:Poll:MaxMinutes</c>).
    /// </summary>
    private int ProcessingWindowMinutes => options.Poll.MaxMinutes;

    /// <summary>
    /// Margin over <c>YouTube:Upload:StartTimeoutSeconds</c> before another run may treat a session-create claim as
    /// abandoned: the client gives up at the timeout, so the extra seconds cover the transaction that stores its answer
    /// (SEC-8).
    /// </summary>
    private const int SessionCreateLeaseMarginSeconds = 10;

    /// <summary>
    /// How long one run's claim on the row's session create lasts (<c>platform_payload.uploadSessionCreateLeaseUntil</c>):
    /// the create's own timeout plus <see cref="SessionCreateLeaseMarginSeconds"/>, so a run that dies holding it blocks
    /// the next attempt for at most one Google timeout.
    /// </summary>
    private TimeSpan SessionCreateLease =>
        TimeSpan.FromSeconds(options.Upload.StartTimeoutSeconds + SessionCreateLeaseMarginSeconds);

    /// <summary>
    /// How long one run's claim on this row's streaming window lasts (<c>platform_payload.uploadStreamLeaseUntil</c>,
    /// SEC-4): one whole chunk timeout plus a renewal gap, because the callback that renews it fires between chunks and
    /// a chunk may legally hold the connection for <c>YouTube:Upload:ChunkTimeoutSeconds</c>. A run that dies stops
    /// renewing and the window becomes claimable one chunk-timeout later.
    /// </summary>
    /// <summary>
    /// How long one run's streaming window stays live without a renewal.
    /// </summary>
    /// <remarks>
    /// It covers the longest stretch in which no progress report can arrive: every chunk attempt may burn the whole
    /// per-chunk timeout and the client retries a refused chunk <see cref="YouTubeUploadOptions.MaxChunkRetries"/>
    /// times before it gives up, so <c>(retries + 1) * chunkTimeout</c> is that stretch and the margin on top absorbs
    /// the back-off between attempts.
    /// </remarks>
    private TimeSpan StreamLease =>
        TimeSpan.FromSeconds(options.Upload.ChunkTimeoutSeconds * (options.Upload.MaxChunkRetries + 1))
        + StreamLeaseMargin;

    /// <summary>The gap between two renewals of the streaming lease, measured from the last one the run wrote.</summary>
    private static readonly TimeSpan StreamLeaseRenewEvery = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long a competing run waits for a lease that is about to expire before it answers "nothing to do": a lease
    /// with more time left than this belongs to a run that is still renewing it, and a shorter one is the tail of a
    /// run that died, so waiting it out is how the row is recovered without writing anything (SEC-4).
    /// </summary>
    private static readonly TimeSpan StreamLeaseWait = TimeSpan.FromSeconds(20);

    /// <summary>The slack a lease keeps past the longest stretch without a progress report (PERF-3).</summary>
    private static readonly TimeSpan StreamLeaseMargin = TimeSpan.FromSeconds(30);

    /// <summary>How often the waiting run re-reads the row's lease while it waits out a nearly expired one.</summary>
    private static readonly TimeSpan StreamLeasePollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How close to its expiry an access token must be before the run asks for a fresh one before it opens the
    /// stream (PERF-5). It is the same threshold <c>TokenService</c> refreshes at, so the ask is guaranteed to be a
    /// real refresh and not a re-read of the same value.
    /// </summary>
    private static readonly TimeSpan TokenSafetyMargin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether this attempt already tried to write its own terminal verdict (<see cref="TryFailExhaustedAsync"/>):
    /// a refused commit inside that very transaction must not start it again.
    /// </summary>
    private bool finalVerdictAttempted;

    /// <inheritdoc />
    public async ValueTask<ErrorOr<UploadOutcome>> Handle(
        PublishYouTubeCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The flag is read before anything else: a rollback of this feature must stop new uploads without touching
        // the rows already queued, and the job swallows the answer so nothing retries five times (06 README §5).
        if (!await flags.IsEnabledAsync(FeatureKey, cancellationToken).ConfigureAwait(false))
        {
            LogFeatureDisabled(request.PublicationId);

            return Error.NotFound(
                ErrorCodes.FeatureDisabled,
                "YouTube publishing is disabled on this environment.");
        }

        var now = clock.UtcNow;

        // Step 1: one cheap read to find the row, its owner's connection and whether an upload may even start. The
        // authoritative re-check happens under the lock in PrepareAsync; this one keeps the common no-op cheap.
        var snapshot = await db.Publications
            .AsNoTracking()
            .Where(publication => publication.Id == request.PublicationId)
            .Select(publication => new
            {
                publication.Status,
                publication.ChannelConnectionId,
                publication.ExternalPostId,
                publication.NextAttemptAt,
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (snapshot is null)
        {
            return PublicationMissing(request.PublicationId);
        }

        // Step 2: terminal rows and the reconcile path involve no state change of the upload itself.
        if (TerminalOutcome(snapshot.Status) is { } terminal)
        {
            var status = snapshot.Status.ToString();
            LogTerminalNoOp(request.PublicationId, status);

            return terminal;
        }

        var run = new RunState(request.PublicationId, snapshot.ChannelConnectionId);

        if (snapshot.ExternalPostId is { Length: > 0 } external)
        {
            // F02: a run whose last response was lost. The video exists, so it is adopted instead of uploaded again.
            return await ReconcileAsync(run, external, cancellationToken).ConfigureAwait(false);
        }

        // YR27-1: an early delivery (a re-enqueued hold, a quota reschedule that is not due yet) is a no-op.
        if (snapshot.NextAttemptAt is { } notBefore && notBefore > now)
        {
            return UploadOutcome.Rescheduled;
        }

        // SEC-4: one run streams a row at a time. A live window belongs to a run that is sending bytes into this
        // row's session right now, so this run writes nothing at all — not a status, not a lease, not a byte — and
        // leaves the outcome to the run that owns the row. Only the tail of a lease (a run that died and stopped
        // renewing) is waited out, which is how the row becomes claimable again without a write.
        if (await WaitForStreamingRunAsync(request.PublicationId, cancellationToken).ConfigureAwait(false) is { } held)
        {
            LogStreamLeaseHeld(request.PublicationId, held);

            // A lease with more time left than StreamLeaseWait is being renewed, and this attempt has nothing to write.
            // Returning a decided outcome here would end the job as a success: Hangfire would not deliver the row again
            // and nothing wakes it, so a lease left behind by a run that threw would strand the row in `uploading`.
            // Asking for another attempt is the same statement the create claim makes when a sibling holds it.
            if (!attempt.IsLastAttempt)
            {
                LogRetrying(request.PublicationId, PublishAttemptException.RetryCode, attempt.Attempt, attempt.MaxAttempts);

                throw new PublishAttemptException(PublishAttemptException.RetryCode);
            }

            // The last attempt leaves the row alone: the lease is genuinely live here (every shorter one was waited
            // out above), so a sibling is streaming this row and failing it would orphan the video that upload owns.
            // The sibling's own transition — `processing`, a reschedule or a verdict — is what wakes the row.
            return UploadOutcome.Rescheduled;
        }

        // Steps 3-10. Every exit that is not a verdict leaves the row claimable: the run's claims are ended before the
        // exception reaches the scheduler, because the next delivery of this row is the one that must stream it.
        try
        {
            return await RunAttemptAsync(run, cancellationToken).ConfigureAwait(false);
        }
        catch (PublishAttemptException)
        {
            await ReleaseClaimsAfterThrownRunAsync(run).ConfigureAwait(false);

            throw;
        }
        catch (OperationCanceledException)
        {
            // A cancelled run (a shutting-down Worker, an aborted caller) is not a verdict, but it must not leave a
            // live window behind either: the next delivery would answer "nothing to do" until the lease expired.
            await ReleaseClaimsAfterThrownRunAsync(run).ConfigureAwait(false);

            throw;
        }
        catch (Exception)
        {
            // Everything else — a provider exception from a lock or a save, a protector refusal, an invariant the run
            // tripped — is still this run's claims to give back, and on the last attempt still this row's verdict.
            // Nobody else writes either: `PublishYouTubeJob`'s catch-all only re-labels the exception, so without the
            // verdict here the attempts would run out with the row left in `uploading` for good.
            await ReleaseClaimsAfterThrownRunAsync(run).ConfigureAwait(false);
            await TryFailExhaustedAsync(
                    run,
                    Error.Failure(PublicationErrorCodes.UploadFailed, "The attempt failed unexpectedly."),
                    CancellationToken.None)
                .ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// Steps 3-10 of one run: the guarded start, the token, and the resume or the new session that streams the object.
    /// </summary>
    /// <param name="run">The run's state.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>The verdict of the run, or the error that decided it.</returns>
    /// <exception cref="PublishAttemptException">The attempt asks the scheduler for another one.</exception>
    private async Task<ErrorOr<UploadOutcome>> RunAttemptAsync(RunState run, CancellationToken cancellationToken)
    {
        // Steps 3-6: the guarded transaction that starts the upload and reserves one unit.
        var prepared = await PrepareAsync(run, cancellationToken).ConfigureAwait(false);
        if (prepared.IsError)
        {
            return prepared.FirstError;
        }

        var (decided, asset) = prepared.Value;

        if (decided is { } outcome)
        {
            return outcome;
        }

        // Step 7: the access token, refreshed under the connection row lock by the one path the platform has. This call
        // opens its own transaction and must never run while the publication lock is held (SEC-5).
        var token = await tokens.GetAccessTokenAsync(run.ConnectionId, cancellationToken).ConfigureAwait(false);

        if (token.IsError)
        {
            return await FailAsync(run, token.FirstError, cancellationToken).ConfigureAwait(false);
        }

        run.Token = token.Value;

        // Steps 8-10: resume the stored session or open a new one, stream the object, and commit the terminal state.
        // The cipher is read off the entity, not projected: `UploadSessionEnc` is a computed member of the payload
        // document and has no column of its own.
        var stored = await db.Publications
            .AsNoTracking()
            .SingleOrDefaultAsync(publication => publication.Id == run.PublicationId, cancellationToken)
            .ConfigureAwait(false);

        var session = stored?.UploadSessionEnc;

        var resumeAt = session is not null
            && protector.TryUnprotect(run.Purpose, session, out var plainSession)
            && Uri.TryCreate(plainSession, UriKind.Absolute, out var sessionUri)
                ? sessionUri
                : null;

        return resumeAt is null
            ? await StartSessionAsync(run, asset!.Value, cancellationToken).ConfigureAwait(false)
            : await ResumeAsync(run, asset!.Value, resumeAt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gives back whatever the run that just threw still held — the session-create claim and its streaming window —
    /// so the attempt the scheduler runs next finds the row claimable.
    /// </summary>
    /// <remarks>
    /// <b>Never in place of the original failure.</b> It rolls back a transaction the throw may have left open and then
    /// releases the claims best effort, on a token of its own: the cancelled attempt's token is exactly the one that
    /// cannot write the lease away, and this write is what makes the next attempt possible.
    /// </remarks>
    /// <param name="run">The run whose claims go back.</param>
    private async Task ReleaseClaimsAfterThrownRunAsync(RunState run)
    {
        await TryRollbackAsync(CancellationToken.None).ConfigureAwait(false);
        await ReleaseRunClaimsAsync(run, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// The guarded first transaction (UC1 steps 3-6): the connection lock, the publication lock, the state and consent
    /// checks, the asset resolve, the incident gate and the one quota reservation, committed together.
    /// </summary>
    /// <param name="run">The run's state; the reservation is recorded on it when this run spent a unit.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>
    /// Either a decided outcome (nothing further to do this run) or the resolved asset to stream; an error only when
    /// the row cannot be read or a decision refused its transition. A refused commit is not an error: it ends the
    /// attempt through <see cref="CommitAsync"/>.
    /// </returns>
    /// <exception cref="PublishAttemptException">The commit of this transaction was refused.</exception>
    private async Task<ErrorOr<(UploadOutcome? Decided, (string StorageKey, string ContentType, long SizeBytes)? Asset)>>
        PrepareAsync(RunState run, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Lock order (STATUS S66-3/SEC-5): the connection row first, the publication row second, never the other way
            // round — the token path takes the connection lock alone, so the order can never deadlock with it.
            var connection = await connections.LockByIdAsync(run.ConnectionId, cancellationToken).ConfigureAwait(false);

            await db.LockPublicationForUpdateAsync(run.PublicationId, cancellationToken).ConfigureAwait(false);

            var row = await db.Publications
                .SingleOrDefaultAsync(publication => publication.Id == run.PublicationId, cancellationToken)
                .ConfigureAwait(false);

            if (row is null)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return PublicationMissing(run.PublicationId);
            }

            // Re-check the snapshot under the lock: a redelivery may have started, ended or rescheduled the row between
            // the unlocked read and this transaction.
            if (TerminalOutcome(row.Status) is { } terminal)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return (terminal, null);
            }

            if (row.ExternalPostId is not null)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return (UploadOutcome.Reconciled, null);
            }

            if (row.NextAttemptAt is { } notBefore && notBefore > now)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return (UploadOutcome.Rescheduled, null);
            }

            var started = row.StartUpload(now);

            if (started.IsError)
            {
                // `publication_consent_required` (no consent) or `publication_invalid_state`: a decided, permanent answer
                // for this row, so the job must not retry it.
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
                LogStartRefused(run.PublicationId, started.FirstError.Code);

                return started.FirstError;
            }

            // S69-5/S66-3: the connection is re-checked under its own lock, before the reservation and before any
            // session. A revoked or expired connection is the owner's action to take, so the row fails for good
            // (D9) — and no Google call, no unit and no object read happen for it.
            if (connection is null || connection.Status != ChannelConnectionStatus.Active)
            {
                return await FailConnectionAsync(run, row, connection?.Status.ToString() ?? "missing", now, cancellationToken)
                    .ConfigureAwait(false);
            }

            var asset = await ResolveAssetAsync(row.EpisodeId, cancellationToken).ConfigureAwait(false);

            if (asset.IsError)
            {
                // A row with no final-video asset can never be uploaded: the verdict is terminal, the unit was never
                // reserved and nothing is released.
                return await FailDecidedAsync(run, row, cancellationToken).ConfigureAwait(false);
            }

            // Step 5: an open incident holds the row instead of failing it (UC4, YR24). Nothing is reserved here, so
            // nothing is released either.
            var open = await incidents
                .HasOpenIncidentAsync(row.Platform, connection.OAuthClientId, cancellationToken)
                .ConfigureAwait(false);

            if (open.IsError)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return open.FirstError;
            }

            if (open.Value is { } incidentId)
            {
                return await HoldAsync(run, row, incidentId, now, cancellationToken).ConfigureAwait(false);
            }

            // Step 6: one unit per `videos.insert`, and never a second one for a resumed row (YR27-2).
            if (row.QuotaReservedDay is null)
            {
                var reserved = await quota.TryReserveAsync(row.Platform, now, cancellationToken).ConfigureAwait(false);

                if (reserved.IsError)
                {
                    // D10: the day is spent, so the row waits for the next Pacific midnight plus the jitter. The unit this
                    // run tried to take was never taken, so nothing is released.
                    var rescheduled = row.RescheduleUpload(
                        PublicationErrorCodes.QuotaExhausted,
                        PtDay.NextRetryAt(now),
                        googleReason: null,
                        now);

                    if (rescheduled.IsError)
                    {
                        await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                        return rescheduled.FirstError;
                    }

                    await CommitAsync(run, cancellationToken).ConfigureAwait(false);

                    LogQuotaExhausted(run.PublicationId);

                    return (UploadOutcome.Rescheduled, null);
                }

                row.MarkQuotaReserved(reserved.Value.Day);
            }

            await CommitAsync(run, cancellationToken).ConfigureAwait(false);

            return (
                null,
                (asset.Value.StorageKey, asset.Value.ContentType, asset.Value.SizeBytes));
        }
        catch
        {
            await TryRollbackAsync(cancellationToken).ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// The reconcile path (UC1 step 3, F02): the row already names a video, so the platform is asked about it and the row
    /// is adopted — never a second <c>videos.insert</c>, whatever the answer.
    /// </summary>
    /// <param name="run">The run's state.</param>
    /// <param name="externalId">The <c>external_post_id</c> the row carries.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>The verdict of the reconcile.</returns>
    private async Task<ErrorOr<UploadOutcome>> ReconcileAsync(
        RunState run,
        string externalId,
        CancellationToken cancellationToken)
    {
        var token = await tokens.GetAccessTokenAsync(run.ConnectionId, cancellationToken).ConfigureAwait(false);

        if (token.IsError)
        {
            return await FailAsync(run, token.FirstError, cancellationToken).ConfigureAwait(false);
        }

        run.Token = token.Value;

        var videos = await youTube
            .GetVideosAsync(token.Value.AccessToken, [externalId], cancellationToken)
            .ConfigureAwait(false);

        if (videos.IsError)
        {
            return await FailAsync(run, videos.FirstError, cancellationToken).ConfigureAwait(false);
        }

        var video = videos.Value
            .FirstOrDefault(candidate => string.Equals(candidate.VideoId, externalId, StringComparison.Ordinal));

        if (video is null)
        {
            // YouTube does not know the id this row carries (the owner deleted the video). A second upload is forbidden
            // by the exactly-once rule, and the processing window is 06.2.5-T1's to end, so the row is left alone.
            LogVideoMissing(run.PublicationId);

            return UploadOutcome.Reconciled;
        }

        if (string.Equals(video.UploadStatus, UploadStatusFailed, StringComparison.OrdinalIgnoreCase))
        {
            return await ApplyAsync(
                    run,
                    new UploadFailureDecision(UploadOutcome.Failed, PublicationErrorCodes.UploadFailed),
                    ReasonError(YouTubeErrorCodes.UploadFailed, video.FailureReason),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (string.Equals(video.UploadStatus, UploadStatusRejected, StringComparison.OrdinalIgnoreCase))
        {
            return await ApplyAsync(
                    run,
                    new UploadFailureDecision(UploadOutcome.Failed, PublicationErrorCodes.Rejected),
                    ReasonError(YouTubeErrorCodes.Rejected, video.RejectionReason),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (string.Equals(video.UploadStatus, UploadStatusDeleted, StringComparison.OrdinalIgnoreCase))
        {
            LogVideoMissing(run.PublicationId);

            return UploadOutcome.Reconciled;
        }

        // `uploaded` / `processed`: the platform holds the video, so the row is adopted (idempotent for the same id).
        return await FinalizeAsync(run, externalId, reconciled: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Probes an open session and finishes, resumes or restarts it (UC1 step 8, G7).
    /// </summary>
    /// <param name="run">The run's state.</param>
    /// <param name="asset">The object to stream.</param>
    /// <param name="sessionUri">The unprotected session URI; a capability, never logged.</param>
    /// <param name="accessToken">The access token.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>The verdict of the run.</returns>
    private async Task<ErrorOr<UploadOutcome>> ResumeAsync(
        RunState run,
        (string StorageKey, string ContentType, long SizeBytes) asset,
        Uri sessionUri,
        CancellationToken cancellationToken)
    {
        var probe = await SessionCallAsync(
                run,
                (accessToken, ct) => youTube.ProbeUploadAsync(accessToken, sessionUri, asset.SizeBytes, ct),
                cancellationToken)
            .ConfigureAwait(false);

        if (probe.IsError)
        {
            return probe.FirstError.Code == YouTubeErrorCodes.SessionExpired
                ? await RestartAsync(run, asset, cancellationToken).ConfigureAwait(false)
                : await FailAsync(run, probe.FirstError, cancellationToken).ConfigureAwait(false);
        }

        return probe.Value switch
        {
            // Google already holds the whole object: the video exists, so the row adopts it and nothing is sent again.
            UploadProbe.Complete complete => await FinalizeAsync(run, complete.VideoId, reconciled: true, cancellationToken)
                .ConfigureAwait(false),

            // 404: the session is gone, so a fresh one is opened — and a fresh unit is reserved for it (YR27-3).
            UploadProbe.Expired => await RestartAsync(run, asset, cancellationToken).ConfigureAwait(false),

            // Partial: stream the rest from the offset Google still needs.
            UploadProbe.Partial partial => await ContinueAsync(run, asset, sessionUri, partial.NextByte, cancellationToken)
                .ConfigureAwait(false),

            _ => await FailAsync(
                    run,
                    Error.Failure(YouTubeErrorCodes.UploadFailed, "The probe answered a state the client does not define."),
                    cancellationToken)
                .ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Restarts an upload whose session expired: reserves a fresh unit for the new session (YR27-3) and opens it.
    /// </summary>
    /// <param name="run">The run's state.</param>
    /// <param name="asset">The object to stream.</param>
    /// <param name="accessToken">The access token.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>The verdict of the run.</returns>
    private async Task<ErrorOr<UploadOutcome>> RestartAsync(
        RunState run,
        (string StorageKey, string ContentType, long SizeBytes) asset,
        CancellationToken cancellationToken)
    {
        var again = await ReserveAgainAsync(run, cancellationToken).ConfigureAwait(false);

        if (again.IsError)
        {
            return again.FirstError;
        }

        return await StartSessionAsync(run, asset, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reserves the unit the restarted session will spend, in its own transaction (YR27-3).</summary>
    /// <param name="run">The run's state; the new reservation replaces the stored day.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns><c>Success</c>, or a refused reschedule's error.</returns>
    private async Task<ErrorOr<Success>> ReserveAgainAsync(RunState run, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await db.LockPublicationForUpdateAsync(run.PublicationId, cancellationToken).ConfigureAwait(false);

            var row = await db.Publications
                .SingleOrDefaultAsync(publication => publication.Id == run.PublicationId, cancellationToken)
                .ConfigureAwait(false);

            if (row is null)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return PublicationMissing(run.PublicationId);
            }

            // The scope's context tracks the row from PrepareAsync, and a tracked instance answers a query with its own
            // stale values and its own stale xmin token — a competing run's write in between would be missed and this
            // transaction's UPDATE would fail the token. The lock is held, so reloading it is the fresh read.
            await db.ReloadAsync(row, cancellationToken).ConfigureAwait(false);

            if (row.Status != PublicationStatus.Uploading)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return PublicationErrors.InvalidState();
            }

            var reserved = await quota.TryReserveAsync(row.Platform, now, cancellationToken).ConfigureAwait(false);

            if (reserved.IsError)
            {
                // The new session cannot be paid for, so the row waits for the next day like a fresh reschedule.
                var rescheduled = row.RescheduleUpload(
                    PublicationErrorCodes.QuotaExhausted,
                    PtDay.NextRetryAt(now),
                    googleReason: null,
                    now);

                if (rescheduled.IsError)
                {
                    await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    return rescheduled.FirstError;
                }

                await CommitAsync(run, cancellationToken).ConfigureAwait(false);

                LogQuotaExhausted(run.PublicationId);

                return reserved.FirstError;
            }

            row.ClearUploadSession();
            row.MarkQuotaReserved(reserved.Value.Day);

            await CommitAsync(run, cancellationToken).ConfigureAwait(false);

            return Result.Success;
        }
        catch
        {
            await TryRollbackAsync(cancellationToken).ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// Opens a new resumable session and streams the whole object (UC1 steps 8-9): the create is claimed in one short
    /// transaction and its answer stored in another, so neither a transaction nor the publication lock is open across the
    /// Google call; the encrypted URI is committed before the first byte, and the object is opened once and disposed in a
    /// <c>finally</c>.
    /// </summary>
    /// <param name="run">The run's state.</param>
    /// <param name="asset">The object to stream.</param>
    /// <param name="accessToken">The access token.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>The verdict of the run.</returns>
    private async Task<ErrorOr<UploadOutcome>> StartSessionAsync(
        RunState run,
        (string StorageKey, string ContentType, long SizeBytes) asset,
        CancellationToken cancellationToken)
    {
        // PERF-5: the token must outlive the stream this call is about to open, so a token within the safety margin
        // of its expiry is replaced here, before the object is opened and before the create.
        await EnsureStreamTokenAsync(run, cancellationToken).ConfigureAwait(false);

        var opened = await blobs.OpenReadAsync(Bucket, asset.StorageKey, cancellationToken).ConfigureAwait(false);

        if (opened.IsError)
        {
            return await FailStorageAsync(run, opened.FirstError, cancellationToken).ConfigureAwait(false);
        }

        var blob = opened.Value;

        try
        {
            // CR-18: one total for the whole upload, cross-checked against the object the store reports, before any
            // Google call and before the first byte.
            var total = UploadTotal.Resolve(asset.SizeBytes, blob);

            if (total.IsError)
            {
                return await FailAsync(run, total.FirstError, cancellationToken).ConfigureAwait(false);
            }

            var claim = await ClaimSessionCreateAsync(run, total.Value, asset.ContentType, cancellationToken)
                .ConfigureAwait(false);

            if (claim.IsError)
            {
                return claim.FirstError;
            }

            var (decided, metadata, resumeAt) = claim.Value;

            if (decided is { } claimed)
            {
                return claimed;
            }

            if (resumeAt is not null)
            {
                return await ResumeAsync(run, asset, resumeAt, cancellationToken).ConfigureAwait(false);
            }

            // SEC-8 / QA D3: nothing is locked and no transaction is open across the create. The claim committed above
            // is what holds the row's one-create rule: a second run reads it and backs off, while a run that dies here
            // leaves a lease that expires by time and a session at Google that the probe/restart design tolerates.
            var created = await SessionCallAsync(
                    run,
                    (accessToken, ct) => youTube.StartUploadAsync(accessToken, metadata!, total.Value, ct),
                    cancellationToken)
                .ConfigureAwait(false);

            if (created.IsError)
            {
                // Google answered, so the claim is over: the failure path ends it, and the marker stays because the
                // insert it names may have been counted (YR19).
                return await FailAsync(run, created.FirstError, cancellationToken).ConfigureAwait(false);
            }

            // F02: the cipher is committed before the first byte, by the transaction that also ends the claim.
            var attached = await AttachSessionAsync(run, created.Value, cancellationToken).ConfigureAwait(false);

            if (attached.IsError)
            {
                return attached.FirstError;
            }

            if (attached.Value is { } moved)
            {
                // The row left `uploading` while the create was in flight: the session is orphaned on purpose rather
                // than attached to a row that is no longer uploading, and a create that was sent is never given back.
                return moved;
            }

            // SEC-4: the write that claims this row's streaming window already committed in the claim transaction
            // above, and the progress callback keeps it alive while the bytes flow.
            var uploaded = await youTube
                .ResumeUploadAsync(
                    AccessTokenOf(run),
                    created.Value,
                    blob.Content,
                    total.Value,
                    new StreamLeaseProgress(this, run),
                    cancellationToken)
                .ConfigureAwait(false);

            // PERF-5: a 401 whose own token had already expired is a stale bearer, not a refused grant. The stream is
            // spent (the chunk loop read the failed chunk into its buffer and never rewinds), so the recovery
            // re-enters this whole step — the attached cipher routes the new run straight to the probe and a fresh
            // ranged open — instead of resending from an advanced stream.
            if (await RefreshTokenForStaleForbiddenAsync(run, uploaded, cancellationToken).ConfigureAwait(false))
            {
                return await StartSessionAsync(run, asset, cancellationToken).ConfigureAwait(false);
            }

            return uploaded.IsError
                ? await FailAsync(run, uploaded.FirstError, cancellationToken).ConfigureAwait(false)
                : await FinalizeAsync(run, uploaded.Value.VideoId, reconciled: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await blob.Content.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Claims the row's next session create in a short transaction (SEC-8, QA D3): the state re-check under the lock,
    /// the metadata read, and the two payload keys that say a create is being issued against this reservation. The
    /// Google call itself runs after this transaction commits.
    /// </summary>
    /// <param name="run">The run's state.</param>
    /// <param name="total">The object's total in bytes, cross-checked by <see cref="UploadTotal"/>.</param>
    /// <param name="mimeType">The object's content type.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>
    /// A decided outcome (a terminal row, a reschedule, another run's session to probe), the metadata to send when the
    /// claim was taken, or the aggregate's refusal.
    /// </returns>
    private async Task<ErrorOr<(UploadOutcome? Decided, VideoInsertRequest? Metadata, Uri? ResumeAt)>> ClaimSessionCreateAsync(
        RunState run,
        long total,
        string mimeType,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await db.LockPublicationForUpdateAsync(run.PublicationId, cancellationToken).ConfigureAwait(false);

            var row = await db.Publications
                .SingleOrDefaultAsync(publication => publication.Id == run.PublicationId, cancellationToken)
                .ConfigureAwait(false);

            if (row is null)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return PublicationMissing(run.PublicationId);
            }

            // The scope's context already tracks this row from PrepareAsync, and a tracked instance answers a query
            // with its own stale values and its own (stale) xmin token — EF would then miss a session a competing run
            // stored in between, and its UPDATE would fail the token. The lock is held, so reloading is the fresh read.
            await db.ReloadAsync(row, cancellationToken).ConfigureAwait(false);

            if (TerminalOutcome(row.Status) is { } terminal)
            {
                // Another run finished the upload while this one was on its way here: nothing to create.
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return (terminal, null, null);
            }

            if (row.Status != PublicationStatus.Uploading)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return (UploadOutcome.Rescheduled, null, null);
            }

            if (row.UploadSessionEnc is { Length: > 0 } storedSession
                && protector.TryUnprotect(run.Purpose, storedSession, out var storedPlain)
                && Uri.TryCreate(storedPlain, UriKind.Absolute, out var storedUri))
            {
                // The competing run stored its session first: probe that one, never a second create.
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return (null, null, storedUri);
            }

            if (row.UploadSessionLeaseUntil is { } held && held > now)
            {
                // Another run is calling the create right now. This run creates nothing — its outcome is the cipher or
                // an ended lease — so it asks for another attempt, or, when the attempts are spent, hands the row a
                // deferred wake instead of failing a healthy upload because a sibling holds the claim (see
                // RetryOrDeferWhileClaimedAsync).
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
                LogSessionCreateClaimed(run.PublicationId, held);

                return (await RetryOrDeferWhileClaimedAsync(run, held, cancellationToken).ConfigureAwait(false), null, null);
            }

            // SEC-4: another run owns this row's streaming window, so this one opens no session, writes nothing and
            // leaves the outcome to the run whose bytes are already flowing. WaitForStreamingRunAsync has already
            // waited out a window that was about to expire, so a lease still live here belongs to a live sibling.
            if (row.UploadStreamLeaseOwner is { } sibling
                && sibling != run.StreamLeaseId
                && row.HasLiveUploadStreamLease(now))
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
                LogStreamLeaseHeld(run.PublicationId, row.UploadStreamLeaseUntil!.Value);

                return (UploadOutcome.Rescheduled, null, null);
            }

            // F13: the request is built from the sticky plan, and a row 06.2.4-T1 has not planned yet gets one
            // derived here and persisted in this very transaction, so every later attempt sends the same request.
            var plan = row.EnsureUploadPlan(now);

            if (plan.IsError)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return plan.FirstError;
            }

            var claimed = row.ClaimUploadSessionCreate(now, now + SessionCreateLease);

            if (claimed.IsError)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return claimed.FirstError;
            }

            // The streaming window is claimed in the same write that claims the create: from here on, only this run
            // streams this row, and a second run sees the lease and backs off (SEC-4).
            var streaming = row.ClaimUploadStreamLease(run.StreamLeaseId, now, now + StreamLease);

            if (streaming.IsError)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return (UploadOutcome.Rescheduled, null, null);
            }

            run.StreamLeaseRenewedAt = now;
            run.SessionCreateIssuedAt = now;

            await CommitAsync(run, cancellationToken).ConfigureAwait(false);

            return (null, BuildRequest(row, plan.Value, total, mimeType), null);
        }
        catch
        {
            await TryRollbackAsync(cancellationToken).ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// What a run does when another run holds this row's session-create claim: another attempt, or — on the last one —
    /// a deferred wake instead of a verdict.
    /// </summary>
    /// <remarks>
    /// <b>Why a deferral and not a failure.</b> The claim being held means a sibling run is uploading exactly this row,
    /// so the row is healthy: failing it would end an upload nothing is wrong with. <c>RescheduleUpload</c> is the
    /// existing wake — it writes <c>scheduled</c>, raises <c>PublicationUpdated</c> and lets the dispatcher schedule the
    /// next attempt at the claim's own expiry, which is when the sibling's create must have answered. The error code it
    /// records is <c>youtube_upload_failed</c>, the vocabulary's closest statement for "this attempt did not complete
    /// the upload", and the next real verdict overwrites it.
    /// </remarks>
    /// <param name="run">The run's state.</param>
    /// <param name="leaseUntil">When the competing claim expires.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns><see cref="UploadOutcome.Rescheduled"/> on the last attempt; otherwise throws for another attempt.</returns>
    private async Task<UploadOutcome?> RetryOrDeferWhileClaimedAsync(
        RunState run,
        DateTimeOffset leaseUntil,
        CancellationToken cancellationToken)
    {
        if (!attempt.IsLastAttempt)
        {
            LogRetrying(run.PublicationId, PublishAttemptException.RetryCode, attempt.Attempt, attempt.MaxAttempts);

            throw new PublishAttemptException(PublishAttemptException.RetryCode);
        }

        return await DeferWhileClaimedAsync(run, leaseUntil, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records the deferred wake of a row whose session create another run holds, in its own transaction.</summary>
    /// <param name="run">The run's state.</param>
    /// <param name="leaseUntil">When the competing claim expires, and so when this row is due again.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns><see cref="UploadOutcome.Rescheduled"/>, or the aggregate's refusal when the row moved meanwhile.</returns>
    private async Task<UploadOutcome?> DeferWhileClaimedAsync(
        RunState run,
        DateTimeOffset leaseUntil,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await db.LockPublicationForUpdateAsync(run.PublicationId, cancellationToken).ConfigureAwait(false);

            var row = await db.Publications
                .SingleOrDefaultAsync(publication => publication.Id == run.PublicationId, cancellationToken)
                .ConfigureAwait(false);

            if (row is null)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return UploadOutcome.Rescheduled;
            }

            await db.ReloadAsync(row, cancellationToken).ConfigureAwait(false);

            var deferred = row.RescheduleUpload(PublicationErrorCodes.UploadFailed, leaseUntil, googleReason: null, now);

            if (deferred.IsError)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return TerminalOutcome(row.Status) ?? UploadOutcome.Rescheduled;
            }

            await CommitAsync(run, cancellationToken).ConfigureAwait(false);

            return UploadOutcome.Rescheduled;
        }
        catch
        {
            await TryRollbackAsync(cancellationToken).ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// Stores the create's answer in a short transaction (F02): the protected URI and the end of the claim, committed
    /// together, so the first byte cannot leave the process for a session the row does not name.
    /// </summary>
    /// <param name="run">The run's state.</param>
    /// <param name="sessionUri">The session Google opened; a capability, protected here and never logged.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>
    /// <see langword="null"/> when the cipher is stored and the run may stream; a verdict when the row moved on
    /// meanwhile; or the aggregate's refusal.
    /// </returns>
    private async Task<ErrorOr<UploadOutcome?>> AttachSessionAsync(
        RunState run,
        Uri sessionUri,
        CancellationToken cancellationToken)
    {
        await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await db.LockPublicationForUpdateAsync(run.PublicationId, cancellationToken).ConfigureAwait(false);

            var row = await db.Publications
                .SingleOrDefaultAsync(publication => publication.Id == run.PublicationId, cancellationToken)
                .ConfigureAwait(false);

            if (row is null)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return PublicationMissing(run.PublicationId);
            }

            await db.ReloadAsync(row, cancellationToken).ConfigureAwait(false);

            var attached = row.AttachUploadSession(protector.Protect(run.Purpose, sessionUri.AbsoluteUri));

            if (attached.IsError)
            {
                // The row was cancelled, failed or finished while the create was in flight. The session Google opened
                // has nothing to be attached to, so it is left orphaned and nothing is released: a create that was sent
                // is never given back (YR19).
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                var status = row.Status;
                LogSessionNotAttached(run.PublicationId, status);

                return TerminalOutcome(status) ?? UploadOutcome.Rescheduled;
            }

            try
            {
                await CommitAsync(run, cancellationToken).ConfigureAwait(false);
            }
            catch (PublishAttemptException)
            {
                // The create was issued, but its session is unrecoverable: the cipher rolled back with the refused
                // transaction, so nothing this row holds can address the session Google just opened. The unit was
                // committed by PrepareAsync and bought nothing streamable, so it goes back — with the day key that
                // names it — before the retry reserves its own (YR19; QA D1).
                await ReleaseAbandonedUnitAsync(run, cancellationToken).ConfigureAwait(false);

                throw;
            }

            return (UploadOutcome?)null;
        }
        catch
        {
            await TryRollbackAsync(cancellationToken).ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// Streams the rest of an object to a session that already holds a prefix (UC1 step 9): the one open, one seek and
    /// one resume, so a lost response costs the bytes it never sent and never the whole video.
    /// </summary>
    /// <param name="run">The run's state.</param>
    /// <param name="asset">The object to stream.</param>
    /// <param name="sessionUri">The open session's URI; a capability, never logged.</param>
    /// <param name="nextByte">The first byte Google still needs.</param>
    /// <param name="accessToken">The access token.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>The verdict of the run.</returns>
    private async Task<ErrorOr<UploadOutcome>> ContinueAsync(
        RunState run,
        (string StorageKey, string ContentType, long SizeBytes) asset,
        Uri sessionUri,
        long nextByte,
        CancellationToken cancellationToken)
    {
        // SEC-4: the streaming window is claimed before a byte of this resume is read, so a duplicate run that got
        // this far writes nothing instead of sending overlapping ranges into the same session.
        var streaming = await ClaimStreamingWindowAsync(run, cancellationToken).ConfigureAwait(false);

        if (streaming.IsError)
        {
            return streaming.FirstError;
        }

        if (streaming.Value is { } decided)
        {
            return decided;
        }

        // PERF-5: the token must outlive the rest of the stream, not just the probe.
        await EnsureStreamTokenAsync(run, cancellationToken).ConfigureAwait(false);

        // PERF-2/CR-17: only the bytes Google still needs are opened — a resume at 1.9 GB of 2 GB no longer reads
        // and discards 1.9 GB from object storage.
        var opened = await blobs
            .OpenReadAsync(Bucket, asset.StorageKey, nextByte, cancellationToken)
            .ConfigureAwait(false);

        if (opened.IsError)
        {
            return await FailStorageAsync(run, opened.FirstError, cancellationToken).ConfigureAwait(false);
        }

        var blob = opened.Value;

        try
        {
            var total = UploadTotal.Resolve(asset.SizeBytes, blob);

            if (total.IsError)
            {
                return await FailAsync(run, total.FirstError, cancellationToken).ConfigureAwait(false);
            }

            // CR-12/PERF-2: the ranged overload's contract is what positions the body — its first readable byte is
            // `fromByte` — so nothing here seeks or discards (a body that is already at the offset cannot be told
            // apart from a whole-object one by length or position: `BlobObject.Length` is the object's total for a
            // range read too, and S3's body is not seekable at all). CR-12's "a shrunken object ends the session"
            // is still enforced, by `UploadTotal.Resolve` against the object's own length.

            var uploaded = await youTube
                .ResumeUploadAsync(
                    AccessTokenOf(run),
                    sessionUri,
                    blob.Content,
                    total.Value,
                    new StreamLeaseProgress(this, run),
                    cancellationToken)
                .ConfigureAwait(false);

            // PERF-5: same stale-bearer recovery as the create path, and for the same reason the retry is a fresh
            // probe plus a fresh ranged open rather than a resend from this spent stream.
            if (await RefreshTokenForStaleForbiddenAsync(run, uploaded, cancellationToken).ConfigureAwait(false))
            {
                return await ResumeAsync(run, asset, sessionUri, cancellationToken).ConfigureAwait(false);
            }

            return uploaded.IsError
                ? await FailAsync(run, uploaded.FirstError, cancellationToken).ConfigureAwait(false)
                : await FinalizeAsync(run, uploaded.Value.VideoId, reconciled: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await blob.Content.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The terminal transaction (UC1 step 10): YouTube holds the video, so the row moves to <c>processing</c> with the
    /// video id, the poller's claim window and the reserved key removed.
    /// </summary>
    /// <param name="run">The run's state.</param>
    /// <param name="videoId">The video Google named.</param>
    /// <param name="reconciled">Whether the video was adopted from an earlier run instead of uploaded by this one.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns><see cref="UploadOutcome.Processing"/>, <see cref="UploadOutcome.Reconciled"/>, or an error.</returns>
    private async Task<ErrorOr<UploadOutcome>> FinalizeAsync(
        RunState run,
        string videoId,
        bool reconciled,
        CancellationToken cancellationToken)
    {
        await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await db.LockPublicationForUpdateAsync(run.PublicationId, cancellationToken).ConfigureAwait(false);

            var row = await db.Publications
                .SingleOrDefaultAsync(publication => publication.Id == run.PublicationId, cancellationToken)
                .ConfigureAwait(false);

            if (row is null)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return PublicationMissing(run.PublicationId);
            }

            // The scope's context tracks the row from PrepareAsync, and a tracked instance answers a query with its own
            // stale values and its own stale xmin token — a competing run's write in between would be missed and this
            // transaction's UPDATE would fail the token. The lock is held, so reloading it is the fresh read.
            await db.ReloadAsync(row, cancellationToken).ConfigureAwait(false);

            var now = clock.UtcNow;

            var moved = reconciled
                ? row.MarkUploadedExternal(videoId, now, ProcessingWindowMinutes)
                : row.MarkProcessing(videoId, now, ProcessingWindowMinutes);

            if (moved.IsError)
            {
                // A duplicate delivery of the same run: the row is already processing or terminal. Nothing to write, and
                // the id it names (if any) is the poller's business, never a reason to upload again.
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
                LogTransitionRefused(run.PublicationId, moved.FirstError.Code, reconciled);

                return UploadOutcome.Reconciled;
            }

            await CommitAsync(run, cancellationToken).ConfigureAwait(false);

            LogUploaded(run.PublicationId, reconciled);

            return reconciled ? UploadOutcome.Reconciled : UploadOutcome.Processing;
        }
        catch
        {
            await TryRollbackAsync(cancellationToken).ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>Classifies one failure and applies it through <see cref="ApplyAsync"/>.</summary>
    /// <param name="run">The run's state.</param>
    /// <param name="error">The failure to classify.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>The verdict of the run.</returns>
    private async Task<ErrorOr<UploadOutcome>> FailAsync(
        RunState run,
        Error error,
        CancellationToken cancellationToken)
    {
        var decision = UploadFailureMap.Map(error) ?? UploadFailureDecision.Retry;

        return await ApplyAsync(run, decision, error, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies one decision (UC1 steps 6-10 + plan §7): a retry leaves the row untouched and asks the scheduler for another
    /// attempt; everything else is one transaction that moves the row, expires the connection or raises the incident,
    /// releases the row's unit and commits — all together, so a crash can never half-apply a verdict.
    /// </summary>
    /// <param name="run">The run's state.</param>
    /// <param name="decision">The classified failure.</param>
    /// <param name="error">The error the decision came from, for its raw Google reason.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <param name="replayStart">
    /// Whether to replay a <c>scheduled → uploading</c> move this transaction needs first: the spent-attempts verdict
    /// (<see cref="TryFailExhaustedAsync"/>) can be written after a refused <see cref="PrepareAsync"/> commit rolled that
    /// move back, and <see cref="Publication.FailUpload"/> only ends an <c>uploading</c> row.
    /// </param>
    /// <returns>The verdict of the run.</returns>
    private async Task<ErrorOr<UploadOutcome>> ApplyAsync(
        RunState run,
        UploadFailureDecision decision,
        Error error,
        CancellationToken cancellationToken,
        bool replayStart = false)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var reason = UploadFailureMap.GoogleReason(error);

        if (decision.KeepsUploading && !attempt.IsLastAttempt)
        {
            // The row keeps `uploading` and its session, so the next attempt resumes from the probe's offset. A retry
            // writes no row at all, so the claims this run stamped before its create (SEC-8) and before its bytes
            // (SEC-4) are given up by `Handle`'s retry exit in a short transaction of its own: the next attempt must not
            // have to wait for a lease to expire.
            LogRetrying(run.PublicationId, error.Code, attempt.Attempt, attempt.MaxAttempts);

            throw new PublishAttemptException(PublishAttemptException.RetryCode);
        }

        // A retryable failure on the last allowed attempt must not leave the row in `uploading` with nothing left to run
        // it: the attempts are spent, which plan §7 maps to `youtube_upload_failed`.
        var effective = decision.KeepsUploading
            ? decision with { Outcome = UploadOutcome.Failed, ErrorCode = PublicationErrorCodes.UploadFailed, Retryable = false }
            : decision;

        var now = clock.UtcNow;

        await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Lock order: the connection first, the publication second (SEC-5).
            var connection = await connections.LockByIdAsync(run.ConnectionId, cancellationToken).ConfigureAwait(false);

            await db.LockPublicationForUpdateAsync(run.PublicationId, cancellationToken).ConfigureAwait(false);

            var row = await db.Publications
                .SingleOrDefaultAsync(publication => publication.Id == run.PublicationId, cancellationToken)
                .ConfigureAwait(false);

            if (row is null)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return PublicationMissing(run.PublicationId);
            }

            // The scope's context tracks the row from PrepareAsync, and a tracked instance answers a query with its own
            // stale values and its own stale xmin token — a competing run's write in between would be missed and this
            // transaction's UPDATE would fail the token. The lock is held, so reloading it is the fresh read.
            await db.ReloadAsync(row, cancellationToken).ConfigureAwait(false);

            if (replayStart && row.Status == PublicationStatus.Scheduled)
            {
                // The refused commit was the prepare's, so its `scheduled → uploading` move rolled back with it. This
                // verdict ends the row anyway, so the move is replayed inside this transaction and never committed on
                // its own — no state a consumer could observe in between.
                var replayed = row.StartUpload(now);

                if (replayed.IsError)
                {
                    await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    return replayed.FirstError;
                }
            }

            var applied = await ApplyTransitionAsync(run, row, connection, effective, error, reason, now, cancellationToken)
                .ConfigureAwait(false);

            if (applied.IsError)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
                LogTransitionRefused(run.PublicationId, applied.FirstError.Code, reconciled: false);

                return applied.FirstError;
            }

            // The verdict observed this run's create, so no claim survives it.
            row.ClearUploadSessionLease();

            // YR19 corrected / SEC-1: the release reads the ROW, never the attempt. A unit comes back only when the
            // reservation never issued a create (or the answer itself says the quota refused it — the one flag the table
            // still sets), because an insert that reached Google may have been counted. `run.Reservation` could not
            // answer this on attempt 2: it is per attempt and its null there would leave the day key behind forever.
            if (ShouldRelease(row, effective))
            {
                var released = await ReleaseRowUnitAsync(row, cancellationToken).ConfigureAwait(false);

                if (released.IsError)
                {
                    await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    return released.FirstError;
                }
            }

            await CommitAsync(run, cancellationToken).ConfigureAwait(false);

            var outcome = effective.Outcome!.Value.ToString();
            LogFailureApplied(run.PublicationId, outcome, effective.ErrorCode ?? "none");

            return effective.Outcome.Value;
        }
        catch
        {
            await TryRollbackAsync(cancellationToken).ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// YR19's release predicate, read off the row (SEC-1): the row holds a day, and either the decision is a quota class
    /// — Google itself says the insert was refused — or no create was ever issued against this reservation.
    /// </summary>
    /// <param name="row">The locked publication.</param>
    /// <param name="decision">The verdict being applied.</param>
    /// <returns>Whether the row's unit goes back to the ledger.</returns>
    private static bool ShouldRelease(Publication row, UploadFailureDecision decision) =>
        row.QuotaReservedDay is not null && (decision.ReleaseReservation || !row.HasUploadCreateIssued);

    /// <summary>
    /// Gives the row's reserved unit back to the ledger and removes the day that names it, inside the caller's
    /// transaction, so the decrement and the key can never disagree (STATUS S72-1).
    /// </summary>
    /// <param name="row">The locked publication holding <see cref="Publication.QuotaReservedDay"/>.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns><c>Success</c>, or the ledger's error.</returns>
    private async Task<ErrorOr<Success>> ReleaseRowUnitAsync(
        Publication row,
        CancellationToken cancellationToken)
    {
        // The guard is the row's own key: an actor that already released cleared it, so this can never decrement the
        // same unit twice (SEC-5).
        if (row.QuotaReservedDay is not { } day)
        {
            return Result.Success;
        }

        var released = await quota
            .ReleaseAsync(new QuotaReservation(row.Platform, day), cancellationToken)
            .ConfigureAwait(false);

        if (released.IsError)
        {
            return released.FirstError;
        }

        row.ClearQuotaReservation();

        return Result.Success;
    }

    /// <summary>
    /// Ends every claim this run still holds on the row — the session-create claim and its own streaming window — in a
    /// transaction of its own, for the paths that write no row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Best effort by design: a refused commit here must not turn "run me again" into a verdict, so the failure is logged
    /// and the lease simply expires by time, which is the same state a crashed run leaves.
    /// </para>
    /// <para>
    /// <b>Both claims, because both block the next attempt.</b> A retryable exit keeps the row <c>uploading</c> with its
    /// session, so the very next delivery of this row is the one that must stream it. A window left behind by this run
    /// reads as a live sibling's (SEC-4) and makes that delivery answer "nothing to do" until the lease expires — the
    /// same hang a duplicate run is supposed to be protected from. Since the run has finished (its exception is on its
    /// way to the scheduler), the window is not streaming anything: it is ended here, owner-scoped, so a lease another
    /// run took over in between is never cleared.
    /// </para>
    /// </remarks>
    /// <param name="run">The run's state; its own id is the streaming lease's owner.</param>
    /// <param name="cancellationToken">The token the release itself runs under.</param>
    private async Task ReleaseRunClaimsAsync(RunState run, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

            await db.LockPublicationForUpdateAsync(run.PublicationId, cancellationToken).ConfigureAwait(false);

            var row = await db.Publications
                .SingleOrDefaultAsync(publication => publication.Id == run.PublicationId, cancellationToken)
                .ConfigureAwait(false);

            if (row is null)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return;
            }

            await db.ReloadAsync(row, cancellationToken).ConfigureAwait(false);

            // The committed truth: a run that never got as far as claiming anything has nothing to give back, and the
            // rollback keeps this transaction from writing anything at all. The session-create claim has no owner key,
            // so it is recognised by the marker this run stamped when the claim committed: a claim another run took
            // over carries its own instant, and clearing that one would hand a live sibling's create to a third run.
            var holdsTheCreate = run.SessionCreateIssuedAt is { } issued
                && row.UploadCreateIssuedAt == issued;

            if (!holdsTheCreate && row.UploadStreamLeaseOwner != run.StreamLeaseId)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return;
            }

            if (holdsTheCreate)
            {
                row.ClearUploadSessionLease();
            }

            // Owner-scoped: a window this run no longer owns belongs to the run that took it over (SEC-4).
            row.ReleaseUploadStreamLease(run.StreamLeaseId);

            await CommitAsync(run, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            await TryRollbackAsync(cancellationToken).ConfigureAwait(false);
            LogClaimReleaseFailed(run.PublicationId, failure.GetType().FullName ?? "unknown");
        }
    }

    /// <summary>The state move of one decided failure, inside the failure transaction.</summary>
    /// <param name="run">The run's state.</param>
    /// <param name="row">The locked publication.</param>
    /// <param name="connection">The locked connection, or <see langword="null"/> when it is gone.</param>
    /// <param name="decision">The verdict to write.</param>
    /// <param name="error">The error it came from (its code names the incident).</param>
    /// <param name="reason">The sanitised Google reason, or <see langword="null"/>.</param>
    /// <param name="now">The clock reading.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns><c>Success</c>, or the aggregate's refusal.</returns>
    private async Task<ErrorOr<Success>> ApplyTransitionAsync(
        RunState run,
        Publication row,
        ChannelConnection? connection,
        UploadFailureDecision decision,
        Error error,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (decision.RaiseIncident)
        {
            // `invalid_client`: the client, not this owner's grant, is the problem — so the incident is raised and the row
            // is held under it in the same commit (plan §7, YR24). Nothing about the row is failed.
            if (connection is null)
            {
                return PublicationErrors.InvalidState();
            }

            var raised = await incidents
                .RaiseAsync(row.Platform, connection.OAuthClientId, error.Code, now, cancellationToken)
                .ConfigureAwait(false);

            if (raised.IsError)
            {
                return raised.FirstError;
            }

            LogIncidentRaised(run.PublicationId, raised.Value.IncidentId, raised.Value.IsNew);

            return row.HoldForIncident(
                raised.Value.IncidentId,
                now.AddSeconds(HoldPollIntervals * (double)options.Poll.IntervalSeconds),
                now);
        }

        if (decision.ExpireConnection && connection is not null && connection.Status == ChannelConnectionStatus.Active)
        {
            // The grant is dead (invalid_grant): the connection moves to `expired` and announces itself with
            // `channel_connection_changed.v1` in this very commit, so the platform sees one dead channel, not one per retry.
            connection.ExpireForReauth(now);
        }

        return decision.Outcome == UploadOutcome.Rescheduled
            ? row.RescheduleUpload(decision.ErrorCode!, PtDay.NextRetryAt(now), reason, now)
            : row.FailUpload(decision.ErrorCode!, reason, now);
    }

    /// <summary>The terminal answer of a connection that can no longer be used (S69-5/S66-3), inside the first transaction.</summary>
    /// <param name="run">The run's state.</param>
    /// <param name="row">The locked publication, already <c>uploading</c>.</param>
    /// <param name="connectionStatus">The status the connection carries, for the log line.</param>
    /// <param name="now">The clock reading.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns><see cref="UploadOutcome.ReauthRequired"/>, or the aggregate's refusal.</returns>
    private async Task<ErrorOr<(UploadOutcome? Decided, (string StorageKey, string ContentType, long SizeBytes)? Asset)>>
        FailConnectionAsync(
            RunState run,
            Publication row,
            string connectionStatus,
            DateTimeOffset now,
            CancellationToken cancellationToken)
    {
        var failed = row.FailUpload(PublicationErrorCodes.ChannelReauthRequired, googleReason: null, now);

        if (failed.IsError)
        {
            await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return failed.FirstError;
        }

        await CommitAsync(run, cancellationToken).ConfigureAwait(false);

        LogConnectionUnusable(run.PublicationId, connectionStatus);

        return (UploadOutcome.ReauthRequired, null);
    }

    /// <summary>Holds the row under an already open incident, inside the first transaction (UC1 step 5).</summary>
    /// <param name="run">The run's state.</param>
    /// <param name="row">The locked publication, already <c>uploading</c>.</param>
    /// <param name="incidentId">The open incident's id.</param>
    /// <param name="now">The clock reading.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns><see cref="UploadOutcome.HeldByIncident"/>, or the aggregate's refusal.</returns>
    private async Task<ErrorOr<(UploadOutcome? Decided, (string StorageKey, string ContentType, long SizeBytes)? Asset)>>
        HoldAsync(
            RunState run,
            Publication row,
            Guid incidentId,
            DateTimeOffset now,
            CancellationToken cancellationToken)
    {
        var held = row.HoldForIncident(
            incidentId,
            now.AddSeconds(HoldPollIntervals * (double)options.Poll.IntervalSeconds),
            now);

        if (held.IsError)
        {
            await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return held.FirstError;
        }

        await CommitAsync(run, cancellationToken).ConfigureAwait(false);

        LogHeldByIncident(run.PublicationId, incidentId);

        return (UploadOutcome.HeldByIncident, null);
    }

    /// <summary>Ends a run that cannot proceed for a reason the aggregate can still record, inside the first transaction.</summary>
    /// <param name="run">The run's state.</param>
    /// <param name="row">The locked publication, already <c>uploading</c>.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>The verdict, or the aggregate's refusal.</returns>
    private async Task<ErrorOr<(UploadOutcome? Decided, (string StorageKey, string ContentType, long SizeBytes)? Asset)>>
        FailDecidedAsync(
            RunState run,
            Publication row,
            CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var failed = row.FailUpload(PublicationErrorCodes.UploadFailed, googleReason: null, now);

        if (failed.IsError)
        {
            await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return failed.FirstError;
        }

        await CommitAsync(run, cancellationToken).ConfigureAwait(false);

        var terminal = UploadOutcome.Failed.ToString();
        LogFailureApplied(run.PublicationId, terminal, PublicationErrorCodes.UploadFailed);

        return (UploadOutcome.Failed, null);
    }

    /// <summary>A storage failure: a missing object is terminal, an outage is another attempt — or a verdict on the last one.</summary>
    /// <param name="run">The run's state.</param>
    /// <param name="error">The store's error.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>The verdict of the run.</returns>
    private async Task<ErrorOr<UploadOutcome>> FailStorageAsync(
        RunState run,
        Error error,
        CancellationToken cancellationToken)
    {
        if (error.Code != ErrorCodes.NotFound)
        {
            // An outage: another attempt, and on the last one the row gets the terminal verdict instead of being left
            // `uploading` with nothing to run it (SEC-2).
            return await RetryOrFailAsync(run, error, cancellationToken).ConfigureAwait(false);
        }

        // The object is gone, so no attempt can ever upload this row.
        return await ApplyAsync(
                run,
                new UploadFailureDecision(UploadOutcome.Failed, PublicationErrorCodes.UploadFailed),
                Error.Failure(YouTubeErrorCodes.UploadFailed, "The episode's video object no longer exists in storage."),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The publication's final-video asset, resolved through the episode's own pointer (CR-18).</summary>
    /// <param name="episodeId">The row's episode.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>The object's key, type and declared size, or <c>youtube_upload_failed</c> before any Google call.</returns>
    private async Task<ErrorOr<(string StorageKey, string ContentType, long SizeBytes)>> ResolveAssetAsync(
        Guid episodeId,
        CancellationToken cancellationToken)
    {
        var assetId = await db.Episodes
            .Where(episode => episode.Id == episodeId)
            .Select(episode => episode.FinalVideoAssetId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (assetId is not { } id)
        {
            return Error.Failure(
                YouTubeErrorCodes.UploadFailed,
                "The episode has no final video asset, so there is nothing to upload.");
        }

        var asset = await db.Assets
            .AsNoTracking()
            .Where(candidate => candidate.Id == id
                && candidate.Kind == AssetKind.Video
                && candidate.DeletedAt == null)
            .Select(candidate => new { candidate.StorageKey, candidate.ContentType, candidate.SizeBytes })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (asset is null || asset.SizeBytes <= 0 || string.IsNullOrWhiteSpace(asset.StorageKey))
        {
            return Error.Failure(
                YouTubeErrorCodes.UploadFailed,
                "The episode's final video asset is missing, deleted or empty.");
        }

        return (asset.StorageKey, asset.ContentType, asset.SizeBytes);
    }

    /// <summary>
    /// The insert one row's sticky plan implies (F13, D6: synthetic media is always declared): the title, the
    /// description and the tags come from the row, and <c>privacyStatus</c>/<c>publishAt</c> come <em>only</em> from
    /// the plan — never from the live columns, whose values an approval can change under a row that was planned
    /// pre-audit.
    /// </summary>
    /// <param name="row">The locked, freshly reloaded row (never a whole-row projection: the plan is a payload key).</param>
    /// <param name="plan">The plan to send verbatim.</param>
    /// <param name="total">The object's total in bytes, cross-checked by <see cref="UploadTotal"/>.</param>
    /// <param name="mimeType">The object's content type.</param>
    /// <returns>The complete request.</returns>
    private static VideoInsertRequest BuildRequest(
        Publication row,
        PublicationPayload.UploadPlan plan,
        long total,
        string mimeType) =>
        new(
            Title: row.Title,
            Description: row.Description,
            Tags: [.. row.Tags],
            PrivacyStatus: plan.PrivacyStatus,
            PublishAt: plan.PublishAt,
            ContainsSyntheticMedia: true,
            SelfDeclaredMadeForKids: false,
            Length: total,
            MimeType: mimeType);

    /// <summary>
    /// Waits out the tail of a streaming lease another run holds (SEC-4), and answers the lease's instant when it
    /// still belongs to a live sibling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No write, ever.</b> A duplicate delivery that finds a live window must not touch the row: setting it back to
    /// <c>scheduled</c> would make the streaming run's own <c>uploading → processing</c> move fail and orphan a video
    /// Google already holds (the SEC-4 hazard). It therefore answers "nothing to do", which the outbox's own wake for
    /// the sibling's outcome makes a complete answer.
    /// </para>
    /// <para>
    /// <b>Why it waits at all.</b> A lease with more time left than <see cref="StreamLeaseWait"/> is being renewed by a
    /// live run, so there is nothing to wait for. A shorter one is the tail of a run that died — it stopped renewing —
    /// and waiting those few seconds is how the row becomes claimable again without an operator.
    /// </para>
    /// </remarks>
    /// <param name="publicationId">The row.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>The live lease's instant, or <see langword="null"/> when no run is streaming this row.</returns>
    private async Task<DateTimeOffset?> WaitForStreamingRunAsync(Guid publicationId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var payload = await db.Publications
                .AsNoTracking()
                .Where(publication => publication.Id == publicationId)
                .Select(publication => publication.PlatformPayloadJson)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            var now = clock.UtcNow;

            if (PublicationPayload.ReadInstant(payload, PublicationPayload.UploadStreamLeaseUntilKey) is not { } leaseUntil
                || leaseUntil <= now)
            {
                return null;
            }

            if (leaseUntil - now > StreamLeaseWait)
            {
                return leaseUntil;
            }

            var wait = leaseUntil - now < StreamLeasePollInterval ? leaseUntil - now : StreamLeasePollInterval;

            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Claims this row's streaming window for the run in its own short transaction (SEC-4), before a byte of a resume
    /// is read: the state re-check under the lock, the terminal answer a row that moved on deserves, and the lease a
    /// duplicate run stops at.
    /// </summary>
    /// <param name="run">The run, whose own id is the lease's owner.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>
    /// <see langword="null"/> when the window is this run's; the outcome to answer when the row finished or another
    /// run owns it (a no-op <c>rescheduled</c>, never a write); or the aggregate's refusal.
    /// </returns>
    private async Task<ErrorOr<UploadOutcome?>> ClaimStreamingWindowAsync(
        RunState run,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await db.LockPublicationForUpdateAsync(run.PublicationId, cancellationToken).ConfigureAwait(false);

            var row = await db.Publications
                .SingleOrDefaultAsync(publication => publication.Id == run.PublicationId, cancellationToken)
                .ConfigureAwait(false);

            if (row is null)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return PublicationMissing(run.PublicationId);
            }

            await db.ReloadAsync(row, cancellationToken).ConfigureAwait(false);

            if (TerminalOutcome(row.Status) is { } terminal)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return (UploadOutcome?)terminal;
            }

            if (row.Status != PublicationStatus.Uploading)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return (UploadOutcome?)UploadOutcome.Rescheduled;
            }

            if (row.UploadStreamLeaseOwner is { } sibling
                && sibling != run.StreamLeaseId
                && row.HasLiveUploadStreamLease(now))
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
                LogStreamLeaseHeld(run.PublicationId, row.UploadStreamLeaseUntil!.Value);

                return (UploadOutcome?)UploadOutcome.Rescheduled;
            }

            var streaming = row.ClaimUploadStreamLease(run.StreamLeaseId, now, now + StreamLease);

            if (streaming.IsError)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return (UploadOutcome?)UploadOutcome.Rescheduled;
            }

            run.StreamLeaseRenewedAt = now;

            await CommitAsync(run, cancellationToken).ConfigureAwait(false);

            return (UploadOutcome?)null;
        }
        catch
        {
            await TryRollbackAsync(cancellationToken).ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// Renews this row's streaming lease when the throttle allows it (SEC-4): called between chunks, from the upload's
    /// own progress callback.
    /// </summary>
    /// <remarks>
    /// <b>Blocking on purpose.</b> The callback runs on the thread that is sending the next chunk, so renewing here can
    /// never overlap this run's own <c>DbContext</c> use and can never land after the outcome transaction: a run whose
    /// lease another run took over stops writing at once (<see cref="Publication.RenewUploadStreamLease"/> answers
    /// <see langword="false"/>) and simply finishes its stream.
    /// </remarks>
    /// <param name="run">The run renewing its window.</param>
    private void RenewStreamLease(RunState run)
    {
        var now = clock.UtcNow;

        if (now - run.StreamLeaseRenewedAt < StreamLeaseRenewEvery)
        {
            return;
        }

        run.StreamLeaseRenewedAt = now;

        try
        {
            RenewStreamLeaseAsync(run, now + StreamLease).GetAwaiter().GetResult();
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // Best effort: a lease that fails to renew expires by time, which is the state a crashed run leaves and
            // the one the waiting run is built to recover from.
            LogStreamLeaseRenewFailed(run.PublicationId, failure.GetType().FullName ?? "unknown");
        }
    }

    /// <summary>Writes one renewal of the streaming lease in a transaction of its own.</summary>
    /// <param name="run">The run renewing its window.</param>
    /// <param name="leaseUntil">The new expiry.</param>
    /// <returns>Nothing; a refusal is logged, never thrown at the stream.</returns>
    private async Task RenewStreamLeaseAsync(RunState run, DateTimeOffset leaseUntil)
    {
        await unitOfWork.BeginAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {
            await db.LockPublicationForUpdateAsync(run.PublicationId, CancellationToken.None).ConfigureAwait(false);

            var row = await db.Publications
                .SingleOrDefaultAsync(
                    publication => publication.Id == run.PublicationId,
                    CancellationToken.None)
                .ConfigureAwait(false);

            if (row is null)
            {
                await unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                return;
            }

            await db.ReloadAsync(row, CancellationToken.None).ConfigureAwait(false);

            if (!row.RenewUploadStreamLease(run.StreamLeaseId, leaseUntil))
            {
                // Another run owns the window now (this lease expired under a stall): writing would steal it back.
                await unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                return;
            }

            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            var committed = await unitOfWork.CommitAsync(CancellationToken.None).ConfigureAwait(false);

            if (committed.IsError)
            {
                LogStreamLeaseRenewFailed(run.PublicationId, committed.FirstError.Code);
            }
        }
        catch
        {
            await TryRollbackAsync(CancellationToken.None).ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// Asks for a fresh access token when the one this run holds would expire inside
    /// <see cref="TokenSafetyMargin"/> (PERF-5): the token is fetched once per run, and an upload can easily outlive
    /// it.
    /// </summary>
    /// <param name="run">The run, whose token is replaced when a fresh one arrives.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>Nothing; a refused refresh leaves the current token in place (it still works until it expires).</returns>
    private async Task EnsureStreamTokenAsync(RunState run, CancellationToken cancellationToken)
    {
        if (run.Token is not { } token
            || run.StreamTokenRefreshed
            || token.ExpiresAt - clock.UtcNow > TokenSafetyMargin)
        {
            return;
        }

        run.StreamTokenRefreshed = true;

        var fresh = await tokens.GetAccessTokenAsync(run.ConnectionId, cancellationToken).ConfigureAwait(false);

        if (fresh.IsError)
        {
            LogTokenRefreshFailed(run.PublicationId, fresh.FirstError.Code);

            return;
        }

        run.Token = fresh.Value;

        LogTokenRefreshed(run.PublicationId, fresh.Value.ExpiresAt);
    }

    /// <summary>
    /// Runs one session-scoped Google call, and gives a <c>401 authError</c> whose own token was already past its
    /// expiry one fresh token and one retry instead of the dead-grant verdict (PERF-5).
    /// </summary>
    /// <remarks>
    /// A <c>401</c> against a token that is still valid stays <c>youtube_reauth_required</c>: that is the owner's grant
    /// being refused, which no retry fixes (plan §7). Only the stale-bearer case — the token the run is holding says it
    /// expired — is a retry, and only once per run, so a genuinely revoked grant cannot loop.
    /// </remarks>
    /// <typeparam name="T">The call's success type.</typeparam>
    /// <param name="run">The run whose token the call uses, and where a refreshed one lands.</param>
    /// <param name="call">The call, given the token to send.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>The call's answer, or the retried call's answer.</returns>
    private async Task<ErrorOr<T>> SessionCallAsync<T>(
        RunState run,
        Func<string, CancellationToken, Task<ErrorOr<T>>> call,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

        var answer = await call(AccessTokenOf(run), cancellationToken).ConfigureAwait(false);

        if (!answer.IsError
            || answer.FirstError.Code != YouTubeErrorCodes.ReauthRequired
            || run.Token is not { } token
            || token.ExpiresAt > clock.UtcNow
            || run.TokenRefreshedAfterAReauth)
        {
            return answer;
        }

        run.TokenRefreshedAfterAReauth = true;

        var fresh = await tokens.GetAccessTokenAsync(run.ConnectionId, cancellationToken).ConfigureAwait(false);

        if (fresh.IsError)
        {
            LogTokenRefreshFailed(run.PublicationId, fresh.FirstError.Code);

            return answer;
        }

        run.Token = fresh.Value;

        LogTokenRefreshed(run.PublicationId, fresh.Value.ExpiresAt);

        return await call(fresh.Value.AccessToken, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers whether a session call was refused as unauthorized by a bearer that had <em>already expired</em>, and
    /// refreshes that bearer as a side effect (PERF-5): the caller re-enters its streaming step, because the stream it
    /// was sending from is spent and cannot be resent from where it stopped.
    /// </summary>
    /// <remarks>
    /// A <c>401</c> against a token that is still valid is the owner's grant being refused and stays
    /// <c>youtube_reauth_required</c> (plan §7): no refresh, no retry. The one retry per run is what keeps a revoked
    /// grant from looping.
    /// </remarks>
    /// <typeparam name="T">The refused call's success type; only its failure is read.</typeparam>
    /// <param name="run">The run whose token is replaced when a fresh one arrives.</param>
    /// <param name="answer">The call's answer.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns><see langword="true"/> when a fresh token arrived and the caller should start the step again.</returns>
    private async Task<bool> RefreshTokenForStaleForbiddenAsync<T>(
        RunState run,
        ErrorOr<T> answer,
        CancellationToken cancellationToken)
    {
        if (!answer.IsError
            || answer.FirstError.Code != YouTubeErrorCodes.ReauthRequired
            || run.Token is not { } token
            || token.ExpiresAt > clock.UtcNow
            || run.StreamRetriedAfterRefresh)
        {
            return false;
        }

        run.StreamRetriedAfterRefresh = true;

        var fresh = await tokens.GetAccessTokenAsync(run.ConnectionId, cancellationToken).ConfigureAwait(false);

        if (fresh.IsError)
        {
            LogTokenRefreshFailed(run.PublicationId, fresh.FirstError.Code);

            return false;
        }

        run.Token = fresh.Value;

        LogTokenRefreshed(run.PublicationId, fresh.Value.ExpiresAt);

        return true;
    }

    /// <summary>The bearer the run sends, which every step after the token fetch has already stored on it.</summary>
    /// <param name="run">The run.</param>
    /// <returns>The access token.</returns>
    /// <exception cref="InvalidOperationException">The run is calling Google before it fetched a token.</exception>
    private static string AccessTokenOf(RunState run) =>
        run.Token?.AccessToken
        ?? throw new InvalidOperationException("The upload run has no access token yet.");

    /// <summary>
    /// The progress hook that keeps the row's streaming lease alive (SEC-4): the client reports the confirmed byte
    /// count between chunks, and every report past the throttle renews the window.
    /// </summary>
    /// <param name="Owner">The handler the renew runs in.</param>
    /// <param name="Run">The run the lease belongs to.</param>
    private sealed class StreamLeaseProgress(PublishYouTubeHandler Owner, RunState Run) : IProgress<long>
    {
        /// <inheritdoc />
        public void Report(long value) => Owner.RenewStreamLease(Run);
    }

    /// <summary>Saves and commits the caller's open transaction, or ends the attempt so the scheduler runs it again.</summary>
    /// <remarks>
    /// <para>
    /// <b>A refused commit is never a verdict.</b> The unit of work rolls a refused commit back, so nothing this run
    /// decided was written — not the transition, not the cipher, not the ledger release — and the only thing that can
    /// write them is another attempt. Answering the refusal as an <c>ErrorOr</c> made it indistinguishable from a
    /// decided outcome: the job swallows every one of those, so a transient failure at the commit left the row
    /// <c>uploading</c> with no session, no unit and nothing to wake it (QA D1). It therefore travels the way "run me
    /// again" travels here — <see cref="PublishAttemptException"/>, the same statement
    /// <see cref="ApplyAsync"/> makes for a retryable Google failure.
    /// </para>
    /// <para>
    /// <b>Unless there is no attempt left.</b> On the last one a refusal would otherwise strand the row for good, so
    /// <see cref="TryFailExhaustedAsync"/> gives it the terminal verdict first — in a transaction of its own, best effort
    /// — and the refusal is still reported as "run me again" (SEC-2).
    /// </para>
    /// <para>
    /// <b>What a caller owes before the throw escapes.</b> Nothing, except the session-URI commit:
    /// <see cref="AttachSessionAsync"/> gives the row's unit back there, because that one was committed by
    /// <see cref="PrepareAsync"/> before the cipher was written (see <see cref="ReleaseAbandonedUnitAsync"/>). Every
    /// other site reserves inside the very transaction that was refused, so its reserve rolled back with it and a
    /// release would take another row's unit.
    /// </para>
    /// </remarks>
    /// <param name="run">The run, for the log line.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <exception cref="PublishAttemptException">The save or the commit was refused.</exception>
    private async Task CommitAsync(RunState run, CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var committed = await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (!committed.IsError)
        {
            return;
        }

        LogCommitRefused(run.PublicationId, committed.FirstError.Code);

        await TryFailExhaustedAsync(
                run,
                Error.Failure(committed.FirstError.Code, "The unit of work refused the commit."),
                cancellationToken)
            .ConfigureAwait(false);

        throw new PublishAttemptException(PublishAttemptException.InfrastructureCode);
    }

    /// <summary>
    /// The one "run me again" statement of this handler: another attempt, and on the last one the terminal verdict plan
    /// §7 assigns to spent attempts first, so the row is never left with nothing that would ever run it (SEC-2).
    /// </summary>
    /// <param name="run">The run's state.</param>
    /// <param name="error">The failure that asked for the attempt.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>Never: it always ends the attempt.</returns>
    /// <exception cref="PublishAttemptException">The retry request, on every attempt.</exception>
    private async Task<ErrorOr<UploadOutcome>> RetryOrFailAsync(
        RunState run,
        Error error,
        CancellationToken cancellationToken)
    {
        await TryFailExhaustedAsync(run, error, cancellationToken).ConfigureAwait(false);

        if (!attempt.IsLastAttempt)
        {
            LogRetrying(run.PublicationId, error.Code, attempt.Attempt, attempt.MaxAttempts);
        }

        throw new PublishAttemptException(PublishAttemptException.RetryCode);
    }

    /// <summary>
    /// Gives a row whose attempts are spent the verdict that ends it (<c>youtube_upload_failed</c>), through the ordinary
    /// failure transaction, so the release rule and the payload keys behave exactly as they do for a decided failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Best effort, never in place of the exception.</b> The caller is about to throw the retry request that ends the
    /// job as failed; a verdict that cannot be written is logged by type and changes nothing, because replacing the
    /// failure with a second one would only hide the first.
    /// </para>
    /// <para>
    /// <b>At most once per attempt.</b> <see cref="CommitAsync"/> may be called from inside the very transaction this
    /// writes — a refused verdict commit is still a refused commit — so the flag stops that recursion at one retry.
    /// </para>
    /// </remarks>
    /// <param name="run">The run's state.</param>
    /// <param name="error">The failure the verdict is written for.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    private async Task TryFailExhaustedAsync(RunState run, Error error, CancellationToken cancellationToken)
    {
        if (!attempt.IsLastAttempt || finalVerdictAttempted)
        {
            return;
        }

        finalVerdictAttempted = true;

        try
        {
            var verdict = await ApplyAsync(
                    run,
                    new UploadFailureDecision(UploadOutcome.Failed, PublicationErrorCodes.UploadFailed),
                    error,
                    cancellationToken,
                    replayStart: true)
                .ConfigureAwait(false);

            if (verdict.IsError)
            {
                LogFinalVerdictFailed(run.PublicationId, verdict.FirstError.Code);
            }
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            LogFinalVerdictFailed(run.PublicationId, failure.GetType().FullName ?? "unknown");
        }
    }

    /// <summary>
    /// Gives back the unit of a session nobody can resume, after a refused commit rolled its cipher back, and removes
    /// the reserved day with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The row decides, not the attempt.</b> The unit is released only when the reloaded row still holds a
    /// <c>quotaReservedDay</c>: another actor that already gave it back cleared the key, and a second decrement would
    /// take some other row's unit (SEC-5). This is why the release no longer reads a per-attempt reservation — attempt 2
    /// of a run has none, and its unit is exactly the one this method has to return.
    /// </para>
    /// <para>
    /// <b>Why a release and not just the throw.</b> <see cref="PrepareAsync"/> committed that unit with everything else
    /// before the create, so it is spent ledger state, not a reservation this failed transaction rolled back — the
    /// opposite of a refused <see cref="PrepareAsync"/> commit, where a release would take another row's unit because
    /// the reserve never landed. The create that bought nothing streamable is the one case in which a unit goes back
    /// although an insert was issued (YR19's exception, QA D1).
    /// </para>
    /// <para>
    /// <b>Publication first, ledger second.</b> The publication row is locked before the ledger is touched, the order the
    /// cancel path takes, so a cancel racing this rare release queues instead of deadlocking (PERF-8). The removal of the
    /// day key is part of the same transaction as the decrement, so the two can never disagree (STATUS S72-1). It is its
    /// own transaction because the refused commit already ended the failed one, and a failure here rolls both back rather
    /// than leaving one of the two written.
    /// </para>
    /// </remarks>
    /// <param name="run">The run whose row's reservation goes back.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    private async Task ReleaseAbandonedUnitAsync(RunState run, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

            await db.LockPublicationForUpdateAsync(run.PublicationId, cancellationToken).ConfigureAwait(false);

            var row = await db.Publications
                .SingleOrDefaultAsync(publication => publication.Id == run.PublicationId, cancellationToken)
                .ConfigureAwait(false);

            if (row is null)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return;
            }

            // The tracked entity still carries this run's uncommitted edits (the cipher above all); reloading takes the
            // committed truth — `uploading`, no session, the reserved day — before the key is read and removed.
            await db.ReloadAsync(row, cancellationToken).ConfigureAwait(false);

            var released = await ReleaseRowUnitAsync(row, cancellationToken).ConfigureAwait(false);

            if (released.IsError)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
                LogAbandonedUnitReleaseFailed(run.PublicationId, released.FirstError.Code);

                return;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var committed = await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);

            if (committed.IsError)
            {
                // Nothing more this attempt can do: the retry the exception asks for is still the right answer, and the
                // unit is the one thing an operator has to reconcile by hand if this line ever appears.
                LogAbandonedUnitReleaseFailed(run.PublicationId, committed.FirstError.Code);
            }
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            await TryRollbackAsync(cancellationToken).ConfigureAwait(false);
            LogAbandonedUnitReleaseFailed(run.PublicationId, failure.GetType().FullName ?? "unknown");
        }
    }

    /// <summary>Rolls back without masking the exception that brought the run here.</summary>
    /// <param name="cancellationToken">The attempt's token.</param>
    private async Task TryRollbackAsync(CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception rollbackFailure)
        {
            LogRollbackFailed(rollbackFailure.GetType().FullName ?? "unknown");
        }
    }

    /// <summary>The verdict a terminal row answers: nothing to upload, nothing written.</summary>
    /// <param name="status">The row's status.</param>
    /// <returns>The outcome, or <see langword="null"/> when the status is not terminal for the upload.</returns>
    private static UploadOutcome? TerminalOutcome(PublicationStatus status) => status switch
    {
        PublicationStatus.Processing => UploadOutcome.Processing,
        PublicationStatus.Published => UploadOutcome.Reconciled,
        PublicationStatus.Failed => UploadOutcome.Failed,
        PublicationStatus.Cancelled => UploadOutcome.Reconciled,
        _ => null,
    };

    /// <summary>The catalogue error of a row this scope cannot see.</summary>
    /// <param name="publicationId">The row's id, for the log.</param>
    /// <returns>The <c>not_found</c> error.</returns>
    private static Error PublicationMissing(Guid publicationId) =>
        Error.NotFound(ErrorCodes.NotFound, $"The publication '{publicationId}' does not exist.");

    /// <summary>An error that carries a raw Google reason for the diagnostic column.</summary>
    /// <param name="code">The catalogue code.</param>
    /// <param name="reason">The sanitised Google reason, or <see langword="null"/>.</param>
    /// <returns>The error, with the failure metadata when a reason is present.</returns>
    private static Error ReasonError(string code, string? reason)
    {
        var sanitized = YouTubeFailure.SanitizeReason(reason);

        return sanitized is null
            ? Error.Failure(code, "The platform refused the video.")
            : Error.Failure(
                code,
                "The platform refused the video.",
                new YouTubeFailure(sanitized, null, false).ToMetadata());
    }

    /// <summary>What one run of the handler carries between its steps.</summary>
    /// <param name="PublicationId">The row being uploaded.</param>
    /// <param name="ConnectionId">The row's channel connection.</param>
    private sealed class RunState(Guid publicationId, Guid connectionId)
    {
        /// <summary>The publication.</summary>
        public Guid PublicationId { get; } = publicationId;

        /// <summary>The connection whose token and incident the run uses.</summary>
        public Guid ConnectionId { get; } = connectionId;

        /// <summary>The protection purpose of the session cipher: <c>tokens.v1/{connectionId}</c> (06 README D4).</summary>
        public string Purpose => ConnectionId.ToString();

        /// <summary>
        /// The access token this run streams with: stored once step 7 fetched it, and replaced by a fresh one when it
        /// is about to expire or when a <c>401</c> named a bearer that already had (PERF-5).
        /// </summary>
        public ChannelAccessToken? Token { get; set; }

        /// <summary>Whether this run already asked for a fresh token before opening a stream (at most once).</summary>
        public bool StreamTokenRefreshed { get; set; }

        /// <summary>Whether this run already answered a stale-bearer <c>401</c> with a fresh token (at most once).</summary>
        public bool TokenRefreshedAfterAReauth { get; set; }

        /// <summary>
        /// Whether this run already re-entered its streaming step after a stale-bearer <c>401</c> (at most once): the
        /// recovery throws away the spent stream and starts again from the probe, so it must not loop.
        /// </summary>
        public bool StreamRetriedAfterRefresh { get; set; }

        /// <summary>
        /// The instant this run stamped as <c>platform_payload.uploadCreateIssuedAt</c> when its session-create claim
        /// committed, or <see langword="null"/> when this run never claimed one. The claim carries no owner key, so the
        /// release compares this against the row's marker: a mismatching marker belongs to a run that took the claim
        /// over (SEC-8).
        /// </summary>
        public DateTimeOffset? SessionCreateIssuedAt { get; set; }

        /// <summary>
        /// This run's own id as the owner of the row's streaming lease (<c>platform_payload.uploadStreamLeaseOwner</c>,
        /// SEC-4): fresh per run, so a run that lost its window can never renew or release another run's.
        /// </summary>
        public Guid StreamLeaseId { get; } = Guid.CreateVersion7();

        /// <summary>When this run last wrote its streaming lease, for the renewal throttle.</summary>
        public DateTimeOffset StreamLeaseRenewedAt { get; set; }
    }

    [LoggerMessage(
        EventId = 6614,
        Level = LogLevel.Information,
        Message = "publication_upload_feature_disabled publicationId={PublicationId}")]
    private partial void LogFeatureDisabled(Guid publicationId);

    [LoggerMessage(
        EventId = 6615,
        Level = LogLevel.Information,
        Message = "publication_upload_terminal_noop publicationId={PublicationId} status={Status}")]
    private partial void LogTerminalNoOp(Guid publicationId, string status);

    [LoggerMessage(
        EventId = 6616,
        Level = LogLevel.Warning,
        Message = "publication_upload_start_refused publicationId={PublicationId} code={Code}")]
    private partial void LogStartRefused(Guid publicationId, string code);

    [LoggerMessage(
        EventId = 6617,
        Level = LogLevel.Warning,
        Message = "publication_upload_retrying publicationId={PublicationId} code={Code} attempt={Attempt} maxAttempts={MaxAttempts}")]
    private partial void LogRetrying(Guid publicationId, string code, int attempt, int maxAttempts);

    [LoggerMessage(
        EventId = 6618,
        Level = LogLevel.Information,
        Message = "publication_uploaded publicationId={PublicationId} reconciled={Reconciled}")]
    private partial void LogUploaded(Guid publicationId, bool reconciled);

    [LoggerMessage(
        EventId = 6619,
        Level = LogLevel.Warning,
        Message = "publication_upload_transition_refused publicationId={PublicationId} code={Code} reconciled={Reconciled}")]
    private partial void LogTransitionRefused(Guid publicationId, string code, bool reconciled);

    [LoggerMessage(
        EventId = 6630,
        Level = LogLevel.Information,
        Message = "publication_upload_failure_applied publicationId={PublicationId} outcome={Outcome} code={Code}")]
    private partial void LogFailureApplied(Guid publicationId, string outcome, string code);

    [LoggerMessage(
        EventId = 6631,
        Level = LogLevel.Warning,
        Message = "publication_upload_connection_unusable publicationId={PublicationId} connectionStatus={ConnectionStatus}")]
    private partial void LogConnectionUnusable(Guid publicationId, string connectionStatus);

    [LoggerMessage(
        EventId = 6632,
        Level = LogLevel.Information,
        Message = "publication_upload_held_by_incident publicationId={PublicationId} incidentId={IncidentId}")]
    private partial void LogHeldByIncident(Guid publicationId, Guid incidentId);

    [LoggerMessage(
        EventId = 6633,
        Level = LogLevel.Warning,
        Message = "publication_upload_incident_raised publicationId={PublicationId} incidentId={IncidentId} isNew={IsNew}")]
    private partial void LogIncidentRaised(Guid publicationId, Guid incidentId, bool isNew);

    [LoggerMessage(
        EventId = 6634,
        Level = LogLevel.Information,
        Message = "publication_upload_quota_exhausted publicationId={PublicationId}")]
    private partial void LogQuotaExhausted(Guid publicationId);

    [LoggerMessage(
        EventId = 6635,
        Level = LogLevel.Warning,
        Message = "publication_upload_video_missing publicationId={PublicationId}")]
    private partial void LogVideoMissing(Guid publicationId);

    [LoggerMessage(
        EventId = 6641,
        Level = LogLevel.Warning,
        Message = "publication_upload_commit_refused publicationId={PublicationId} code={Code}")]
    private partial void LogCommitRefused(Guid publicationId, string code);

    [LoggerMessage(
        EventId = 6642,
        Level = LogLevel.Warning,
        Message = "publication_upload_abandoned_unit_release_failed publicationId={PublicationId} reason={Reason}")]
    private partial void LogAbandonedUnitReleaseFailed(Guid publicationId, string reason);

    [LoggerMessage(
        EventId = 6643,
        Level = LogLevel.Information,
        Message = "publication_upload_session_create_claimed publicationId={PublicationId} leaseUntil={LeaseUntil}")]
    private partial void LogSessionCreateClaimed(Guid publicationId, DateTimeOffset leaseUntil);

    [LoggerMessage(
        EventId = 6644,
        Level = LogLevel.Information,
        Message = "publication_upload_session_not_attached publicationId={PublicationId} status={Status}")]
    private partial void LogSessionNotAttached(Guid publicationId, PublicationStatus status);

    [LoggerMessage(
        EventId = 6645,
        Level = LogLevel.Warning,
        Message = "publication_upload_final_verdict_failed publicationId={PublicationId} reason={Reason}")]
    private partial void LogFinalVerdictFailed(Guid publicationId, string reason);

    [LoggerMessage(
        EventId = 6646,
        Level = LogLevel.Warning,
        Message = "publication_upload_session_claim_release_failed publicationId={PublicationId} reason={Reason}")]
    private partial void LogClaimReleaseFailed(Guid publicationId, string reason);

    [LoggerMessage(
        EventId = 6695,
        Level = LogLevel.Information,
        Message = "publication_upload_stream_lease_held publicationId={PublicationId} leaseUntil={LeaseUntil}")]
    private partial void LogStreamLeaseHeld(Guid publicationId, DateTimeOffset leaseUntil);

    [LoggerMessage(
        EventId = 6696,
        Level = LogLevel.Warning,
        Message = "publication_upload_stream_lease_renew_failed publicationId={PublicationId} reason={Reason}")]
    private partial void LogStreamLeaseRenewFailed(Guid publicationId, string reason);

    [LoggerMessage(
        EventId = 6697,
        Level = LogLevel.Information,
        Message = "publication_upload_token_refreshed publicationId={PublicationId} expiresAt={ExpiresAt}")]
    private partial void LogTokenRefreshed(Guid publicationId, DateTimeOffset expiresAt);

    [LoggerMessage(
        EventId = 6698,
        Level = LogLevel.Warning,
        Message = "publication_upload_token_refresh_failed publicationId={PublicationId} code={Code}")]
    private partial void LogTokenRefreshFailed(Guid publicationId, string code);

    [LoggerMessage(
        EventId = 6636,
        Level = LogLevel.Warning,
        Message = "publication_upload_rollback_failed exceptionType={ExceptionType}")]
    private partial void LogRollbackFailed(string exceptionType);
}
