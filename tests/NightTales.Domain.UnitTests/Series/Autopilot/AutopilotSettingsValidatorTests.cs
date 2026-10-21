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

    /// <summary>
    /// The zone id is bounded before the runtime resolves it: the longest real IANA ids stay valid, an id over the
    /// cap is a field error (never an exception out of the loader), and a path-like value is refused by the charset.
    /// </summary>
    [Fact]
    public void AutopilotSettingsValidator_TimeZone_IsBoundedAndCharsetChecked()
    {
        // The longest shipped id (32) and the `Etc/GMT+N` spelling are inside both rules.
        Validate(Replace(Valid, "\"Asia/Ho_Chi_Minh\"", "\"America/Argentina/ComodRivadavia\""))
            .IsError.ShouldBeFalse();
        Validate(Replace(Valid, "\"Asia/Ho_Chi_Minh\"", "\"Etc/GMT+5\"")).IsError.ShouldBeFalse();

        // Exactly at the cap the length rule passes, so the resolution rule answers instead.
        var atCap = new string('a', AutopilotSettingsValidator.TimeZoneMaxChars);
        var resolvedNone = Validate(Replace(Valid, "\"Asia/Ho_Chi_Minh\"", $"\"{atCap}\""));
        resolvedNone.IsError.ShouldBeTrue();
        resolvedNone.FirstError.Description.ShouldContain("is not a time zone id");

        var tooLong = Validate(
            Replace(Valid, "\"Asia/Ho_Chi_Minh\"", $"\"{new string('a', AutopilotSettingsValidator.TimeZoneMaxChars + 1)}\""));
        tooLong.IsError.ShouldBeTrue();
        Fields(tooLong).ShouldHaveSingleItem().ShouldBe("cadence.time_zone");

        // The length rule answers here, not the loader: an unresolvable id of any length has the same field path.
        tooLong.FirstError.Description.ShouldContain("at most");

        // A path-like value never reaches the loader.
        var path = Validate(Replace(Valid, "\"Asia/Ho_Chi_Minh\"", "\"../../../etc/passwd\""));
        path.IsError.ShouldBeTrue();
        Fields(path).ShouldHaveSingleItem().ShouldBe("cadence.time_zone");
        path.FirstError.Description.ShouldNotContain("resolves");
    }

    /// <summary>
    /// The tzdata build directories are not zone names (SEC-7, D-7): on a tzdata host the loader resolves
    /// <c>posix/…</c>, <c>right/…</c> and <c>SystemV/…</c> as paths, so before this rule they passed validation and
    /// widened the metric tag with a second spelling of a zone the plain id already names. The boundary is the
    /// directory prefix, in any case; the plain IANA ids keep passing beside it.
    /// </summary>
    [Fact]
    public void AutopilotSettingsValidator_TimeZone_RefusesTzdataDirectories()
    {
        string[] refused =
        [
            "posix/Asia/Ho_Chi_Minh",
            "right/Asia/Ho_Chi_Minh",
            "SystemV/EST5",
            "POSIX/America/Argentina/ComodRivadavia",
            "Systemv/Etc/GMT+5",
        ];

        foreach (var id in refused)
        {
            var result = Validate(Replace(Valid, "\"Asia/Ho_Chi_Minh\"", $"\"{id}\""));

            // The new rule answers, not the loader: the message names the directories, so a host that happens to
            // resolve the path cannot make this pass for the wrong reason.
            result.IsError.ShouldBeTrue($"{id} is a tzdata directory entry, not an IANA zone id");
            Fields(result).ShouldHaveSingleItem().ShouldBe("cadence.time_zone");
            result.FirstError.Description.ShouldContain("directory entry");
        }

        // The nearest neighbours of those ids stay valid: the rule is the prefix, not the containing zone.
        Validate(Replace(Valid, "\"Asia/Ho_Chi_Minh\"", "\"Asia/Ho_Chi_Minh\"")).IsError.ShouldBeFalse();
        Validate(Replace(Valid, "\"Asia/Ho_Chi_Minh\"", "\"America/Argentina/ComodRivadavia\""))
            .IsError.ShouldBeFalse();
        Validate(Replace(Valid, "\"Asia/Ho_Chi_Minh\"", "\"Etc/GMT+5\"")).IsError.ShouldBeFalse();
    }

    /// <summary>
    /// The gate-profile ref is a bounded slug: the cap is inclusive, and whitespace or punctuation outside
    /// <c>_</c>, <c>-</c> and <c>.</c> is a field error rather than a stored value the profile resolver cannot mean.
    /// </summary>
    [Fact]
    public void AutopilotSettingsValidator_GateThresholdsRef_IsBoundedAndCharsetChecked()
    {
        var atCap = new string('p', AutopilotSettingsValidator.GateThresholdsRefMaxChars);
        Validate(Replace(Valid, "\"default\"", $"\"{atCap}\"")).IsError.ShouldBeFalse();
        Validate(Replace(Valid, "\"default\"", "\"strict-v1.2_final\"")).IsError.ShouldBeFalse();

        var tooLong = Validate(
            Replace(Valid, "\"default\"", $"\"{new string('p', AutopilotSettingsValidator.GateThresholdsRefMaxChars + 1)}\""));
        tooLong.IsError.ShouldBeTrue();
        Fields(tooLong).ShouldHaveSingleItem().ShouldBe("gate_thresholds_ref");

        var withSpace = Validate(Replace(Valid, "\"default\"", "\"strict v1\""));
        withSpace.IsError.ShouldBeTrue();
        Fields(withSpace).ShouldHaveSingleItem().ShouldBe("gate_thresholds_ref");
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
