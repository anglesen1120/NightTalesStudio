using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ErrorOr;
using NightTales.Domain.Rendering.Subtitles;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Rendering.Subtitles;

/// <summary>
/// Independent QA probes over <c>SubtitlePresetCatalog</c> (05.1.2-T1): hostile JSON shapes (named number
/// literals, exponents, out-of-decimal-range numbers, deep nesting, trailing content, a BOM, extra members),
/// the precedence of the D6 rule list, and the decimal scale the file's text puts on the MPT wire. Tests that
/// pin a behaviour which looks wrong are commented and reported as QA findings.
/// </summary>
public sealed class SubtitlePresetCatalogQaTests
{
    private const string Shipped =
        """
        { "schema": "subtitle-presets.v1", "revision": 1, "fontName": "BeVietnamPro-Bold.ttf",
          "presets": {
            "landscape": { "fontSize": 84, "strokeWidth": 3.0, "strokeColor": "#000000", "textForeColor": "#FFFFFF",
                           "textBackgroundColor": false, "roundedSubtitleBackground": false,
                           "subtitlePosition": "bottom", "customPosition": 70.0, "maxCharsPerLine": 42, "maxLines": 2 },
            "portrait":  { "fontSize": 64, "strokeWidth": 3.0, "strokeColor": "#000000", "textForeColor": "#FFFFFF",
                           "textBackgroundColor": false, "roundedSubtitleBackground": false,
                           "subtitlePosition": "custom", "customPosition": 62.0, "maxCharsPerLine": 28, "maxLines": 2 } } }
        """;

    // ------------------------------------------------------------------ Hostile JSON shapes ------------------

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e2")]
    [InlineData("1e30")]
    [InlineData("1e100")]
    [InlineData("9223372036854775808")]
    [InlineData("1.5")]
    [InlineData("\"84\"")]
    [InlineData("true")]
    [InlineData("null")]
    public void Hostile_font_size_values_are_rejected_without_throwing(string literal)
    {
        var result = Should.NotThrow(() => SubtitlePresetCatalog.Load(Utf8(WithFontSize(literal))));

        result.IsError.ShouldBeTrue($"fontSize={literal} must not load");
        result.Errors.Count.ShouldBe(1);
    }

    [Fact]
    public void Repeated_members_are_rejected_in_the_nested_preset_too()
    {
        var json = Shipped.Replace("\"fontSize\": 84,", "\"fontSize\": 84, \"fontSize\": 84,", StringComparison.Ordinal);

        Rejected(json).Code.ShouldBe(SubtitlePresetCatalog.DocumentInvalidCode);
    }

    [Theory]
    [InlineData("}")]
    [InlineData("{}")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("[]")]
    public void A_root_that_is_not_the_schema_document_is_rejected(string root)
    {
        Rejected(root).Code.ShouldBe(SubtitlePresetCatalog.DocumentInvalidCode);
    }

    [Fact]
    public void Trailing_content_after_the_document_is_rejected()
    {
        Rejected(Shipped + " {\"extra\": 1}").Code.ShouldBe(SubtitlePresetCatalog.DocumentInvalidCode);
    }

    // ------------------------------------------------------------------ Rule precedence -----------------------

    [Theory]
    [InlineData("wrong", 1, "BeVietnamPro-Bold.ttf", 84, "schema_invalid")]
    [InlineData("subtitle-presets.v1", 0, "wrong.ttf", 200, "revision_not_positive")]
    [InlineData("subtitle-presets.v1", 1, "wrong.ttf", 200, "font_name_invalid")]
    [InlineData("subtitle-presets.v1", 1, "BeVietnamPro-Bold.ttf", 200, "font_size_out_of_range")]
    public void The_first_failing_d6_rule_wins(
        string schema, int revision, string fontName, int fontSize, string expectedCode)
    {
        var json = Shipped
            .Replace("\"schema\": \"subtitle-presets.v1\"", $"\"schema\": \"{schema}\"", StringComparison.Ordinal)
            .Replace("\"revision\": 1,", $"\"revision\": {revision},", StringComparison.Ordinal)
            .Replace("\"fontName\": \"BeVietnamPro-Bold.ttf\"", $"\"fontName\": \"{fontName}\"", StringComparison.Ordinal)
            .Replace("\"fontSize\": 84,", $"\"fontSize\": {fontSize},", StringComparison.Ordinal);

        Rejected(json).Code.ShouldBe(expectedCode);
    }

    [Fact]
    public void Stroke_colour_and_position_rules_follow_the_font_size_rule()
    {
        var badColor = Shipped.Replace("\"strokeColor\": \"#000000\"", "\"strokeColor\": \"#0000000\"", StringComparison.Ordinal);
        Rejected(badColor).Code.ShouldBe(SubtitlePresetCatalog.ColorInvalidCode);

        var badPosition = Shipped.Replace("\"subtitlePosition\": \"bottom\"", "\"subtitlePosition\": \"Bottom\"", StringComparison.Ordinal);
        Rejected(badPosition).Code.ShouldBe(SubtitlePresetCatalog.PositionUnknownCode);
    }

