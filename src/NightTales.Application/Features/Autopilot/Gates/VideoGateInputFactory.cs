using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Domain.Assets;
using NightTales.Domain.Rendering;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Gates;

namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>
/// Builds the video gate's input (02.8.2 plan §3.2) from the stored rows, defensively: it reads the finished
/// render's <c>quality_report</c> (<c>render-quality.v1</c>, 05 README D10), the <c>subtitles.enabled</c> flag of
/// <c>render_jobs.params</c> (<c>render-params.v1</c>, 05 README D11) and the episode's series aspect, and it never
/// throws on data.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every missing member becomes a null one.</b> The report is written from measurements a render node took, so a
/// member that is missing, renamed or unmeasurable is a <see langword="null"/> in
/// <see cref="QualityReportFacts"/> (CR-6, 05 P2) and fails its own check with
/// <see cref="GateDetailCodes.FieldAbsent"/> rather than being read as a zero or a <c>false</c>. An unreadable,
/// non-object or foreign-schema document is read as "no report at all", which fails check 1 and closes every
/// dependent check: the video stays in <c>video_review</c> and the owner decides, never an auto-approval on data the
/// gate could not read.
/// </para>
/// <para>
/// <b>The member names are the document's.</b> Every key read here is the <c>JsonPropertyName</c> of
/// <see cref="RenderQualityReportV1"/> (D10's frozen names, pinned by the D14 fixtures); the tests feed the parser a
/// report the type itself serialised, so a rename in the type fails them instead of silently degrading to "owner
/// review". The <c>images</c> block is 03.4.2-T1's (§3.3 of its plan) and is read the same defensive way: its
/// absence leaves both image counts null, because "the images could not be judged" must never read as "no image was
/// lost".
/// </para>
/// <para>
/// <b>Nothing here touches the database.</b> The caller loads the rows under its own tenant scope
/// (<c>ISystemContext.For(run.UserId)</c>, 09 README D10) and hands the parts over, so this class is a pure mapping
/// and needs no unit of work.
/// </para>
/// </remarks>
/// <param name="logger">The ids-only log of a document this class could not read.</param>
public sealed partial class VideoGateInputFactory(ILogger<VideoGateInputFactory> logger)
{
    /// <summary>The <c>schema</c> member of <c>render-quality.v1</c> (<see cref="RenderQualityReportV1.Schema"/>, D10).</summary>
    private const string SchemaMember = "schema";

    /// <summary>The <c>pass</c> member (<see cref="RenderQualityReportV1.Pass"/>): the report's own verdict (check 2).</summary>
    private const string PassMember = "pass";

    /// <summary>The <c>duration_sec</c> member (<see cref="RenderQualityReportV1.DurationSec"/>, check 3).</summary>
    private const string DurationSecMember = "duration_sec";

    /// <summary>The <c>tts_duration_sec</c> member (<see cref="RenderQualityReportV1.TtsDurationSec"/>, check 3).</summary>
    private const string TtsDurationSecMember = "tts_duration_sec";

    /// <summary>The <c>integrated_lufs</c> member (<see cref="RenderQualityReportV1.IntegratedLufs"/>, check 4).</summary>
    private const string IntegratedLufsMember = "integrated_lufs";

    /// <summary>The <c>width</c> member (<see cref="RenderQualityReportV1.Width"/>, check 5).</summary>
    private const string WidthMember = "width";

    /// <summary>The <c>height</c> member (<see cref="RenderQualityReportV1.Height"/>, check 5).</summary>
    private const string HeightMember = "height";

    /// <summary>The <c>subtitles_present</c> member (<see cref="RenderQualityReportV1.SubtitlesPresent"/>, check 6).</summary>
    private const string SubtitlesPresentMember = "subtitles_present";

    /// <summary>The <c>sha256</c> member (<see cref="RenderQualityReportV1.Sha256"/>, check 9).</summary>
    private const string Sha256Member = "sha256";

    /// <summary>The <c>hints</c> member (<see cref="RenderQualityReportV1.Hints"/>, check 10).</summary>
    private const string HintsMember = "hints";

    /// <summary>The <c>images</c> member (<see cref="RenderQualityReportV1.Images"/>, 03.4.2-T1 plan §3.3, check 7).</summary>
    private const string ImagesMember = "images";

