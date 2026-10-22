using System.Text.Json;
using ErrorOr;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Publishing;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Publishing;

/// <summary>
/// The upload half of <see cref="Publication"/> (06 README D9/D10/D13, plan §3.1–§3.2, YR24/YR27): the seven
/// transitions <c>PublishYouTubeJob</c> drives, their status guards, the <c>platform_payload</c> keys each one writes
/// or removes, and the <see cref="PublicationUpdated"/> each one raises so the outbox re-schedules the row.
/// </summary>
/// <remarks>
/// Pure unit tests: no database, no clock, no context. The PT-day arithmetic (<c>PtDay</c>), the mapping from a Google
/// failure to one of these calls (<c>UploadFailureMap</c>) and the re-enqueue subscriber live in
/// <c>tests/NightTales.Application.UnitTests/Publishing/</c> and in the integration suite, because each of them needs
/// the world this aggregate deliberately does not see.
/// </remarks>
public sealed class PublicationUploadTransitionsTests
{
    private static readonly Guid UserId = Guid.Parse("0199a2b3-1000-7000-8000-000000000001");
    private static readonly Guid EpisodeId = Guid.Parse("0199a2b3-1000-7000-8000-000000000002");
    private static readonly Guid ChannelConnectionId = Guid.Parse("0199a2b3-1000-7000-8000-000000000003");
    private static readonly Guid IncidentId = Guid.Parse("0199a2b3-1000-7000-8000-000000000004");

    /// <summary>The create moment; every later instant in these tests is relative to it.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The moment the upload job starts: the <c>scheduled → uploading</c> stamp.</summary>
    private static readonly DateTimeOffset UploadStartedAt = Now.AddMinutes(10);

    /// <summary><c>YouTube:ConsentTextVersion</c> as the host has it (06 README D7).</summary>
    private const string TextVersion = "pc.v1";

    /// <summary>The default title of these tests: one that passes every G4 rule.</summary>
    private const string Title = "Night Tales: The Lighthouse";

    /// <summary>The default description of these tests.</summary>
    private const string Description = "A keeper's last night on the rock.";

    /// <summary>The ciphertext a caller stores: never a URI, and never echoed by the aggregate.</summary>
    private const string ProtectedSessionUri = "CfDJ8-ciphertext-of-the-session-uri";

    /// <summary>The PT quota day of the reserve step (06.3.1-T1's <c>PacificQuotaDay.Day</c>).</summary>
    private static readonly DateOnly QuotaDay = new(2026, 10, 19);

    /// <summary>A video id, shaped like YouTube's.</summary>
    private const string VideoId = "dQw4w9WgXcQ";

    [Fact]
    public void StartUpload_WithoutConsent_IsInvalid()
    {
        // 06 README D9's defence in depth, asserted here for the transition table this task completes: the upload job
        // may not start a row nobody consented to, whatever the job itself believes.
        var publication = Created();

        var result = publication.StartUpload(UploadStartedAt);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(PublicationErrors.ConsentRequiredCode);
        result.FirstError.Type.ShouldBe(ErrorType.Conflict);
        publication.Status.ShouldBe(PublicationStatus.AwaitingConsent);
        publication.UpdatedAt.ShouldBe(Now, "a refused upload must not move the stamp");
        publication.DomainEvents.OfType<PublicationUpdated>().Count().ShouldBe(1);
    }

    [Theory]
    [InlineData(PublicationStatus.Processing)]
    [InlineData(PublicationStatus.Published)]
    [InlineData(PublicationStatus.Failed)]
    [InlineData(PublicationStatus.Cancelled)]
    public void StartUpload_FromTerminalStatus_IsRefused(PublicationStatus status)
    {
        var publication = Uploading();
        SetStatus(publication, status);
        var stamp = publication.UpdatedAt;

        var result = publication.StartUpload(Now.AddHours(1));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(PublicationErrors.InvalidStateCode);
        publication.Status.ShouldBe(status);
        publication.UpdatedAt.ShouldBe(stamp, "a refused transition writes nothing");
    }

    [Fact]
    public void AttachUploadSession_StoresTheCiphertext_AndKeepsEveryForeignKey()
    {
        // 06 README D4/plan §3.2: the row carries the Data-Protection ciphertext of the resumable session URI, and the
        // keys of the tasks that share the document (06.2.1-T1's pre-audit stamp, 06.2.2-T2's notice id) survive it.
        var publication = Uploading(
            platformPayloadJson: """{"preAudit":true,"requestedPrivacy":"public","vetoNoticeEmailId":"0199a2b3-1000-7000-8000-000000000009"}""");
        var eventCount = publication.DomainEvents.OfType<PublicationUpdated>().Count();

        var result = publication.AttachUploadSession(ProtectedSessionUri);

        result.IsError.ShouldBeFalse();
        publication.UploadSessionEnc.ShouldBe(ProtectedSessionUri);
        publication.Status.ShouldBe(PublicationStatus.Uploading, "attaching a session is not a status move");

        var payload = Payload(publication);
        payload.TryGetProperty("preAudit", out var preAudit).ShouldBeTrue();
        preAudit.GetBoolean().ShouldBeTrue();
        payload.GetProperty("requestedPrivacy").GetString().ShouldBe("public");
        payload.GetProperty("vetoNoticeEmailId").GetString().ShouldBe("0199a2b3-1000-7000-8000-000000000009");

        // Bookkeeping only: no event, no stamp — like StartUpload's resume no-op (06 YR18).
        publication.DomainEvents.OfType<PublicationUpdated>().Count().ShouldBe(eventCount);
        publication.UpdatedAt.ShouldBe(UploadStartedAt);
    }

    [Fact]
    public void AttachUploadSession_ReplacesTheStoredValue()
    {
        // A session-expired restart opens a new session and overwrites the old ciphertext (plan §7, YR27-3).
        var publication = Uploading();
        publication.AttachUploadSession(ProtectedSessionUri).IsError.ShouldBeFalse();

        var result = publication.AttachUploadSession("CfDJ8-the-second-session");

        result.IsError.ShouldBeFalse();
        publication.UploadSessionEnc.ShouldBe("CfDJ8-the-second-session");
    }

