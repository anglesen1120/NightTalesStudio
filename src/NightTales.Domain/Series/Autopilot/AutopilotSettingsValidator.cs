using System.Globalization;
using System.Text;
using System.Text.Json;
using ErrorOr;
using NightTales.Domain.Common;

namespace NightTales.Domain.Series.Autopilot;

/// <summary>
/// The strict reader, validator and writer of <c>series.autopilot_settings</c> (plan §3.1, decision 1): the
/// jsonb text goes in, either an <see cref="AutopilotSettings"/> or every reason it is not one comes out, and
/// <see cref="Serialize"/> writes the same document back so 02.8.3-T2 can round-trip what it stores.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every violation, not the first one.</b> <see cref="Validate"/> answers with one
/// <see cref="ErrorType.Validation"/> error per broken rule, each carrying the dotted field path in
/// <c>metadata["field"]</c> — <c>cadence.time_zone</c>, <c>daily_cap</c>, <c>banned_themes</c> … — under the
/// single code <c>autopilot_settings_invalid</c> (plan §3.1), so the API can answer a complete
/// <c>errors</c> map instead of making the owner fix one field per round trip. <see cref="RootField"/> is the
/// field of an error about the document itself (absent, not JSON, not an object).
/// </para>
/// <para>
/// <b>Never throws, never falls back.</b> A malformed document is an error, not an exception: the JSON is read
/// with <see cref="JsonDocument"/> inside a try/catch for
/// <see cref="JsonException"/> only. In particular an unknown IANA id is <c>cadence.time_zone</c> and never a
/// silent UTC fallback (README §10 T7), and an unknown key is rejected at the top level and inside
/// <c>cadence</c> — <c>additionalProperties: false</c> semantics, so a typo cannot be ignored (plan §3.1).
/// </para>
/// <para>
/// <b>Nothing is normalised.</b> <c>days_of_week</c> keeps the owner's stored order and the two text fields are
/// kept exactly as written, so <see cref="Serialize"/> echoes the document the owner stored and a round trip
/// cannot silently reorder a cadence. Nothing is defaulted either, because a default would hide a value the
/// owner never wrote; the wrap-gap horizon sorts a copy internally (<see cref="CadenceIntervalDays"/>).
/// </para>
/// <para>
/// <b>What this class does not do.</b> It does not decide whether the series may run autopilot (authorization,
/// plan §3.7) and does not read the run's channel: it only states whether a settings document is usable.
/// </para>
/// </remarks>
public static class AutopilotSettingsValidator
{
    /// <summary>The single error code of an unusable settings document (plan §3.1).</summary>
    public const string InvalidCode = "autopilot_settings_invalid";

    /// <summary>The error metadata key carrying the dotted field path.</summary>
    public const string FieldMetadataKey = "field";

    /// <summary>The field path of an error about the document itself rather than one of its members.</summary>
    public const string RootField = "$";

    /// <summary>The only schema version this build reads.</summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>The most themes a settings document may carry (plan §3.1).</summary>
    public const int BannedThemesMaxItems = 20;

    /// <summary>The longest one banned theme may be, in characters (plan §3.1).</summary>
    public const int BannedThemeMaxChars = 100;

    /// <summary>The longest tone brief, in characters (plan §3.1).</summary>
    public const int ToneMaxChars = 500;

    /// <summary>
    /// The longest <c>gate_thresholds_ref</c> this build accepts, in characters. The value is a profile name from
    /// the gate options (<c>default</c> is the shipped one), so a short slug is the whole of its vocabulary; the cap
    /// keeps a stored document from carrying a value the gate resolver and the logs never meant to hold.
    /// </summary>
    public const int GateThresholdsRefMaxChars = 64;

    /// <summary>
    /// The longest <c>cadence.time_zone</c> id this build accepts, in characters. The longest id in the IANA
    /// database is 32 characters (<c>America/Argentina/ComodRivadavia</c>); the cap is checked before the runtime
    /// resolves the id, so an oversized value is a validation error instead of a loader exception.
    /// </summary>
    public const int TimeZoneMaxChars = 64;

    private const int DailyCapMin = 1;
    private const int DailyCapMax = 5;
    private const int BufferDaysMin = 1;
    private const int BufferDaysMax = 7;
    private const int VetoWindowHoursMin = 2;
    private const int VetoWindowHoursMax = 48;
    private const int PreAuditPauseAfterMin = 1;
    private const int PreAuditPauseAfterMax = 20;
    private const int LeadMinutesMin = 1;
    private const int LeadMinutesMax = 1_440;

