using ErrorOr;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Autopilot;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Series.Autopilot;

/// <summary>
/// Plan §3.1 <c>autopilot_settings</c>: the strict reader, every range, the field path of each error and the
/// snake_case document <see cref="AutopilotSettingsValidator.Serialize"/> writes back.
/// </summary>
/// <remarks>
/// Pure tests: every document is a literal and no test touches a database, the clock or the network. The
/// time-zone cases rely on this runtime's IANA tz database, the same requirement the start-up self-check states.
/// </remarks>
public sealed class AutopilotSettingsValidatorTests
{
    /// <summary>The plan §3.1 example document, verbatim; each test mutates exactly the part it is about.</summary>
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

    [Fact]
    public void AutopilotSettingsValidator_HappyPath_ParsesEveryField()
    {
        var result = Validate(Valid);

        result.IsError.ShouldBeFalse(string.Join("; ", result.Errors.Select(error => error.Description)));

        var settings = result.Value;
        settings.SchemaVersion.ShouldBe(AutopilotSettingsValidator.SupportedSchemaVersion);
        settings.Cadence.DaysOfWeek.Count.ShouldBe(2);
        settings.Cadence.DaysOfWeek[0].ShouldBe(DayOfWeek.Monday);
        settings.Cadence.DaysOfWeek[1].ShouldBe(DayOfWeek.Thursday);
        settings.Cadence.PublishTime.ShouldBe(new TimeOnly(19, 0));
        settings.Cadence.TimeZoneId.ShouldBe("Asia/Ho_Chi_Minh");
        settings.DailyCap.ShouldBe(1);
        settings.BufferDays.ShouldBe(2);
        settings.Privacy.ShouldBe(PrivacyStatus.Private);
        settings.VetoWindowHours.ShouldBe(12);
        settings.PreAuditMode.ShouldBe(PreAuditMode.KeepCadencePrivate);
        settings.PreAuditPauseAfter.ShouldBe(3);
        settings.GateThresholdsRef.ShouldBe("default");
        settings.Tone.ShouldBe("atmospheric, restrained, no gore, first person");
        settings.BannedThemes.Count.ShouldBe(2);
        settings.BannedThemes[0].ShouldBe("children in danger");
        settings.RenderLeadMinutes.ShouldBe(15);
        settings.StoryLeadMinutes.ShouldBe(10);

        // Plan §3.2: mon + thu is a 4-day gap (thu -> mon), so a 2-day buffer plans 6 days ahead.
        AutopilotSettingsValidator.CadenceIntervalDays(settings.Cadence).ShouldBe(4);
        AutopilotSettingsValidator.HorizonDays(settings).ShouldBe(6);
    }

    [Fact]
    public void AutopilotSettingsValidator_RoundTrips_ThroughSerialize()
    {
        var first = Validate(Valid).Value;

        var wire = AutopilotSettingsValidator.Serialize(first);

        // The document is the plan §3.1 wire format: snake_case keys, enum wire names, no CLR names.
        wire.ShouldContain("\"schema_version\":1");
        wire.ShouldContain("\"days_of_week\":[\"mon\",\"thu\"]");
        wire.ShouldContain("\"publish_time\":\"19:00\"");
        wire.ShouldContain("\"time_zone\":\"Asia/Ho_Chi_Minh\"");
        wire.ShouldContain("\"privacy\":\"private\"");
        wire.ShouldContain("\"pre_audit_mode\":\"keep_cadence_private\"");
        wire.ShouldContain("\"render_lead_minutes\":15");
        wire.ShouldNotContain("SchemaVersion");
        wire.ShouldNotContain("KeepCadencePrivate");

        // Reading it back produces the same values and the same bytes.
        var second = Validate(wire);
        second.IsError.ShouldBeFalse();

        var settings = second.Value;
        settings.SchemaVersion.ShouldBe(first.SchemaVersion);
        settings.Cadence.DaysOfWeek.ShouldBe(first.Cadence.DaysOfWeek);
        settings.Cadence.PublishTime.ShouldBe(first.Cadence.PublishTime);
        settings.Cadence.TimeZoneId.ShouldBe(first.Cadence.TimeZoneId);
        settings.DailyCap.ShouldBe(first.DailyCap);
        settings.BufferDays.ShouldBe(first.BufferDays);
        settings.Privacy.ShouldBe(first.Privacy);
        settings.VetoWindowHours.ShouldBe(first.VetoWindowHours);
        settings.PreAuditMode.ShouldBe(first.PreAuditMode);
        settings.PreAuditPauseAfter.ShouldBe(first.PreAuditPauseAfter);
        settings.GateThresholdsRef.ShouldBe(first.GateThresholdsRef);
        settings.Tone.ShouldBe(first.Tone);
        settings.BannedThemes.ShouldBe(first.BannedThemes);
        settings.RenderLeadMinutes.ShouldBe(first.RenderLeadMinutes);
        settings.StoryLeadMinutes.ShouldBe(first.StoryLeadMinutes);

        AutopilotSettingsValidator.Serialize(settings).ShouldBe(wire);
    }

