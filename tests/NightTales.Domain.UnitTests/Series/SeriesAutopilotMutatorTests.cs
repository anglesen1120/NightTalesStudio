using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Autopilot;
using NightTales.Domain.Series.Events;
using Shouldly;
using Xunit;

// The test namespace ends in `Series`, which shadows the aggregate's name inside it, so the entity is
// aliased (09.4.1-T1).
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Domain.UnitTests.Series;

/// <summary>
/// The autopilot half of the <see cref="SeriesAggregate"/> (02.8.3 plan §3.1, §3.7): storing the settings,
/// enabling and disabling, the current-authorization pointer, the acceptance event and the implicit disable of
/// an archive.
/// </summary>
/// <remarks>
/// Pure unit tests: no database, no clock, no context. The covered-field diff, the legal acceptance, the
/// re-auth and the channel checks are the command's steps and are tested where they run; the aggregate only
/// answers the state it owns (no settings to enable, and the pointer and pause state a disable clears).
/// </remarks>
public sealed class SeriesAutopilotMutatorTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 1, 9, 30, 0, TimeSpan.Zero);

    private const string SettingsJson =
        """{"schema_version":1,"cadence":{"days_of_week":["mon"],"publish_time":"19:00","time_zone":"Asia/Ho_Chi_Minh"},"daily_cap":1,"buffer_days":2,"privacy":"private","veto_window_hours":12,"pre_audit_mode":"keep_cadence_private","pre_audit_pause_after":3,"gate_thresholds_ref":"default","tone":"dry","banned_themes":[],"render_lead_minutes":null,"story_lead_minutes":null}""";

    [Fact]
    public void ApplyAutopilotSettings_StoresTheDocumentVerbatim()
    {
        var series = Create();

        series.ApplyAutopilotSettings(SettingsJson, FixedNow.AddMinutes(1));

        series.AutopilotSettings.ShouldBe(SettingsJson, "the aggregate stores what the validator approved");
        series.UpdatedAt.ShouldBe(FixedNow.AddMinutes(1));
        series.AutopilotEnabled.ShouldBeFalse("applying settings is not enabling the autopilot");
    }

    [Fact]
    public void ApplyAutopilotSettings_EmptyDocument_Throws()
    {
        var series = Create();

        Should.Throw<ArgumentException>(() => series.ApplyAutopilotSettings("   ", FixedNow));
        series.AutopilotSettings.ShouldBeNull();
    }

    [Fact]
    public void EnableAutopilot_WithoutSettings_ReturnsAutopilotSettingsInvalid()
    {
        var series = Create();

        var result = series.EnableAutopilot(FixedNow);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(SeriesAggregate.AutopilotSettingsInvalidCode);
        result.FirstError.Code.ShouldBe(ErrorCodes.AutopilotSettingsInvalid);
        result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.Validation);
        series.AutopilotEnabled.ShouldBeFalse("a refused enable leaves the flag off");
    }

    [Fact]
    public void EnableAutopilot_WithSettings_TurnsItOn()
    {
        var series = Create();
        series.ApplyAutopilotSettings(SettingsJson, FixedNow.AddMinutes(1));
        series.ClearDomainEvents();

        series.EnableAutopilot(FixedNow.AddMinutes(2)).IsError.ShouldBeFalse();

        series.AutopilotEnabled.ShouldBeTrue();
        series.UpdatedAt.ShouldBe(FixedNow.AddMinutes(2));
        series.DomainEvents.ShouldBeEmpty("enabling is audited by the command, not announced by the aggregate");
    }

    [Fact]
    public void EnableAutopilot_AlreadyEnabled_IsIdempotent()
    {
        var series = Create();
        series.ApplyAutopilotSettings(SettingsJson, FixedNow);
        series.EnableAutopilot(FixedNow);
        var enabledAt = series.UpdatedAt;

        series.EnableAutopilot(FixedNow.AddMinutes(5)).IsError.ShouldBeFalse();

        series.AutopilotEnabled.ShouldBeTrue();
        series.UpdatedAt.ShouldBe(enabledAt, "a no-op enable must not bump the row's xmin (the series ETag)");
    }

    [Fact]
    public void DisableAutopilot_ClearsThePointerAndThePauseState()
    {
        var series = Create();
        var authorization = Authorization(series);
        series.ApplyAutopilotSettings(SettingsJson, FixedNow);
        series.EnableAutopilot(FixedNow);
        series.SetCurrentAutopilotAuthorization(authorization.Id, FixedNow);
        SetPaused(series, "spend_cap_reached");

        series.DisableAutopilot(FixedNow.AddMinutes(3));

        series.AutopilotEnabled.ShouldBeFalse();
        series.CurrentAutopilotAuthorizationId.ShouldBeNull();
        series.AutopilotPausedAt.ShouldBeNull();
        series.AutopilotPauseReason.ShouldBeNull();
        series.UpdatedAt.ShouldBe(FixedNow.AddMinutes(3));
        series.AutopilotSettings.ShouldBe(SettingsJson, "the settings document outlives the flag");
    }

    [Fact]
    public void DisableAutopilot_WhenNothingIsSet_ChangesNothing()
    {
        var series = Create();
        var untouched = series.UpdatedAt;

        series.DisableAutopilot(FixedNow.AddHours(5));

        series.AutopilotEnabled.ShouldBeFalse();
        series.UpdatedAt.ShouldBe(untouched, "a no-op disable must not bump the row's xmin (the series ETag)");
    }

    [Fact]
    public void SetCurrentAutopilotAuthorization_SetsAndClearsThePointer()
    {
        var series = Create();
        var authorizationId = Guid.NewGuid();

        series.SetCurrentAutopilotAuthorization(authorizationId, FixedNow.AddMinutes(1));

        series.CurrentAutopilotAuthorizationId.ShouldBe(authorizationId);

        series.SetCurrentAutopilotAuthorization(null, FixedNow.AddMinutes(2));

        series.CurrentAutopilotAuthorizationId.ShouldBeNull();
        series.UpdatedAt.ShouldBe(FixedNow.AddMinutes(2));
    }

    [Fact]
    public void AcceptAutopilotAuthorization_SetsThePointerAndRaisesTheEvent()
    {
        var series = Create();
        var authorization = Authorization(series, version: 4);

        series.AcceptAutopilotAuthorization(authorization, FixedNow.AddMinutes(1));

        series.CurrentAutopilotAuthorizationId.ShouldBe(authorization.Id);
        series.UpdatedAt.ShouldBe(FixedNow.AddMinutes(1));
        series.AutopilotEnabled.ShouldBeFalse("accepting the text is not enabling the autopilot");

        var raised = series.DomainEvents.OfType<AutopilotAuthorizationAccepted>().ShouldHaveSingleItem();
        raised.UserId.ShouldBe(series.UserId);
        raised.SeriesId.ShouldBe(series.Id);
        raised.AuthorizationId.ShouldBe(authorization.Id);
        raised.Version.ShouldBe(4);
        raised.OccurredAt.ShouldBe(FixedNow.AddMinutes(1));
    }

    [Fact]
    public void AcceptAutopilotAuthorization_NullRow_Throws()
    {
        var series = Create();

        Should.Throw<ArgumentNullException>(() => series.AcceptAutopilotAuthorization(null!, FixedNow));
        series.CurrentAutopilotAuthorizationId.ShouldBeNull();
    }

    [Fact]
    public void AcceptAutopilotAuthorization_RowOfAnotherSeries_Throws()
    {
        var series = Create();
        var foreign = SeriesAutopilotAuthorization.Accept(
            series.UserId,
            Guid.NewGuid(),
            1,
            "autopilot-auth.v1",
            new string('a', 64),
            Topic.Ghost,
            Guid.NewGuid(),
            """{"snapshot":1}""",
            PrivacyStatus.Private,
            FixedNow);

        Should.Throw<ArgumentException>(() => series.AcceptAutopilotAuthorization(foreign, FixedNow));
        series.CurrentAutopilotAuthorizationId.ShouldBeNull();
        series.DomainEvents.OfType<AutopilotAuthorizationAccepted>().ShouldBeEmpty();
    }

    [Fact]
    public void AcceptAutopilotAuthorization_RevokedRow_Throws()
    {
        var series = Create();
        var revoked = Authorization(series);
        revoked.Revoke(AutopilotAuthorizationRevokeReason.OwnerRevoked, FixedNow);

        Should.Throw<ArgumentException>(() => series.AcceptAutopilotAuthorization(revoked, FixedNow));
        series.CurrentAutopilotAuthorizationId.ShouldBeNull();
        series.DomainEvents.OfType<AutopilotAuthorizationAccepted>().ShouldBeEmpty();
    }

    [Fact]
    public void Archive_WithAutopilotOn_DisablesItInTheSameCall()
    {
        var series = Create();
        var authorization = Authorization(series);
        series.ApplyAutopilotSettings(SettingsJson, FixedNow);
        series.EnableAutopilot(FixedNow);
        series.SetCurrentAutopilotAuthorization(authorization.Id, FixedNow);
        SetPaused(series, "gate_failed_streak");
        series.ClearDomainEvents();

        series.Archive(FixedNow.AddMinutes(4)).IsError.ShouldBeFalse();

        series.ArchivedAt.ShouldBe(FixedNow.AddMinutes(4));
        series.AutopilotEnabled.ShouldBeFalse();
        series.CurrentAutopilotAuthorizationId.ShouldBeNull();
        series.AutopilotPausedAt.ShouldBeNull();
        series.AutopilotPauseReason.ShouldBeNull();
        series.DomainEvents.OfType<SeriesArchived>().ShouldHaveSingleItem();
        series.DomainEvents.ShouldHaveSingleItem("the implicit disable announces nothing of its own");
    }

    [Fact]
    public void Archive_WithAutopilotOff_RaisesOnlyTheArchiveEvent()
    {
        var series = Create();
        series.ClearDomainEvents();

        series.Archive(FixedNow).IsError.ShouldBeFalse();

        series.DomainEvents.OfType<SeriesArchived>().ShouldHaveSingleItem();
        series.DomainEvents.Count.ShouldBe(1);
    }

    [Fact]
    public void Archive_Twice_StillRaisesOneEvent()
    {
        var series = Create();
        series.ApplyAutopilotSettings(SettingsJson, FixedNow);
        series.EnableAutopilot(FixedNow);

        series.Archive(FixedNow).IsError.ShouldBeFalse();
        series.Archive(FixedNow.AddHours(1)).IsError.ShouldBeFalse();

        series.DomainEvents.OfType<SeriesArchived>().ShouldHaveSingleItem();
        series.ArchivedAt.ShouldBe(FixedNow);
    }

    /// <summary>
    /// The Domain spells its own codes (D1: ErrorOr is its only package), so the constant is joined to the
    /// catalogue here, the way <c>SeriesTests.Series_Codes_MatchTheErrorCatalogue</c> joins the other two.
    /// </summary>
    [Fact]
    public void Series_AutopilotSettingsInvalidCode_MatchesTheErrorCatalogue()
    {
        SeriesAggregate.AutopilotSettingsInvalidCode.ShouldBe("autopilot_settings_invalid");
        SeriesAggregate.AutopilotSettingsInvalidCode.ShouldBe(ErrorCodes.AutopilotSettingsInvalid);
    }

    private static SeriesAggregate Create() => SeriesAggregate.Create(
        Guid.NewGuid(),
        new SeriesDraft(
            "Chuỗi thử nghiệm",
            Topic.Ghost,
            Language.Vi,
            Aspect.Landscape,
            "nts-test-voice-vi",
            Guid.NewGuid(),
            null),
        FixedNow);

    private static SeriesAutopilotAuthorization Authorization(SeriesAggregate series, int version = 1) =>
        SeriesAutopilotAuthorization.Accept(
            series.UserId,
            series.Id,
            version,
            "autopilot-auth.v1",
            new string('b', 64),
            series.Topic,
            Guid.NewGuid(),
            """{"snapshot":1}""",
            PrivacyStatus.Private,
            FixedNow);

    /// <summary>
    /// Writes the pause state the pause command will write (02.8.3 plan §3.6; that command and its aggregate
    /// method are a later task's). The two columns have no public setter, so the test sets them through the
    /// private accessor — the same way <c>SeriesTests</c> flips the autopilot flag.
    /// </summary>
    private static void SetPaused(SeriesAggregate series, string reason)
    {
        typeof(SeriesAggregate).GetProperty(nameof(SeriesAggregate.AutopilotPausedAt))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(series, [FixedNow]);

        typeof(SeriesAggregate).GetProperty(nameof(SeriesAggregate.AutopilotPauseReason))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(series, [reason]);
    }
}
