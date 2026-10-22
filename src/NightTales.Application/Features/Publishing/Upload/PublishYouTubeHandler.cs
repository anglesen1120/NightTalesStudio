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
/// <b>Three short transactions, never one long one.</b> The first commits the <c>scheduled → uploading</c> move, the
/// quota reservation and <c>platform_payload.quotaReservedDay</c> together, under the connection lock and then the
/// publication lock; the second commits the protected session URI <em>before</em> the first byte leaves the process;
/// the third commits the terminal state (or the failure verdict) with the ledger release. No transaction is open while
/// a Google call or the object stream runs: a lock held across an HTTP call would pin a pooled connection for minutes
/// and a stream cannot be rolled back ([RULED] 06.2.3 plan §3.2, STATUS S66-3/SEC-5).
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

    /// <summary>How long the poller may claim the row once YouTube is transcoding it (<c>YouTube:Poll:MaxMinutes</c>).</summary>
    private int ProcessingWindowMinutes => options.Poll.MaxMinutes;

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
            return await FailAsync(run, token.FirstError, sessionCreateSent: false, cancellationToken)
                .ConfigureAwait(false);
        }

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
            ? await StartSessionAsync(run, asset!.Value, token.Value.AccessToken, cancellationToken).ConfigureAwait(false)
            : await ResumeAsync(run, asset!.Value, resumeAt, token.Value.AccessToken, cancellationToken).ConfigureAwait(false);
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
                run.Reservation = reserved.Value;
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
            return await FailAsync(run, token.FirstError, sessionCreateSent: false, cancellationToken)
                .ConfigureAwait(false);
        }

        var videos = await youTube
            .GetVideosAsync(token.Value.AccessToken, [externalId], cancellationToken)
            .ConfigureAwait(false);

        if (videos.IsError)
        {
            return await FailAsync(run, videos.FirstError, sessionCreateSent: false, cancellationToken)
                .ConfigureAwait(false);
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
                    sessionCreateSent: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (string.Equals(video.UploadStatus, UploadStatusRejected, StringComparison.OrdinalIgnoreCase))
        {
            return await ApplyAsync(
                    run,
                    new UploadFailureDecision(UploadOutcome.Failed, PublicationErrorCodes.Rejected),
                    ReasonError(YouTubeErrorCodes.Rejected, video.RejectionReason),
                    sessionCreateSent: false,
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
        string accessToken,
        CancellationToken cancellationToken)
    {
        var probe = await youTube
            .ProbeUploadAsync(accessToken, sessionUri, asset.SizeBytes, cancellationToken)
            .ConfigureAwait(false);

        if (probe.IsError)
        {
            return probe.FirstError.Code == YouTubeErrorCodes.SessionExpired
                ? await RestartAsync(run, asset, accessToken, cancellationToken).ConfigureAwait(false)
                : await FailAsync(run, probe.FirstError, sessionCreateSent: false, cancellationToken).ConfigureAwait(false);
        }

        return probe.Value switch
        {
            // Google already holds the whole object: the video exists, so the row adopts it and nothing is sent again.
            UploadProbe.Complete complete => await FinalizeAsync(run, complete.VideoId, reconciled: true, cancellationToken)
                .ConfigureAwait(false),

            // 404: the session is gone, so a fresh one is opened — and a fresh unit is reserved for it (YR27-3).
            UploadProbe.Expired => await RestartAsync(run, asset, accessToken, cancellationToken).ConfigureAwait(false),

            // Partial: stream the rest from the offset Google still needs.
            UploadProbe.Partial partial => await ContinueAsync(run, asset, sessionUri, partial.NextByte, accessToken, cancellationToken)
                .ConfigureAwait(false),

            _ => await FailAsync(
                    run,
                    Error.Failure(YouTubeErrorCodes.UploadFailed, "The probe answered a state the client does not define."),
                    sessionCreateSent: false,
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
        string accessToken,
        CancellationToken cancellationToken)
    {
        var again = await ReserveAgainAsync(run, cancellationToken).ConfigureAwait(false);

        if (again.IsError)
        {
            return again.FirstError;
        }

        return await StartSessionAsync(run, asset, accessToken, cancellationToken).ConfigureAwait(false);
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
            run.Reservation = reserved.Value;

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
    /// Opens a new resumable session and streams the whole object (UC1 steps 8-9): the release-per-1600-units call is
    /// made exactly once per session, the encrypted URI is committed before the first byte, and the object is opened
    /// once and disposed in a <c>finally</c>.
    /// </summary>
    /// <param name="run">The run's state.</param>
    /// <param name="asset">The object to stream.</param>
    /// <param name="accessToken">The access token.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>The verdict of the run.</returns>
    private async Task<ErrorOr<UploadOutcome>> StartSessionAsync(
        RunState run,
        (string StorageKey, string ContentType, long SizeBytes) asset,
        string accessToken,
        CancellationToken cancellationToken)
    {
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
                return await FailAsync(run, total.FirstError, run.SessionCreateSent, cancellationToken).ConfigureAwait(false);
            }

            // §Idempotency "the row lock (one runner per publication)" applies to the session too: the claim on Google is
            // the session itself, so the publication lock is held from just before the create until the cipher is
            // committed (F02). A second run of the same publication queues on that lock and then finds the session the
            // first one stored, so it probes and resumes it instead of opening a second one. A crash inside this window
            // rolls the cipher back with the transaction, so the retry finds no session and opens one (step 8).
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
                    // Another run finished the upload while this one waited for the lock: nothing to create.
                    await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    return terminal;
                }

                if (row.Status != PublicationStatus.Uploading)
                {
                    await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    return UploadOutcome.Rescheduled;
                }

                if (row.UploadSessionEnc is { Length: > 0 } storedSession
                    && protector.TryUnprotect(run.Purpose, storedSession, out var storedPlain)
                    && Uri.TryCreate(storedPlain, UriKind.Absolute, out var storedUri))
                {
                    // The competing run stored its session first: probe that one, never a second create.
                    await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    return await ResumeAsync(run, asset, storedUri, accessToken, cancellationToken).ConfigureAwait(false);
                }

                var metadata = await ReadMetadataAsync(run.PublicationId, total.Value, asset.ContentType, cancellationToken)
                    .ConfigureAwait(false);

                if (metadata.IsError)
                {
                    await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    return metadata.FirstError;
                }

                // From here on the session may exist at Google: the unit is spent and cannot be given back (YR19).
                run.SessionCreateSent = true;

                var created = await youTube
                    .StartUploadAsync(accessToken, metadata.Value, total.Value, cancellationToken)
                    .ConfigureAwait(false);

                if (created.IsError)
                {
                    await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    return await FailAsync(run, created.FirstError, sessionCreateSent: true, cancellationToken)
                        .ConfigureAwait(false);
                }

                // F02: the cipher is committed before the first byte, in the same transaction as the create.
                var attached = row.AttachUploadSession(protector.Protect(run.Purpose, created.Value.AbsoluteUri));

                if (attached.IsError)
                {
                    await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    return attached.FirstError;
                }

                try
                {
                    await CommitAsync(run, cancellationToken).ConfigureAwait(false);
                }
                catch (PublishAttemptException)
                {
                    // The create was issued, but its session is unrecoverable: the cipher rolled back with the refused
                    // transaction, so nothing this row holds can address the session Google just opened. The unit this
                    // run reserved was committed by PrepareAsync and bought nothing streamable, so it goes back — with
                    // the day key that names it — before the retry reserves its own (YR19; QA D1).
                    await ReleaseAbandonedUnitAsync(run, row, cancellationToken).ConfigureAwait(false);

                    throw;
                }

                var session = created.Value;

                var uploaded = await youTube
                    .ResumeUploadAsync(accessToken, session, blob.Content, total.Value, progress: null, cancellationToken)
                    .ConfigureAwait(false);

                return uploaded.IsError
                    ? await FailAsync(run, uploaded.FirstError, sessionCreateSent: true, cancellationToken).ConfigureAwait(false)
                    : await FinalizeAsync(run, uploaded.Value.VideoId, reconciled: false, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await TryRollbackAsync(cancellationToken).ConfigureAwait(false);

                throw;
            }
        }
        finally
        {
            await blob.Content.DisposeAsync().ConfigureAwait(false);
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
        string accessToken,
        CancellationToken cancellationToken)
    {
        var opened = await blobs.OpenReadAsync(Bucket, asset.StorageKey, cancellationToken).ConfigureAwait(false);

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
                return await FailAsync(run, total.FirstError, run.SessionCreateSent, cancellationToken).ConfigureAwait(false);
            }

            // CR-12: the offset comes from the probe, so a shrunken object ends the session instead of reading past it.
            var positioned = await UploadStreamPositioning
                .EnsureAtAsync(blob.Content, nextByte, cancellationToken)
                .ConfigureAwait(false);

            if (positioned.IsError)
            {
                return await FailAsync(run, positioned.FirstError, run.SessionCreateSent, cancellationToken)
                    .ConfigureAwait(false);
            }

            var uploaded = await youTube
                .ResumeUploadAsync(accessToken, sessionUri, blob.Content, total.Value, progress: null, cancellationToken)
                .ConfigureAwait(false);

            return uploaded.IsError
                ? await FailAsync(run, uploaded.FirstError, sessionCreateSent: true, cancellationToken).ConfigureAwait(false)
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
    /// <param name="sessionCreateSent">Whether a session-create call was issued by this run.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>The verdict of the run.</returns>
    private async Task<ErrorOr<UploadOutcome>> FailAsync(
        RunState run,
        Error error,
        bool sessionCreateSent,
        CancellationToken cancellationToken)
    {
        var decision = UploadFailureMap.Map(error) ?? UploadFailureDecision.Retry;

        return await ApplyAsync(run, decision, error, sessionCreateSent, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies one decision (UC1 steps 6-10 + plan §7): a retry leaves the row untouched and asks the scheduler for another
    /// attempt; everything else is one transaction that moves the row, expires the connection or raises the incident,
    /// releases this run's unit and commits — all together, so a crash can never half-apply a verdict.
    /// </summary>
    /// <param name="run">The run's state.</param>
    /// <param name="decision">The classified failure.</param>
    /// <param name="error">The error the decision came from, for its raw Google reason.</param>
    /// <param name="sessionCreateSent">Whether a session-create call was issued by this run.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>The verdict of the run.</returns>
    private async Task<ErrorOr<UploadOutcome>> ApplyAsync(
        RunState run,
        UploadFailureDecision decision,
        Error error,
        bool sessionCreateSent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var reason = UploadFailureMap.GoogleReason(error);

        if (decision.KeepsUploading && !attempt.IsLastAttempt)
        {
            // The row keeps `uploading` and its session, so the next attempt resumes from the probe's offset.
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

            var applied = await ApplyTransitionAsync(run, row, connection, effective, error, reason, now, cancellationToken)
                .ConfigureAwait(false);

            if (applied.IsError)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
                LogTransitionRefused(run.PublicationId, applied.FirstError.Code, reconciled: false);

                return applied.FirstError;
            }

            // D10 / YR19 corrected: only the unit *this run* reserved, and only when YouTube never received the upload in
            // this run (or the answer itself says the quota refused it — the one flag the table still sets). A 401 or
            // `invalid_client` that answers a create already issued keeps the unit Google may have counted (QA D2).
            // The key is cleared with the release, so a later cancel can never give the same unit back twice (S72-1).
            if (run.Reservation is { } reservation && (effective.ReleaseReservation || !sessionCreateSent))
            {
                var released = await quota.ReleaseAsync(reservation, cancellationToken).ConfigureAwait(false);

                if (released.IsError)
                {
                    await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    return released.FirstError;
                }

                row.ClearQuotaReservation();
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

    /// <summary>A storage failure: a missing object is terminal, an outage is another attempt.</summary>
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
            // The store is down: the row keeps `uploading` and its unit, and the scheduler runs the attempt again.
            LogRetrying(run.PublicationId, error.Code, attempt.Attempt, attempt.MaxAttempts);

            throw new PublishAttemptException(PublishAttemptException.RetryCode);
        }

        // The object is gone, so no attempt can ever upload this row.
        return await ApplyAsync(
                run,
                new UploadFailureDecision(UploadOutcome.Failed, PublicationErrorCodes.UploadFailed),
                Error.Failure(YouTubeErrorCodes.UploadFailed, "The episode's video object no longer exists in storage."),
                run.SessionCreateSent,
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

    /// <summary>The metadata the insert carries, read from the row itself (D6: synthetic media is always declared).</summary>
    /// <param name="publicationId">The row.</param>
    /// <param name="total">The object's total in bytes, cross-checked by <see cref="UploadTotal"/>.</param>
    /// <param name="mimeType">The object's content type.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>The complete request, or <c>not_found</c> when the row is gone.</returns>
    private async Task<ErrorOr<VideoInsertRequest>> ReadMetadataAsync(
        Guid publicationId,
        long total,
        string mimeType,
        CancellationToken cancellationToken)
    {
        var row = await db.Publications
            .AsNoTracking()
            .SingleOrDefaultAsync(publication => publication.Id == publicationId, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return PublicationMissing(publicationId);
        }

        return new VideoInsertRequest(
            Title: row.Title,
            Description: row.Description,
            Tags: [.. row.Tags],
            PrivacyStatus: row.Privacy,
            PublishAt: row.PublishAt,
            ContainsSyntheticMedia: true,
            SelfDeclaredMadeForKids: false,
            Length: total,
            MimeType: mimeType);
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
    /// <b>What a caller owes before the throw escapes.</b> Nothing, except the session-URI commit:
    /// <see cref="StartSessionAsync"/> gives its unit back there, because that one was committed by
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

        throw new PublishAttemptException(PublishAttemptException.InfrastructureCode);
    }

    /// <summary>
    /// Gives back the unit of a session nobody can resume, after a refused commit rolled its cipher back, and removes
    /// the reserved day with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only this run's own reservation.</b> <see cref="RunState.Reservation"/> is set where this run called
    /// <c>TryReserveAsync</c> (<see cref="PrepareAsync"/> step 6 or <see cref="ReserveAgainAsync"/>); a resume of a row
    /// that already held a reserved day never reserved one here, so its unit belongs to the earlier run and is left
    /// alone (YR27-2).
    /// </para>
    /// <para>
    /// <b>Why a release and not just the throw.</b> <see cref="PrepareAsync"/> committed that unit with everything else
    /// before the create, so it is spent ledger state, not a reservation this failed transaction rolled back — the
    /// opposite of a refused <see cref="PrepareAsync"/> commit, where a release would take another row's unit because
    /// the reserve never landed.
    /// </para>
    /// <para>
    /// <b>The key travels with the release.</b> <c>quotaReservedDay</c> names the unit, and two readers act on it: the
    /// retry's <see cref="PrepareAsync"/> (a row that still holds a day skips the reservation, YR27-2) and the cancel
    /// path (it releases whatever day the key names, 06.2.6-T1). Leaving it behind would make the retry upload with no
    /// unit counted and would hand a later cancel a second decrement of a unit already given back — so the removal is
    /// part of the same transaction as the decrement (STATUS S72-1's rule for every other release). It is its own
    /// transaction because the refused commit already ended the failed one, and a failure here rolls both back rather
    /// than leaving one of the two written.
    /// </para>
    /// </remarks>
    /// <param name="run">The run whose reservation goes back.</param>
    /// <param name="row">The locked row whose reserved day is removed; reloaded because it still carries the cipher the refused transaction wrote.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    private async Task ReleaseAbandonedUnitAsync(RunState run, Publication row, CancellationToken cancellationToken)
    {
        if (run.Reservation is not { } reservation)
        {
            return;
        }

        run.Reservation = null;

        try
        {
            await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

            // The tracked entity still carries this run's uncommitted edits (the cipher above all); reloading takes the
            // committed truth — `uploading`, no session, the reserved day — before the key is removed from it.
            await db.ReloadAsync(row, cancellationToken).ConfigureAwait(false);

            var released = await quota.ReleaseAsync(reservation, cancellationToken).ConfigureAwait(false);

            if (released.IsError)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
                LogAbandonedUnitReleaseFailed(run.PublicationId, released.FirstError.Code);

                return;
            }

            row.ClearQuotaReservation();

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

        /// <summary>The unit this run reserved; <see langword="null"/> when the row already held one (YR27-2).</summary>
        public QuotaReservation? Reservation { get; set; }

        /// <summary>Whether this run issued a <c>videos.insert</c> session create (YR19's release predicate).</summary>
        public bool SessionCreateSent { get; set; }

        /// <summary>The protection purpose of the session cipher: <c>tokens.v1/{connectionId}</c> (06 README D4).</summary>
        public string Purpose => ConnectionId.ToString();
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
        EventId = 6636,
        Level = LogLevel.Warning,
        Message = "publication_upload_rollback_failed exceptionType={ExceptionType}")]
    private partial void LogRollbackFailed(string exceptionType);
}
