namespace NightTales.Application.Features.Rendering;

/// <summary>
/// The <c>Images</c> section (03 README D3/D8/D9, 03.2.1-T1). <see cref="ClipSeconds"/> changes affect new renders
/// only, and K in the story prompts (02 D4) should change with it.
/// </summary>
public sealed class ImagesOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Images";

    /// <summary>The quality values the relay accepts (D9); unset means <see cref="AutoQuality"/>.</summary>
    public static readonly IReadOnlyList<string> AllowedQualities = ["low", "medium", "high", "auto"];

    /// <summary>The API default quality, used while <see cref="Quality"/> is unset.</summary>
    public const string AutoQuality = "auto";

    /// <summary>The text-prompt tokens charged per image on top of the image output (D8).</summary>
    public const int PromptTextTokens = 200;

    /// <summary>Seconds one image stays on screen (<c>video_clip_duration</c> = <c>60 ÷ K</c>, K = 6).</summary>
    public int ClipSeconds { get; set; } = 10;

    /// <summary>Free spare terms on top of the needed images, as a share of them.</summary>
    public double SpareShare { get; set; } = 0.15;

    /// <summary>Below this share of the needed images the plan raises <c>scenes_sparse</c>.</summary>
    public double SparseShare { get; set; } = 0.7;

    /// <summary>The share of skipped images a succeeded render tolerates without review (03.4.2).</summary>
    public double MaxSkippedShare { get; set; } = 0.10;

    /// <summary>The image model whose price the estimate reads from <c>price_versions.rates.image</c>.</summary>
    public string Model { get; set; } = "gpt-image-2";

    /// <summary>The relay quality mirrored from <c>ImageRelay:Quality</c>; <c>null</c> or empty = unset = <see cref="AutoQuality"/>.</summary>
    public string? Quality { get; set; }

    /// <summary>Image output tokens assumed per image while the price version has no price for <see cref="Model"/> (D8 placeholder).</summary>
    public int PlaceholderTokensPerImage { get; set; } = 6_000;

    /// <summary>USD per 1M image output tokens of the placeholder estimate (O8).</summary>
    public decimal PlaceholderImageOutPer1MUsd { get; set; } = 30m;

    /// <summary>USD per 1M text input tokens of the placeholder estimate (O8).</summary>
    public decimal PlaceholderTextInPer1MUsd { get; set; } = 5m;

    /// <summary><see cref="Quality"/> or <see cref="AutoQuality"/> when unset.</summary>
    public string QualityOrAuto => string.IsNullOrWhiteSpace(Quality) ? AutoQuality : Quality;
}