    [Fact]
    public void AttachUploadSession_FromScheduled_IsRefused()
    {
        // The session URI is written only for an upload that is running: a row that never started has no session to
        // store, and storing one would let a retry resume an upload YouTube never saw begin.
        var publication = Consented();

        var result = publication.AttachUploadSession(ProtectedSessionUri);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(PublicationErrors.InvalidStateCode);
        publication.UploadSessionEnc.ShouldBeNull();
    }

    [Fact]
    public void AttachUploadSession_WithABlankValue_Throws()
    {
        var publication = Uploading();

        Should.Throw<ArgumentException>(() => publication.AttachUploadSession(" "));
        publication.UploadSessionEnc.ShouldBeNull();
    }

    [Fact]
    public void ClearUploadSession_RemovesTheKey()
    {
        var publication = Uploading();
        publication.AttachUploadSession(ProtectedSessionUri).IsError.ShouldBeFalse();

        publication.ClearUploadSession();

        publication.UploadSessionEnc.ShouldBeNull();
        Payload(publication).TryGetProperty(PublicationPayload.UploadSessionEncKey, out _)
            .ShouldBeFalse("the key is removed, never stored as a null");
    }

    [Fact]
    public void MarkProcessing_ClearsNextAttemptAndError()
    {
        // plan §3.1: `uploading → processing` sets `external_post_id`, clears `next_attempt_at` and sets
        // `error_code = NULL`, so a row the poller owns can no longer be woken by a stale retry instant.
        var publication = Uploading();
        publication.RescheduleUpload(
            PublicationErrorCodes.QuotaExhausted,
            Now.AddDays(1),
            "quotaExceeded",
            UploadStartedAt)
            .IsError.ShouldBeFalse();
        publication.StartUpload(Now.AddMinutes(20)).IsError.ShouldBeFalse();
        publication.NextAttemptAt.ShouldBe(Now.AddDays(1));
        publication.ErrorCode.ShouldBe(PublicationErrorCodes.QuotaExhausted);

        var processedAt = Now.AddMinutes(25);
        var result = publication.MarkProcessing(VideoId, processedAt, maxMinutes: 45);

        result.IsError.ShouldBeFalse();
        publication.Status.ShouldBe(PublicationStatus.Processing);
        publication.ExternalPostId.ShouldBe(VideoId);
        publication.ExternalUrl.ShouldBe($"https://www.youtube.com/watch?v={VideoId}");
        publication.NextAttemptAt.ShouldBeNull();
        publication.ErrorCode.ShouldBeNull();
        publication.UpdatedAt.ShouldBe(processedAt);

        var raised = publication.DomainEvents.OfType<PublicationUpdated>().Last();
        raised.Status.ShouldBe(PublicationStatus.Processing);
        raised.ErrorCode.ShouldBeNull();
        raised.OccurredAt.ShouldBe(processedAt);
    }

    [Fact]
    public void MarkProcessing_WritesTheProcessingDeadlineForThePoller()
    {
        // plan §3.2/§4 (06.2.5-T1's claim condition): `processing = { startedAt, deadlineAt, polls }`, with the
        // deadline `YouTube:Poll:MaxMinutes` after the start and the count at zero.
        var publication = Uploading();
        var processedAt = Now.AddMinutes(30);

        publication.MarkProcessing(VideoId, processedAt, maxMinutes: 90).IsError.ShouldBeFalse();

        var processing = publication.Processing.ShouldNotBeNull();
        processing.StartedAt.ShouldBe(processedAt);
        processing.DeadlineAt.ShouldBe(processedAt.AddMinutes(90));
        processing.Polls.ShouldBe(0);
    }

    [Fact]
    public void MarkProcessing_RemovesTheReservedDayAndTheHold()
    {
        // The upload was received, so the unit is spent (06.2.6-T1 may no longer release it) and the row is no longer
        // held: both keys are removed, never nulled.
        var publication = Uploading(quotaDay: QuotaDay);
        publication.QuotaReservedDay.ShouldBe(QuotaDay);
        SetPayload(publication, PublicationPayload.WithKey(
            publication.PlatformPayloadJson,
            PublicationPayload.HeldByIncidentIdKey,
            IncidentId.ToString()));

        publication.MarkProcessing(VideoId, Now.AddMinutes(30), maxMinutes: 45).IsError.ShouldBeFalse();

        publication.QuotaReservedDay.ShouldBeNull();
        publication.HeldByIncidentId.ShouldBeNull();

        var payload = Payload(publication);
        payload.TryGetProperty(PublicationPayload.QuotaReservedDayKey, out _).ShouldBeFalse();
        payload.TryGetProperty(PublicationPayload.HeldByIncidentIdKey, out _).ShouldBeFalse();
    }

    [Fact]
    public void MarkProcessing_WithANonPositiveDeadline_Throws()
    {
        // `YouTube:Poll:MaxMinutes` is an option the caller passes; a zero or negative window is a misconfiguration the
        // aggregate refuses instead of writing a deadline the poller can never claim.
        var publication = Uploading();

        Should.Throw<ArgumentOutOfRangeException>(() => publication.MarkProcessing(VideoId, Now.AddMinutes(30), 0));
        publication.Status.ShouldBe(PublicationStatus.Uploading);
    }

    [Theory]
    [InlineData(PublicationStatus.Processing)]
    [InlineData(PublicationStatus.Published)]
    [InlineData(PublicationStatus.Failed)]
    [InlineData(PublicationStatus.Cancelled)]
    public void MarkProcessing_FromAnythingButUploading_IsRefused(PublicationStatus status)
    {
        // Strict on purpose: a duplicate enqueue of the same run must not rewrite the deadline of a row the poller is
        // already watching, and a terminal row never returns to the queue.
        var publication = Uploading();
        SetStatus(publication, status);
        var stamp = publication.UpdatedAt;

        var result = publication.MarkProcessing(VideoId, Now.AddMinutes(30), maxMinutes: 45);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(PublicationErrors.InvalidStateCode);
        publication.Status.ShouldBe(status);
        publication.UpdatedAt.ShouldBe(stamp);
        publication.ExternalPostId.ShouldBeNull();
    }