    [Fact]
    public void AutopilotSettingsValidator_InvalidTimeZone_Fails()
    {
        var json = Replace(Valid, "\"Asia/Ho_Chi_Minh\"", "\"Mars/Olympus_Mons\"");

        var result = Validate(json);

        result.IsError.ShouldBeTrue();
        result.Errors.Count.ShouldBe(1);
        result.FirstError.Code.ShouldBe(AutopilotSettingsValidator.InvalidCode);
        result.FirstError.Type.ShouldBe(ErrorType.Validation);
        Fields(result).ShouldHaveSingleItem().ShouldBe("cadence.time_zone");
    }

    [Fact]
    public void AutopilotSettingsValidator_PublishTimeFormat_Fails()
    {
        // The pattern is ^([01]\d|2[0-3]):[0-5]\d$ — a single-digit hour is a 400, never a silent "7:00" parse.
        var result = Validate(Replace(Valid, "\"19:00\"", "\"7:00\""));

        result.IsError.ShouldBeTrue();
        result.Errors.Count.ShouldBe(1);
        Fields(result).ShouldHaveSingleItem().ShouldBe("cadence.publish_time");

        // 24:00 and 19:60 are refused too; 00:00 and 23:59 are the inclusive ends.
        Validate(Replace(Valid, "\"19:00\"", "\"24:00\"")).IsError.ShouldBeTrue();
        Validate(Replace(Valid, "\"19:00\"", "\"19:60\"")).IsError.ShouldBeTrue();
        Validate(Replace(Valid, "\"19:00\"", "\"00:00\"")).IsError.ShouldBeFalse();
        Validate(Replace(Valid, "\"19:00\"", "\"23:59\"")).IsError.ShouldBeFalse();
    }

    [Fact]
    public void AutopilotSettingsValidator_UnknownKey_Fails()
    {
        // additionalProperties: false — a typo must not be ignored, and the field names the offending key.
        var result = Validate(Replace(Valid, "\"daily_cap\": 1,", "\"daily_cap\": 1,\n  \"daily_capp\": 1,"));

        result.IsError.ShouldBeTrue();
        result.Errors.Count.ShouldBe(1);
        Fields(result).ShouldHaveSingleItem().ShouldBe("daily_capp");
    }

    [Fact]
    public void AutopilotSettingsValidator_UnknownCadenceKey_Fails()
    {
        var json = Replace(
            Valid,
            "\"time_zone\": \"Asia/Ho_Chi_Minh\" }",
            "\"time_zone\": \"Asia/Ho_Chi_Minh\", \"tz\": \"UTC\" }");

        var result = Validate(json);

        result.IsError.ShouldBeTrue();
        Fields(result).ShouldHaveSingleItem().ShouldBe("cadence.tz");
    }

