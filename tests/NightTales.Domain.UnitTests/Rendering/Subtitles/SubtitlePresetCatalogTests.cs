using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ErrorOr;
using NightTales.Domain.Rendering.Subtitles;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Rendering.Subtitles;

/// <summary>
/// The <c>subtitle-presets.v1</c> catalog rules of 05 README D6 (05.1.2-T1 Tests table): the two shipped
/// presets, every rejection the Design paragraph lists, the exact decimal scale the golden wire comparison
/// needs, and the embedded <c>seed/subtitle-presets.v1.json</c> itself. Each rejected file is the shipped
/// document with one value changed, so a rejection is provably caused by that rule and not by the rest of the
/// file; the file is either the shipped one or the one mutated by the same builder.
/// </summary>
public sealed class SubtitlePresetCatalogTests
{
    private static readonly string[] Aspects = [SubtitlePresetCatalog.LandscapeAspect, SubtitlePresetCatalog.PortraitAspect];

    /// <summary>Colours that must not pass <c>^#[0-9A-F]{6}$</c>: lower-case hex, the three-digit form, no <c>#</c>, a non-hex letter.</summary>
    private static readonly string[] BadColors = ["#ffffff", "#FFF", "FFFFFF", "#GGGGGG"];

    /// <summary>The tracked seed file the embedded resource must stay equal to.</summary>
    private static readonly string SeedFile = Path.Combine(
        RepoRoot(),
        "seed",
        "subtitle-presets.v1.json");

    private static readonly byte[] DuplicateMember =
        Encoding.UTF8.GetBytes(
            """{ "schema": "subtitle-presets.v1", "schema": "subtitle-presets.v1", "revision": 1, "fontName": "BeVietnamPro-Bold.ttf", "presets": {} }""");

    /// <summary>Inputs that are not a readable document: empty, truncated, trailing comma, plain text.</summary>
    private static readonly byte[][] Malformed =
    [
        [],
        Encoding.UTF8.GetBytes("{"),
        Encoding.UTF8.GetBytes("{ \"schema\": \"subtitle-presets.v1\", }"),
        Encoding.UTF8.GetBytes("not json"),
    ];

    [Fact]
    public void Landscape_preset_values()
    {
        var preset = Loaded(Document()).For(SubtitlePresetCatalog.LandscapeAspect);

        preset.Aspect.ShouldBe("landscape");
        preset.FontName.ShouldBe(SubtitlePresetCatalog.FontNameValue);
        preset.FontSize.ShouldBe(84);
        preset.StrokeWidth.ShouldBe(3.0m);
        preset.StrokeColor.ShouldBe("#000000");
        preset.TextForeColor.ShouldBe("#FFFFFF");
        preset.TextBackgroundColor.ShouldBeFalse();
        preset.RoundedSubtitleBackground.ShouldBeFalse();
        preset.SubtitlePosition.ShouldBe("bottom");
        preset.CustomPosition.ShouldBe(70.0m);
        preset.MaxCharsPerLine.ShouldBe(42);
        preset.MaxLines.ShouldBe(2);
    }

    [Fact]
    public void Portrait_preset_values()
    {
        var preset = Loaded(Document()).For(SubtitlePresetCatalog.PortraitAspect);

        preset.Aspect.ShouldBe("portrait");
        preset.FontName.ShouldBe(SubtitlePresetCatalog.FontNameValue);
        preset.FontSize.ShouldBe(64);
        preset.StrokeWidth.ShouldBe(3.0m);
        preset.StrokeColor.ShouldBe("#000000");
        preset.TextForeColor.ShouldBe("#FFFFFF");
        preset.TextBackgroundColor.ShouldBeFalse();
        preset.RoundedSubtitleBackground.ShouldBeFalse();
        preset.SubtitlePosition.ShouldBe("custom");
        preset.CustomPosition.ShouldBe(62.0m);
        preset.MaxCharsPerLine.ShouldBe(28);
        preset.MaxLines.ShouldBe(2);
    }

    [Fact]
    public void Preserves_the_decimal_scale_of_the_file()
    {
        // `3.0`, not `3`: the golden fixture comparison is byte-level, so a decimal must not lose its scale.
        var preset = Loaded(Document()).For(SubtitlePresetCatalog.LandscapeAspect);

        preset.StrokeWidth.ToString(CultureInfo.InvariantCulture).ShouldBe("3.0");
        preset.CustomPosition.ToString(CultureInfo.InvariantCulture).ShouldBe("70.0");
    }

    [Fact]
    public void For_unknown_aspect_throws()
    {
        var catalog = Loaded(Document());

        Should.Throw<ArgumentException>(() => catalog.For("square"));
        Should.Throw<ArgumentException>(() => catalog.For("Landscape"));
        Should.Throw<ArgumentException>(() => catalog.For(" "));
    }