    [Fact]
    public void MarkUploadedExternal_ReconcilesWithoutUploadingAgain()
    {
        // plan §3.1: the reconcile path of a run whose last response was lost — the row already has a video, so it
        // reaches `processing` without a second upload.
        var publication = Uploading(quotaDay: QuotaDay);

        var result = publication.MarkUploadedExternal(VideoId, Now.AddMinutes(20), maxMinutes: 45);

        result.IsError.ShouldBeFalse();
        publication.Status.ShouldBe(PublicationStatus.Processing);
        publication.ExternalPostId.ShouldBe(VideoId);
        publication.ExternalUrl.ShouldBe($"https://www.youtube.com/watch?v={VideoId}");
        publication.QuotaReservedDay.ShouldBeNull();
        publication.Processing.ShouldNotBeNull().DeadlineAt.ShouldBe(Now.AddMinutes(65));
    }

    [Fact]
    public void MarkUploadedExternal_OnAProcessingRowWithTheSameVideoId_IsANoOp()
    {
        // A redelivered job must not extend the poller's deadline or raise a second event.
        var publication = Uploading();
        publication.MarkUploadedExternal(VideoId, Now.AddMinutes(20), maxMinutes: 45).IsError.ShouldBeFalse();
        var deadline = publication.Processing.ShouldNotBeNull().DeadlineAt;
        var eventCount = publication.DomainEvents.OfType<PublicationUpdated>().Count();

        var repeat = publication.MarkUploadedExternal(VideoId, Now.AddHours(2), maxMinutes: 45);

        repeat.IsError.ShouldBeFalse();
        publication.Processing.ShouldNotBeNull().DeadlineAt.ShouldBe(deadline);
        publication.UpdatedAt.ShouldBe(Now.AddMinutes(20));
        publication.DomainEvents.OfType<PublicationUpdated>().Count().ShouldBe(eventCount, "a no-op raises nothing");
    }

    [Fact]
    public void MarkUploadedExternal_OnAProcessingRowWithAnotherVideoId_IsRefused()
    {
        // Two different video ids mean two uploads, which is the bug this path exists to prevent: the retry is refused
        // rather than papered over.
        var publication = Uploading();
        publication.MarkUploadedExternal(VideoId, Now.AddMinutes(20), maxMinutes: 45).IsError.ShouldBeFalse();

        var result = publication.MarkUploadedExternal("aSecondVideo", Now.AddMinutes(21), maxMinutes: 45);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(PublicationErrors.InvalidStateCode);
        publication.ExternalPostId.ShouldBe(VideoId);
    }

    [Theory]
    [InlineData(PublicationStatus.Published)]
    [InlineData(PublicationStatus.Failed)]
    [InlineData(PublicationStatus.Cancelled)]
    public void MarkUploadedExternal_FromATerminalState_IsRefused(PublicationStatus status)
    {
        var publication = Uploading();
        SetStatus(publication, status);

        var result = publication.MarkUploadedExternal(VideoId, Now.AddMinutes(20), maxMinutes: 45);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(PublicationErrors.InvalidStateCode);
        publication.Status.ShouldBe(status);
        publication.ExternalPostId.ShouldBeNull();
    }

    [Fact]
    public void RescheduleUpload_KeepsTheSessionUri()
    {
        // plan §3.1 + §Failure handling ("quota exhausted with a session already open"): the retry must resume the open
        // session instead of uploading the whole file again, so the ciphertext survives the reschedule.
        var publication = Uploading(quotaDay: QuotaDay);
        publication.AttachUploadSession(ProtectedSessionUri).IsError.ShouldBeFalse();

        var nextAttemptAt = Now.AddDays(1).AddMinutes(4);
        var result = publication.RescheduleUpload(
            PublicationErrorCodes.QuotaExhausted,
            nextAttemptAt,
            "quotaExceeded",
            UploadStartedAt.AddMinutes(1));

        result.IsError.ShouldBeFalse();
        publication.Status.ShouldBe(PublicationStatus.Scheduled);
        publication.UploadSessionEnc.ShouldBe(ProtectedSessionUri, "the session is still usable");
        publication.QuotaReservedDay.ShouldBe(QuotaDay, "releasing the unit is the handler's decision, not this call's");
    }

    [Fact]
    public void RescheduleUpload_WakesTheRowWithTheReasonAndTheInstant()
    {
        // YR3/UC4: the raised `scheduled` event is what makes PublicationScheduledForUploadHandler schedule
        // PublishYouTubeJob at `next_attempt_at` — without it the row would sit scheduled with nothing to wake it.
        var publication = Uploading();
        var nextAttemptAt = Now.AddDays(1).AddMinutes(7);
        var rescheduledAt = UploadStartedAt.AddMinutes(2);

        var result = publication.RescheduleUpload(
            PublicationErrorCodes.QuotaExhausted,
            nextAttemptAt,
            "quotaExceeded",
            rescheduledAt);

        result.IsError.ShouldBeFalse();
        publication.Status.ShouldBe(PublicationStatus.Scheduled);
        publication.ErrorCode.ShouldBe(PublicationErrorCodes.QuotaExhausted);
        publication.NextAttemptAt.ShouldBe(nextAttemptAt);
        publication.LastGoogleReason.ShouldBe("quotaExceeded");
        publication.UpdatedAt.ShouldBe(rescheduledAt);

        var raised = publication.DomainEvents.OfType<PublicationUpdated>().Last();
        raised.PublicationId.ShouldBe(publication.Id);
        raised.EpisodeId.ShouldBe(EpisodeId);
        raised.UserId.ShouldBe(UserId);
        raised.Status.ShouldBe(PublicationStatus.Scheduled);
        raised.ErrorCode.ShouldBe(PublicationErrorCodes.QuotaExhausted);
        raised.OccurredAt.ShouldBe(rescheduledAt);
    }

