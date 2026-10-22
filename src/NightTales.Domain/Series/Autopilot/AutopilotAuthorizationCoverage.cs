using System.Text;
using System.Text.Json;

namespace NightTales.Domain.Series.Autopilot;

/// <summary>
/// The covered-field comparison of plan §3.7: which part of an accepted authorization a later state of the
/// series contradicts, and the snapshot the acceptance stores to answer that question.
/// </summary>
/// <remarks>
/// <para>
/// The comparison is explicitly field by field, never a diff of the rendered text: the text also names the
/// series title, and a rename must not revoke (README §10 T10). The covered fields are the calendar the owner
/// agreed to — <c>series.topic</c>, <c>series.default_channel_id</c>, <c>cadence.*</c>, <c>daily_cap</c>,
/// <c>privacy</c>, <c>veto_window_hours</c>, <c>pre_audit_mode</c> and the audit status the text carries
/// (<c>pre_audit_pending</c>). Everything else — <c>tone</c>, <c>banned_themes</c>,
/// <c>gate_thresholds_ref</c>, <c>render_lead_minutes</c>, <c>story_lead_minutes</c>,
/// <c>buffer_days</c>, <c>pre_audit_pause_after</c> and the title — is deliberately outside the snapshot:
/// changing one of them keeps the authorization the owner accepted.
/// </para>
/// <para>
/// <b>Order of the answer.</b> A topic change outranks a channel change, and both outrank a text change, so the
/// reason a row records is the most specific fact of the request rather than "some covered field moved". Two
/// covered fields changing at once therefore record the first of <see
/// cref="AutopilotAuthorizationRevokeReason.TopicChanged"/>, <see
/// cref="AutopilotAuthorizationRevokeReason.ChannelChanged"/>, <see
/// cref="AutopilotAuthorizationRevokeReason.TextChanged"/>.
/// </para>
/// <para>
/// <b>Fail closed.</b> A stored snapshot that does not parse, is not an object, or is missing a covered member
/// answers <see cref="AutopilotAuthorizationRevokeReason.TextChanged"/>: an unreadable agreement is not the
/// agreement the series is in now. The same applies to a stored channel connection of <c>null</c>, which no
/// acceptance writes (<see cref="SeriesAutopilotAuthorization.Accept"/> requires one).
/// </para>
/// </remarks>
public static class AutopilotAuthorizationCoverage
{
    /// <summary>The one covered member the settings document does not carry: the audit status the text names.</summary>
    private const string PreAuditPendingMember = "pre_audit_pending";

    /// <summary>The covered members the snapshot copies out of the settings document, in document order.</summary>
    private static readonly string[] CoveredMembers =
        ["cadence", "daily_cap", "privacy", "veto_window_hours", "pre_audit_mode"];

