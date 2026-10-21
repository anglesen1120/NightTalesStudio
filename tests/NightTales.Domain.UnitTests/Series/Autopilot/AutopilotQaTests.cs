using System.Text;
using NightTales.Domain.Series.Autopilot;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Series.Autopilot;

/// <summary>
/// The independent QA pass over 02.8.3-T1's domain surface: the corners the author-written classes assert only
/// indirectly — "no terminal state has an exit", the resume ladder's ladder property, the plan §3.6 pause codes,
/// slot arithmetic at the local-midnight and horizon boundaries, and the plan §3.1 range boundaries.
/// </summary>
/// <remarks>
/// Written by a reviewer who did not write the product code. Pure tests: literals only, no clock, no database.
/// </remarks>
public sealed class AutopilotQaTests
{
    private static readonly SlotCalculator Calculator = new();

    private static readonly AutopilotRunState[] TerminalStates =
    [
        AutopilotRunState.Published,
        AutopilotRunState.PublishedPrivateLocked,
        AutopilotRunState.GateFailed,
        AutopilotRunState.CancelledByOwner,
        AutopilotRunState.Failed,
    ];

    /// <summary>
    /// Plan §3.6's complete table, as the wire codes the <c>ck_autopilot_runs_pause_reason</c> CHECK accepts: the
    /// snake_case of the member name, except row 6b whose code is <c>autopilot_channel_unsupported</c>.
    /// </summary>
    public static TheoryData<AutopilotPauseReason, string> PauseReasonCodes
    {
        get
        {
            var table = new TheoryData<AutopilotPauseReason, string>
            {
                { AutopilotPauseReason.GateFailedStreak, "gate_failed_streak" },
                { AutopilotPauseReason.SpendCapReached, "spend_cap_reached" },
                { AutopilotPauseReason.RunCostCapReached, "run_cost_cap_reached" },
                { AutopilotPauseReason.ProviderFundsExhausted, "provider_funds_exhausted" },
                { AutopilotPauseReason.ChannelNotActive, "channel_not_active" },
                { AutopilotPauseReason.ChannelUnsupported, "autopilot_channel_unsupported" },
                { AutopilotPauseReason.OauthClientIncident, "oauth_client_incident" },
                { AutopilotPauseReason.YoutubeQuotaExhausted, "youtube_quota_exhausted" },
                { AutopilotPauseReason.UploadFailed, "upload_failed" },
                { AutopilotPauseReason.VideoRejected, "video_rejected" },
                { AutopilotPauseReason.VetoNoticeUndelivered, "veto_notice_undelivered" },
                { AutopilotPauseReason.RepeatedRunFailures, "repeated_run_failures" },
                { AutopilotPauseReason.SlotMissed, "slot_missed" },
                { AutopilotPauseReason.PreAuditPrivateLimit, "pre_audit_private_limit" },
                { AutopilotPauseReason.ContentReported, "content_reported" },
                { AutopilotPauseReason.OwnerPaused, "owner_paused" },
            };

            return table;
        }
    }

    /// <summary>
    /// Plan §3.1's ranges, at both edges: a value just outside each range fails and the boundary value passes, so a
    /// widened or narrowed validator cannot slip through.
    /// </summary>
    public static TheoryData<string, object, bool> RangeBoundaries
    {
        get
        {
            var table = new TheoryData<string, object, bool>();

            foreach (var buffer in new[] { 0, 1, 7, 8 })
            {
                table.Add("buffer_days", buffer, buffer is >= 1 and <= 7);
            }

            foreach (var cap in new[] { 0, 1, 5, 6 })
            {
                table.Add("daily_cap", cap, cap is >= 1 and <= 5);
            }

            foreach (var veto in new[] { 1, 2, 48, 49 })
            {
                table.Add("veto_window_hours", veto, veto is >= 2 and <= 48);
            }

            foreach (var pauseAfter in new[] { 0, 1, 20, 21 })
            {
                table.Add("pre_audit_pause_after", pauseAfter, pauseAfter is >= 1 and <= 20);
            }

            return table;
        }
    }