    [Fact]
    public void RescheduleUpload_WithoutAGoogleReason_KeepsTheLastOne()
    {
        // The key is the last reason Google gave: an attempt with nothing to report cannot erase it.
        var publication = Uploading();
        publication.RescheduleUpload(PublicationErrorCodes.UploadFailed, Now.AddMinutes(5), "transportError", UploadStartedAt)
            .IsError.ShouldBeFalse();
        publication.LastGoogleReason.ShouldBe("transportError");

        publication.StartUpload(Now.AddMinutes(6)).IsError.ShouldBeFalse();
        publication.RescheduleUpload(PublicationErrorCodes.UploadFailed, Now.AddMinutes(9), null, Now.AddMinutes(7))
            .IsError.ShouldBeFalse();

        publication.LastGoogleReason.ShouldBe("transportError");
        Payload(publication).GetProperty(PublicationPayload.LastGoogleReasonKey).GetString()
            .ShouldBe("transportError");
    }

    [Theory]
    [InlineData(PublicationStatus.Scheduled)]
    [InlineData(PublicationStatus.Processing)]
    [InlineData(PublicationStatus.Published)]
    [InlineData(PublicationStatus.Failed)]
    [InlineData(PublicationStatus.Cancelled)]
    public void RescheduleUpload_FromAnythingButUploading_IsRefused(PublicationStatus status)
    {
        // A terminal or processing row must never be pulled back into the queue by a reschedule.
        var publication = Uploading();
        SetStatus(publication, status);
        var stamp = publication.UpdatedAt;

        var result = publication.RescheduleUpload(
            PublicationErrorCodes.QuotaExhausted,
            Now.AddDays(1),
            "quotaExceeded",
            Now.AddHours(1));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(PublicationErrors.InvalidStateCode);
        publication.Status.ShouldBe(status);
        publication.NextAttemptAt.ShouldBeNull();
        publication.UpdatedAt.ShouldBe(stamp);
    }

    [Fact]
    public void FailUpload_IsTerminalAndKeepsTheGoogleReason()
    {
        // plan §7: `youtube_rejected` → `failed`/`youtube_rejected`, with the raw Google reason kept for support in
        // platform_payload (never in error_code, never in the outbox).
        var publication = Uploading(quotaDay: QuotaDay);
        var failedAt = Now.AddMinutes(40);

        var result = publication.FailUpload(PublicationErrorCodes.Rejected, "invalidPublishAt", failedAt);

        result.IsError.ShouldBeFalse();
        publication.Status.ShouldBe(PublicationStatus.Failed);
        publication.ErrorCode.ShouldBe(PublicationErrorCodes.Rejected);
        publication.NextAttemptAt.ShouldBeNull();
        publication.LastGoogleReason.ShouldBe("invalidPublishAt");
        publication.QuotaReservedDay.ShouldBe(
            QuotaDay,
            "the unit is the handler's to release (YR19); a failed row keeps the day so a resume never reserves twice");
        publication.UpdatedAt.ShouldBe(failedAt);

        var raised = publication.DomainEvents.OfType<PublicationUpdated>().Last();
        raised.Status.ShouldBe(PublicationStatus.Failed);
        raised.ErrorCode.ShouldBe(PublicationErrorCodes.Rejected);
        raised.OccurredAt.ShouldBe(failedAt);
    }

    [Fact]
    public void FailUpload_Repeated_IsANoOp()
    {
        // The retry of a run whose commit was lost reports the same verdict: a success that writes nothing.
        var publication = Uploading();
        publication.FailUpload(PublicationErrorCodes.UploadFailed, "backendError", Now.AddMinutes(30))
            .IsError.ShouldBeFalse();
        var eventCount = publication.DomainEvents.OfType<PublicationUpdated>().Count();

        var repeat = publication.FailUpload(PublicationErrorCodes.UploadFailed, "backendError", Now.AddHours(1));

        repeat.IsError.ShouldBeFalse();
        publication.UpdatedAt.ShouldBe(Now.AddMinutes(30), "the repeat must not move the stamp");
        publication.DomainEvents.OfType<PublicationUpdated>().Count().ShouldBe(eventCount, "the repeat raises nothing");
    }

    [Fact]
    public void FailUpload_WithAnotherVerdictOnAFailedRow_IsRefused()
    {
        // The first verdict stands: a later classification cannot relabel a failed row (a new row is a new publish).
        var publication = Uploading();
        publication.FailUpload(PublicationErrorCodes.Rejected, "invalidPublishAt", Now.AddMinutes(30))
            .IsError.ShouldBeFalse();

        var result = publication.FailUpload(PublicationErrorCodes.UploadFailed, "backendError", Now.AddHours(1));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(PublicationErrors.InvalidStateCode);
        publication.ErrorCode.ShouldBe(PublicationErrorCodes.Rejected);
    }

    [Theory]
    [InlineData(PublicationStatus.Scheduled)]
    [InlineData(PublicationStatus.Processing)]
    [InlineData(PublicationStatus.Published)]
    [InlineData(PublicationStatus.Cancelled)]
    public void FailUpload_FromAnythingButUploading_IsRefused(PublicationStatus status)
    {
        var publication = Uploading();
        SetStatus(publication, status);

        var result = publication.FailUpload(PublicationErrorCodes.UploadFailed, "backendError", Now.AddHours(1));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(PublicationErrors.InvalidStateCode);
        publication.Status.ShouldBe(status);
    }

    [Fact]
    public void HoldForIncident_DefersTheNextAttemptAndKeepsTheSessionAndTheUnit()
    {
        // YR24: back to `scheduled` with `platform_incident`, the incident's id in the payload and a *deferred*
        // next_attempt_at (never null), so the row re-checks the incident on its own instead of hot-looping. The open
        // session and the reserved unit survive, because a hold is not a failure (UC1 step 5 releases nothing).
        var publication = Uploading(quotaDay: QuotaDay);
        publication.AttachUploadSession(ProtectedSessionUri).IsError.ShouldBeFalse();
        var deferredAt = Now.AddSeconds(150);
        var heldAt = UploadStartedAt.AddMinutes(1);

        var result = publication.HoldForIncident(IncidentId, deferredAt, heldAt);

        result.IsError.ShouldBeFalse();
        publication.Status.ShouldBe(PublicationStatus.Scheduled);
        publication.ErrorCode.ShouldBe(PublicationErrorCodes.PlatformIncident);
        publication.NextAttemptAt.ShouldBe(deferredAt);
        publication.HeldByIncidentId.ShouldBe(IncidentId);
        publication.UploadSessionEnc.ShouldBe(ProtectedSessionUri);
        publication.QuotaReservedDay.ShouldBe(QuotaDay);
        publication.UpdatedAt.ShouldBe(heldAt);

        var raised = publication.DomainEvents.OfType<PublicationUpdated>().Last();
        raised.Status.ShouldBe(PublicationStatus.Scheduled);
        raised.ErrorCode.ShouldBe(PublicationErrorCodes.PlatformIncident);
        raised.OccurredAt.ShouldBe(heldAt);
    }

