using System.Globalization;
using System.Text.Json;
using ErrorOr;

namespace NightTales.Domain.Rendering.Subtitles;

/// <summary>
/// The <c>subtitle-presets.v1</c> catalog of 05 README D6: the tracked <c>seed/subtitle-presets.v1.json</c>,
/// embedded in the Domain assembly (a deployment cannot start without it) and read once at startup, never per
/// render. The mapper asks for one aspect's <see cref="SubtitlePreset"/> through <see cref="For"/>; a value
/// change means <c>revision + 1</c> in the file, which lands in <c>subtitles.preset</c> (D11).
/// </summary>
/// <remarks>
/// Every rule of the D6 validation list, and the structural checks this reader adds on top, is an
/// <see cref="ErrorOr{TValue}"/> outcome with a stable snake_case code and never an exception, and the
/// <em>first</em> failing rule in the list order wins, so one bad file always reports one error.
/// <see cref="Load"/> takes the bytes, not a path: the caller owns where the file came from.
/// </remarks>
public sealed class SubtitlePresetCatalog
{
    /// <summary>The logical name of the embedded <c>seed/subtitle-presets.v1.json</c>.</summary>
    public const string ResourceName = "NightTales.Domain.Rendering.Subtitles.subtitle-presets.v1.json";

    /// <summary>The expected <c>schema</c> value (D6).</summary>
    public const string SchemaName = "subtitle-presets.v1";

    /// <summary>The one bundled subtitle font (D7); a catalog naming another one is rejected.</summary>
    public const string FontNameValue = "BeVietnamPro-Bold.ttf";

    /// <summary>The landscape preset key (D6).</summary>
    public const string LandscapeAspect = "landscape";

    /// <summary>The portrait preset key (D6).</summary>
    public const string PortraitAspect = "portrait";

    /// <summary>The smallest <c>fontSize</c> a preset may carry.</summary>
    public const int MinFontSize = 24;

    /// <summary>The largest <c>fontSize</c> a preset may carry.</summary>
    public const int MaxFontSize = 160;

    /// <summary>The smallest <c>strokeWidth</c> a preset may carry.</summary>
    public const decimal MinStrokeWidth = 0m;

    /// <summary>The largest <c>strokeWidth</c> a preset may carry.</summary>
    public const decimal MaxStrokeWidth = 10m;

    /// <summary>The smallest <c>customPosition</c> a preset may carry (a percentage of the frame height).</summary>
    public const decimal MinCustomPosition = 0m;

    /// <summary>The largest <c>customPosition</c> a preset may carry.</summary>
    public const decimal MaxCustomPosition = 100m;

    /// <summary>The code of an assembly that does not carry the embedded catalog.</summary>
    public const string ResourceMissingCode = "subtitle_presets_resource_missing";

    /// <summary>
    /// The code of a file that is not a readable <c>subtitle-presets.v1</c> document; mirrors
    /// <c>MusicCatalogFileReader.CatalogInvalidCode</c> (the <c>music_catalog_invalid</c> shape code of D8).
    /// </summary>
    public const string DocumentInvalidCode = "subtitle_presets_invalid";

    /// <summary>The code of a <c>schema</c> other than <see cref="SchemaName"/>.</summary>
    public const string SchemaInvalidCode = "schema_invalid";

    /// <summary>The code of a <c>revision</c> below 1.</summary>
    public const string RevisionNotPositiveCode = "revision_not_positive";

    /// <summary>The code of an absent <c>presets.landscape</c> or <c>presets.portrait</c>.</summary>
    public const string AspectMissingCode = "aspect_missing";

    /// <summary>The code of a <c>fontName</c> other than <see cref="FontNameValue"/> (D7).</summary>
    public const string FontNameInvalidCode = "font_name_invalid";

    /// <summary>The code of a <c>fontSize</c> outside <see cref="MinFontSize"/>–<see cref="MaxFontSize"/>.</summary>
    public const string FontSizeOutOfRangeCode = "font_size_out_of_range";

    /// <summary>The code of a <c>strokeWidth</c> outside <see cref="MinStrokeWidth"/>–<see cref="MaxStrokeWidth"/>.</summary>
    public const string StrokeWidthOutOfRangeCode = "stroke_width_out_of_range";

    /// <summary>The code of a colour that is not <c>#RRGGBB</c> upper-case (the MPT wire form, D3).</summary>
    public const string ColorInvalidCode = "color_invalid";

    /// <summary>The code of a <c>subtitlePosition</c> outside the C16 set.</summary>
    public const string PositionUnknownCode = "subtitle_position_unknown";

    /// <summary>The code of a <c>customPosition</c> outside <see cref="MinCustomPosition"/>–<see cref="MaxCustomPosition"/>.</summary>
    public const string CustomPositionOutOfRangeCode = "custom_position_out_of_range";

    /// <summary>The code of a <c>maxCharsPerLine</c> below 1.</summary>
    public const string MaxCharsPerLineInvalidCode = "max_chars_per_line_invalid";