    [Fact]
    public void Embedded_seed_is_revision_one_with_the_d7_font()
    {
        var catalog = Embedded();

        catalog.Revision.ShouldBe(1);
        catalog.FontName.ShouldBe("BeVietnamPro-Bold.ttf");
        catalog.For(SubtitlePresetCatalog.LandscapeAspect).FontSize.ShouldBe(84);
        catalog.For(SubtitlePresetCatalog.PortraitAspect).FontSize.ShouldBe(64);
    }

    [Fact]
    public void Embedded_resource_matches_the_tracked_seed_file()
    {
        // The embedded copy is the tracked file (05.2.3-T1): a later edit to one of them must not drift.
        var embedded = Embedded();
        var tracked = Loaded(File.ReadAllBytes(SeedFile));

        tracked.Revision.ShouldBe(embedded.Revision);
        tracked.FontName.ShouldBe(embedded.FontName);
        foreach (var aspect in Aspects)
        {
            tracked.For(aspect).ShouldBe(embedded.For(aspect));
        }
    }

    [Fact]
    public void Rejects_wrong_schema()
    {
        Rejected(Document(schema: "subtitle-presets.v2")).Code.ShouldBe(SubtitlePresetCatalog.SchemaInvalidCode);
        Rejected(Document(schema: "Subtitle-Presets.v1")).Code.ShouldBe(SubtitlePresetCatalog.SchemaInvalidCode);
    }

    [Fact]
    public void Rejects_revision_zero()
    {
        Rejected(Document(revision: 0)).Code.ShouldBe(SubtitlePresetCatalog.RevisionNotPositiveCode);
        Rejected(Document(revision: -1)).Code.ShouldBe(SubtitlePresetCatalog.RevisionNotPositiveCode);
    }

    [Fact]
    public void Rejects_bad_font_name()
    {
        // D7: the one bundled font; the Medium fallback is a node-side choice, never a catalog value.
        Rejected(Document(fontName: "Arial.ttf")).Code.ShouldBe(SubtitlePresetCatalog.FontNameInvalidCode);
        Rejected(Document(fontName: "BeVietnamPro-Medium.ttf")).Code.ShouldBe(SubtitlePresetCatalog.FontNameInvalidCode);
    }

    [Fact]
    public void Rejects_missing_aspect()
    {
        var landscapeOnly = new JsonObject { [SubtitlePresetCatalog.LandscapeAspect] = Preset() };
        var portraitOnly = new JsonObject { [SubtitlePresetCatalog.PortraitAspect] = PortraitPreset() };

        var missingPortrait = Rejected(Document(presets: landscapeOnly));
        missingPortrait.Code.ShouldBe(SubtitlePresetCatalog.AspectMissingCode);
        missingPortrait.Description.ShouldContain("portrait");

        var missingLandscape = Rejected(Document(presets: portraitOnly));
        missingLandscape.Code.ShouldBe(SubtitlePresetCatalog.AspectMissingCode);
        missingLandscape.Description.ShouldContain("landscape");
    }

    [Fact]
    public void Rejects_font_size_out_of_range()
    {
        Rejected(Document(presets: Presets(landscape: Preset(fontSize: SubtitlePresetCatalog.MinFontSize - 1))))
            .Code.ShouldBe(SubtitlePresetCatalog.FontSizeOutOfRangeCode);
        Rejected(Document(presets: Presets(landscape: Preset(fontSize: SubtitlePresetCatalog.MaxFontSize + 1))))
            .Code.ShouldBe(SubtitlePresetCatalog.FontSizeOutOfRangeCode);
        Rejected(Document(presets: Presets(portrait: PortraitPreset(fontSize: SubtitlePresetCatalog.MaxFontSize + 1))))
            .Code.ShouldBe(SubtitlePresetCatalog.FontSizeOutOfRangeCode);
    }

    [Fact]
    public void Accepts_font_size_at_both_bounds()
    {
        Loaded(Document(presets: Presets(landscape: Preset(fontSize: SubtitlePresetCatalog.MinFontSize))))
            .For(SubtitlePresetCatalog.LandscapeAspect).FontSize.ShouldBe(24);
        Loaded(Document(presets: Presets(landscape: Preset(fontSize: SubtitlePresetCatalog.MaxFontSize))))
            .For(SubtitlePresetCatalog.LandscapeAspect).FontSize.ShouldBe(160);
    }

    [Fact]
    public void Rejects_stroke_width_out_of_range()
    {
        Rejected(Document(presets: Presets(landscape: Preset(strokeWidth: SubtitlePresetCatalog.MaxStrokeWidth + 1m))))
            .Code.ShouldBe(SubtitlePresetCatalog.StrokeWidthOutOfRangeCode);
        Rejected(Document(presets: Presets(landscape: Preset(strokeWidth: -1m))))
            .Code.ShouldBe(SubtitlePresetCatalog.StrokeWidthOutOfRangeCode);
    }

