using NightTales.Domain.Common;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Autopilot;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Series.Autopilot;

/// <summary>
/// Plan §3.7's covered-field comparison (<see cref="AutopilotAuthorizationCoverage"/>): one test per covered
/// field, one per field that must not revoke, and the fail-closed answers.
/// </summary>
/// <remarks>
/// The baseline is the plan §3.1 example document parsed by the real validator, so the snapshot under test is
/// the one an acceptance would actually store. Every test moves exactly one input of the comparison.
/// </remarks>
public sealed class AuthorizationCoverageTests
{
    /// <summary>The plan §3.1 example document, verbatim (the same literal the validator's own tests use).</summary>
    private const string Valid = """
        {
          "schema_version": 1,
          "cadence": { "days_of_week": ["mon", "thu"], "publish_time": "19:00", "time_zone": "Asia/Ho_Chi_Minh" },
          "daily_cap": 1,
          "buffer_days": 2,
          "privacy": "private",
          "veto_window_hours": 12,
          "pre_audit_mode": "keep_cadence_private",
          "pre_audit_pause_after": 3,
          "gate_thresholds_ref": "default",
          "tone": "atmospheric, restrained, no gore, first person",
          "banned_themes": ["children in danger", "real named people"],
          "render_lead_minutes": 15,
          "story_lead_minutes": 10
        }
        """;

    private static readonly Guid Channel = Guid.Parse("018f6c1e-4a4b-7c3d-9e2f-1a2b3c4d5e6f");

    private static readonly AutopilotSettings Baseline = AutopilotSettingsValidator.Validate(Valid).Value;

    [Fact]
    public void SnapshotJson_CarriesExactlyTheCoveredMembers()
    {
        var snapshot = AutopilotAuthorizationCoverage.SnapshotJson(Baseline, preAuditPending: false);

        snapshot.ShouldBe(
            """{"cadence":{"days_of_week":["mon","thu"],"publish_time":"19:00","time_zone":"Asia/Ho_Chi_Minh"},"daily_cap":1,"privacy":"private","veto_window_hours":12,"pre_audit_mode":"keep_cadence_private","pre_audit_pending":false}""");

        // Not covered, so not stored: the whole point of the explicit snapshot (plan §3.7).
        snapshot.ShouldNotContain("tone", Case.Sensitive);
        snapshot.ShouldNotContain("banned_themes", Case.Sensitive);
        snapshot.ShouldNotContain("gate_thresholds_ref", Case.Sensitive);
        snapshot.ShouldNotContain("buffer_days", Case.Sensitive);
        snapshot.ShouldNotContain("pre_audit_pause_after", Case.Sensitive);
        snapshot.ShouldNotContain("render_lead_minutes", Case.Sensitive);
        snapshot.ShouldNotContain("story_lead_minutes", Case.Sensitive);
        snapshot.ShouldNotContain("title", Case.Sensitive);
    }

    [Fact]
    public void SnapshotJson_CarriesTheAuditBit()
    {
        var snapshot = AutopilotAuthorizationCoverage.SnapshotJson(Baseline, preAuditPending: true);

        snapshot.ShouldContain("\"pre_audit_pending\":true", Case.Sensitive);
    }

    [Fact]
    public void FirstCoveredChange_NothingMoved_IsNull()
    {
        var snapshot = AutopilotAuthorizationCoverage.SnapshotJson(Baseline, preAuditPending: false);

        Change(snapshot, Topic.Ghost, Channel, Topic.Ghost, Channel, Baseline, preAuditPending: false)
            .ShouldBeNull();
    }

    [Fact]
    public void FirstCoveredChange_TopicMoved_IsTopicChanged()
    {
        var snapshot = AutopilotAuthorizationCoverage.SnapshotJson(Baseline, preAuditPending: false);

        Change(snapshot, Topic.Ghost, Channel, Topic.TrueCrime, Channel, Baseline, preAuditPending: false)
            .ShouldBe(AutopilotAuthorizationRevokeReason.TopicChanged);
    }

    [Fact]
    public void FirstCoveredChange_ChannelMoved_IsChannelChanged()
    {
        var snapshot = AutopilotAuthorizationCoverage.SnapshotJson(Baseline, preAuditPending: false);

        Change(snapshot, Topic.Ghost, Channel, Topic.Ghost, Guid.NewGuid(), Baseline, preAuditPending: false)
            .ShouldBe(AutopilotAuthorizationRevokeReason.ChannelChanged);
    }

    [Fact]
    public void FirstCoveredChange_StoredChannelMissing_IsChannelChanged()
    {
        // An acceptance always names a channel; a row without one cannot be the agreement the series is in.
        var snapshot = AutopilotAuthorizationCoverage.SnapshotJson(Baseline, preAuditPending: false);

        Change(snapshot, Topic.Ghost, storedChannel: null, Topic.Ghost, Channel, Baseline, preAuditPending: false)
            .ShouldBe(AutopilotAuthorizationRevokeReason.ChannelChanged);
    }

