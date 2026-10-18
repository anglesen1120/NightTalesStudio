using ErrorOr;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Publishing;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Publishing;

/// <summary>
/// The create/consent/cancel half of <see cref="Publication"/> (06 README D6/D7/D8/D9/D13, YR1/YR18): what the
/// factory stamps on a new row, who may consent and with which text version, which states may still cancel, and the
/// upload transition's consent floor and resume no-op.
/// </summary>
/// <remarks>
/// Pure unit tests: no database, no clock, no context. The veto-window arithmetic of YR1 (which needs the series'
/// settings and <c>YouTubeOptions</c>) belongs to the create handler, and the partial-unique 409 to its Application
/// and integration tests.
/// </remarks>
public sealed class PublicationTests
{
    private static readonly Guid UserId = Guid.Parse("0199a2b3-0000-7000-8000-000000000001");
    private static readonly Guid EpisodeId = Guid.Parse("0199a2b3-0000-7000-8000-000000000002");
    private static readonly Guid ChannelConnectionId = Guid.Parse("0199a2b3-0000-7000-8000-000000000003");
    private static readonly Guid AuthorizationId = Guid.Parse("0199a2b3-0000-7000-8000-000000000004");

    /// <summary>The create moment; every later instant in these tests is relative to it.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    /// <summary><c>YouTube:ConsentTextVersion</c> as the host has it (06 README D7).</summary>
    private const string TextVersion = "pc.v1";

    [Fact]
    public void Create_StartsInAwaitingConsent_RaisesPublicationUpdated()
    {
        var publishAt = Now.AddHours(3);
        var tags = new List<string> { "horror", "story" };

        var publication = Create(metadata: Metadata(tags: tags, privacy: "unlisted", publishAt: publishAt));

        publication.Id.ShouldNotBe(Guid.Empty);
        publication.UserId.ShouldBe(UserId);
        publication.EpisodeId.ShouldBe(EpisodeId);
        publication.ChannelConnectionId.ShouldBe(ChannelConnectionId);
        publication.Platform.ShouldBe("youtube", "the platform column is the wire value of Platform.YouTube");

        // 06 README D6: the row is created awaiting its consent with the synthetic-media declaration pinned on and
        // the made-for-kids declaration pinned off; the metadata is stored exactly as the handler resolved it.
        publication.Status.ShouldBe(PublicationStatus.AwaitingConsent);
        publication.Title.ShouldBe(Title);
        publication.Description.ShouldBe(Description);
        publication.Tags.ShouldBe(["horror", "story"]);
        publication.Privacy.ShouldBe("unlisted");
        publication.PublishAt.ShouldBe(publishAt);
        publication.ContainsSyntheticMedia.ShouldBeTrue();
        publication.SelfDeclaredMadeForKids.ShouldBeFalse();

        publication.ConsentAt.ShouldBeNull();
        publication.ConsentTextVersion.ShouldBeNull();
        publication.ConsentSource.ShouldBeNull();
        publication.NextAttemptAt.ShouldBeNull();
        publication.VetoEndsAt.ShouldBeNull();
        publication.AutopilotAuthorizationId.ShouldBeNull();
        publication.VetoNoticeSentAt.ShouldBeNull();
        publication.ExternalPostId.ShouldBeNull();
        publication.ExternalUrl.ShouldBeNull();
        publication.PublishedAt.ShouldBeNull();
        publication.PlatformPayloadJson.ShouldBeNull();
        publication.ErrorCode.ShouldBeNull();
        publication.CreatedAt.ShouldBe(Now);
        publication.UpdatedAt.ShouldBe(Now);

        // The tags are copied, so the tracked row cannot change under the caller when it reuses the metadata.
        tags.Add("later");
        publication.Tags.ShouldBe(["horror", "story"]);

        // 06 README D13: exactly one publication_updated.v1 row, with ids, the status and no error code — never a
        // title, a description or a channel name (D17).
        var raised = publication.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<PublicationUpdated>();
        raised.PublicationId.ShouldBe(publication.Id);
        raised.EpisodeId.ShouldBe(EpisodeId);
        raised.UserId.ShouldBe(UserId);
        raised.Status.ShouldBe(PublicationStatus.AwaitingConsent);
        raised.ErrorCode.ShouldBeNull();
        raised.OccurredAt.ShouldBe(Now);
    }