    [Fact]
    public void HoldForIncident_WithAnEmptyIncidentId_Throws()
    {
        // The id is what makes the hold self-checking (06.2.3-T3 selects on it); an empty one is a caller bug.
        var publication = Uploading();

        Should.Throw<ArgumentOutOfRangeException>(
            () => publication.HoldForIncident(Guid.Empty, Now.AddSeconds(150), UploadStartedAt));
        publication.Status.ShouldBe(PublicationStatus.Uploading);
    }

    [Theory]
    [InlineData(PublicationStatus.Scheduled)]
    [InlineData(PublicationStatus.Processing)]
    [InlineData(PublicationStatus.Published)]
    [InlineData(PublicationStatus.Failed)]
    [InlineData(PublicationStatus.Cancelled)]
    public void HoldForIncident_FromAnythingButUploading_IsRefused(PublicationStatus status)
    {
        var publication = Uploading();
        SetStatus(publication, status);

        var result = publication.HoldForIncident(IncidentId, Now.AddSeconds(150), Now.AddHours(1));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(PublicationErrors.InvalidStateCode);
        publication.HeldByIncidentId.ShouldBeNull();
        publication.Status.ShouldBe(status);
    }

    [Fact]
    public void ReleaseHold_ClearsTheHoldAndSchedulesTheNextCheck()
    {
        // YR27-4: 06.2.3-T3's clear command calls this on tracked entities so the event fires and the outbox schedules
        // the wake-up — a set-based UPDATE would raise nothing (F6).
        var publication = Uploading(quotaDay: QuotaDay);
        publication.AttachUploadSession(ProtectedSessionUri).IsError.ShouldBeFalse();
        publication.HoldForIncident(IncidentId, Now.AddSeconds(150), UploadStartedAt).IsError.ShouldBeFalse();
        var nextAttemptAt = Now.AddSeconds(400);
        var releasedAt = Now.AddSeconds(360);

        var result = publication.ReleaseHold(nextAttemptAt, releasedAt);

        result.IsError.ShouldBeFalse();
        publication.Status.ShouldBe(PublicationStatus.Scheduled, "a released row keeps the status the hold left it in");
        publication.ErrorCode.ShouldBeNull();
        publication.NextAttemptAt.ShouldBe(nextAttemptAt);
        publication.HeldByIncidentId.ShouldBeNull();
        publication.QuotaReservedDay.ShouldBe(QuotaDay, "the hold never touched the reservation");
        publication.UploadSessionEnc.ShouldBe(ProtectedSessionUri);
        publication.UpdatedAt.ShouldBe(releasedAt);

        var raised = publication.DomainEvents.OfType<PublicationUpdated>().Last();
        raised.Status.ShouldBe(PublicationStatus.Scheduled);
        raised.ErrorCode.ShouldBeNull();
        raised.OccurredAt.ShouldBe(releasedAt);
    }

    [Fact]
    public void ReleaseHold_OnAProcessingRow_KeepsProcessing()
    {
        // The poller keeps watching a processing row; the release only drops the hold and names the instant.
        var publication = Uploading();
        publication.HoldForIncident(IncidentId, Now.AddSeconds(150), UploadStartedAt).IsError.ShouldBeFalse();
        publication.StartUpload(Now.AddSeconds(160)).IsError.ShouldBeFalse();
        SetStatus(publication, PublicationStatus.Processing);

        var result = publication.ReleaseHold(Now.AddSeconds(500), Now.AddSeconds(480));

        result.IsError.ShouldBeFalse();
        publication.Status.ShouldBe(PublicationStatus.Processing);
        publication.ErrorCode.ShouldBeNull();
        publication.NextAttemptAt.ShouldBe(Now.AddSeconds(500));
        publication.DomainEvents.OfType<PublicationUpdated>().Last().Status.ShouldBe(PublicationStatus.Processing);
    }

    [Fact]
    public void ReleaseHold_WhenTheRowIsNotHeld_IsRefused()
    {
        // A quota-rescheduled row also carries an error code and a next attempt; releasing it here would clear its
        // reason and pull its retry forward, so only a held row may be released.
        var publication = Uploading();
        publication.RescheduleUpload(
            PublicationErrorCodes.QuotaExhausted,
            Now.AddDays(1),
            "quotaExceeded",
            UploadStartedAt)
            .IsError.ShouldBeFalse();
        var stamp = publication.UpdatedAt;

        var result = publication.ReleaseHold(Now.AddSeconds(10), Now.AddHours(1));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(PublicationErrors.InvalidStateCode);
        publication.ErrorCode.ShouldBe(PublicationErrorCodes.QuotaExhausted);
        publication.NextAttemptAt.ShouldBe(Now.AddDays(1));
        publication.UpdatedAt.ShouldBe(stamp);
    }

    [Theory]
    [InlineData(PublicationStatus.Published)]
    [InlineData(PublicationStatus.Failed)]
    [InlineData(PublicationStatus.Cancelled)]
    public void ReleaseHold_FromATerminalState_IsRefused(PublicationStatus status)
    {
        var publication = Uploading();
        publication.HoldForIncident(IncidentId, Now.AddSeconds(150), UploadStartedAt).IsError.ShouldBeFalse();
        SetStatus(publication, status);

        var result = publication.ReleaseHold(Now.AddSeconds(500), Now.AddSeconds(480));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(PublicationErrors.InvalidStateCode);
        publication.Status.ShouldBe(status);
    }

