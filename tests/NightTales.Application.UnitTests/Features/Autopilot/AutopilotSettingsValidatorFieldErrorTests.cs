using System.Text.Json;
using ErrorOr;
using NightTales.Application.Features.Autopilot.Dtos;
using NightTales.Domain.Series.Autopilot;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Autopilot;

/// <summary>
/// The two settings-key contracts of 02.8.3-T2 §3.1 that the API and the client both depend on (task §Tests, D4): every
/// validation failure is keyed by the JSON path of the member that broke it, and every key of the settings document has a
/// default value in the plan's example and a member on the DTO <c>GET</c> answers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the path matters.</b> <c>PUT /series/{id}/autopilot</c> answers 400 <c>autopilot_settings_invalid</c> with an
/// <c>errors</c> map whose keys are the paths the validator's metadata carries (<c>ProblemDetailsWriter</c> groups the
/// errors by it), and the settings form marks the field the owner must fix from the same string. A path that drifted
/// (a CLR member name, a bare key for a nested member, the root marker for a member) would put the message under a field
/// the client does not render, so the rule is pinned here at the validator rather than only through HTTP.
/// </para>
/// <para>
/// <b>Why the DTO is checked by reflection.</b> The DTO's <c>From</c> reads its values out of
/// <c>AutopilotSettingsValidator.Serialize</c>, one named member at a time; a new §3.1 key added to the record and the
/// document but not to the DTO would be invisible to the client while every named test still passed.
/// </para>
/// </remarks>
public sealed class AutopilotSettingsValidatorFieldErrorTests
{
    /// <summary>
    /// The plan §3.1 example document, verbatim — the "default" every key has (02.8.3 plan §3.1, the same literal the
    /// Domain suite uses).
    /// </summary>
    private const string Example = """
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

    /// <summary>
    /// Task §Tests <c>AutopilotSettingsValidator_FieldErrors_AreKeyedByJsonPath</c>: one document that breaks every rule
    /// answers one error per broken rule, each carrying the dotted path of the member it is about — nested members
    /// included (<c>cadence.publish_time</c>, <c>cadence.time_zone</c>) and an unknown key under its own path.
    /// </summary>
    /// <remarks>
    /// The document is built from the example so only the members under test differ. A path may carry more than one error
    /// (a duplicated weekday is one broken rule but the array may also be empty), so the assertion is over the set of
    /// paths: no path outside the broken set, and every broken member represented.
    /// </remarks>
    [Fact]
    public void AutopilotSettingsValidator_FieldErrors_AreKeyedByJsonPath()
    {
        const string Broken = """
            {
              "schema_version": 2,
              "cadence": {
                "days_of_week": ["mon", "mon"],
                "publish_time": "25:00",
                "time_zone": "Mars/Olympus_Mons",
                "offset": 1
              },
              "daily_cap": 0,
              "buffer_days": 9,
              "privacy": "secret",
              "veto_window_hours": 1,
              "pre_audit_mode": "pause_after_three_private",
              "pre_audit_pause_after": 0,
              "gate_thresholds_ref": "not a profile name",
              "tone": "",
              "banned_themes": [""],
              "render_lead_minutes": 0,
              "story_lead_minutes": "ten",
              "extra_member": true
            }
            """;

        var expected = new[]
        {
            "schema_version",
            "cadence.days_of_week",
            "cadence.publish_time",
            "cadence.time_zone",
            "cadence.offset",
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
            "extra_member",
        };

        var result = AutopilotSettingsValidator.Validate(Broken);

        result.IsError.ShouldBeTrue();
        result.Errors.ShouldNotBeEmpty();

        foreach (var error in result.Errors)
        {
            error.Code.ShouldBe(AutopilotSettingsValidator.InvalidCode);
            error.Type.ShouldBe(ErrorType.Validation);
            error.Metadata.ShouldNotBeNull();
            error.Metadata!.ShouldContainKey(AutopilotSettingsValidator.FieldMetadataKey);
        }

        var paths = result.Errors
            .Select(error => (string)error.Metadata![AutopilotSettingsValidator.FieldMetadataKey])
            .ToList();

        paths.Distinct(StringComparer.Ordinal).ShouldBe(expected.Order(StringComparer.Ordinal), ignoreOrder: true);

        // A path is a path, not a label: each nested one addresses a member that exists in the document it came from,
        // so a client can resolve it against the body it sent (and against the document GET returns unchanged).
        using var document = JsonDocument.Parse(Broken);
        foreach (var path in paths.Where(path => path.Contains('.', StringComparison.Ordinal)).Distinct(StringComparer.Ordinal))
        {
            var segments = path.Split('.');
            segments.Length.ShouldBe(2, $"'{path}' is nested one level, like the `cadence` object it names");

            document.RootElement.TryGetProperty(segments[0], out var parent).ShouldBeTrue($"'{segments[0]}' is not in the body");
            parent.ValueKind.ShouldBe(JsonValueKind.Object);
            parent.TryGetProperty(segments[1], out _).ShouldBeTrue($"'{path}' does not resolve against the body");
        }
    }

    /// <summary>
    /// A document that is not usable at all is a root error under <c>$</c> and nothing else: there is no member to key the
    /// message by, and the API must not invent one (plan §3.1, <c>RootField</c>).
    /// </summary>
    [Fact]
    public void AutopilotSettingsValidator_UnreadableDocument_IsKeyedByTheRootField()
    {
        foreach (var body in new[] { string.Empty, "   ", "{", "[]", "null" })
        {
            var result = AutopilotSettingsValidator.Validate(body);

            result.IsError.ShouldBeTrue($"'{body}' must be refused");
            result.Errors.ShouldHaveSingleItem().Metadata![AutopilotSettingsValidator.FieldMetadataKey]
                .ShouldBe(AutopilotSettingsValidator.RootField);
        }
    }

    /// <summary>
    /// Task §Tests <c>AutopilotDtos_EverySettingsKey_HasADefaultAndCopy</c>: each key of the plan §3.1 document has a
    /// default value in the example, a member of the same name on <see cref="AutopilotSettings"/>, and a member on
    /// <see cref="AutopilotSettingsDto"/> that <c>From</c> fills with exactly that value.
    /// </summary>
    /// <remarks>
    /// The only key the DTO deliberately omits is <c>schema_version</c>: the client never writes the document's version
    /// (the route does), so the exclusion is asserted by name instead of being implied by the loop.
    /// </remarks>
    [Fact]
    public void AutopilotDtos_EverySettingsKey_HasADefaultAndCopy()
    {
        var settings = AutopilotSettingsValidator.Validate(Example);
        settings.IsError.ShouldBeFalse(string.Join("; ", settings.Errors.Select(error => error.Description)));

        var dto = AutopilotSettingsDto.From(settings.Value);
        var wire = AutopilotSettingsValidator.Serialize(settings.Value);

        using var document = JsonDocument.Parse(wire);
        var root = document.RootElement;

        // Every DTO member is copyable: the record it copies from has a member of the same name.
        var dtoMembers = typeof(AutopilotSettingsDto).GetProperties();
        foreach (var member in dtoMembers)
        {
            typeof(AutopilotSettings).GetProperty(member.Name).ShouldNotBeNull(
                $"AutopilotSettingsDto.{member.Name} has no source member on AutopilotSettings, so `From` could not copy it");
        }

        // The one member with a type of its own travels as the document's `cadence` object.
        var cadence = dto.Cadence;
        var storedCadence = root.GetProperty("cadence");
        cadence.DaysOfWeek.ShouldBe(
            storedCadence.GetProperty("days_of_week").EnumerateArray().Select(day => day.GetString() ?? string.Empty));
        cadence.PublishTime.ShouldBe(storedCadence.GetProperty("publish_time").GetString());
        cadence.TimeZone.ShouldBe(storedCadence.GetProperty("time_zone").GetString());

        // Every flat member of the DTO carries the document's value for its own key, so a new key cannot reach the record
        // without reaching the wire shape (and vice versa).
        foreach (var member in dtoMembers.Where(member => member.Name != nameof(AutopilotSettingsDto.Cadence)))
        {
            var key = SnakeCase(member.Name);

            root.TryGetProperty(key, out var stored).ShouldBeTrue(
                $"the document has no '{key}' key for AutopilotSettingsDto.{member.Name}");

            var copied = JsonSerializer.SerializeToElement(member.GetValue(dto), member.PropertyType);

            JsonElement.DeepEquals(stored, copied).ShouldBeTrue(
                $"AutopilotSettingsDto.{member.Name} does not copy the document's '{key}' value "
                + $"(document: {stored.GetRawText()}, DTO: {copied.GetRawText()})");
        }

        // The excluded key, by name: the document's version is the server's, not the client's.
        dtoMembers.Select(member => member.Name).ShouldNotContain(
            nameof(AutopilotSettings.SchemaVersion),
            "`schema_version` is written by the route's validator, so the DTO must not offer it");
        root.TryGetProperty("schema_version", out _).ShouldBeTrue();

        // And the reverse direction: every key the document carries is either on the DTO or the excluded version.
        var dtoKeys = dtoMembers.Select(member => SnakeCase(member.Name)).ToHashSet(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            (dtoKeys.Contains(property.Name) || property.Name == "schema_version").ShouldBeTrue(
                $"'{property.Name}' is a settings key the DTO does not publish");
        }
    }

    /// <summary>The snake_case spelling a settings key uses: <c>PreAuditMode</c> is <c>pre_audit_mode</c>.</summary>
    /// <param name="name">The DTO member name.</param>
    /// <returns>The wire key.</returns>
    private static string SnakeCase(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length + 4);
        foreach (var character in name)
        {
            if (char.IsAsciiLetterUpper(character) && builder.Length > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