    [Theory]
    [MemberData(nameof(PauseReasonCodes))]
    public void Qa_PauseReason_WireCodes_AreThePlanSection36Codes(AutopilotPauseReason reason, string code)
    {
        // The member name is the Pascal spelling of the code, so an independent re-derivation catches a misnamed
        // member (which would silently make the CHECK reject a real pause) — the one documented exception aside.
        var derived = reason == AutopilotPauseReason.ChannelUnsupported
            ? "autopilot_channel_unsupported"
            : PascalToSnake(reason.ToString());

        derived.ShouldBe(code);
        AutopilotPauseReasonWireShape(code).ShouldBeTrue($"plan §3.6 code '{code}' is not a valid snake_case wire value");
    }

    [Fact]
    public void Qa_PauseReason_HasExactlyTheSixteenPlanTriggers()
    {
        Enum.GetValues<AutopilotPauseReason>().Length.ShouldBe(16, "plan §3.6 rows 1-16 (17 is a banner, not a pause)");
    }

    [Fact]
    public void Qa_StateMachine_EveryTerminalState_HasNoExit()
    {
        foreach (var terminal in TerminalStates)
        {
            foreach (var target in Enum.GetValues<AutopilotRunState>())
            {
                AutopilotRunStateMachine.CanTransition(terminal, target).ShouldBeFalse(
                    $"plan §3.5 rule 3: {terminal} is terminal, so ({terminal} → {target}) is not a row");
            }

            var run = AutopilotRunScenario.RunAt(terminal);

            run.FinishedAt.ShouldNotBeNull($"{terminal} sets finished_at");

            Should.Throw<InvalidOperationException>(
                () => run.AdvanceTo(AutopilotRunState.Planned, step: null, AutopilotRunScenario.Now));

            if (terminal != AutopilotRunState.Failed)
            {
                // `any → failed` never includes a terminal row; Fail on `failed` itself is the documented no-op.
                Should.Throw<InvalidOperationException>(
                    () => run.Fail("qa_terminal_exit", AutopilotRunScenario.Now),
                    $"{terminal} must not become failed");
            }
        }
    }

    [Fact]
    public void Qa_Run_PauseOnEveryTerminalState_Throws()
    {
        foreach (var terminal in TerminalStates)
        {
            var run = AutopilotRunScenario.RunAt(terminal);

            Should.Throw<InvalidOperationException>(
                () => run.Pause(AutopilotPauseReason.OwnerPaused, AutopilotRunScenario.Now),
                $"a finished run ({terminal}) has nothing left to pause");
        }
    }

    private static AutopilotRun PausedAtStoryGate()
    {
        var run = AutopilotRunScenario.StoryGate();
        run.Pause(AutopilotPauseReason.OwnerPaused, AutopilotRunScenario.Now);

        return run;
    }

    [Fact]
    public void Qa_Run_PausedRun_ResumesOnlyToTheStateItsStepImplies()
    {
        // Paused from `story_gate` with no render spent: the only non-terminal target the ladder allows is
        // StoryGate, every other non-terminal state throws even though CanTransition admits the family.
        var run = PausedAtStoryGate();

        foreach (var target in Enum.GetValues<AutopilotRunState>())
        {
            if (AutopilotRunStateMachine.IsTerminal(target) || target == AutopilotRunState.Paused)
            {
                continue;
            }

            var copy = PausedAtStoryGate();

            if (target == AutopilotRunState.StoryGate)
            {
                copy.AdvanceTo(target, step: null, AutopilotRunScenario.Now);
                copy.State.ShouldBe(AutopilotRunState.StoryGate);
                copy.Outcome.ShouldBeNull();
                copy.PauseReason.ShouldBeNull();

                continue;
            }

            Should.Throw<InvalidOperationException>(
                () => copy.AdvanceTo(target, step: null, AutopilotRunScenario.Now),
                $"a run paused at step '{copy.Step}' may not resume straight into {target}");
        }

        run.Resume(AutopilotRunScenario.Now.AddMinutes(1));
        run.State.ShouldBe(AutopilotRunState.StoryGate);

        // A run that is not paused has no pause to resume.
        Should.Throw<InvalidOperationException>(() => run.Resume(AutopilotRunScenario.Now.AddMinutes(2)));
    }

