using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NightTales.Application.Rendering.Quality;

namespace NightTales.Application.Abstractions.Rendering;

/// <summary>
/// The stored verdict <c>render_jobs.quality_report</c> = <c>render-quality.v1</c> (05 README D10),
/// written by the 09.6.3-T2 ingest from <see cref="RenderQualityEvaluator.Evaluate"/> plus the
/// <c>images</c> block 03.4.2-T1 owns (P3). The property order <em>is</em> the document order the D14
/// fixtures pin, and every member carries its wire name, so no naming policy is involved.
/// </summary>
/// <remarks>
/// 02.8.2's video gate reads <see cref="Pass"/>, <see cref="DurationSec"/>, <see cref="TtsDurationSec"/>,
/// <see cref="IntegratedLufs"/>, <see cref="Width"/>, <see cref="Height"/>, <see cref="SubtitlesPresent"/>,
/// <see cref="Sha256"/> and <see cref="Images"/> defensively (CR-6, P2): a missing member must degrade to
/// "owner review", never to a wrong pass, so every measurable field is nullable rather than zeroed and
/// no member is <c>[JsonRequired]</c> on read.
/// </remarks>
public sealed record RenderQualityReportV1
{
    /// <summary>The one document schema string.</summary>
    public const string SchemaName = "render-quality.v1";

    /// <summary>The document schema; <c>render-quality.v1</c>.</summary>
    [JsonPropertyName("schema")]
    public string Schema { get; init; } = SchemaName;

    /// <summary>Whether every blocking check passed (D10); <c>false</c> ends the job <c>render_quality_failed</c>.</summary>
    [JsonPropertyName("pass")]
    public bool Pass { get; init; }

    /// <summary>The <see cref="RenderQualityPolicy.PolicyVersion"/> that produced the verdict.</summary>
    [JsonPropertyName("policy_version")]
    public int PolicyVersion { get; init; }

    /// <summary>The <c>render_check.py</c> version that measured the file, or <c>null</c> with no report.</summary>
    [JsonPropertyName("check_tool_version")]
    public string? CheckToolVersion { get; init; }

    /// <summary>The measured container duration, in seconds.</summary>
    [JsonPropertyName("duration_sec")]
    public double? DurationSec { get; init; }

    /// <summary>The measured narration duration, in seconds (F17).</summary>
    [JsonPropertyName("tts_duration_sec")]
    public double? TtsDurationSec { get; init; }

    /// <summary>The integrated loudness of the served file, in LUFS.</summary>
    [JsonPropertyName("integrated_lufs")]
    public double? IntegratedLufs { get; init; }

    /// <summary>The true peak of the served file, in dBTP.</summary>
    [JsonPropertyName("true_peak_dbtp")]
    public double? TruePeakDbtp { get; init; }

    /// <summary>The frame width in pixels.</summary>
    [JsonPropertyName("width")]
    public int? Width { get; init; }

    /// <summary>The frame height in pixels.</summary>
    [JsonPropertyName("height")]
    public int? Height { get; init; }

    /// <summary>Whether the measured file carried an SRT.</summary>
    [JsonPropertyName("subtitles_present")]
    public bool SubtitlesPresent { get; init; }

    /// <summary>The SHA-256 of the served file; the ingest compares it with the downloaded bytes.</summary>
    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }

    /// <summary>Whether the served file is the loudness-normalised one.</summary>
    [JsonPropertyName("normalized")]
    public bool Normalized { get; init; }

    /// <summary>The subtitle counts and the derived sync-suspect share; <c>null</c> when nothing was measurable.</summary>
    [JsonPropertyName("subtitles")]
    public RenderQualitySubtitles? Subtitles { get; init; }

    /// <summary>The blocking checks, in D10's order.</summary>
    [JsonPropertyName("checks")]
    public IReadOnlyList<QualityCheck> Checks { get; init; } = [];

    /// <summary>The advisory hints, in D10's order; never blocking.</summary>
    [JsonPropertyName("hints")]
    public IReadOnlyList<string> Hints { get; init; } = [];

    /// <summary>
    /// The <c>images.v1</c> block of 03.4.2-T1, merged in the ingest's own transaction. The evaluator
    /// always returns <c>null</c> here: 05.5.1 never writes it (P3, CR-3).
    /// </summary>
    [JsonPropertyName("images")]
    public JsonNode? Images { get; init; }
}