    /// <summary>The <c>images.skipped</c> array whose length is check 7's skipped count (03 D7).</summary>
    private const string ImagesSkippedMember = "skipped";

    /// <summary>The <c>images.moderated_count</c> scalar check 7 adds to the skipped count (03 CR-3).</summary>
    private const string ImagesModeratedCountMember = "moderated_count";

    /// <summary>
    /// The <c>gate_thresholds_ref</c> key of <c>series.autopilot_settings</c> (02.8.2 plan §3.3): the profile name
    /// the gate thresholds resolve through, <c>default</c> when absent. The document's other keys belong to
    /// 02.8.3-T1, so only this one is read and a document without it is a series with no profile.
    /// </summary>
    private const string GateThresholdsRefMember = "gate_thresholds_ref";

    /// <summary>
    /// Maps one finished render, its stored report, its asset row and the series aspect into
    /// <see cref="VideoGateInput"/>.
    /// </summary>
    /// <param name="renderJob">The <c>succeeded</c> render job the verdict belongs to.</param>
    /// <param name="asset">The episode's video asset row, or <see langword="null"/> when there is none (check 9 fails closed).</param>
    /// <param name="aspect">The episode's series aspect (check 5, C14).</param>
    /// <param name="imageModerationEvents">
    /// How many <c>moderation_events</c> rows the render job has with <c>source = openai_image</c> (check 8).
    /// </param>
    /// <returns>The gate input; every unreadable part of it is a null member, never an exception.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="renderJob"/> is null.</exception>
    public VideoGateInput Build(
        RenderJob renderJob,
        Asset? asset,
        Aspect aspect,
        int imageModerationEvents)
    {
        ArgumentNullException.ThrowIfNull(renderJob);

        return new VideoGateInput(
            ParseQualityReport(renderJob.QualityReport),
            new RenderJobFacts(renderJob.Id),
            AssetFactsOf(asset),
            ResolveSubtitlesEnabled(renderJob.Params?.Subtitles?.Enabled),
            aspect,
            imageModerationEvents);
    }

    /// <summary>
    /// Reads the stored <c>render-quality.v1</c> document into the gate's facts.
    /// </summary>
    /// <param name="qualityReportJson">The <c>render_jobs.quality_report</c> text, or null/blank when none was stored.</param>
    /// <returns>
    /// The facts, or <see langword="null"/> when there is no document, it is not a JSON object, it is unreadable, or
    /// its <c>schema</c> is not <c>render-quality.v1</c> — every one of which fails check 1 and closes the checks
    /// that read a member. A document that parses yields facts whose members are null wherever the document omitted
    /// or mistyped them.
    /// </returns>
    public QualityReportFacts? ParseQualityReport(string? qualityReportJson)
    {
        if (string.IsNullOrWhiteSpace(qualityReportJson))
        {
            return null;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(qualityReportJson);
        }
        catch (JsonException)
        {
            LogReportUnreadable(qualityReportJson.Length);

            return null;
        }

        if (parsed is not JsonObject document)
        {
            LogReportNotAnObject();

            return null;
        }

        var schema = StringOf(document[SchemaMember]);
        if (!string.Equals(schema, RenderQualityReportV1.SchemaName, StringComparison.Ordinal))
        {
            LogReportSchemaForeign(schema);

            return null;
        }

        var images = document[ImagesMember] as JsonObject;
        if (images is null)
        {
            LogImagesBlockAbsent();
        }

        return new QualityReportFacts(
            Pass: FlagOf(document[PassMember]),
            DurationSec: DoubleOf(document[DurationSecMember]),
            TtsDurationSec: DoubleOf(document[TtsDurationSecMember]),
            IntegratedLufs: DoubleOf(document[IntegratedLufsMember]),
            Width: IntOf(document[WidthMember]),
            Height: IntOf(document[HeightMember]),
            SubtitlesPresent: FlagOf(document[SubtitlesPresentMember]),
            Sha256: StringOf(document[Sha256Member]),
            SkippedSceneCount: images?[ImagesSkippedMember] is JsonArray skipped ? skipped.Count : null,
            ModeratedImageCount: images is null ? null : IntOf(images[ImagesModeratedCountMember]),
            Hints: ReadHints(document[HintsMember]));
    }

