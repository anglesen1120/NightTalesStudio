using ErrorOr;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Publishing.Upload;
using NightTales.Domain.Publishing;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Publishing;

/// <summary>
/// <c>UploadFailureMap</c> (06.2.3-T2 plan §7, 06 README D9): every Google failure becomes exactly one decision — the
/// verdict, the catalogue code, whether this run's unit goes back, whether the connection expires and whether an
/// incident is raised — and an error the table does not own stays unclassified so a local problem can never be written
/// as a platform verdict.
/// </summary>
public sealed class UploadFailureMapTests
{
    [Fact]
    public void QuotaExceeded_ReleasesAndReschedules()
    {
        // D10: YouTube never received the upload, so the unit goes back and the row waits for the next Pacific day.
        var decision = UploadFailureMap.Map(YouTubeError(YouTubeErrorCodes.QuotaExceeded, "quotaExceeded", 403));

        decision.ShouldNotBeNull();
        decision.Outcome.ShouldBe(UploadOutcome.Rescheduled);
        decision.ErrorCode.ShouldBe(PublicationErrorCodes.QuotaExhausted);
        decision.ReleaseReservation.ShouldBeTrue();
        decision.RaiseIncident.ShouldBeFalse();
        decision.ExpireConnection.ShouldBeFalse();
        decision.Retryable.ShouldBeFalse();
    }

    [Fact]
    public void ClientBroken_RaisesIncidentAndHolds()
    {
        // `invalid_client` is a problem of the OAuth client, not of this owner's grant: the row is held under the
        // incident (YR24) instead of being failed, and this run's untouched unit is given back.
        var decision = UploadFailureMap.Map(YouTubeError(YouTubeErrorCodes.ClientBroken, "invalid_client", 401));

        decision.ShouldNotBeNull();
        decision.Outcome.ShouldBe(UploadOutcome.HeldByIncident);
        decision.ErrorCode.ShouldBe(PublicationErrorCodes.PlatformIncident);
        decision.RaiseIncident.ShouldBeTrue();
        decision.ReleaseReservation.ShouldBeTrue();
        decision.ExpireConnection.ShouldBeFalse();
        decision.Retryable.ShouldBeFalse();
    }

    [Fact]
    public void ReauthRequired_FailsThePublication()
    {
        // A dead grant is the owner's decision to fix: the row is terminal for this row and the connection expires.
        var decision = UploadFailureMap.Map(YouTubeError(YouTubeErrorCodes.ReauthRequired, "invalid_grant", 400));

        decision.ShouldNotBeNull();
        decision.Outcome.ShouldBe(UploadOutcome.ReauthRequired);
        decision.ErrorCode.ShouldBe(PublicationErrorCodes.ChannelReauthRequired);
        decision.ExpireConnection.ShouldBeTrue();
        decision.RaiseIncident.ShouldBeFalse();
        decision.ReleaseReservation.ShouldBeTrue();
    }

    [Fact]
    public void Validation_FailsWithoutIncident()
    {
        // Google refused the upload itself (metadata, file or channel policy): no retry can succeed, and a refusal of
        // the video is not an incident of the platform.
        var decision = UploadFailureMap.Map(YouTubeError(YouTubeErrorCodes.UploadFailed, "invalidPublishAt", 400));

        decision.ShouldNotBeNull();
        decision.Outcome.ShouldBe(UploadOutcome.Failed);
        decision.ErrorCode.ShouldBe(PublicationErrorCodes.UploadFailed);
        decision.RaiseIncident.ShouldBeFalse();
        decision.ExpireConnection.ShouldBeFalse();
        decision.ReleaseReservation.ShouldBeFalse();
        decision.Retryable.ShouldBeFalse();
    }

    [Fact]
    public void Rejected_FailsThePublicationAsRejected()
    {
        var decision = UploadFailureMap.Map(YouTubeError(YouTubeErrorCodes.Rejected, "copyright", 200));

        decision.ShouldNotBeNull();
        decision.Outcome.ShouldBe(UploadOutcome.Failed);
        decision.ErrorCode.ShouldBe(PublicationErrorCodes.Rejected);
        decision.Retryable.ShouldBeFalse();
    }