    [Fact]
    public void MarkQuotaReserved_WritesTheIsoPacificDay_AndClearRemovesIt()
    {
        // STATUS S72-1 / plan §3.2: one ISO `yyyy-MM-dd` Pacific quota day, written at the reserve step, and *removed*
        // — never nulled, never rewritten to another day — when the unit is released or the upload finishes.
        var publication = Consented();

        var result = publication.MarkQuotaReserved(QuotaDay);

        result.IsError.ShouldBeFalse();
        publication.QuotaReservedDay.ShouldBe(QuotaDay);
        Payload(publication).GetProperty(PublicationPayload.QuotaReservedDayKey).GetString().ShouldBe("2026-10-19");

        publication.ClearQuotaReservation();

        publication.QuotaReservedDay.ShouldBeNull();
        Payload(publication).TryGetProperty(PublicationPayload.QuotaReservedDayKey, out _)
            .ShouldBeFalse("a removed key is absent, which is what 06.2.6-T1's reader treats as 'nothing reserved'");
    }

    [Fact]
    public void MarkQuotaReserved_OnAResumedUploadingRow_IsAllowed()
    {
        // YR27-3: a session-expired restart reserves a fresh unit while the row is already uploading.
        var publication = Uploading();

        publication.MarkQuotaReserved(QuotaDay.AddDays(1)).IsError.ShouldBeFalse();

        publication.QuotaReservedDay.ShouldBe(QuotaDay.AddDays(1));
    }

    [Theory]
    [InlineData(PublicationStatus.AwaitingConsent)]
    [InlineData(PublicationStatus.Processing)]
    [InlineData(PublicationStatus.Published)]
    [InlineData(PublicationStatus.Failed)]
    [InlineData(PublicationStatus.Cancelled)]
    public void MarkQuotaReserved_OutsideTheReservableStates_IsRefused(PublicationStatus status)
    {
        var publication = Uploading();
        SetStatus(publication, status);

        var result = publication.MarkQuotaReserved(QuotaDay);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(PublicationErrors.InvalidStateCode);
        publication.QuotaReservedDay.ShouldBeNull();
    }

    [Fact]
    public void PublicationErrorCodes_MatchTheErrorCatalogue()
    {
        // The Domain cannot reference Application/Common/Errors/ErrorCodes.cs (ErrorOr is its only package), so the
        // upload vocabulary is spelled here; this is the join between the two, exactly as
        // PublicationErrors_Codes_MatchTheErrorCatalogue does it for the refused-command codes.
        PublicationErrorCodes.QuotaExhausted.ShouldBe(ErrorCodes.YoutubeQuotaExhausted);
        PublicationErrorCodes.ChannelReauthRequired.ShouldBe(ErrorCodes.ChannelReauthRequired);
        PublicationErrorCodes.PlatformIncident.ShouldBe(ErrorCodes.PlatformIncident);
        PublicationErrorCodes.UploadFailed.ShouldBe(ErrorCodes.YoutubeUploadFailed);
        PublicationErrorCodes.Rejected.ShouldBe(ErrorCodes.YoutubeRejected);
    }

    [Fact]
    public void PayloadKeys_UseTheVocabularyOfThePlan()
    {
        // plan §3.2 names the keys; the Application-side readers (PublicationPlatformPayload's `quotaReservedDay`) and
        // 06.2.5-T1's poller key on these exact spellings, so a rename here is a wire change.
        PublicationPayload.UploadSessionEncKey.ShouldBe("uploadSessionEnc");
        PublicationPayload.QuotaReservedDayKey.ShouldBe("quotaReservedDay");
        PublicationPayload.HeldByIncidentIdKey.ShouldBe("heldByIncidentId");
        PublicationPayload.ProcessingKey.ShouldBe("processing");
        PublicationPayload.LastGoogleReasonKey.ShouldBe("lastGoogleReason");

        var publication = Uploading();
        publication.MarkProcessing(VideoId, Now.AddMinutes(30), maxMinutes: 45).IsError.ShouldBeFalse();

        var processing = Payload(publication).GetProperty("processing");
        processing.TryGetProperty("startedAt", out _).ShouldBeTrue();
        processing.TryGetProperty("deadlineAt", out _).ShouldBeTrue();
        processing.GetProperty("polls").GetInt32().ShouldBe(0);
    }

    [Fact]
    public void PublicationPayload_ReplacesADocumentNobodyCanParse()
    {
        // Only a hand-edited row reaches this state: bookkeeping is replaced, never thrown out of, so one bad row can
        // never fail an upload job (the 02 autopilot-settings precedent).
        var publication = Uploading(platformPayloadJson: "{not json");

        var result = publication.MarkQuotaReserved(QuotaDay);

        result.IsError.ShouldBeFalse();
        publication.QuotaReservedDay.ShouldBe(QuotaDay);
    }

    [Fact]
    public void PublicationPayload_ReadsGarbageAsAbsent()
    {
        PublicationPayload.ReadQuotaDay("{not json").ShouldBeNull();
        PublicationPayload.ReadQuotaDay("""{"quotaReservedDay":"19/10/2026"}""").ShouldBeNull();
        PublicationPayload.ReadQuotaDay("""{"quotaReservedDay":null}""").ShouldBeNull();
        PublicationPayload.ReadGuid("""{"heldByIncidentId":"not-a-guid"}""", PublicationPayload.HeldByIncidentIdKey)
            .ShouldBeNull();
        PublicationPayload.ReadProcessing("""{"processing":{"startedAt":"nope"}}""").ShouldBeNull();
    }

    [Fact]
    public void PublicationPayload_RemovalFromAnUnreadableDocument_KeepsItUntouched()
    {
        // There is no readable key to remove, and rewriting would destroy the only copy of whatever the document holds.
        PublicationPayload.WithoutKey("{not json", PublicationPayload.QuotaReservedDayKey).ShouldBe("{not json");
        PublicationPayload.WithoutKey(null, PublicationPayload.QuotaReservedDayKey).ShouldBeNull();
        PublicationPayload.WithoutKey("""{"preAudit":true}""", PublicationPayload.QuotaReservedDayKey)
            .ShouldBe("""{"preAudit":true}""");
    }