    /// <summary>
    /// The <c>subtitles.enabled</c> flag of <c>render_jobs.params</c> (05 D11): a document without it counts as
    /// "subtitles requested", so checks 6 and 10 judge the SRT instead of being skipped (fail closed).
    /// </summary>
    /// <param name="enabled">The stored flag, or <see langword="null"/> when the document omits it.</param>
    /// <returns>The flag, defaulting to <c>true</c>.</returns>
    public static bool ResolveSubtitlesEnabled(bool? enabled) => enabled ?? true;

    /// <summary>
    /// The gate's view of the episode's asset row: the row itself when there is one, and the placeholder
    /// (<see cref="Guid.Empty"/> with no hash and no duration) when the row is missing or unreadable, which fails
    /// <c>asset_integrity</c> instead of passing it.
    /// </summary>
    /// <param name="asset">The stored asset, or <see langword="null"/>.</param>
    /// <returns>The facts the gate evaluates.</returns>
    public static AssetFacts AssetFactsOf(Asset? asset) => asset is null
        ? new AssetFacts(Guid.Empty, null, null)
        : new AssetFacts(asset.Id, asset.Sha256, asset.DurationSec);

    /// <summary>
    /// The <c>gate_thresholds_ref</c> of a series' <c>autopilot_settings</c> document (plan §3.3): the profile name
    /// the thresholds resolve through.
    /// </summary>
    /// <param name="autopilotSettings">The stored <c>series.autopilot_settings</c> text, or null when there is none.</param>
    /// <returns>
    /// The profile name, or <see langword="null"/> when the series has no settings, the document is unreadable or not
    /// an object, or the key is absent or not a non-blank string. The caller resolves null to the <c>default</c>
    /// profile and never disables the gate (plan §10 decision 6).
    /// </returns>
    public string? ReadGateThresholdsRef(string? autopilotSettings)
    {
        if (string.IsNullOrWhiteSpace(autopilotSettings))
        {
            return null;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(autopilotSettings);
        }
        catch (JsonException)
        {
            LogSettingsUnreadable(autopilotSettings.Length);

            return null;
        }

        if (parsed is not JsonObject settings)
        {
            LogSettingsNotAnObject();

            return null;
        }

        var reference = StringOf(settings[GateThresholdsRefMember]);

        return string.IsNullOrWhiteSpace(reference) ? null : reference;
    }

    /// <summary>The strings of a JSON array; anything else (an absent member, another type, non-string entries) is dropped.</summary>
    private static List<string> ReadHints(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        var hints = new List<string>(array.Count);
        foreach (var element in array)
        {
            if (StringOf(element) is { Length: > 0 } hint)
            {
                hints.Add(hint);
            }
        }

        return hints;
    }

    private static bool? FlagOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static double? DoubleOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    private static int? IntOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    [LoggerMessage(
        EventId = 6490,
        Level = LogLevel.Warning,
        Message = "A stored quality report of {Length} characters could not be parsed; the video gate reads it as absent.")]
    private partial void LogReportUnreadable(int length);

    [LoggerMessage(
        EventId = 6491,
        Level = LogLevel.Warning,
        Message = "A stored quality report is not a JSON object; the video gate reads it as absent.")]
    private partial void LogReportNotAnObject();

    [LoggerMessage(
        EventId = 6492,
        Level = LogLevel.Warning,
        Message = "A stored quality report carries schema {Schema} instead of render-quality.v1; the video gate reads it as absent.")]
    private partial void LogReportSchemaForeign(string? schema);

    [LoggerMessage(
        EventId = 6493,
        Level = LogLevel.Debug,
        Message = "A stored quality report carries no images block; the video gate fails its failed-scenes check closed.")]
    private partial void LogImagesBlockAbsent();

    [LoggerMessage(
        EventId = 6494,
        Level = LogLevel.Warning,
        Message = "A stored autopilot_settings document of {Length} characters could not be read; the gate resolves the default profile.")]
    private partial void LogSettingsUnreadable(int length);

    [LoggerMessage(
        EventId = 6495,
        Level = LogLevel.Warning,
        Message = "A stored autopilot_settings document is not a JSON object; the gate resolves the default profile.")]
    private partial void LogSettingsNotAnObject();
}