    [Fact]
    public void FirstCoveredChange_TopicOutranksTheTextChange()
    {
        var snapshot = AutopilotAuthorizationCoverage.SnapshotJson(Baseline, preAuditPending: false);
        var moved = Baseline with { DailyCap = 2 };

        Change(snapshot, Topic.Ghost, Channel, Topic.TrueCrime, Channel, moved, preAuditPending: false)
            .ShouldBe(AutopilotAuthorizationRevokeReason.TopicChanged);
    }

    [Fact]
    public void FirstCoveredChange_DaysOfWeekMoved_IsTextChanged()
    {
        var snapshot = AutopilotAuthorizationCoverage.SnapshotJson(Baseline, preAuditPending: false);
        var moved = Baseline with
        {
            Cadence = Baseline.Cadence with { DaysOfWeek = [DayOfWeek.Monday, DayOfWeek.Friday] },
        };

        Change(snapshot, Topic.Ghost, Channel, Topic.Ghost, Channel, moved, preAuditPending: false)
            .ShouldBe(AutopilotAuthorizationRevokeReason.TextChanged);
    }

    [Fact]
    public void FirstCoveredChange_DaysOfWeekReordered_IsNotACoveredChange()
    {
        // The authorization text sorts and de-duplicates the weekdays, so `thu, mon` and `mon, thu` are the same
        // sentence and the same hash: a reorder contradicts nothing the owner accepted (plan §3.7).
        var snapshot = AutopilotAuthorizationCoverage.SnapshotJson(Baseline, preAuditPending: false);
        var reordered = Baseline with
        {
            Cadence = Baseline.Cadence with { DaysOfWeek = [DayOfWeek.Thursday, DayOfWeek.Monday] },
        };

        Change(snapshot, Topic.Ghost, Channel, Topic.Ghost, Channel, reordered, preAuditPending: false)
            .ShouldBeNull();
    }

    [Fact]
    public void FirstCoveredChange_PublishTimeMoved_IsTextChanged()
    {
        var snapshot = AutopilotAuthorizationCoverage.SnapshotJson(Baseline, preAuditPending: false);
        var moved = Baseline with { Cadence = Baseline.Cadence with { PublishTime = new TimeOnly(20, 30) } };

        Change(snapshot, Topic.Ghost, Channel, Topic.Ghost, Channel, moved, preAuditPending: false)
            .ShouldBe(AutopilotAuthorizationRevokeReason.TextChanged);
    }

    [Fact]
    public void FirstCoveredChange_TimeZoneMoved_IsTextChanged()
    {
        var snapshot = AutopilotAuthorizationCoverage.SnapshotJson(Baseline, preAuditPending: false);
        var moved = Baseline with { Cadence = Baseline.Cadence with { TimeZoneId = "Asia/Tokyo" } };

        Change(snapshot, Topic.Ghost, Channel, Topic.Ghost, Channel, moved, preAuditPending: false)
            .ShouldBe(AutopilotAuthorizationRevokeReason.TextChanged);
    }

    [Fact]
    public void FirstCoveredChange_DailyCapMoved_IsTextChanged()
    {
        var snapshot = AutopilotAuthorizationCoverage.SnapshotJson(Baseline, preAuditPending: false);

        Change(snapshot, Topic.Ghost, Channel, Topic.Ghost, Channel, Baseline with { DailyCap = 2 }, preAuditPending: false)
            .ShouldBe(AutopilotAuthorizationRevokeReason.TextChanged);
    }

    [Fact]
    public void FirstCoveredChange_PrivacyMoved_IsTextChanged()
    {
        var snapshot = AutopilotAuthorizationCoverage.SnapshotJson(Baseline, preAuditPending: false);

        Change(
                snapshot,
                Topic.Ghost,
                Channel,
                Topic.Ghost,
                Channel,
                Baseline with { Privacy = PrivacyStatus.Public },
                preAuditPending: false)
            .ShouldBe(AutopilotAuthorizationRevokeReason.TextChanged);
    }

    [Fact]
    public void FirstCoveredChange_VetoWindowMoved_IsTextChanged()
    {
        var snapshot = AutopilotAuthorizationCoverage.SnapshotJson(Baseline, preAuditPending: false);

        Change(snapshot, Topic.Ghost, Channel, Topic.Ghost, Channel, Baseline with { VetoWindowHours = 24 }, preAuditPending: false)
            .ShouldBe(AutopilotAuthorizationRevokeReason.TextChanged);
    }

    [Fact]
    public void FirstCoveredChange_PreAuditModeMoved_IsTextChanged()
    {
        var snapshot = AutopilotAuthorizationCoverage.SnapshotJson(Baseline, preAuditPending: false);

        Change(
                snapshot,
                Topic.Ghost,
                Channel,
                Topic.Ghost,
                Channel,
                Baseline with { PreAuditMode = PreAuditMode.PauseAfterNPrivate },
                preAuditPending: false)
            .ShouldBe(AutopilotAuthorizationRevokeReason.TextChanged);
    }