    /// <summary>The code of a <c>maxLines</c> below 1.</summary>
    public const string MaxLinesInvalidCode = "max_lines_invalid";

    /// <summary><c>#RRGGBB</c>: seven characters.</summary>
    private const int ColorLength = 7;

    /// <summary>The C16 <c>subtitle_position</c> values, in the order D6 lists them.</summary>
    private static readonly string[] Positions = ["bottom", "top", "center", "two_thirds_bottom", "custom"];

    // A repeated member would silently keep the last value (MusicCatalogFileReader's SEC-1 rule): refuse the
    // file instead. JsonDocument needs a ReadOnlyMemory<byte>, hence the one copy of the (small,
    // startup-only) document.
    private static readonly JsonDocumentOptions DuplicateRejectingOptions = new() { AllowDuplicateProperties = false };

    private readonly SubtitlePreset _landscape;
    private readonly SubtitlePreset _portrait;

    private SubtitlePresetCatalog(int revision, string fontName, SubtitlePreset landscape, SubtitlePreset portrait)
    {
        Revision = revision;
        FontName = fontName;
        _landscape = landscape;
        _portrait = portrait;
    }

    /// <summary>The file's <c>revision</c>; it becomes <c>subtitles.preset = &lt;aspect&gt;@&lt;Revision&gt;</c> (D11).</summary>
    public int Revision { get; }

    /// <summary>The file's <c>fontName</c>, always <see cref="FontNameValue"/> (D7).</summary>
    public string FontName { get; }

    /// <summary>Loads the catalog embedded in this assembly; the startup path.</summary>
    /// <returns>
    /// The catalog, or <see cref="ResourceMissingCode"/> when the assembly does not carry the resource (fail
    /// fast at startup, never a null catalog).
    /// </returns>
    public static ErrorOr<SubtitlePresetCatalog> LoadEmbedded()
    {
        using var stream = typeof(SubtitlePresetCatalog).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return Error.Failure(
                ResourceMissingCode,
                $"The assembly '{typeof(SubtitlePresetCatalog).Assembly.GetName().Name}' has no embedded resource named '{ResourceName}'.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);

        return Load(bytes);
    }

    /// <summary>Parses and validates a <c>subtitle-presets.v1</c> document.</summary>
    /// <param name="json">The raw UTF-8 JSON, as tracked in <c>seed/subtitle-presets.v1.json</c>.</param>
    /// <returns>The catalog, or the first failing rule of the D6 list.</returns>
    public static ErrorOr<SubtitlePresetCatalog> Load(ReadOnlySpan<byte> json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.ToArray(), DuplicateRejectingOptions);
        }
        catch (JsonException)
        {
            return Invalid("the file is not JSON");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !TryString(root, "schema", out var schema))
            {
                return Invalid("schema is missing or not a string");
            }

            if (schema != SchemaName)
            {
                return Error.Validation(SchemaInvalidCode, $"schema={schema}");
            }

            if (!root.TryGetProperty("revision", out var revisionElement)
                || revisionElement.ValueKind != JsonValueKind.Number
                || !revisionElement.TryGetInt32(out var revision))
            {
                return Invalid("revision is missing or not an integer");
            }

            if (revision < 1)
            {
                return Error.Validation(RevisionNotPositiveCode, $"revision={revision}");
            }

            if (!root.TryGetProperty("presets", out var presets) || presets.ValueKind != JsonValueKind.Object)
            {
                return Invalid("presets is missing or not an object");
            }

            if (!IsPresetObject(presets, LandscapeAspect))
            {
                return MissingAspect(LandscapeAspect);
            }

            if (!IsPresetObject(presets, PortraitAspect))
            {
                return MissingAspect(PortraitAspect);
            }

            if (!TryString(root, "fontName", out var fontName))
            {
                return Invalid("fontName is missing or not a string");
            }

            if (fontName != FontNameValue)
            {
                return Error.Validation(FontNameInvalidCode, $"fontName={fontName}");
            }

            var landscape = ReadPreset(presets, LandscapeAspect, fontName!);
            if (landscape.IsError)
            {
                return landscape.Errors;
            }

            var portrait = ReadPreset(presets, PortraitAspect, fontName!);
            if (portrait.IsError)
            {
                return portrait.Errors;
            }