    [Fact]
    public void Accepts_stroke_width_at_both_bounds()
    {
        Loaded(Document(presets: Presets(landscape: Preset(strokeWidth: SubtitlePresetCatalog.MinStrokeWidth))))
            .For(SubtitlePresetCatalog.LandscapeAspect).StrokeWidth.ShouldBe(0m);
        Loaded(Document(presets: Presets(landscape: Preset(strokeWidth: SubtitlePresetCatalog.MaxStrokeWidth))))
            .For(SubtitlePresetCatalog.LandscapeAspect).StrokeWidth.ShouldBe(10m);
    }

    [Fact]
    public void Rejects_bad_color()
    {
        foreach (var color in BadColors)
        {
            Rejected(Document(presets: Presets(landscape: Preset(strokeColor: color))))
                .Code.ShouldBe(SubtitlePresetCatalog.ColorInvalidCode);
            Rejected(Document(presets: Presets(landscape: Preset(textForeColor: color))))
                .Code.ShouldBe(SubtitlePresetCatalog.ColorInvalidCode);
        }
    }

    [Fact]
    public void Rejects_unknown_position()
    {
        // C16: the five values MPT accepts, exactly as spelled.
        Rejected(Document(presets: Presets(landscape: Preset(subtitlePosition: "middle"))))
            .Code.ShouldBe(SubtitlePresetCatalog.PositionUnknownCode);
        Rejected(Document(presets: Presets(landscape: Preset(subtitlePosition: "Bottom"))))
            .Code.ShouldBe(SubtitlePresetCatalog.PositionUnknownCode);
        Rejected(Document(presets: Presets(landscape: Preset(subtitlePosition: "two thirds bottom"))))
            .Code.ShouldBe(SubtitlePresetCatalog.PositionUnknownCode);
    }

    [Fact]
    public void Rejects_custom_position_out_of_range()
    {
        Rejected(Document(presets: Presets(landscape: Preset(customPosition: SubtitlePresetCatalog.MaxCustomPosition + 1m))))
            .Code.ShouldBe(SubtitlePresetCatalog.CustomPositionOutOfRangeCode);
        Rejected(Document(presets: Presets(landscape: Preset(customPosition: -0.1m))))
            .Code.ShouldBe(SubtitlePresetCatalog.CustomPositionOutOfRangeCode);
    }

    [Fact]
    public void Accepts_custom_position_at_both_bounds()
    {
        Loaded(Document(presets: Presets(landscape: Preset(customPosition: SubtitlePresetCatalog.MinCustomPosition))))
            .For(SubtitlePresetCatalog.LandscapeAspect).CustomPosition.ShouldBe(0m);
        Loaded(Document(presets: Presets(landscape: Preset(customPosition: SubtitlePresetCatalog.MaxCustomPosition))))
            .For(SubtitlePresetCatalog.LandscapeAspect).CustomPosition.ShouldBe(100m);
    }

    [Fact]
    public void Rejects_non_positive_line_limits()
    {
        Rejected(Document(presets: Presets(landscape: Preset(maxCharsPerLine: 0))))
            .Code.ShouldBe(SubtitlePresetCatalog.MaxCharsPerLineInvalidCode);
        Rejected(Document(presets: Presets(landscape: Preset(maxLines: 0))))
            .Code.ShouldBe(SubtitlePresetCatalog.MaxLinesInvalidCode);
        Rejected(Document(presets: Presets(portrait: PortraitPreset(maxLines: -2))))
            .Code.ShouldBe(SubtitlePresetCatalog.MaxLinesInvalidCode);
    }

    [Fact]
    public void Rejects_member_of_the_wrong_type()
    {
        var preset = Preset();
        preset["textBackgroundColor"] = "false";

        Rejected(Document(presets: Presets(landscape: preset))).Code.ShouldBe(SubtitlePresetCatalog.DocumentInvalidCode);
    }

    [Fact]
    public void Rejects_malformed_json_without_throwing()
    {
        foreach (var bytes in Malformed)
        {
            var result = Should.NotThrow(() => SubtitlePresetCatalog.Load(bytes));

            result.IsError.ShouldBeTrue();
            result.FirstError.Code.ShouldBe(SubtitlePresetCatalog.DocumentInvalidCode);
        }

        // A valid JSON value that is not the document (root not an object) fails the same way.
        var scalar = Should.NotThrow(() => SubtitlePresetCatalog.Load(Encoding.UTF8.GetBytes("\"subtitle-presets.v1\"")));
        scalar.IsError.ShouldBeTrue();
        scalar.FirstError.Code.ShouldBe(SubtitlePresetCatalog.DocumentInvalidCode);
    }

