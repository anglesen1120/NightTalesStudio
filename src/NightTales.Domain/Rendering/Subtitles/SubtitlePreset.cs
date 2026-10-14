namespace NightTales.Domain.Rendering.Subtitles;

/// <summary>
/// One aspect's subtitle preset of <c>seed/subtitle-presets.v1.json</c> (05 README D6), with the catalog's font
/// (D7) so the mapper needs no second argument. <see cref="MaxCharsPerLine"/> and <see cref="MaxLines"/> are not
/// MPT fields and never reach the submit body.
/// </summary>
/// <param name="Aspect">The preset key, <c>landscape</c> or <c>portrait</c>.</param>
/// <param name="FontName">The catalog's <c>fontName</c> (<c>BeVietnamPro-Bold.ttf</c>, D7).</param>
/// <param name="FontSize">MPT <c>font_size</c>.</param>
/// <param name="StrokeWidth">MPT <c>stroke_width</c>.</param>
/// <param name="StrokeColor">MPT <c>stroke_color</c>, <c>#RRGGBB</c> upper-case.</param>
/// <param name="TextForeColor">MPT <c>text_fore_color</c>, <c>#RRGGBB</c> upper-case.</param>
/// <param name="TextBackgroundColor">MPT <c>text_background_color</c>.</param>
/// <param name="RoundedSubtitleBackground">MPT <c>rounded_subtitle_background</c>.</param>
/// <param name="SubtitlePosition">MPT <c>subtitle_position</c>.</param>
/// <param name="CustomPosition">MPT <c>custom_position</c>, 0–100.</param>
/// <param name="MaxCharsPerLine">The advisory line limit of <c>subtitle_fit.py</c> (D9).</param>
/// <param name="MaxLines">The advisory line count of <c>subtitle_fit.py</c> (D9).</param>
public sealed record SubtitlePreset(
    string Aspect,
    string FontName,
    int FontSize,
    decimal StrokeWidth,
    string StrokeColor,
    string TextForeColor,
    bool TextBackgroundColor,
    bool RoundedSubtitleBackground,
    string SubtitlePosition,
    decimal CustomPosition,
    int MaxCharsPerLine,
    int MaxLines);