    [Fact]
    public void TerminalStates_RefuseEveryUploadTransition()
    {
        // One sweep over the terminal exits: no transition may revive a failed, published or cancelled row — the row is
        // final for this publication, and a re-publish is a new row (plan §3.1 invariants).
        foreach (var terminal in new[]
        {
            PublicationStatus.Published,
            PublicationStatus.Failed,
            PublicationStatus.Cancelled,
        })
        {
            var publication = Uploading();
            publication.AttachUploadSession(ProtectedSessionUri).IsError.ShouldBeFalse();
            SetStatus(publication, terminal);
            var stamp = publication.UpdatedAt;
            var eventCount = publication.DomainEvents.OfType<PublicationUpdated>().Count();

            publication.StartUpload(Now.AddHours(1)).FirstError.Code.ShouldBe(PublicationErrors.InvalidStateCode);
            publication.AttachUploadSession("CfDJ8-another").FirstError.Code.ShouldBe(PublicationErrors.InvalidStateCode);
            publication.MarkProcessing(VideoId, Now.AddHours(1), maxMinutes: 45).FirstError.Code
                .ShouldBe(PublicationErrors.InvalidStateCode);
            publication.MarkUploadedExternal(VideoId, Now.AddHours(1), maxMinutes: 45).FirstError.Code
                .ShouldBe(PublicationErrors.InvalidStateCode);
            publication.RescheduleUpload(PublicationErrorCodes.QuotaExhausted, Now.AddDays(1), "quotaExceeded", Now.AddHours(1))
                .FirstError.Code.ShouldBe(PublicationErrors.InvalidStateCode);
            publication.HoldForIncident(IncidentId, Now.AddSeconds(150), Now.AddHours(1)).FirstError.Code
                .ShouldBe(PublicationErrors.InvalidStateCode);
            publication.ReleaseHold(Now.AddSeconds(150), Now.AddHours(1)).FirstError.Code
                .ShouldBe(PublicationErrors.InvalidStateCode);
            publication.MarkQuotaReserved(QuotaDay).FirstError.Code.ShouldBe(PublicationErrors.InvalidStateCode);

            publication.Status.ShouldBe(terminal);
            publication.UpdatedAt.ShouldBe(stamp);
            publication.NextAttemptAt.ShouldBeNull();
            publication.ExternalPostId.ShouldBeNull();
            publication.UploadSessionEnc.ShouldBe(ProtectedSessionUri);
            publication.DomainEvents.OfType<PublicationUpdated>().Count().ShouldBe(eventCount);
        }
    }

    [Fact]
    public void ClaimUploadSessionCreate_StampsTheMarkAndTheLease()
    {
        // SEC-8/QA D3: the keys a run writes before its `videos.insert` leaves the process. The marker is the persistent
        // "a create was issued against this reservation" fact (YR19), the lease is the time-bounded claim that keeps a
        // second run from opening a second session.
        var publication = Uploading(quotaDay: QuotaDay);
        var claimedAt = UploadStartedAt.AddMinutes(1);
        var leaseUntil = claimedAt.AddSeconds(40);
        var eventsBefore = publication.DomainEvents.OfType<PublicationUpdated>().Count();

        var result = publication.ClaimUploadSessionCreate(claimedAt, leaseUntil);

        result.IsError.ShouldBeFalse();
        publication.UploadCreateIssuedAt.ShouldBe(claimedAt);
        publication.UploadSessionLeaseUntil.ShouldBe(leaseUntil);
        publication.HasUploadCreateIssued.ShouldBeTrue();
        publication.QuotaReservedDay.ShouldBe(QuotaDay, "the claim says nothing about the unit");
        publication.Status.ShouldBe(PublicationStatus.Uploading);

        var payload = Payload(publication);
        payload.GetProperty(PublicationPayload.UploadCreateIssuedAtKey).GetString().ShouldNotBeNullOrWhiteSpace();
        payload.GetProperty(PublicationPayload.UploadSessionLeaseKey).GetString().ShouldNotBeNullOrWhiteSpace();
        publication.DomainEvents.OfType<PublicationUpdated>().Count().ShouldBe(
            eventsBefore,
            "a claim is bookkeeping, not a transition: no event the dispatcher could act on");
    }

    [Theory]
    [InlineData(PublicationStatus.Processing)]
    [InlineData(PublicationStatus.Published)]
    [InlineData(PublicationStatus.Failed)]
    [InlineData(PublicationStatus.Cancelled)]
    public void ClaimUploadSessionCreate_FromATerminalRow_IsRefused(PublicationStatus status)
    {
        var publication = Uploading();
        SetStatus(publication, status);
        var payload = publication.PlatformPayloadJson;

        var result = publication.ClaimUploadSessionCreate(UploadStartedAt, UploadStartedAt.AddSeconds(40));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(PublicationErrors.InvalidStateCode);
        publication.UploadCreateIssuedAt.ShouldBeNull();
        publication.PlatformPayloadJson.ShouldBe(payload, "a refused claim writes nothing");
    }

    [Fact]
    public void ClearUploadSessionLease_KeepsTheCreateMarker()
    {
        // Ending the claim says "nobody is calling Google now"; it must not erase the fact that a call was made, which
        // is what the release rule reads on a later attempt (YR19/SEC-1).
        var publication = Uploading(quotaDay: QuotaDay);
        var claimedAt = UploadStartedAt.AddMinutes(1);
        publication.ClaimUploadSessionCreate(claimedAt, claimedAt.AddSeconds(40)).IsError.ShouldBeFalse();

        publication.ClearUploadSessionLease();

        publication.UploadSessionLeaseUntil.ShouldBeNull();
        publication.UploadCreateIssuedAt.ShouldBe(claimedAt);
        Payload(publication).TryGetProperty(PublicationPayload.UploadSessionLeaseKey, out _)
            .ShouldBeFalse("the key is removed, never stored as null");
    }

    [Fact]
    public void AttachUploadSession_EndsTheClaimInTheSameWrite()
    {
        // The cipher commit is the transaction that records the create's outcome, so the lease goes with it.
        var publication = Uploading(quotaDay: QuotaDay);
        publication.ClaimUploadSessionCreate(UploadStartedAt, UploadStartedAt.AddSeconds(40)).IsError.ShouldBeFalse();

        publication.AttachUploadSession(ProtectedSessionUri).IsError.ShouldBeFalse();

        publication.UploadSessionEnc.ShouldBe(ProtectedSessionUri);
        publication.UploadSessionLeaseUntil.ShouldBeNull();
        publication.UploadCreateIssuedAt.ShouldBe(UploadStartedAt, "the insert was issued, and the marker says so");
    }