    [Fact]
    public void Transport_KeepsUploading()
    {
        // No usable answer: the row keeps `uploading` (so the next attempt resumes from the probe's offset) and the
        // attempt is retried — nothing is written and nothing is released.
        var decision = UploadFailureMap.Map(YouTubeError(YouTubeErrorCodes.Transport, null, 503, isTransport: true));

        decision.ShouldNotBeNull();
        decision.KeepsUploading.ShouldBeTrue();
        decision.Outcome.ShouldBeNull();
        decision.ErrorCode.ShouldBeNull();
        decision.Retryable.ShouldBeTrue();
        decision.ReleaseReservation.ShouldBeFalse();
        decision.RaiseIncident.ShouldBeFalse();
    }

    [Fact]
    public void SessionExpired_KeepsUploading()
    {
        // A 404 on the session is not a verdict: a fresh run probes, reserves again and opens a new session (YR27-3).
        var decision = UploadFailureMap.Map(YouTubeError(YouTubeErrorCodes.SessionExpired, null, 404));

        decision.ShouldNotBeNull();
        decision.KeepsUploading.ShouldBeTrue();
        decision.Retryable.ShouldBeTrue();
        decision.ReleaseReservation.ShouldBeFalse();
    }

    [Fact]
    public void TokenServiceDeadEnd_FailsWithoutExpiringTheConnection()
    {
        // `ITokenService`'s uniform answer covers a missing row, a revoked row and an unreadable cipher: the connection
        // is already unusable, so only the row is failed and no expiry event is raised a second time.
        var decision = UploadFailureMap.Map(
            Error.NotFound(ErrorCodes.ChannelReauthRequired, "The channel connection must be reconnected."));

        decision.ShouldNotBeNull();
        decision.Outcome.ShouldBe(UploadOutcome.ReauthRequired);
        decision.ErrorCode.ShouldBe(PublicationErrorCodes.ChannelReauthRequired);
        decision.ExpireConnection.ShouldBeFalse();
        decision.ReleaseReservation.ShouldBeTrue();
    }

    [Theory]
    [InlineData(ErrorCodes.NotFound)]
    [InlineData(ErrorCodes.ValidationFailed)]
    [InlineData(ErrorCodes.FeatureDisabled)]
    public void LocalError_IsNotOwned(string code)
    {
        // A domain refusal or a storage outage must be decided by the caller: turning one into `youtube_upload_failed`
        // would fail a publication for a problem of this host.
        UploadFailureMap.Map(Error.Failure(code, "A local failure.")).ShouldBeNull();
    }

    [Fact]
    public void FailedUploadResult_MapsThroughTheErrorOrWrapper()
    {
        ErrorOr<UploadResult> failure = YouTubeError(YouTubeErrorCodes.UploadFailed, "invalidTitle", 400);

        var decision = UploadFailureMap.Map(failure);

        decision.ShouldNotBeNull();
        decision.Outcome.ShouldBe(UploadOutcome.Failed);
        decision.ErrorCode.ShouldBe(PublicationErrorCodes.UploadFailed);
    }

    [Fact]
    public void SucceededUploadResult_IsNotAFailure()
    {
        UploadFailureMap.Map(new UploadResult("dQw4w9WgXcQ")).ShouldBeNull();
    }

    [Fact]
    public void GoogleReason_ReturnsTheSanitisedReason()
    {
        var error = YouTubeError(YouTubeErrorCodes.UploadFailed, "invalidPublishAt", 400);

        UploadFailureMap.GoogleReason(error).ShouldBe("invalidPublishAt");
        UploadFailureMap.GoogleReason(Error.Failure(ErrorCodes.NotFound, "no metadata")).ShouldBeNull();
    }

    /// <summary>One failure the way the Google client carries it: a catalogue code plus the sanitised answer.</summary>
    private static Error YouTubeError(string code, string? reason, int? status, bool isTransport = false) =>
        Error.Failure(code, "The platform refused the request.", new YouTubeFailure(reason, status, isTransport).ToMetadata());
}