    [Fact]
    public void Create_AutopilotPublication_KeepsTheVetoAndThePreAuditStamp()
    {
        var vetoEndsAt = Now.AddHours(2);

        var publication = Create(
            autopilotAuthorizationId: AuthorizationId,
            vetoEndsAt: vetoEndsAt,
            platformPayloadJson: """{"preAudit":true,"requestedPrivacy":"public"}""");

        publication.AutopilotAuthorizationId.ShouldBe(AuthorizationId);
        publication.VetoEndsAt.ShouldBe(vetoEndsAt);
        publication.PlatformPayloadJson.ShouldBe("""{"preAudit":true,"requestedPrivacy":"public"}""");
        publication.Status.ShouldBe(PublicationStatus.AwaitingConsent);
    }

    [Fact]
    public void Create_ManualPublicationWithAVetoEnd_IsRefused()
    {
        // 06 README D6/YR1: a manual publication has no veto window — the owner who created it is the one who may
        // cancel it — so the handler computing one for it is a bug the aggregate refuses to store.
        var result = Publication.Create(
            UserId,
            EpisodeId,
            ChannelConnectionId,
            Metadata(),
            autopilotAuthorizationId: null,
            vetoEndsAt: Now.AddHours(2),
            platformPayloadJson: null,
            Now);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(YouTubeMetadataRules.ValidationFailedCode);
        result.FirstError.Type.ShouldBe(ErrorType.Validation);
        result.FirstError.Metadata!["field"].ShouldBe("vetoEndsAt");
    }

    [Fact]
    public void Consent_RequiresCurrentTextVersion()
    {
        var publication = Created();
        var at = Now.AddMinutes(5);

        var result = publication.Consent("pc.v0", TextVersion, at);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(PublicationErrors.ConsentTextOutdatedCode);
        result.FirstError.Type.ShouldBe(ErrorType.Validation);
        result.FirstError.Metadata!["field"].ShouldBe("consentTextVersion");

        // A refused consent leaves the row exactly as it was, and raises nothing.
        publication.Status.ShouldBe(PublicationStatus.AwaitingConsent);
        publication.ConsentAt.ShouldBeNull();
        publication.ConsentTextVersion.ShouldBeNull();
        publication.ConsentSource.ShouldBeNull();
        publication.UpdatedAt.ShouldBe(Now);
        publication.DomainEvents.OfType<PublicationUpdated>().Count().ShouldBe(1, "only the create event");
    }

    [Fact]
    public void Consent_FromAwaitingConsent_SchedulesAndRaisesPublicationUpdated()
    {
        var publication = Created();
        var at = Now.AddMinutes(5);

        var result = publication.Consent(TextVersion, TextVersion, at);

        result.IsError.ShouldBeFalse();
        publication.Status.ShouldBe(PublicationStatus.Scheduled);
        publication.ConsentAt.ShouldBe(at);
        publication.ConsentTextVersion.ShouldBe(TextVersion);
        publication.ConsentSource.ShouldBe("user");
        publication.AutopilotAuthorizationId.ShouldBeNull("a manual consent never writes an authorization");
        publication.UpdatedAt.ShouldBe(at);

        var raised = publication.DomainEvents.OfType<PublicationUpdated>().Last();
        raised.Status.ShouldBe(PublicationStatus.Scheduled);
        raised.OccurredAt.ShouldBe(at);
        raised.ErrorCode.ShouldBeNull();
    }

    [Fact]
    public void Consent_WhenTheRowIsNoLongerAwaitingConsent_IsRefused()
    {
        var publication = Consented();

        var result = publication.Consent(TextVersion, TextVersion, Now.AddMinutes(10));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(PublicationErrors.InvalidStateCode);
        publication.Status.ShouldBe(PublicationStatus.Scheduled);
        publication.ConsentAt.ShouldBe(Now.AddMinutes(5));
    }