    [Fact]
    public void HasUploadCreateIssued_ReadsAStoredSessionAsAnIssuedCreate()
    {
        // A row seeded by an earlier run (or written before the marker existed) carries the cipher and no marker: a
        // session URI only exists because an insert answered it, so the release rule must treat it as spent.
        var publication = Uploading(quotaDay: QuotaDay);
        publication.AttachUploadSession(ProtectedSessionUri).IsError.ShouldBeFalse();

        publication.UploadCreateIssuedAt.ShouldBeNull();
        publication.HasUploadCreateIssued.ShouldBeTrue();
    }

    [Fact]
    public void MarkQuotaReserved_StartsTheNewReservationWithoutAClaim()
    {
        // YR27-3: a restart reserves again, so the new unit has no create against it and nobody holds its claim.
        var publication = Uploading(quotaDay: QuotaDay);
        publication.ClaimUploadSessionCreate(UploadStartedAt, UploadStartedAt.AddSeconds(40)).IsError.ShouldBeFalse();

        var nextDay = QuotaDay.AddDays(1);
        publication.MarkQuotaReserved(nextDay).IsError.ShouldBeFalse();

        publication.QuotaReservedDay.ShouldBe(nextDay);
        publication.UploadCreateIssuedAt.ShouldBeNull();
        publication.UploadSessionLeaseUntil.ShouldBeNull();
    }

    [Fact]
    public void ClearQuotaReservation_RemovesTheCreateMarkerWithTheDay()
    {
        // SEC-1/S72-1: the two keys describe one reservation, so a row that holds no day must never look like one whose
        // insert Google counted.
        var publication = Uploading(quotaDay: QuotaDay);
        publication.ClaimUploadSessionCreate(UploadStartedAt, UploadStartedAt.AddSeconds(40)).IsError.ShouldBeFalse();

        publication.ClearQuotaReservation();

        publication.QuotaReservedDay.ShouldBeNull();
        publication.UploadCreateIssuedAt.ShouldBeNull();

        var payload = Payload(publication);
        payload.TryGetProperty(PublicationPayload.QuotaReservedDayKey, out _).ShouldBeFalse();
        payload.TryGetProperty(PublicationPayload.UploadCreateIssuedAtKey, out _).ShouldBeFalse();
    }

    [Fact]
    public void MarkProcessing_RemovesTheUploadClaimsWithTheReservedDay()
    {
        // The upload was received: the day is spent for good, so the create marker and any surviving claim go with it.
        var publication = Uploading(quotaDay: QuotaDay);
        publication.ClaimUploadSessionCreate(UploadStartedAt, UploadStartedAt.AddSeconds(40)).IsError.ShouldBeFalse();

        publication.MarkProcessing(VideoId, UploadStartedAt.AddMinutes(5), maxMinutes: 45).IsError.ShouldBeFalse();

        publication.QuotaReservedDay.ShouldBeNull();
        publication.UploadCreateIssuedAt.ShouldBeNull();
        publication.UploadSessionLeaseUntil.ShouldBeNull();
        publication.Processing.ShouldNotBeNull("the poller's block is still written");
    }

    /// <summary>The parsed <c>platform_payload</c> of a row that has one.</summary>
    /// <param name="publication">The row to read.</param>
    /// <returns>The document.</returns>
    private static JsonElement Payload(Publication publication)
    {
        var json = publication.PlatformPayloadJson.ShouldNotBeNull();

        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }

    private static PublicationMetadata Metadata() =>
        new(Title, Description, ["horror", "story"], "private", PublishAt: null);

    private static Publication Create(string? platformPayloadJson = null)
    {
        var result = Publication.Create(
            UserId,
            EpisodeId,
            ChannelConnectionId,
            Metadata(),
            autopilotAuthorizationId: null,
            vetoEndsAt: null,
            platformPayloadJson,
            Now);

        result.IsError.ShouldBeFalse();

        return result.Value;
    }

    private static Publication Created() => Create();

    private static Publication Consented()
    {
        var publication = Created();
        publication.Consent(TextVersion, TextVersion, Now.AddMinutes(5)).IsError.ShouldBeFalse();

        return publication;
    }

    /// <summary>The row the upload job holds when it starts: consented, moved to <c>uploading</c>.</summary>
    /// <param name="quotaDay">The PT day to reserve a unit on, when the test needs one reserved.</param>
    /// <param name="platformPayloadJson">The stored payload to start from, when the test needs foreign keys.</param>
    private static Publication Uploading(DateOnly? quotaDay = null, string? platformPayloadJson = null)
    {
        var publication = Create(platformPayloadJson);
        publication.Consent(TextVersion, TextVersion, Now.AddMinutes(5)).IsError.ShouldBeFalse();
        publication.StartUpload(UploadStartedAt).IsError.ShouldBeFalse();

        if (quotaDay is not null)
        {
            publication.MarkQuotaReserved(quotaDay.Value).IsError.ShouldBeFalse();
        }

        return publication;
    }

    /// <summary>
    /// Writes the payload of a tracked row the way a later task's own member would, for the states no transition of the
    /// aggregate produces on its own (a held row that is not this test's).
    /// </summary>
    /// <param name="publication">The row to write.</param>
    /// <param name="payload">The document to store.</param>
    private static void SetPayload(Publication publication, string? payload) =>
        typeof(Publication)
            .GetProperty(nameof(Publication.PlatformPayloadJson))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(publication, [payload]);

    /// <summary>
    /// Moves the aggregate to <paramref name="status"/> the way the poller and the go-live job will (06.2.5-T1): no
    /// member of the aggregate reaches <c>published</c>, and only <see cref="Publication.Cancel"/> reaches
    /// <c>cancelled</c> before the upload, so those rows of the state machine need the column written directly.
    /// </summary>
    private static void SetStatus(Publication publication, PublicationStatus status) =>
        typeof(Publication)
            .GetProperty(nameof(Publication.Status))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(publication, [status]);
}