    /// <summary>
    /// Writes the covered snapshot an acceptance stores in
    /// <c>series_autopilot_authorizations.cadence</c>.
    /// </summary>
    /// <remarks>
    /// The document is the settings document minus every non-covered member, with the audit bit added:
    /// <c>{"cadence":{"days_of_week":[…],"publish_time":"HH:mm","time_zone":"…"},"daily_cap":n,
    /// "privacy":"…","veto_window_hours":n,"pre_audit_mode":"…","pre_audit_pending":bool}</c> (README D-block
    /// D-7). The spellings are not repeated here: the cadence, the privacy and the pre-audit mode are copied
    /// out of <see cref="AutopilotSettingsValidator.Serialize"/>'s document, so the snapshot and the stored
    /// settings can never disagree about how a weekday, a privacy or a mode is spelled.
    /// </remarks>
    /// <param name="settings">The settings the accepted text was rendered from.</param>
    /// <param name="preAuditPending">Whether the channel's YouTube audit was still pending when it was rendered.</param>
    /// <returns>The stored snapshot, without indentation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is <see langword="null"/>.</exception>
    public static string SnapshotJson(AutopilotSettings settings, bool preAuditPending)
    {
        ArgumentNullException.ThrowIfNull(settings);

        using var settingsDocument = JsonDocument.Parse(AutopilotSettingsValidator.Serialize(settings));

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            foreach (var member in CoveredMembers)
            {
                writer.WritePropertyName(member);
                settingsDocument.RootElement.GetProperty(member).WriteTo(writer);
            }

            writer.WriteBoolean(PreAuditPendingMember, preAuditPending);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>
    /// Answers which covered field a later state of the series contradicts (plan §3.7), or
    /// <see langword="null"/> when the acceptance still holds.
    /// </summary>
    /// <param name="storedSnapshotJson">The <c>cadence</c> column of the accepted row.</param>
    /// <param name="storedTopic">The accepted topic.</param>
    /// <param name="storedChannelConnectionId">The accepted channel connection.</param>
    /// <param name="currentTopic">The series' topic now.</param>
    /// <param name="currentChannelId">The series' default channel connection now.</param>
    /// <param name="currentSettings">The settings now.</param>
    /// <param name="currentPreAuditPending">Whether the channel's YouTube audit is pending now.</param>
    /// <returns>The revoke reason, or <see langword="null"/> when no covered field moved.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="storedSnapshotJson"/> or <paramref name="currentSettings"/> is <see langword="null"/>.
    /// </exception>
    public static AutopilotAuthorizationRevokeReason? FirstCoveredChange(
        string storedSnapshotJson,
        Topic storedTopic,
        Guid? storedChannelConnectionId,
        Topic currentTopic,
        Guid? currentChannelId,
        AutopilotSettings currentSettings,
        bool currentPreAuditPending)
    {
        ArgumentNullException.ThrowIfNull(storedSnapshotJson);
        ArgumentNullException.ThrowIfNull(currentSettings);

        if (storedTopic != currentTopic)
        {
            return AutopilotAuthorizationRevokeReason.TopicChanged;
        }

        if (storedChannelConnectionId is null || storedChannelConnectionId != currentChannelId)
        {
            return AutopilotAuthorizationRevokeReason.ChannelChanged;
        }

        return CoveredFieldsMatch(storedSnapshotJson, SnapshotJson(currentSettings, currentPreAuditPending))
            ? null
            : AutopilotAuthorizationRevokeReason.TextChanged;
    }

    /// <summary>
    /// Compares the covered members of two snapshots: the stored one, which might not be a document this build
    /// wrote, and the one this build just produced for the current state.
    /// </summary>
    /// <remarks>
    /// A malformed, non-object or partly missing stored document answers <see langword="false"/> (fail closed,
    /// see the type remarks) rather than throwing: the caller's answer to that is a revocation, not a 500.
    /// <c>cadence.days_of_week</c> is compared as the stored order of weekday names, because that is what the
    /// rendered text lists and what the settings document round-trips (the validator never reorders).
    /// </remarks>
    /// <param name="storedSnapshotJson">The stored snapshot.</param>
    /// <param name="currentSnapshotJson">The snapshot of the current state, from <see cref="SnapshotJson"/>.</param>
    /// <returns>Whether every covered member is equal.</returns>
    private static bool CoveredFieldsMatch(string storedSnapshotJson, string currentSnapshotJson)
    {
        JsonDocument stored;
        try
        {
            stored = JsonDocument.Parse(storedSnapshotJson);
        }
        catch (JsonException)
        {
            return false;
        }

        using (stored)
        {
            using var current = JsonDocument.Parse(currentSnapshotJson);

            var storedRoot = stored.RootElement;
            var currentRoot = current.RootElement;

            if (storedRoot.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return CadenceMatches(storedRoot, currentRoot)
                && IntMatches(storedRoot, currentRoot, "daily_cap")
                && TextMatches(storedRoot, currentRoot, "privacy")
                && IntMatches(storedRoot, currentRoot, "veto_window_hours")
                && TextMatches(storedRoot, currentRoot, "pre_audit_mode")
                && BoolMatches(storedRoot, currentRoot, PreAuditPendingMember);
        }
    }

    /// <summary>Compares <c>cadence.{days_of_week, publish_time, time_zone}</c> member by member.</summary>
    /// <param name="stored">The stored snapshot's root.</param>
    /// <param name="current">The current snapshot's root.</param>
    /// <returns>Whether the whole cadence is equal.</returns>
    private static bool CadenceMatches(JsonElement stored, JsonElement current)
    {
        if (!TryObject(stored, "cadence", out var storedCadence)
            || !TryObject(current, "cadence", out var currentCadence))
        {
            return false;
        }

        return DaysOfWeekMatch(storedCadence, currentCadence)
            && TextMatches(storedCadence, currentCadence, "publish_time")
            && TextMatches(storedCadence, currentCadence, "time_zone");
    }

    /// <summary>Compares the two weekday arrays name by name, in order.</summary>
    /// <param name="stored">The stored cadence object.</param>
    /// <param name="current">The current cadence object.</param>
    /// <returns>Whether both arrays carry the same names in the same order.</returns>
    private static bool DaysOfWeekMatch(JsonElement stored, JsonElement current)
    {
        if (!stored.TryGetProperty("days_of_week", out var storedDays)
            || !current.TryGetProperty("days_of_week", out var currentDays)
            || storedDays.ValueKind != JsonValueKind.Array
            || currentDays.ValueKind != JsonValueKind.Array
            || storedDays.GetArrayLength() != currentDays.GetArrayLength())
        {
            return false;
        }

        var storedNames = storedDays.EnumerateArray();
        var currentNames = currentDays.EnumerateArray();

        while (storedNames.MoveNext() && currentNames.MoveNext())
        {
            if (!SameText(storedNames.Current, currentNames.Current))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Compares one text member of two objects.</summary>
    /// <param name="stored">The stored object.</param>
    /// <param name="current">The current object.</param>
    /// <param name="member">The member name.</param>
    /// <returns>Whether both hold the same string.</returns>
    private static bool TextMatches(JsonElement stored, JsonElement current, string member) =>
        stored.TryGetProperty(member, out var storedValue)
        && current.TryGetProperty(member, out var currentValue)
        && SameText(storedValue, currentValue);

    /// <summary>Compares one integer member of two objects.</summary>
    /// <param name="stored">The stored object.</param>
    /// <param name="current">The current object.</param>
    /// <param name="member">The member name.</param>
    /// <returns>Whether both hold the same number.</returns>
    private static bool IntMatches(JsonElement stored, JsonElement current, string member) =>
        stored.TryGetProperty(member, out var storedValue)
        && current.TryGetProperty(member, out var currentValue)
        && storedValue.ValueKind == JsonValueKind.Number
        && currentValue.ValueKind == JsonValueKind.Number
        && storedValue.TryGetInt32(out var storedNumber)
        && currentValue.TryGetInt32(out var currentNumber)
        && storedNumber == currentNumber;

    /// <summary>Compares one boolean member of two objects.</summary>
    /// <param name="stored">The stored object.</param>
    /// <param name="current">The current object.</param>
    /// <param name="member">The member name.</param>
    /// <returns>Whether both hold the same boolean.</returns>
    private static bool BoolMatches(JsonElement stored, JsonElement current, string member) =>
        stored.TryGetProperty(member, out var storedValue)
        && current.TryGetProperty(member, out var currentValue)
        && storedValue.ValueKind == currentValue.ValueKind
        && storedValue.ValueKind is JsonValueKind.True or JsonValueKind.False
        && storedValue.GetBoolean() == currentValue.GetBoolean();

    /// <summary>Reads one member of an object.</summary>
    /// <param name="parent">The parent element.</param>
    /// <param name="member">The member name.</param>
    /// <param name="value">The member, when it is an object.</param>
    /// <returns>Whether the member exists and is an object.</returns>
    private static bool TryObject(JsonElement parent, string member, out JsonElement value) =>
        parent.TryGetProperty(member, out value) && value.ValueKind == JsonValueKind.Object;

    /// <summary>Whether two elements are the same string.</summary>
    /// <param name="stored">The stored element.</param>
    /// <param name="current">The current element.</param>
    /// <returns>Whether both are strings holding the same ordinal text.</returns>
    private static bool SameText(JsonElement stored, JsonElement current) =>
        stored.ValueKind == JsonValueKind.String
        && current.ValueKind == JsonValueKind.String
        && string.Equals(stored.GetString(), current.GetString(), StringComparison.Ordinal);
}