    private static readonly HashSet<string> RootKeys = new(StringComparer.Ordinal)
    {
        "schema_version",
        "cadence",
        "daily_cap",
        "buffer_days",
        "privacy",
        "veto_window_hours",
        "pre_audit_mode",
        "pre_audit_pause_after",
        "gate_thresholds_ref",
        "tone",
        "banned_themes",
        "render_lead_minutes",
        "story_lead_minutes",
    };

    private static readonly HashSet<string> CadenceKeys = new(StringComparer.Ordinal)
    {
        "days_of_week",
        "publish_time",
        "time_zone",
    };

    private static readonly Dictionary<string, DayOfWeek> WeekdayNames = new(StringComparer.Ordinal)
    {
        ["mon"] = DayOfWeek.Monday,
        ["tue"] = DayOfWeek.Tuesday,
        ["wed"] = DayOfWeek.Wednesday,
        ["thu"] = DayOfWeek.Thursday,
        ["fri"] = DayOfWeek.Friday,
        ["sat"] = DayOfWeek.Saturday,
        ["sun"] = DayOfWeek.Sunday,
    };

    /// <summary>Reads and validates one <c>series.autopilot_settings</c> document.</summary>
    /// <param name="json">The stored jsonb text; absent or blank is an error, not a default document.</param>
    /// <returns>The settings, or every rule the document breaks as <c>autopilot_settings_invalid</c>.</returns>
    public static ErrorOr<AutopilotSettings> Validate(string? json)
    {
        var errors = new List<Error>();

        if (string.IsNullOrWhiteSpace(json))
        {
            errors.Add(Violation(RootField, "a settings document is required."));

            return errors;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            errors.Add(Violation(RootField, "must be valid JSON."));

            return errors;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add(Violation(RootField, "must be a JSON object."));

                return errors;
            }

            RejectUnknownKeys(root, string.Empty, RootKeys, errors);

            var schemaVersion = ReadInt(root, "schema_version", "schema_version", int.MinValue, int.MaxValue, errors);
            if (schemaVersion is { } version && version != SupportedSchemaVersion)
            {
                errors.Add(Violation("schema_version", Invariant($"must be {SupportedSchemaVersion}.")));
            }

            var cadence = ReadCadence(root, errors);
            var dailyCap = ReadInt(root, "daily_cap", "daily_cap", DailyCapMin, DailyCapMax, errors);
            var bufferDays = ReadInt(root, "buffer_days", "buffer_days", BufferDaysMin, BufferDaysMax, errors);
            var privacy = ReadPrivacy(root, errors);
            var vetoWindowHours = ReadInt(
                root, "veto_window_hours", "veto_window_hours", VetoWindowHoursMin, VetoWindowHoursMax, errors);
            var preAuditMode = ReadPreAuditMode(root, errors);
            var preAuditPauseAfter = ReadInt(
                root, "pre_audit_pause_after", "pre_audit_pause_after", PreAuditPauseAfterMin, PreAuditPauseAfterMax, errors);
            var gateThresholdsRef = ReadGateThresholdsRef(root, errors);
            var tone = ReadTone(root, errors);
            var bannedThemes = ReadBannedThemes(root, errors);
            var renderLeadMinutes = ReadOptionalInt(
                root, "render_lead_minutes", "render_lead_minutes", LeadMinutesMin, LeadMinutesMax, errors);
            var storyLeadMinutes = ReadOptionalInt(
                root, "story_lead_minutes", "story_lead_minutes", LeadMinutesMin, LeadMinutesMax, errors);

            if (errors.Count > 0)
            {
                return errors;
            }

            return new AutopilotSettings(
                schemaVersion!.Value,
                cadence!,
                dailyCap!.Value,
                bufferDays!.Value,
                privacy!.Value,
                vetoWindowHours!.Value,
                preAuditMode!.Value,
                preAuditPauseAfter!.Value,
                gateThresholdsRef!,
                tone!,
                bannedThemes!,
                renderLeadMinutes,
                storyLeadMinutes);
        }
    }

    /// <summary>Writes the canonical <c>series.autopilot_settings</c> document of a settings record.</summary>
    /// <remarks>
    /// The key order, the snake_case spellings and the enum wire names are plan §3.1's, so
    /// <c>Serialize(Validate(x).Value)</c> is the same document a client wrote and a second
    /// <see cref="Validate"/> reads it back unchanged. The text carries no indentation: it is stored, compared
    /// and diffed, not shown.
    /// </remarks>
    /// <param name="settings">The settings to write.</param>
    /// <returns>The jsonb text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is <see langword="null"/>.</exception>
    public static string Serialize(AutopilotSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", settings.SchemaVersion);

            writer.WriteStartObject("cadence");
            writer.WriteStartArray("days_of_week");
            foreach (var day in settings.Cadence.DaysOfWeek)
            {
                writer.WriteStringValue(WeekdayWire(day));
            }

            writer.WriteEndArray();
            writer.WriteString("publish_time", settings.Cadence.PublishTime.ToString("HH:mm", CultureInfo.InvariantCulture));
            writer.WriteString("time_zone", settings.Cadence.TimeZoneId);
            writer.WriteEndObject();

            writer.WriteNumber("daily_cap", settings.DailyCap);
            writer.WriteNumber("buffer_days", settings.BufferDays);
            writer.WriteString("privacy", PrivacyWire(settings.Privacy));
            writer.WriteNumber("veto_window_hours", settings.VetoWindowHours);
            writer.WriteString("pre_audit_mode", PreAuditModeWire.ToWire(settings.PreAuditMode));
            writer.WriteNumber("pre_audit_pause_after", settings.PreAuditPauseAfter);
            writer.WriteString("gate_thresholds_ref", settings.GateThresholdsRef);
            writer.WriteString("tone", settings.Tone);

            writer.WriteStartArray("banned_themes");
            foreach (var theme in settings.BannedThemes)
            {
                writer.WriteStringValue(theme);
            }

            writer.WriteEndArray();

            WriteOptionalNumber(writer, "render_lead_minutes", settings.RenderLeadMinutes);
            WriteOptionalNumber(writer, "story_lead_minutes", settings.StoryLeadMinutes);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>
    /// The planning horizon of a settings document (plan §3.2):
    /// <c>buffer_days + max(cadence interval in days, 1)</c>.
    /// </summary>
    /// <remarks>
    /// The cadence interval is the largest gap in days between consecutive cadence weekdays <em>wrapping the
    /// week</em> — <c>mon + thu</c> is 3 days one way and 4 days the other, so the interval is 4 and a
    /// <c>buffer_days = 2</c> document plans 6 days ahead. That always covers the next slot even with a single
    /// weekday, which is the point of the formula: a horizon of only <c>buffer_days</c> would find nothing on
    /// the five days between two Mondays and the series would silently stop planning.
    /// </remarks>
    /// <param name="settings">The settings.</param>
    /// <returns>How many days ahead of <c>now</c> the planner asks <see cref="SlotCalculator"/> for slots.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is <see langword="null"/>.</exception>
    public static int HorizonDays(AutopilotSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.BufferDays + CadenceIntervalDays(settings.Cadence);
    }

    /// <summary>The largest gap, in days, between consecutive cadence weekdays, wrapping the week.</summary>
    /// <param name="cadence">The cadence.</param>
    /// <returns>1..7; a single weekday is 7, an empty (invalid) list is 1.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cadence"/> is <see langword="null"/>.</exception>
    public static int CadenceIntervalDays(AutopilotCadence cadence)
    {
        ArgumentNullException.ThrowIfNull(cadence);

        var days = cadence.DaysOfWeek.Select(WeekIndex).Distinct().OrderBy(static day => day).ToArray();
        if (days.Length == 0)
        {
            return 1;
        }

        if (days.Length == 1)
        {
            return 7;
        }

        var largest = 0;
        for (var index = 1; index < days.Length; index++)
        {
            largest = Math.Max(largest, days[index] - days[index - 1]);
        }

        return Math.Max(largest, days[0] + 7 - days[^1]);
    }

    /// <summary>
    /// The plan §3.4 warnings a valid document still deserves — today the one the plan names: the render and
    /// story leads (plus the veto window and the margin) do not fit the window <c>buffer_days</c> opens, so
    /// <c>start_at</c> falls back to <c>story_start_at</c> and the production window is the tight one.
    /// </summary>
    /// <remarks>
    /// A warning is not a validation error: a <c>long</c> episode with the default <c>render_lead_minutes</c>
    /// produces exactly this way and the owner may accept it. The caller (the settings route) shows the strings;
    /// the plan's rule is that only <c>autopilot_settings_invalid</c> refuses a document.
    /// </remarks>
    /// <param name="settings">The settings under test.</param>
    /// <param name="options">The configured lead times and estimates.</param>
    /// <returns>The warnings, empty when the buffer covers the whole production chain.</returns>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> Warnings(AutopilotSettings settings, AutopilotTimingOptions options)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);

        var timing = AutopilotTiming.Compute(DateTimeOffset.UnixEpoch, settings, options);
        var bufferStart = DateTimeOffset.UnixEpoch.AddDays(-settings.BufferDays);

        return timing.StartAt < bufferStart
            ? [Invariant(
                $"The render and story leads plus the veto window and the creation margin exceed the {settings.BufferDays} day buffer, so start_at falls back to story_start_at and the production window is tight (plan §3.4).")]
            : [];
    }

    private static AutopilotCadence? ReadCadence(JsonElement root, List<Error> errors)
    {
        const string Field = "cadence";

        if (!root.TryGetProperty(Field, out var cadence))
        {
            errors.Add(Violation(Field, "is required."));

            return null;
        }

        if (cadence.ValueKind != JsonValueKind.Object)
        {
            errors.Add(Violation(Field, "must be an object."));

            return null;
        }

        RejectUnknownKeys(cadence, Field + ".", CadenceKeys, errors);

        var daysOfWeek = ReadDaysOfWeek(cadence, errors);
        var publishTime = ReadPublishTime(cadence, errors);
        var timeZoneId = ReadTimeZone(cadence, errors);

        if (daysOfWeek is null || publishTime is null || timeZoneId is null)
        {
            return null;
        }

        return new AutopilotCadence(daysOfWeek, publishTime.Value, timeZoneId);
    }

    private static DayOfWeek[]? ReadDaysOfWeek(JsonElement cadence, List<Error> errors)
    {
        const string Field = "cadence.days_of_week";

        if (!cadence.TryGetProperty("days_of_week", out var element))
        {
            errors.Add(Violation(Field, "is required."));

            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            errors.Add(Violation(Field, "must be an array of weekday names (mon..sun)."));

            return null;
        }

        var failed = false;
        var days = new List<DayOfWeek>();
        var seen = new HashSet<DayOfWeek>();

        if (element.GetArrayLength() == 0)
        {
            errors.Add(Violation(Field, "must name at least one weekday."));
            failed = true;
        }

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                errors.Add(Violation(Field, "must contain weekday names (mon..sun)."));
                failed = true;

                continue;
            }

            var name = item.GetString();
            if (name is null || !WeekdayNames.TryGetValue(name, out var day))
            {
                errors.Add(Violation(Field, Invariant($"'{name}' is not a weekday name (mon..sun).")));
                failed = true;

                continue;
            }

            if (!seen.Add(day))
            {
                errors.Add(Violation(Field, Invariant($"'{name}' appears more than once.")));
                failed = true;
            }
            else
            {
                days.Add(day);
            }
        }

        return failed ? null : days.ToArray();
    }

    private static TimeOnly? ReadPublishTime(JsonElement cadence, List<Error> errors)
    {
        const string Field = "cadence.publish_time";

        if (!TryText(cadence, "publish_time", Field, errors, out var text))
        {
            return null;
        }

        if (!IsPublishTime(text))
        {
            errors.Add(Violation(Field, "must match hh:mm, from 00:00 to 23:59."));

            return null;
        }

        return new TimeOnly(
            int.Parse(text.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture),
            int.Parse(text.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The <c>cadence.time_zone</c> id: a length and charset guard first, then the runtime's own resolution.
    /// </summary>
    /// <remarks>
    /// The guard exists because the id is resolved with <see cref="TimeZoneInfo.FindSystemTimeZoneById(string)"/>,
    /// which on a tzdata host accepts any path below the zone directory (<c>posix/…</c>, <c>right/…</c>) and can
    /// throw for a value it considers a malformed path. Capping the length and allowing only the characters real
    /// IANA ids use (<c>America/Argentina/ComodRivadavia</c>, <c>Etc/GMT+5</c>) keeps the stored document inside
    /// the vocabulary the metric tag and the logs can hold, and turns an oversized value into a plain validation
    /// error instead of an exception escaping the validator.
    /// </remarks>
    private static string? ReadTimeZone(JsonElement cadence, List<Error> errors)
    {
        const string Field = "cadence.time_zone";

        if (!TryText(cadence, "time_zone", Field, errors, out var id))
        {
            return null;
        }

        if (RuneCount(id) > TimeZoneMaxChars)
        {
            errors.Add(Violation(Field, Invariant($"must be at most {TimeZoneMaxChars} characters.")));

            return null;
        }

        if (!IsTimeZoneId(id))
        {
            errors.Add(Violation(Field, "must be a time zone id of letters, digits, '/', '_', '-' or '+'."));

            return null;
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            errors.Add(Violation(Field, Invariant($"'{id}' is not a time zone id this runtime resolves.")));

            return null;
        }
        catch (InvalidTimeZoneException)
        {
            errors.Add(Violation(Field, Invariant($"'{id}' resolves to time zone data this runtime cannot use.")));

            return null;
        }

        return id;
    }

    private static PrivacyStatus? ReadPrivacy(JsonElement root, List<Error> errors)
    {
        const string Field = "privacy";

        if (!TryText(root, Field, Field, errors, out var text))
        {
            return null;
        }

        switch (text)
        {
            case "public":
                return PrivacyStatus.Public;
            case "unlisted":
                return PrivacyStatus.Unlisted;
            case "private":
                return PrivacyStatus.Private;
            default:
                errors.Add(Violation(Field, "must be public, unlisted or private."));

                return null;
        }
    }

    private static PreAuditMode? ReadPreAuditMode(JsonElement root, List<Error> errors)
    {
        const string Field = "pre_audit_mode";

        if (!TryText(root, Field, Field, errors, out var text))
        {
            return null;
        }

        if (PreAuditModeWire.TryParse(text, out var mode))
        {
            return mode;
        }

        errors.Add(Violation(Field, "must be keep_cadence_private or pause_after_n_private."));

        return null;
    }

    /// <summary>
    /// The <c>gate_thresholds_ref</c> profile name: bounded and restricted to the characters a profile key uses,
    /// because the value selects a gate profile and reaches the gate evaluators' diagnostics.
    /// </summary>
    private static string? ReadGateThresholdsRef(JsonElement root, List<Error> errors)
    {
        const string Field = "gate_thresholds_ref";

        if (!TryText(root, Field, Field, errors, out var reference))
        {
            return null;
        }

        if (RuneCount(reference) > GateThresholdsRefMaxChars)
        {
            errors.Add(Violation(Field, Invariant($"must be at most {GateThresholdsRefMaxChars} characters.")));

            return null;
        }

        if (!IsProfileName(reference))
        {
            errors.Add(Violation(Field, "must be a profile name of letters, digits, '_', '-' or '.'."));

            return null;
        }

        return reference;
    }

    /// <summary>Whether every character is one a gate-profile key uses (ASCII letters, digits, <c>_</c>, <c>-</c>, <c>.</c>).</summary>
    private static bool IsProfileName(string value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether every character is one an IANA id uses (ASCII letters, digits, <c>/</c>, <c>_</c>, <c>-</c>,
    /// <c>+</c>): the separators of <c>America/Argentina/ComodRivadavia</c>, <c>Asia/Ho_Chi_Minh</c> and
    /// <c>Etc/GMT+5</c>, and nothing path-like or whitespace.
    /// </summary>
    private static bool IsTimeZoneId(string value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('/' or '_' or '-' or '+'))
            {
                return false;
            }
        }

        return true;
    }

    private static string? ReadTone(JsonElement root, List<Error> errors)
    {
        const string Field = "tone";

        if (!TryText(root, Field, Field, errors, out var tone))
        {
            return null;
        }

        if (RuneCount(tone) > ToneMaxChars)
        {
            errors.Add(Violation(Field, Invariant($"must be at most {ToneMaxChars} characters.")));

            return null;
        }

        return tone;
    }

    private static List<string>? ReadBannedThemes(JsonElement root, List<Error> errors)
    {
        const string Field = "banned_themes";

        if (!root.TryGetProperty(Field, out var element))
        {
            errors.Add(Violation(Field, "is required."));

            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            errors.Add(Violation(Field, "must be an array of strings."));

            return null;
        }

        var failed = false;

        if (element.GetArrayLength() > BannedThemesMaxItems)
        {
            errors.Add(Violation(Field, Invariant($"must hold at most {BannedThemesMaxItems} themes.")));
            failed = true;
        }

        var themes = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                errors.Add(Violation(Field, "must contain strings."));
                failed = true;

                continue;
            }

            var theme = item.GetString();
            if (string.IsNullOrWhiteSpace(theme))
            {
                errors.Add(Violation(Field, "must not contain a blank theme."));
                failed = true;

                continue;
            }

            if (RuneCount(theme) > BannedThemeMaxChars)
            {
                errors.Add(Violation(Field, Invariant($"must keep every theme at {BannedThemeMaxChars} characters or fewer.")));
                failed = true;

                continue;
            }

            themes.Add(theme);
        }

        return failed ? null : themes;
    }

    private static int? ReadInt(
        JsonElement parent,
        string name,
        string field,
        int min,
        int max,
        List<Error> errors)
    {
        if (!parent.TryGetProperty(name, out var element))
        {
            errors.Add(Violation(field, "is required."));

            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        {
            errors.Add(Violation(field, "must be an integer."));

            return null;
        }

        if (min != int.MinValue && (value < min || value > max))
        {
            errors.Add(Violation(field, Invariant($"must be between {min} and {max}.")));

            return null;
        }

        return value;
    }

    private static int? ReadOptionalInt(
        JsonElement parent,
        string name,
        string field,
        int min,
        int max,
        List<Error> errors)
    {
        if (!parent.TryGetProperty(name, out var element))
        {
            errors.Add(Violation(field, "is required (null or an integer)."));

            return null;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        {
            errors.Add(Violation(field, "must be null or an integer."));

            return null;
        }

        if (value < min || value > max)
        {
            errors.Add(Violation(field, Invariant($"must be between {min} and {max}.")));

            return null;
        }

        return value;
    }

    private static bool TryText(
        JsonElement parent,
        string name,
        string field,
        List<Error> errors,
        out string value)
    {
        value = string.Empty;

        if (!parent.TryGetProperty(name, out var element))
        {
            errors.Add(Violation(field, "is required."));

            return false;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            errors.Add(Violation(field, "must be a string."));

            return false;
        }

        var text = element.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            errors.Add(Violation(field, "must not be empty."));

            return false;
        }

        value = text;

        return true;
    }

    private static void RejectUnknownKeys(
        JsonElement element,
        string prefix,
        HashSet<string> known,
        List<Error> errors)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!known.Contains(property.Name))
            {
                errors.Add(Violation(prefix + property.Name, "is not a known autopilot setting."));
            }
        }
    }

    private static bool IsPublishTime(string text) =>
        text.Length == 5
        && char.IsAsciiDigit(text[0])
        && char.IsAsciiDigit(text[1])
        && text[2] == ':'
        && char.IsAsciiDigit(text[3])
        && char.IsAsciiDigit(text[4])
        && text[0] is '0' or '1' or '2'
        && (text[0] != '2' || text[1] <= '3')
        && text[3] <= '5';

    private static int WeekIndex(DayOfWeek day) => day == DayOfWeek.Sunday ? 6 : (int)day - 1;

    private static string WeekdayWire(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "mon",
        DayOfWeek.Tuesday => "tue",
        DayOfWeek.Wednesday => "wed",
        DayOfWeek.Thursday => "thu",
        DayOfWeek.Friday => "fri",
        DayOfWeek.Saturday => "sat",
        DayOfWeek.Sunday => "sun",
        _ => throw new ArgumentOutOfRangeException(nameof(day), day, "Unknown weekday."),
    };

    private static string PrivacyWire(PrivacyStatus privacy) => privacy switch
    {
        PrivacyStatus.Public => "public",
        PrivacyStatus.Unlisted => "unlisted",
        PrivacyStatus.Private => "private",
        _ => throw new ArgumentOutOfRangeException(nameof(privacy), privacy, "Unknown privacy."),
    };

    private static int RuneCount(string value) => value.EnumerateRunes().Count();

    private static void WriteOptionalNumber(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static string Invariant(FormattableString message) => FormattableString.Invariant(message);

    private static Error Violation(string field, string description) => Error.Validation(
        InvalidCode,
        description,
        new Dictionary<string, object>(StringComparer.Ordinal) { [FieldMetadataKey] = field });
}
