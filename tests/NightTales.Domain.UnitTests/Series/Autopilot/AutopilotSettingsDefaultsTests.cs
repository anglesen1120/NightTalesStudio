using System.Text;
using System.Text.Json;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Autopilot;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Series.Autopilot;

/// <summary>
/// The settings record's own completeness (02.8.3-T2 task §Tests, D4): <see cref="AutopilotSettingsValidator.Serialize"/>
/// writes one member per property of <see cref="AutopilotSettings"/> and
/// <see cref="AutopilotSettingsValidator.Validate"/> reads every one of them back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why reflection.</b> The validator's happy-path test names the members it knows, so a member added to the record and
/// forgotten in <c>Serialize</c> would be silently stored as nothing — the row would lose the owner's value on the next
/// write while every named assertion still passed. The two tests here walk the record instead: the document must carry
/// exactly one key per property, and a full round trip must return every value unchanged.
/// </para>
/// <para>
/// <b>What is deliberately not asserted with <c>==</c>.</b> The record holds two collections, whose record equality is by
/// reference (<see cref="AutopilotSettings"/> documents that), so the round-trip test compares members one by one and the
/// two lists as sequences.
/// </para>
/// </remarks>
public sealed class AutopilotSettingsDefaultsTests
{
    /// <summary>One document with a distinct, in-range value for every member of plan §3.1.</summary>
    private static readonly AutopilotSettings Distinct = new(
        AutopilotSettingsValidator.SupportedSchemaVersion,
        new AutopilotCadence([DayOfWeek.Wednesday, DayOfWeek.Friday], new TimeOnly(7, 45), "Europe/Berlin"),
        DailyCap: 3,
        BufferDays: 5,
        Privacy: PrivacyStatus.Unlisted,
        VetoWindowHours: 36,
        PreAuditMode: PreAuditMode.PauseAfterNPrivate,
        PreAuditPauseAfter: 7,
        GateThresholdsRef: "strict",
        Tone: "dry, clinical, second person",
        BannedThemes: ["gore", "politics"],
        RenderLeadMinutes: 42,
        StoryLeadMinutes: null);

    /// <summary>
    /// Task §Tests <c>AutopilotSettings_SerializeEveryMember</c>: the canonical document carries one key per property of
    /// the record (no member is written twice into one key, none is dropped) and reading it back returns every value.
    /// </summary>
    [Fact]
    public void AutopilotSettings_SerializeEveryMember()
    {
        var properties = typeof(AutopilotSettings).GetProperties();

        var wire = AutopilotSettingsValidator.Serialize(Distinct);

        using var document = JsonDocument.Parse(wire);
        var root = document.RootElement;
        root.ValueKind.ShouldBe(JsonValueKind.Object);

        var keys = new List<string>();
        foreach (var property in root.EnumerateObject())
        {
            keys.Add(property.Name);
        }

        keys.Count.ShouldBe(
            properties.Length,
            "Serialize must write exactly one key per member of the record; a member it forgets is silently not stored");

        foreach (var property in properties)
        {
            var key = SnakeCase(property.Name);
            keys.ShouldContain(key, $"'{property.Name}' has no key in the serialised document");
        }

        // The round trip: every member comes back with the value it went in with. A `null` lead stays `null` rather
        // than becoming an absent key, which is what makes the document readable by the strict validator.
        var read = AutopilotSettingsValidator.Validate(wire);
        read.IsError.ShouldBeFalse(string.Join("; ", read.Errors.Select(error => error.Description)));

        var settings = read.Value;
        settings.SchemaVersion.ShouldBe(Distinct.SchemaVersion);
        settings.Cadence.DaysOfWeek.ShouldBe(Distinct.Cadence.DaysOfWeek);
        settings.Cadence.PublishTime.ShouldBe(Distinct.Cadence.PublishTime);
        settings.Cadence.TimeZoneId.ShouldBe(Distinct.Cadence.TimeZoneId);
        settings.DailyCap.ShouldBe(Distinct.DailyCap);
        settings.BufferDays.ShouldBe(Distinct.BufferDays);
        settings.Privacy.ShouldBe(Distinct.Privacy);
        settings.VetoWindowHours.ShouldBe(Distinct.VetoWindowHours);
        settings.PreAuditMode.ShouldBe(Distinct.PreAuditMode);
        settings.PreAuditPauseAfter.ShouldBe(Distinct.PreAuditPauseAfter);
        settings.GateThresholdsRef.ShouldBe(Distinct.GateThresholdsRef);
        settings.Tone.ShouldBe(Distinct.Tone);
        settings.BannedThemes.ShouldBe(Distinct.BannedThemes);
        settings.RenderLeadMinutes.ShouldBe(Distinct.RenderLeadMinutes);
        settings.StoryLeadMinutes.ShouldBe(Distinct.StoryLeadMinutes);

        AutopilotSettingsValidator.Serialize(settings).ShouldBe(wire, "the document is canonical, so a second write is the same bytes");
    }

    /// <summary>
    /// A `<c>null</c>` lead is written as the JSON literal and not dropped: the validator refuses an absent member
    /// ("is required (null or an integer)"), so a document that omitted it would not read back at all.
    /// </summary>
    [Fact]
    public void AutopilotSettings_SerializeEveryMember_NullLeadIsAKeyNotAnAbsence()
    {
        var wire = AutopilotSettingsValidator.Serialize(Distinct);

        wire.ShouldContain("\"story_lead_minutes\":null");

        using var document = JsonDocument.Parse(wire);
        document.RootElement.TryGetProperty("story_lead_minutes", out var value).ShouldBeTrue();
        value.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>The snake_case spelling of a member name: <c>PreAuditMode</c> is <c>pre_audit_mode</c>.</summary>
    /// <param name="name">The CLR property name.</param>
    /// <returns>The wire key.</returns>
    private static string SnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
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