    [Theory]
    [InlineData("bottom")]
    [InlineData("top")]
    [InlineData("center")]
    [InlineData("two_thirds_bottom")]
    [InlineData("custom")]
    public void Every_c16_position_is_accepted(string position)
    {
        var json = Shipped
            .Replace("\"subtitlePosition\": \"bottom\"", $"\"subtitlePosition\": \"{position}\"", StringComparison.Ordinal)
            .Replace("\"subtitlePosition\": \"custom\"", $"\"subtitlePosition\": \"{position}\"", StringComparison.Ordinal);

        var loaded = SubtitlePresetCatalog.Load(Utf8(json));
        loaded.IsError.ShouldBeFalse(string.Join("; ", loaded.Errors.Select(error => error.Code)));
        loaded.Value.For(SubtitlePresetCatalog.LandscapeAspect).SubtitlePosition.ShouldBe(position);
    }

    [Fact]
    public void A_preset_key_that_is_not_an_object_is_aspect_missing()
    {
        var node = JsonNode.Parse(Shipped)!.AsObject();
        node["presets"]!.AsObject()["landscape"] = new JsonArray(1, 2);

        Rejected(node.ToJsonString()).Code.ShouldBe(SubtitlePresetCatalog.AspectMissingCode);
    }

    // ------------------------------------------------------------------ Decimal scale on the wire ------------

    [Fact]
    public void Shipped_seed_decimals_keep_the_one_decimal_place_the_golden_wire_needs()
    {
        var catalog = SubtitlePresetCatalog.LoadEmbedded().Value;

        catalog.For(SubtitlePresetCatalog.LandscapeAspect).StrokeWidth.ToString(CultureInfo.InvariantCulture).ShouldBe("3.0");
        catalog.For(SubtitlePresetCatalog.LandscapeAspect).CustomPosition.ToString(CultureInfo.InvariantCulture).ShouldBe("70.0");
        catalog.For(SubtitlePresetCatalog.PortraitAspect).CustomPosition.ToString(CultureInfo.InvariantCulture).ShouldBe("62.0");
    }

    [Theory]
    [InlineData("70")]
    [InlineData("70.0")]
    [InlineData("70.00")]
    [InlineData("7e1")]
    public void Decimal_members_of_the_file_are_accepted_whatever_their_scale(string literal)
    {
        // QA observation (low risk; the file is tracked and reviewed): only the numeric value is validated, so
        // "70.00" or "7e1" loads and the preset then serializes with that scale. The shipped file's scale is
        // pinned by SubtitlePresetCatalogTests.Preserves_the_decimal_scale_of_the_file, but a scale variant
        // inside the O4 range passes every rule here and would still break the byte-golden submit comparison.
        var json = Shipped.Replace("\"customPosition\": 70.0,", $"\"customPosition\": {literal},", StringComparison.Ordinal);

        var loaded = SubtitlePresetCatalog.Load(Utf8(json));

        loaded.IsError.ShouldBeFalse($"customPosition={literal}: {string.Join("; ", loaded.Errors.Select(error => error.Code))}");
        loaded.Value.For(SubtitlePresetCatalog.LandscapeAspect).CustomPosition.ShouldBe(70m);
    }

    [Fact]
    public void Custom_position_outside_the_decimal_range_is_rejected()
    {
        var json = Shipped.Replace("\"customPosition\": 70.0,", "\"customPosition\": 1e20,", StringComparison.Ordinal);

        Rejected(json).Code.ShouldBe(SubtitlePresetCatalog.CustomPositionOutOfRangeCode);
    }

    // ------------------------------------------------------------------ API surface ---------------------------

    [Fact]
    public void For_null_and_blank_are_argument_errors()
    {
        var catalog = SubtitlePresetCatalog.LoadEmbedded().Value;

        Should.Throw<ArgumentNullException>(() => catalog.For(null!));
        Should.Throw<ArgumentException>(() => catalog.For(string.Empty));
        Should.Throw<ArgumentException>(() => catalog.For(" landscape"));
        catalog.For("landscape").Aspect.ShouldBe("landscape");
    }

    [Fact]
    public void Load_is_deterministic_and_rejects_a_negative_revision()
    {
        var first = SubtitlePresetCatalog.Load(Utf8(Shipped)).Value;
        var second = SubtitlePresetCatalog.Load(Utf8(Shipped)).Value;

        first.Revision.ShouldBe(second.Revision);
        first.For("landscape").ShouldBe(second.For("landscape"));

        Rejected(Shipped.Replace("\"revision\": 1,", "\"revision\": -1,", StringComparison.Ordinal))
            .Code.ShouldBe(SubtitlePresetCatalog.RevisionNotPositiveCode);
    }

    // ------------------------------------------------------------------ Helpers -------------------------------

    private static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    private static string WithFontSize(string literal) =>
        Shipped.Replace("\"fontSize\": 84,", $"\"fontSize\": {literal},", StringComparison.Ordinal);

    private static Error Rejected(string json)
    {
        var result = SubtitlePresetCatalog.Load(Utf8(json));

        result.IsError.ShouldBeTrue("this document must be rejected");
        result.Errors.Count.ShouldBe(1);
        return result.FirstError;
    }
}
