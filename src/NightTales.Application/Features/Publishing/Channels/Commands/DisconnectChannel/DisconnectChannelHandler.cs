using System.Globalization;
using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Compliance.Deletion;
using NightTales.Application.Features.Publishing.Commands.CancelPublication;
using NightTales.Application.Telemetry;
using NightTales.Domain.Common;
using NightTales.Domain.Compliance.Deletion;
using NightTales.Domain.Publishing;

namespace NightTales.Application.Features.Publishing.Channels.Commands.DisconnectChannel;

/// <summary>
/// Disconnects a channel (06.4.1-T1 §Design steps 1-10, 06 README D15/D17/D18): locks the connection, revokes the
/// grant at Google best effort, wipes both tokens, cancels every publication that has not started uploading and gives
/// its quota unit back, schedules the channel-scope deletion and lets the outbox interceptor publish the change.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order is the contract.</b> The connection row is locked and loaded through the tenant filter first, so another
/// owner's id and an unknown id are the same 404 and two concurrent disconnects of one row serialize. The Google call
/// sits <em>before</em> the state change on purpose: it is the only step that can meet a dead network, and a revoke
/// answered after the tokens were wiped could never be retried (R10 wants the wipe to happen either way).
/// </para>
/// <para>
/// <b>The revoke never fails the request (R10).</b> A transport failure or a 5xx gets three attempts in total, with a
/// 250 ms / 1 s backoff plus 0-20 % jitter <em>between</em> them through <see cref="TimeProvider"/> (a seam, so a test
/// that wants one can inject a fake provider; a test left on the default <c>TimeProvider.System</c> does pay the real
/// backoff of 1.25 s, up to 1.5 s with jitter, and no sleep is spent after the last failure); after that, for every
/// non-retryable answer and for a client that throws instead of answering, <c>youtube_channel_revoke_failed_total</c>
/// is incremented once and the disconnect continues — the local tokens are wiped and the deletion is scheduled
/// whatever Google answered, because the owner asked for the disconnect to happen. A cancelled request is the one
/// exception to that: it propagates rather than being recorded as a failed revoke, since the caller is gone. A token
/// Google reports as already revoked (<c>400 invalid_token</c>) is a success, not a failure: the grant is dead, which
/// is what the call wanted.
/// </para>
/// <para>
/// <b>The cascade reuses the cancel's own step.</b> Every <c>awaiting_consent</c>/<c>scheduled</c> row of the channel
/// is cancelled through <see cref="PublicationCancelStep"/> (the same aggregate call and the same
/// <c>actor=system:disconnect</c> audit value the cancel route writes for a user) and its reserved unit is given back
/// through <see cref="PublicationQuotaRelease.ReleaseManyAsync"/> — one ledger statement per day and one gauge
/// publication for the whole cascade, not one per row (STATUS S72-3). A row that moved to <c>uploading</c> between the
/// query and the lock is skipped, never a reason to fail the disconnect: a video already on its way to YouTube is not
/// deleted here (D15).
/// </para>
/// <para>
/// <b>Written once.</b> The connection touches the row, the cascade's rows and the deletion request; the outbox row
/// (<c>channel_connection_changed.v1</c>, raised by the aggregate's <c>Revoke</c>) and the audit row commit in the
/// same transaction as the unit of work. The repeat of a lost response writes none of them, and — once the engine
/// holds any request for the same duty, open or completed — schedules no second one either (D4).
/// </para>
/// </remarks>
public sealed partial class DisconnectChannelHandler(
    IChannelConnectionLocker locker,
    IAppDbContext db,
    ITokenProtector protector,
    IYouTubeClient youtube,
    IQuotaLedger quotaLedger,
    IDeletionRequests deletionRequests,
    NightTalesMetrics metrics,
    ICurrentUser currentUser,
    IClock clock,
    TimeProvider time,
    ILogger<DisconnectChannelHandler> logger)
    : ICommandHandler<DisconnectChannelCommand, ErrorOr<Success>>
{
    /// <summary>
    /// The ladder's attempts in total, the first one immediate (06.4.1-T1 task §Design "retry loop 3 attempts"; the
    /// acceptance pins <c>/revoke</c> being called three times, so there is no sleep after the third failure).
    /// </summary>
    private const int MaxAttempts = 3;

    /// <summary>
    /// The backoff <em>between</em> attempts, each with 0-20 % jitter: <c>250 ms</c> before attempt 2 and <c>1 s</c>
    /// before attempt 3. The plan's third rung (<c>4 s</c>) would only be needed by a fourth attempt, which the
    /// contract caps away; the two waits are what a disconnect actually spends.
    /// </summary>
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(1),
    ];

    /// <summary>The upper bound of the per-retry jitter, as a fraction of the delay.</summary>
    private const double MaxJitter = 0.2;

    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> Handle(
        DisconnectChannelCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The tenant comes from the scope, never from the command: an anonymous scope owns nothing, so the locked read
        // below answers the same 404 (09 README D10).
        if (currentUser.Id is not { } userId)
        {
            return Errors.NotFound();
        }

        // Step 1: the row lock first, exactly the lock the 06.4.2 refresh path takes, so a disconnect and a token
        // refresh of one connection can never interleave. Another user's id, an unknown id and an anonymous scope all
        // lock nothing and answer the same 404.
        var connection = await locker
            .LockByIdAsync(command.ChannelId, cancellationToken)
            .ConfigureAwait(false);

        if (connection is null)
        {
            return Errors.NotFound();
        }

        var now = clock.UtcNow;

        // Step 2: the lost-response repeat. The row already holds the state this request would write, so nothing is
        // re-revoked at Google (the ciphertexts are gone — there is nothing to revoke) and neither the state nor the
        // outbox changes. The create only runs while no duty is on record: the engine's open-row dedupe cannot see a
        // request it already completed, so re-entering it after the purge would open a second clock over data that is
        // gone (06.4.1-T1 §Failure handling "Connection already revoked and its request already completed_at"). With
        // no row at all, the engine's own idempotent create still runs, because a first attempt whose response was
        // lost may have rolled the request back with it.
        if (connection.Status == ChannelConnectionStatus.Revoked)
        {
            var recorded = await deletionRequests
                .ExistsAsync(
                    userId,
                    DeletionScope.Channel,
                    connection.Id,
                    DeletionReason.InAppRevoke,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!recorded)
            {
                var repeat = await ScheduleDeletionAsync(userId, connection.Id, cancellationToken).ConfigureAwait(false);

                if (repeat.IsError)
                {
                    return repeat.Errors;
                }
            }

            LogAlreadyRevoked(connection.Id);

            return Result.Success;
        }

        // The identity and the platform are read before the transition: `Revoke` keeps them (the channel-scope purge
        // nulls them later), so the audit projection cannot depend on the order of the two steps.
        var platform = connection.Platform.ToWire();
        var oauthClientId = connection.OAuthClientId;
        var externalAccountId = connection.ExternalAccountId ?? string.Empty;

        // Steps 3-4: the plaintext token, then the Google revoke. The refresh token first — it is the long-lived one,
        // and revoking it kills the grant the access token belongs to as well. `Guid.ToString()` is the "D" form both
        // sides of the protector's purpose use.
        var purpose = connection.Id.ToString("D", CultureInfo.InvariantCulture);
        var token = Unprotect(purpose, connection.RefreshTokenEnc) ?? Unprotect(purpose, connection.AccessTokenEnc);

        string googleRevoke;

        if (token is null)
        {
            // No usable plaintext: the rewrite only ever stores a protected pair, so this is a hand-repaired or
            // foreign-key-ring row. Google is not called, and the wipe below still happens (R10).
            LogRevokeSkipped(connection.Id);

            googleRevoke = DisconnectGoogleRevoke.Skipped;
        }
        else
        {
            var revoke = await RevokeWithRetryAsync(token, cancellationToken).ConfigureAwait(false);

            if (revoke.Outcome is { } outcome)
            {
                googleRevoke = outcome == RevokeOutcome.Revoked
                    ? DisconnectGoogleRevoke.Revoked
                    : DisconnectGoogleRevoke.AlreadyRevoked;
            }
            else
            {
                metrics.RecordYoutubeChannelRevokeFailed();

                LogRevokeFailed(connection.Id, revoke.Attempts, revoke.ErrorCode ?? ErrorCodes.ServiceUnavailable);

                googleRevoke = DisconnectGoogleRevoke.Failed;
            }
        }

        // Step 5: the aggregate owns the transition — both ciphertexts are dropped, `revoked_at`/`token_expires_at`
        // move to this instant and the row leaves the partial unique index, so the same Google channel can be
        // connected again by anyone. The aggregate raises `channel_connection_changed.v1`, which the outbox
        // interceptor stores in this unit of work (step 8).
        connection.Revoke(now);

        // Step 6: every publication of this channel that has not started uploading is cancelled, and the units they
        // still hold are given back. The rows are locked one by one, exactly as the cancel route locks its row.
        var cancelled = await CancelOpenPublicationsAsync(connection.Id, now, cancellationToken).ConfigureAwait(false);

        var released = await PublicationQuotaRelease
            .ReleaseManyAsync(cancelled, quotaLedger, logger, cancellationToken)
            .ConfigureAwait(false);

        if (released.IsError)
        {
            // The ledger's own refusal rolls the whole disconnect back: a committed disconnect whose unit was never
            // released would strand the reservation with nothing left to release it (06.2.6-T1 §Design step 5).
            return released.Errors;
        }

        // Step 7: the channel-scope deletion request — the 13 §5.4 duty, scheduled in the same transaction as the
        // state change so a committed disconnect always has a purge behind it (D18).
        var scheduled = await ScheduleDeletionAsync(userId, connection.Id, cancellationToken).ConfigureAwait(false);

        if (scheduled.IsError)
        {
            return scheduled.Errors;
        }

        // Steps 9-10: the audit projection (the pipeline's AuditBehavior writes the row on this success path only) and
        // the id-only log line. The save and the commit are the unit of work's.
        command.Outcome = new DisconnectedChannelOutcome(
            connection.Id,
            platform,
            oauthClientId,
            externalAccountId,
            cancelled.Count,
            released.Value,
            googleRevoke);

        LogDisconnected(connection.Id, cancelled.Count, released.Value, googleRevoke);

        return Result.Success;
    }

    /// <summary>
    /// Reads the plaintext of one stored cipher, or <see langword="null"/> when the column is empty or the payload
    /// cannot be unprotected under this connection's purpose (a rotated key ring, a hand-edited row).
    /// </summary>
    /// <param name="purpose">The connection's protector purpose suffix.</param>
    /// <param name="cipher">The stored ciphertext, or <see langword="null"/>.</param>
    /// <returns>The plaintext, or <see langword="null"/>.</returns>
    private string? Unprotect(string purpose, string? cipher) =>
        protector.TryUnprotect(purpose, cipher ?? string.Empty, out var plaintext) ? plaintext : null;

    /// <summary>
    /// Calls Google's revoke with the plan's retry ladder: only a transport failure or a 5xx
    /// (<see cref="ErrorCodes.ServiceUnavailable"/>) is retried, three times at most. Every other answer — including
    /// <c>400 invalid_token</c>, which the client reports as an already-revoked outcome — is final, and so is a
    /// client that throws a non-cancellation exception instead of answering: that is not the transient class, so it
    /// gets no retry and only the exception's type name is reported (never its message or the token). Cancellation is
    /// never turned into a failed attempt: an <see cref="OperationCanceledException"/>, and any exception thrown while
    /// the caller's own token is cancelled, propagates to the pipeline instead.
    /// </summary>
    /// <param name="token">The plaintext token to revoke.</param>
    /// <param name="cancellationToken">Cancels the call and the backoff.</param>
    /// <returns>
    /// The outcome and the number of attempts when Google answered, or <see langword="null"/> and the last error code
    /// when the ladder ran out (the caller counts that as a failure).
    /// </returns>
    private async Task<(RevokeOutcome? Outcome, int Attempts, string? ErrorCode)> RevokeWithRetryAsync(
        string token,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            ErrorOr<RevokeOutcome> result;

            try
            {
                result = await youtube.RevokeAsync(token, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // The port answers an ErrorOr, but an implausible answer can still throw (a corrupt /revoke body,
                // a malformed RevokeEndpoint, a request exception the client did not map). Letting it escape would
                // roll the unit of work back and keep the tokens, which D15 forbids: the wipe happens whatever
                // Google answers. It is not the transient `service_unavailable` class, so it is final with no retry,
                // and only the exception's type name leaves this method — a message could echo the token.
                // The filter keeps both cancellation shapes out: an OperationCanceledException always propagates, and
                // so does anything thrown while the caller's own token is cancelled (an aborted request can surface
                // as an HttpRequestException) — the caller is gone, so there is nothing to wipe for, exactly the
                // condition GoogleYouTubeClient.PostFormAsync uses to rethrow instead of logging unreachable.
                return (null, attempt, ex.GetType().Name);
            }

            if (!result.IsError)
            {
                return (result.Value, attempt, null);
            }

            var error = result.FirstError;

            if (error.Code != ErrorCodes.ServiceUnavailable || attempt >= MaxAttempts)
            {
                return (null, attempt, error.Code);
            }

            // The delay goes through the injected TimeProvider, so a test that needs one can inject a fake provider and
            // skip the wait; a test left on the default TimeProvider.System pays the real backoff (06.4.1-T1 §Testing).
            await Task.Delay(Jittered(RetryDelays[attempt - 1]), time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Spreads the retries of concurrent disconnects: a delay plus 0-20 % of itself, never a negative span.</summary>
    /// <param name="delay">The ladder's delay.</param>
    /// <returns>The jittered delay.</returns>
    private static TimeSpan Jittered(TimeSpan delay) =>
        TimeSpan.FromTicks((long)(delay.Ticks * (1 + (Random.Shared.NextDouble() * MaxJitter))));

    /// <summary>
    /// Step 6: cancels every publication of the channel that has not started uploading and returns the rows it
    /// cancelled, in id order so the test's assertions and the outbox rows are stable.
    /// </summary>
    /// <remarks>
    /// The ids are read first and locked one by one, then the locked rows are re-read: a row that moved to
    /// <c>uploading</c> between the two statements is skipped by this method's own predicate instead of failing the
    /// whole disconnect with the aggregate's 409 (STATUS S72-3). <c>Cancelled</c> rows are filtered by the query, so
    /// the cascade never re-cancels what the cancel route already cancelled (no second outbox row, no second release).
    /// </remarks>
    /// <param name="connectionId">The connection whose open publications are cancelled.</param>
    /// <param name="now">The disconnect instant, used as the cancel instant of every row.</param>
    /// <param name="cancellationToken">Cancels the reads and the locks.</param>
    /// <returns>The cancelled rows, in id order.</returns>
    private async Task<List<Publication>> CancelOpenPublicationsAsync(
        Guid connectionId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var ids = await db.Publications
            .Where(row => row.ChannelConnectionId == connectionId)
            .Where(row => row.Status == PublicationStatus.AwaitingConsent || row.Status == PublicationStatus.Scheduled)
            .OrderBy(row => row.Id)
            .Select(row => row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (ids.Count == 0)
        {
            return [];
        }

        foreach (var id in ids)
        {
            await db.LockPublicationForUpdateAsync(id, cancellationToken).ConfigureAwait(false);
        }

        var locked = await db.Publications
            .Where(row => ids.Contains(row.Id))
            .OrderBy(row => row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var cancelled = new List<Publication>(locked.Count);

        foreach (var publication in locked)
        {
            if (publication.Status is not (PublicationStatus.AwaitingConsent or PublicationStatus.Scheduled))
            {
                // It moved on between the read and the lock: the upload job owns it now, so it is skipped rather than
                // failing the disconnect (a video already being uploaded is not deleted here).
                LogCascadeSkipped(publication.Id);

                continue;
            }

            var step = PublicationCancelStep.Cancel(publication, now, CancelAuditKeys.ActorDisconnect);

            if (step.IsError)
            {
                // The same skip, from the aggregate's own guard: the transition refuses every status but the two the
                // predicate above already accepted, so only a concurrent write can reach this.
                LogCascadeSkipped(publication.Id);

                continue;
            }

            cancelled.Add(publication);
        }

        return cancelled;
    }

    /// <summary>Schedules the channel-scope deletion request for the caller (D18, 13 §5.4).</summary>
    /// <param name="userId">The owning account.</param>
    /// <param name="connectionId">The connection to purge after the grace period.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><c>Success</c>, or the engine's own refusal.</returns>
    private async ValueTask<ErrorOr<Success>> ScheduleDeletionAsync(
        Guid userId,
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        var request = await deletionRequests
            .CreateAsync(
                userId,
                DeletionScope.Channel,
                connectionId,
                DeletionReason.InAppRevoke,
                cancellationToken)
            .ConfigureAwait(false);

        return request.IsError ? request.Errors : Result.Success;
    }

    [LoggerMessage(
        EventId = 6506,
        Level = LogLevel.Information,
        Message = "Channel connection {ConnectionId} was disconnected: {PublicationsCancelled} publication(s) cancelled, {QuotaUnitsReleased} quota unit(s) released, googleRevoke={GoogleRevoke}.")]
    private partial void LogDisconnected(
        Guid connectionId,
        int publicationsCancelled,
        int quotaUnitsReleased,
        string googleRevoke);

    [LoggerMessage(
        EventId = 6507,
        Level = LogLevel.Information,
        Message = "Channel connection {ConnectionId} was already revoked; the repeat changed no state and writes no audit or outbox row.")]
    private partial void LogAlreadyRevoked(Guid connectionId);

    [LoggerMessage(
        EventId = 6508,
        Level = LogLevel.Warning,
        Message = "Channel connection {ConnectionId} stored no token that can be unprotected; the Google revoke was skipped and the tokens are wiped.")]
    private partial void LogRevokeSkipped(Guid connectionId);

    [LoggerMessage(
        EventId = 6509,
        Level = LogLevel.Warning,
        Message = "The Google revoke of channel connection {ConnectionId} failed after {Attempts} attempt(s) ({ErrorCode}); the tokens are wiped and the disconnect continues.")]
    private partial void LogRevokeFailed(Guid connectionId, int attempts, string errorCode);

    [LoggerMessage(
        EventId = 6510,
        Level = LogLevel.Information,
        Message = "Publication {PublicationId} of the disconnected channel was not cancelled: it is no longer awaiting consent or scheduled.")]
    private partial void LogCascadeSkipped(Guid publicationId);
}