    [Fact]
    public void FirstCoveredChange_PreAuditPendingMoved_IsTextChanged()
    {
        // The audit-status bit the text carries: pending -> approved is a different text (D-7).
        var snapshot = AutopilotAuthorizationCoverage.SnapshotJson(Baseline, preAuditPending: true);

        Change(snapshot, Topic.Ghost, Channel, Topic.Ghost, Channel, Baseline, preAuditPending: false)
            .ShouldBe(AutopilotAuthorizationRevokeReason.TextChanged);
    }

    [Fact]
    public void FirstCoveredChange_ToneMoved_IsNotACoveredChange()
    {
        var snapshot = AutopilotAuthorizationCoverage.SnapshotJson(Baseline, preAuditPending: false);
        var moved = Baseline with { Tone = "dry, procedural, third person" };

        Change(snapshot, Topic.Ghost, Channel, Topic.Ghost, Channel, moved, preAuditPending: false)
            .ShouldBeNull();
    }

    [Fact]
    public void FirstCoveredChange_BannedThemesMoved_IsNotACoveredChange()
    {
        var snapshot = AutopilotAuthorizationCoverage.SnapshotJson(Baseline, preAuditPending: false);
        var moved = Baseline with { BannedThemes = ["real named people"] };

        Change(snapshot, Topic.Ghost, Channel, Topic.Ghost, Channel, moved, preAuditPending: false)
            .ShouldBeNull();
    }

    [Fact]
    public void FirstCoveredChange_GateThresholdsRefMoved_IsNotACoveredChange()
    {
        var snapshot = AutopilotAuthorizationCoverage.SnapshotJson(Baseline, preAuditPending: false);

        Change(snapshot, Topic.Ghost, Channel, Topic.Ghost, Channel, Baseline with { GateThresholdsRef = "strict" }, preAuditPending: false)
            .ShouldBeNull();
    }

    [Fact]
    public void FirstCoveredChange_RenderLeadMoved_IsNotACoveredChange()
    {
        var snapshot = AutopilotAuthorizationCoverage.SnapshotJson(Baseline, preAuditPending: false);

        Change(snapshot, Topic.Ghost, Channel, Topic.Ghost, Channel, Baseline with { RenderLeadMinutes = 30 }, preAuditPending: false)
            .ShouldBeNull();
    }

    [Fact]
    public void FirstCoveredChange_StoryLeadClearedToTheFallback_IsNotACoveredChange()
    {
        var snapshot = AutopilotAuthorizationCoverage.SnapshotJson(Baseline, preAuditPending: false);

        Change(snapshot, Topic.Ghost, Channel, Topic.Ghost, Channel, Baseline with { StoryLeadMinutes = null }, preAuditPending: false)
            .ShouldBeNull();
    }

    [Fact]
    public void FirstCoveredChange_BufferDaysMoved_IsNotACoveredChange()
    {
        var snapshot = AutopilotAuthorizationCoverage.SnapshotJson(Baseline, preAuditPending: false);

        Change(snapshot, Topic.Ghost, Channel, Topic.Ghost, Channel, Baseline with { BufferDays = 5 }, preAuditPending: false)
            .ShouldBeNull();
    }

    [Fact]
    public void FirstCoveredChange_PreAuditPauseAfterMoved_IsNotACoveredChange()
    {
        var snapshot = AutopilotAuthorizationCoverage.SnapshotJson(Baseline, preAuditPending: false);

        Change(snapshot, Topic.Ghost, Channel, Topic.Ghost, Channel, Baseline with { PreAuditPauseAfter = 5 }, preAuditPending: false)
            .ShouldBeNull();
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"cadence":{"days_of_week":["mon"]},"daily_cap":1,"privacy":"private","veto_window_hours":12,"pre_audit_mode":"keep_cadence_private"}""")]
    public void FirstCoveredChange_UnreadableSnapshot_IsTextChanged(string storedSnapshot)
    {
        // Fail closed: an agreement this build cannot read is not the agreement the series is in now.
        Change(storedSnapshot, Topic.Ghost, Channel, Topic.Ghost, Channel, Baseline, preAuditPending: false)
            .ShouldBe(AutopilotAuthorizationRevokeReason.TextChanged);
    }

    [Fact]
    public void FirstCoveredChange_NullSnapshot_Throws()
    {
        Should.Throw<ArgumentNullException>(() => Change(
            null!,
            Topic.Ghost,
            Channel,
            Topic.Ghost,
            Channel,
            Baseline,
            preAuditPending: false));
    }

    private static AutopilotAuthorizationRevokeReason? Change(
        string storedSnapshot,
        Topic storedTopic,
        Guid? storedChannel,
        Topic currentTopic,
        Guid? currentChannel,
        AutopilotSettings current,
        bool preAuditPending) =>
        AutopilotAuthorizationCoverage.FirstCoveredChange(
            storedSnapshot,
            storedTopic,
            storedChannel,
            currentTopic,
            currentChannel,
            current,
            preAuditPending);
}