            return new SubtitlePresetCatalog(revision, fontName!, landscape.Value, portrait.Value);
        }
    }

    /// <summary>One aspect's preset, carrying the catalog's <see cref="FontName"/> (D7).</summary>
    /// <param name="aspect">The preset key: <c>landscape</c> or <c>portrait</c>.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="aspect"/> is neither key. This is a programming error, not user input: the caller
    /// validates the series aspect first (<c>RenderSubmitMapper.BuildSubmit</c> reports <c>render.aspect_invalid</c>).
    /// </exception>
    public SubtitlePreset For(string aspect)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aspect);

        return aspect switch
        {
            LandscapeAspect => _landscape,
            PortraitAspect => _portrait,
            _ => throw new ArgumentException(
                $"Unknown aspect '{aspect}'; expected '{LandscapeAspect}' or '{PortraitAspect}'.",
                nameof(aspect)),
        };
    }

    private static ErrorOr<SubtitlePreset> ReadPreset(JsonElement presets, string aspect, string fontName)
    {
        var element = presets.GetProperty(aspect);
        if (!TryInt(element, "fontSize", out var fontSize)
            || !TryDecimal(element, "strokeWidth", out var strokeWidth)
            || !TryString(element, "strokeColor", out var strokeColor)
            || !TryString(element, "textForeColor", out var textForeColor)
            || !TryBool(element, "textBackgroundColor", out var textBackgroundColor)
            || !TryBool(element, "roundedSubtitleBackground", out var roundedSubtitleBackground)
            || !TryString(element, "subtitlePosition", out var subtitlePosition)
            || !TryDecimal(element, "customPosition", out var customPosition)
            || !TryInt(element, "maxCharsPerLine", out var maxCharsPerLine)
            || !TryInt(element, "maxLines", out var maxLines))
        {
            return Invalid($"presets.{aspect} is missing a member or has a member of the wrong type");
        }

        if (fontSize < MinFontSize || fontSize > MaxFontSize)
        {
            return Error.Validation(
                FontSizeOutOfRangeCode,
                $"presets.{aspect}.fontSize={fontSize} is outside [{MinFontSize}, {MaxFontSize}]");
        }

        if (strokeWidth < MinStrokeWidth || strokeWidth > MaxStrokeWidth)
        {
            return Error.Validation(
                StrokeWidthOutOfRangeCode,
                $"presets.{aspect}.strokeWidth={strokeWidth} is outside [{MinStrokeWidth}, {MaxStrokeWidth}]");
        }

        if (!IsColor(strokeColor))
        {
            return Error.Validation(ColorInvalidCode, $"presets.{aspect}.strokeColor={strokeColor} is not #RRGGBB upper-case");
        }

        if (!IsColor(textForeColor))
        {
            return Error.Validation(ColorInvalidCode, $"presets.{aspect}.textForeColor={textForeColor} is not #RRGGBB upper-case");
        }

        if (!Positions.Contains(subtitlePosition!, StringComparer.Ordinal))
        {
            return Error.Validation(PositionUnknownCode, $"presets.{aspect}.subtitlePosition={subtitlePosition}");
        }

        if (customPosition < MinCustomPosition || customPosition > MaxCustomPosition)
        {
            return Error.Validation(
                CustomPositionOutOfRangeCode,
                $"presets.{aspect}.customPosition={customPosition} is outside [{MinCustomPosition}, {MaxCustomPosition}]");
        }

        if (maxCharsPerLine < 1)
        {
            return Error.Validation(MaxCharsPerLineInvalidCode, $"presets.{aspect}.maxCharsPerLine={maxCharsPerLine}");
        }

        if (maxLines < 1)
        {
            return Error.Validation(MaxLinesInvalidCode, $"presets.{aspect}.maxLines={maxLines}");
        }

        return new SubtitlePreset(
            aspect,
            fontName,
            fontSize,
            strokeWidth,
            strokeColor!,
            textForeColor!,
            textBackgroundColor,
            roundedSubtitleBackground,
            subtitlePosition!,
            customPosition,
            maxCharsPerLine,
            maxLines);
    }

    private static bool IsPresetObject(JsonElement presets, string aspect) =>
        presets.TryGetProperty(aspect, out var element) && element.ValueKind == JsonValueKind.Object;

    private static Error MissingAspect(string aspect) =>
        Error.Validation(AspectMissingCode, $"presets.{aspect} is missing or not an object");

    /// <summary><c>^#[0-9A-F]{6}$</c> (D3): the wire form is upper-case, so a lower-case colour is rejected.</summary>
    private static bool IsColor(string? value)
    {
        if (value is not { Length: ColorLength } || value[0] != '#')
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            if (value[index] is not ((>= '0' and <= '9') or (>= 'A' and <= 'F')))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryString(JsonElement element, string property, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(property, out var found) || found.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = found.GetString();

        return true;
    }

    private static bool TryInt(JsonElement element, string property, out int value)
    {
        value = 0;

        return element.TryGetProperty(property, out var found)
            && found.ValueKind == JsonValueKind.Number
            && found.TryGetInt32(out value);
    }

    private static bool TryDecimal(JsonElement element, string property, out decimal value)
    {
        value = 0m;
        if (!element.TryGetProperty(property, out var found) || found.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        // Parsed from the raw token, not `GetDecimal()`, because the scale must survive: the D3 fixtures carry
        // `3.0` and `70.0`, and the golden comparison of 05.1.2-T1 is byte-level, so a `3` would not match.
        return decimal.TryParse(found.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryBool(JsonElement element, string property, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(property, out var found)
            || found.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = found.GetBoolean();

        return true;
    }

    private static Error Invalid(string detail) => Error.Validation(DocumentInvalidCode, detail);
}