    [Fact]
    public void AutopilotSettingsValidator_ToneAndBannedThemes_Limits()
    {
        var tone = new string('t', AutopilotSettingsValidator.ToneMaxChars + 1);
        var themes = string.Join(
            ", ",
            Enumerable.Range(1, AutopilotSettingsValidator.BannedThemesMaxItems + 1)
                .Select(index => $"\"theme {index}\""));

        var json = Replace(
            Replace(Valid, "\"atmospheric, restrained, no gore, first person\"", $"\"{tone}\""),
            "[\"children in danger\", \"real named people\"]",
            $"[{themes}]");

        var result = Validate(json);

        result.IsError.ShouldBeTrue();
        result.Errors.Count.ShouldBe(2);

        var fields = Fields(result);
        fields.ShouldContain("tone");
        fields.ShouldContain("banned_themes");

        // At the limits both pass: 500 characters and 20 themes of 100 characters.
        var atLimit = Replace(
            Replace(Valid, "\"atmospheric, restrained, no gore, first person\"", $"\"{new string('t', AutopilotSettingsValidator.ToneMaxChars)}\""),
            "[\"children in danger\", \"real named people\"]",
            $"[\"{string.Join("\", \"", Enumerable.Repeat(new string('w', AutopilotSettingsValidator.BannedThemeMaxChars), AutopilotSettingsValidator.BannedThemesMaxItems))}\"]");

        Validate(atLimit).IsError.ShouldBeFalse();
    }

    [Fact]
    public void AutopilotSettingsValidator_CollectsEveryError()
    {
        // Three independent broken ranges answer with three errors, not the first one: the API can show a
        // complete errors map (plan §3.1's whole point).
        var json = Replace(
            Replace(
                Replace(Valid, "\"daily_cap\": 1,", "\"daily_cap\": 0,"),
                "\"buffer_days\": 2,",
                "\"buffer_days\": 8,"),
            "\"privacy\": \"private\",",
            "\"privacy\": \"secret\",");

        var result = Validate(json);

        result.IsError.ShouldBeTrue();
        result.Errors.Count.ShouldBe(3);
        result.Errors.ShouldAllBe(error => error.Code == AutopilotSettingsValidator.InvalidCode);

        var fields = Fields(result);
        fields.ShouldContain("daily_cap");
        fields.ShouldContain("buffer_days");
        fields.ShouldContain("privacy");
    }

    [Fact]
    public void AutopilotSettingsValidator_DaysOfWeekRejectsDuplicatesAndUnknownNames()
    {
        var result = Validate(Replace(Valid, "[\"mon\", \"thu\"]", "[\"mon\", \"mon\", \"funday\"]"));

        result.IsError.ShouldBeTrue();
        result.Errors.Count.ShouldBe(2);
        Fields(result).ShouldAllBe(field => field == "cadence.days_of_week");

        // An empty list and a missing list are both refused.
        Validate(Replace(Valid, "[\"mon\", \"thu\"]", "[]")).IsError.ShouldBeTrue();
        Validate(Replace(Valid, "\"days_of_week\": [\"mon\", \"thu\"], ", "")).IsError.ShouldBeTrue();
    }

    [Fact]
    public void AutopilotSettingsValidator_KeepsDaysOfWeekOrder()
    {
        // A round trip echoes the stored document: the validator never reorders days_of_week, and the horizon
        // sorts a copy for its wrap-gap interval, so the order cannot change the plan.
        var result = Validate(Replace(Valid, "[\"mon\", \"thu\"]", "[\"thu\", \"mon\"]"));

        result.IsError.ShouldBeFalse();

        var settings = result.Value;
        settings.Cadence.DaysOfWeek.Count.ShouldBe(2);
        settings.Cadence.DaysOfWeek[0].ShouldBe(DayOfWeek.Thursday);
        settings.Cadence.DaysOfWeek[1].ShouldBe(DayOfWeek.Monday);
        AutopilotSettingsValidator.Serialize(settings).ShouldContain("\"days_of_week\":[\"thu\",\"mon\"]");
        AutopilotSettingsValidator.HorizonDays(settings).ShouldBe(6);
    }

    [Fact]
    public void AutopilotSettingsValidator_EmptyDocument_Fails()
    {
        Validate(null).IsError.ShouldBeTrue();
        Validate("   ").IsError.ShouldBeTrue();
        Fields(Validate(null)).ShouldHaveSingleItem().ShouldBe(AutopilotSettingsValidator.RootField);
        Validate("{").IsError.ShouldBeTrue();
        Validate("[]").IsError.ShouldBeTrue();
    }

    [Fact]
    public void AutopilotSettingsValidator_LeadMinutes_NullAllowedButZeroRejected()
    {
        // null means "use Autopilot:RenderEstimate:Minutes" (plan §3.4); a zero lead would collapse the chain.
        Validate(Replace(Valid, "\"render_lead_minutes\": 15", "\"render_lead_minutes\": null")).IsError.ShouldBeFalse();
        Validate(Replace(Valid, "\"story_lead_minutes\": 10", "\"story_lead_minutes\": null")).IsError.ShouldBeFalse();

        var result = Validate(Replace(Valid, "\"render_lead_minutes\": 15", "\"render_lead_minutes\": 0"));

        result.IsError.ShouldBeTrue();
        Fields(result).ShouldHaveSingleItem().ShouldBe("render_lead_minutes");

        Validate(Replace(Valid, "\"story_lead_minutes\": 10", "\"story_lead_minutes\": 1441")).IsError.ShouldBeTrue();
        Validate(Replace(Valid, "\"story_lead_minutes\": 10", "\"story_lead_minutes\": 1440")).IsError.ShouldBeFalse();
    }

    [Fact]
    public void AutopilotSettingsValidator_Warnings_FlagTightProductionWindow()
    {
        // The plan §3.4 worked example: buffer 2 d covers the whole chain, so there is nothing to warn about.
        var options = new AutopilotTimingOptions(
            PublishLeadMinutes: 120,
            MinVetoWindowHours: 2,
            PublicationCreateMarginMinutes: 10,
            RenderEstimateMinutes: 15,
            StoryEstimateMinutes: 10,
            MaxBufferDays: 7);

        AutopilotSettingsValidator.Warnings(Validate(Valid).Value, options).ShouldBeEmpty();

        // A 48 h veto window with a 1-day buffer cannot fit: start_at falls back to story_start_at.
        var tight = Validate(
                Replace(
                    Replace(Valid, "\"buffer_days\": 2,", "\"buffer_days\": 1,"),
                    "\"veto_window_hours\": 12,",
                    "\"veto_window_hours\": 48,"))
            .Value;

        AutopilotSettingsValidator.Warnings(tight, options).ShouldHaveSingleItem();
    }

    [Fact]
    public void AutopilotSettingsValidator_SchemaVersion_OtherThanOneFails()
    {
        var result = Validate(Replace(Valid, "\"schema_version\": 1", "\"schema_version\": 2"));

        result.IsError.ShouldBeTrue();
        Fields(result).ShouldHaveSingleItem().ShouldBe("schema_version");
    }

    [Fact]
    public void AutopilotSettingsValidator_PreAuditMode_AndPrivacyAreClosedLists()
    {
        Validate(Replace(Valid, "\"keep_cadence_private\"", "\"pause_after_n_private\"")).IsError.ShouldBeFalse();

        var mode = Validate(Replace(Valid, "\"keep_cadence_private\"", "\"pause_after_three_private\""));
        mode.IsError.ShouldBeTrue();
        Fields(mode).ShouldHaveSingleItem().ShouldBe("pre_audit_mode");

        var privacy = Validate(Replace(Valid, "\"privacy\": \"private\"", "\"privacy\": \"Private\""));
        privacy.IsError.ShouldBeTrue();
        Fields(privacy).ShouldHaveSingleItem().ShouldBe("privacy");
    }

    private static ErrorOr<AutopilotSettings> Validate(string? json) => AutopilotSettingsValidator.Validate(json);

    private static string Replace(string json, string oldValue, string newValue) =>
        json.Replace(oldValue, newValue, StringComparison.Ordinal);

    private static List<string> Fields(ErrorOr<AutopilotSettings> result)
    {
        var fields = new List<string>(result.Errors.Count);
        foreach (var error in result.Errors)
        {
            fields.Add(error.Metadata![AutopilotSettingsValidator.FieldMetadataKey].ShouldBeOfType<string>());
        }

        return fields;
    }
}