    [Fact]
    public void Qa_SlotCalculator_UnknownTimeZone_Throws()
    {
        var cadence = Cadence([DayOfWeek.Monday], 19, 0, "Nowhere/Atlantis");

        Should.Throw<TimeZoneNotFoundException>(
            () => Calculator.NextSlots(cadence, new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), 7, 1));
    }

    [Fact]
    public void Qa_SlotCalculator_MidnightPublishTime_RollsTheLocalDay()
    {
        // Monday 2026-10-19 00:30 in Ho Chi Minh City: that Monday's own 00:00 slot is 30 minutes in the past, so
        // the first slot is the NEXT Monday's midnight — 2026-10-25T17:00Z — not a second slot of today.
        var cadence = Cadence([DayOfWeek.Monday], 0, 0, "Asia/Ho_Chi_Minh");
        var now = new DateTimeOffset(2026, 10, 18, 17, 30, 0, TimeSpan.Zero);

        Calculator.NextSlots(cadence, now, 1, 1).ShouldBeEmpty("the horizon's last local day is today");

        var slots = Calculator.NextSlots(cadence, now, 8, 1);

        slots.ShouldHaveSingleItem().ShouldBe(new DateTimeOffset(2026, 10, 25, 17, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Qa_SlotCalculator_HorizonEndIsInclusiveInTheSeriesZone()
    {
        // Wednesday 2026-10-21 07:00 in Ho Chi Minh City; the next Thursday slot is 19:00 local = 12:00Z.
        var cadence = Cadence([DayOfWeek.Thursday], 19, 0, "Asia/Ho_Chi_Minh");
        var now = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);

        Calculator.NextSlots(cadence, now, 0, 1).ShouldBeEmpty("a zero-day horizon holds only today (Wednesday)");

        Calculator.NextSlots(cadence, now, 1, 1)
            .ShouldBe([new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero)]);
    }

    [Fact]
    public void Qa_ApplyDailyCap_CountsOccupiedSlotsOfTheChannelDay_AndKeepsTheEarliestCandidates()
    {
        var first = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        var second = new DateTimeOffset(2026, 10, 22, 13, 0, 0, TimeSpan.Zero);
        var nextDay = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

        // Unsorted candidates, one occupied slot already on the day: cap 2 admits the earliest candidate of that
        // local day and one of the next day, and never the later candidate of the full day.
        var admitted = SlotCalculator.ApplyDailyCap(
            [nextDay, second, first],
            "Asia/Ho_Chi_Minh",
            dailyCap: 2,
            existingOccupiedSlots: [new DateTimeOffset(2026, 10, 22, 1, 0, 0, TimeSpan.Zero)]);

        admitted.ShouldBe([first, nextDay]);
    }

    [Theory]
    [MemberData(nameof(RangeBoundaries))]
    public void Qa_SettingsValidator_HoldsThePlanSection31RangeAtBothEdges(string key, object value, bool accepted)
    {
        var result = AutopilotSettingsValidator.Validate(DocumentWith(key, value));

        result.IsError.ShouldBe(
            !accepted,
            $"plan §3.1 range of {key} (value {value}): "
            + string.Join("; ", result.Errors.Select(error => $"{error.Code}:{error.Description}")));
    }

    /// <summary>The plan §3.1 example document with exactly one top-level key replaced.</summary>
    private static string DocumentWith(string key, object value) =>
        $$"""
        {
          "schema_version": 1,
          "cadence": { "days_of_week": ["mon", "thu"], "publish_time": "19:00", "time_zone": "Asia/Ho_Chi_Minh" },
          "daily_cap": {{(key == "daily_cap" ? value : 1)}},
          "buffer_days": {{(key == "buffer_days" ? value : 2)}},
          "privacy": "private",
          "veto_window_hours": {{(key == "veto_window_hours" ? value : 12)}},
          "pre_audit_mode": "keep_cadence_private",
          "pre_audit_pause_after": {{(key == "pre_audit_pause_after" ? value : 3)}},
          "gate_thresholds_ref": "default",
          "tone": "atmospheric, restrained",
          "banned_themes": [],
          "render_lead_minutes": null,
          "story_lead_minutes": null
        }
        """;

    private static bool AutopilotPauseReasonWireShape(string code)
    {
        if (code.Length == 0 || code[0] < 'a' || code[0] > 'z')
        {
            return false;
        }

        return code.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
            && !code.Contains("__", StringComparison.Ordinal)
            && code[^1] != '_';
    }

    private static string PascalToSnake(string name)
    {
        var builder = new StringBuilder(name.Length + 8);

        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];

            if (char.IsUpper(character))
            {
                if (index > 0)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(character));

                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static AutopilotCadence Cadence(
        IReadOnlyList<DayOfWeek> days,
        int hour,
        int minute,
        string zone) =>
        new(days, new TimeOnly(hour, minute), zone);
}