    [Fact]
    public void Rejects_duplicate_member()
    {
        // SEC-1 parity with MusicCatalogFileReader: a repeated member would silently keep one of the two values.
        var result = Should.NotThrow(() => SubtitlePresetCatalog.Load(DuplicateMember));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(SubtitlePresetCatalog.DocumentInvalidCode);
    }

    /// <summary>A document with the shipped values; every override is one member of one preset.</summary>
    private static JsonObject Document(
        string schema = SubtitlePresetCatalog.SchemaName,
        int revision = 1,
        string fontName = SubtitlePresetCatalog.FontNameValue,
        JsonObject? presets = null) => new()
        {
            ["schema"] = schema,
            ["revision"] = revision,
            ["fontName"] = fontName,
            ["presets"] = presets ?? Presets(),
        };

    private static JsonObject Presets(JsonObject? landscape = null, JsonObject? portrait = null) => new()
    {
        [SubtitlePresetCatalog.LandscapeAspect] = landscape ?? Preset(),
        [SubtitlePresetCatalog.PortraitAspect] = portrait ?? PortraitPreset(),
    };

    /// <summary>The D6 landscape preset; the parameter default is the shipped value.</summary>
    private static JsonObject Preset(
        int fontSize = 84,
        decimal strokeWidth = 3.0m,
        string strokeColor = "#000000",
        string textForeColor = "#FFFFFF",
        bool textBackgroundColor = false,
        bool roundedSubtitleBackground = false,
        string subtitlePosition = "bottom",
        decimal customPosition = 70.0m,
        int maxCharsPerLine = 42,
        int maxLines = 2) => new()
        {
            ["fontSize"] = fontSize,
            ["strokeWidth"] = strokeWidth,
            ["strokeColor"] = strokeColor,
            ["textForeColor"] = textForeColor,
            ["textBackgroundColor"] = textBackgroundColor,
            ["roundedSubtitleBackground"] = roundedSubtitleBackground,
            ["subtitlePosition"] = subtitlePosition,
            ["customPosition"] = customPosition,
            ["maxCharsPerLine"] = maxCharsPerLine,
            ["maxLines"] = maxLines,
        };

    /// <summary>The D6 portrait preset: the shipped values, with the parameters the D6 overrides land on.</summary>
    private static JsonObject PortraitPreset(
        int fontSize = 64,
        decimal strokeWidth = 3.0m,
        string strokeColor = "#000000",
        string textForeColor = "#FFFFFF",
        bool textBackgroundColor = false,
        bool roundedSubtitleBackground = false,
        string subtitlePosition = "custom",
        decimal customPosition = 62.0m,
        int maxCharsPerLine = 28,
        int maxLines = 2) => new()
        {
            ["fontSize"] = fontSize,
            ["strokeWidth"] = strokeWidth,
            ["strokeColor"] = strokeColor,
            ["textForeColor"] = textForeColor,
            ["textBackgroundColor"] = textBackgroundColor,
            ["roundedSubtitleBackground"] = roundedSubtitleBackground,
            ["subtitlePosition"] = subtitlePosition,
            ["customPosition"] = customPosition,
            ["maxCharsPerLine"] = maxCharsPerLine,
            ["maxLines"] = maxLines,
        };

    private static SubtitlePresetCatalog Loaded(JsonObject document) => Loaded(Encoding.UTF8.GetBytes(document.ToJsonString()));

    private static SubtitlePresetCatalog Loaded(byte[] json)
    {
        var result = SubtitlePresetCatalog.Load(json);
        result.IsError.ShouldBeFalse(Describe(result));
        return result.Value;
    }

    private static SubtitlePresetCatalog Embedded()
    {
        var result = SubtitlePresetCatalog.LoadEmbedded();
        result.IsError.ShouldBeFalse(Describe(result));
        return result.Value;
    }

    /// <summary>The failure text of a load, or nothing when it loaded; the first error is never read on success.</summary>
    private static string Describe(ErrorOr<SubtitlePresetCatalog> result) =>
        result.IsError ? $"{result.FirstError.Code}: {result.FirstError.Description}" : string.Empty;

    /// <summary>The single error of a rejected document: the rules stop at the first failure, by design.</summary>
    private static Error Rejected(JsonObject document)
    {
        var result = SubtitlePresetCatalog.Load(Encoding.UTF8.GetBytes(document.ToJsonString()));

        result.IsError.ShouldBeTrue();
        result.Errors.Count.ShouldBe(1);
        return result.FirstError;
    }

    /// <summary>The repository root, found by walking up from the test output to <c>NightTales.sln</c>.</summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NightTales.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root (NightTales.sln) above the test output.");
    }
}