    [Fact]
    public void ConsentFromAuthorization_StampsTheAutopilotSourceAndTheAuthorization()
    {
        var publication = Create(autopilotAuthorizationId: AuthorizationId, vetoEndsAt: Now.AddHours(2));
        var at = Now.AddHours(2);

        var result = publication.ConsentFromAuthorization(TextVersion, AuthorizationId, at);

        result.IsError.ShouldBeFalse();
        publication.Status.ShouldBe(PublicationStatus.Scheduled);
        publication.ConsentAt.ShouldBe(at);
        publication.ConsentTextVersion.ShouldBe(TextVersion);
        publication.ConsentSource.ShouldBe("autopilot");
        publication.AutopilotAuthorizationId.ShouldBe(AuthorizationId);
        publication.UpdatedAt.ShouldBe(at);

        var raised = publication.DomainEvents.OfType<PublicationUpdated>().Last();
        raised.Status.ShouldBe(PublicationStatus.Scheduled);
        raised.OccurredAt.ShouldBe(at);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancel_FromTheTwoCancellableStates_CancelsAndRaises(bool consented)
    {
        var publication = consented ? Consented() : Created();
        var at = Now.AddMinutes(15);

        var result = publication.Cancel(at);

        result.IsError.ShouldBeFalse();
        publication.Status.ShouldBe(PublicationStatus.Cancelled);
        publication.UpdatedAt.ShouldBe(at);

        var raised = publication.DomainEvents.OfType<PublicationUpdated>().Last();
        raised.Status.ShouldBe(PublicationStatus.Cancelled);
        raised.OccurredAt.ShouldBe(at);
    }

    [Fact]
    public void Cancel_IllegalFrom_Uploading()
    {
        // 06 README D7: once the upload runs there is a video to take down rather than a job to stop, so the two
        // states past `scheduled` can no longer cancel.
        var publication = Uploading();
        var uploadStartedAt = Now.AddMinutes(10);

        var result = publication.Cancel(Now.AddMinutes(30));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(PublicationErrors.InvalidStateCode);
        publication.Status.ShouldBe(PublicationStatus.Uploading);
        publication.UpdatedAt.ShouldBe(uploadStartedAt, "a refused cancel must not move the stamp");
        publication.DomainEvents.OfType<PublicationUpdated>().Count().ShouldBe(2, "the create and consent events");
    }

    [Theory]
    [InlineData(PublicationStatus.Processing)]
    [InlineData(PublicationStatus.Published)]
    [InlineData(PublicationStatus.Failed)]
    [InlineData(PublicationStatus.Cancelled)]
    public void Cancel_FromTheLaterStates_IsRefused(PublicationStatus status)
    {
        var publication = Uploading();
        SetStatus(publication, status);

        var result = publication.Cancel(Now.AddMinutes(30));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(PublicationErrors.InvalidStateCode);
        publication.Status.ShouldBe(status);
    }

    [Fact]
    public void StartUpload_WithoutConsent_Fails()
    {
        // 06 README D9 defence in depth: the upload job may not start a row nobody consented to, whatever the job
        // itself believes about the row's state.
        var publication = Created();

        var result = publication.StartUpload(Now.AddMinutes(10));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(PublicationErrors.ConsentRequiredCode);
        result.FirstError.Type.ShouldBe(ErrorType.Conflict);
        publication.Status.ShouldBe(PublicationStatus.AwaitingConsent);
        publication.UpdatedAt.ShouldBe(Now, "a refused upload must not move the stamp");
        publication.DomainEvents.OfType<PublicationUpdated>().Count().ShouldBe(1);
    }

    [Fact]
    public void StartUpload_FromScheduled_MovesToUploading()
    {
        var publication = Consented();
        var at = Now.AddMinutes(10);

        var result = publication.StartUpload(at);

        result.IsError.ShouldBeFalse();
        publication.Status.ShouldBe(PublicationStatus.Uploading);
        publication.UpdatedAt.ShouldBe(at);

        // The upload transition raises nothing: the consent event already told the Worker to schedule this upload
        // (06 YR3).
        publication.DomainEvents.OfType<PublicationUpdated>().Count().ShouldBe(2);
    }

    [Fact]
    public void StartUpload_WhenUploading_IsResumeNoOp()
    {
        // 06 YR18: an upload that is already running is the retry of one whose response was lost, so the second
        // call is a success that writes nothing — the job then reaches the probe/resume step of F02.
        var publication = Uploading();
        var startedAt = Now.AddMinutes(10);

        var resume = publication.StartUpload(Now.AddMinutes(12));

        resume.IsError.ShouldBeFalse();
        publication.Status.ShouldBe(PublicationStatus.Uploading);
        publication.UpdatedAt.ShouldBe(startedAt, "a resume writes no state");
        publication.DomainEvents.OfType<PublicationUpdated>().Count().ShouldBe(2, "a resume raises no event");
    }

    [Theory]
    [InlineData(PublicationStatus.Processing)]
    [InlineData(PublicationStatus.Published)]
    [InlineData(PublicationStatus.Failed)]
    [InlineData(PublicationStatus.Cancelled)]
    public void StartUpload_FromTheLaterStates_IsRefused(PublicationStatus status)
    {
        var publication = Uploading();
        SetStatus(publication, status);

        var result = publication.StartUpload(Now.AddMinutes(30));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(PublicationErrors.InvalidStateCode);
        publication.Status.ShouldBe(status);
    }

    [Fact]
    public void PublicationErrors_Codes_MatchTheErrorCatalogue()
    {
        // The Domain cannot reference Application/Common/Errors/ErrorCodes.cs (ErrorOr is its only package), so it
        // spells the seven codes itself; the HTTP status each maps to is the Api catalogue's. This test is the join
        // between the two, and it is why a rename cannot slip through unnoticed.
        PublicationErrors.InvalidStateCode.ShouldBe(ErrorCodes.PublicationInvalidState);
        PublicationErrors.ConflictCode.ShouldBe(ErrorCodes.PublicationConflict);
        PublicationErrors.ChannelNotActiveCode.ShouldBe(ErrorCodes.ChannelNotActive);
        PublicationErrors.EpisodeInvalidStateCode.ShouldBe(ErrorCodes.EpisodeInvalidState);
        PublicationErrors.ConsentTextOutdatedCode.ShouldBe(ErrorCodes.ConsentTextOutdated);
        PublicationErrors.ConsentRequiredCode.ShouldBe(ErrorCodes.PublicationConsentRequired);
        PublicationErrors.PrivacyLockedCode.ShouldBe(ErrorCodes.PublicationPrivacyLocked);
    }

    [Fact]
    public void PublicationStatus_And_ConsentSource_UseTheWireValuesOfTheSchema()
    {
        // The enums persist through the snake_case converter of 09 README D8 and the CHECK constraints of
        // 06.2.1-T1's migration: the member names, the columns and the CHECK lists must agree.
        Enum.GetNames<PublicationStatus>().Select(SnakeCase).ShouldBe(
            ["awaiting_consent", "scheduled", "uploading", "processing", "published", "failed", "cancelled"]);
        ConsentSource.User.ToWire().ShouldBe("user");
        ConsentSource.Autopilot.ToWire().ShouldBe("autopilot");
    }

    /// <summary>The default title of these tests: one that passes every G4 rule.</summary>
    private const string Title = "Night Tales: The Lighthouse";

    /// <summary>The default description of these tests.</summary>
    private const string Description = "A keeper's last night on the rock.";

    private static PublicationMetadata Metadata(
        IReadOnlyList<string>? tags = null,
        string privacy = "private",
        DateTimeOffset? publishAt = null) =>
        new(Title, Description, tags ?? ["horror", "story"], privacy, publishAt);

    private static Publication Create(
        PublicationMetadata? metadata = null,
        Guid? autopilotAuthorizationId = null,
        DateTimeOffset? vetoEndsAt = null,
        string? platformPayloadJson = null)
    {
        var result = Publication.Create(
            UserId,
            EpisodeId,
            ChannelConnectionId,
            metadata ?? Metadata(),
            autopilotAuthorizationId,
            vetoEndsAt,
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

    private static Publication Uploading()
    {
        var publication = Consented();
        publication.StartUpload(Now.AddMinutes(10)).IsError.ShouldBeFalse();

        return publication;
    }

    /// <summary>
    /// Moves the aggregate to <paramref name="status"/> the way the poller and the go-live job will; no method of the
    /// aggregate reaches <c>processing</c>, <c>published</c> or <c>failed</c> yet (06.2.3-T2, 06.2.5-T1), so those
    /// rows of the state machine cannot be exercised without writing the column.
    /// </summary>
    private static void SetStatus(Publication publication, PublicationStatus status) =>
        typeof(Publication)
            .GetProperty(nameof(Publication.Status))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(publication, [status]);

    /// <summary>The snake_case wire value the enum converter of 09 README D8 writes.</summary>
    private static string SnakeCase(string member) => System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(member);
}
