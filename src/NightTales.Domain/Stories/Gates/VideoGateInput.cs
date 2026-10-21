using NightTales.Domain.Series;

namespace NightTales.Domain.Stories.Gates;

/// <summary>
/// Everything the video half of the gate evaluates (02.8.2 plan §3.2), assembled by the Application from the
/// finished <c>render_jobs</c> row, its stored <c>render-quality.v1</c> report (05 README D10), the
/// <c>assets</c> row and the series aspect. Primitives only, so <see cref="VideoGateRules"/> stays pure.
/// </summary>
/// <param name="Report">
/// The stored <c>quality_report</c>, or <see langword="null"/> when the column is empty: a null report fails
/// <c>quality_report_present</c> and every dependent check closed.
/// </param>
/// <param name="RenderJob">The finished render job's own facts.</param>
/// <param name="Asset">The stored asset row the report was ingested into.</param>
/// <param name="SubtitlesEnabled"><c>render_jobs.params.subtitles.enabled</c>; gates checks 6 and 10.</param>
/// <param name="Aspect">The series aspect, which decides the frame size check 5 requires (C14).</param>
/// <param name="ImageModerationEvents">
/// How many <c>moderation_events</c> rows this render job has with <c>source = openai_image</c> (plan §3.2
/// check 8). It is that row count, not <c>quality_report.images.moderated_count</c>, which check 7 already
/// counts.
/// </param>
public sealed record VideoGateInput(
    QualityReportFacts? Report,
    RenderJobFacts RenderJob,
    AssetFacts Asset,
    bool SubtitlesEnabled,
    Aspect Aspect,
    int ImageModerationEvents);

/// <summary>
/// The gate-relevant facts of the stored <c>render-quality.v1</c> report (05 README D10). Every member is
/// nullable on purpose (CR-6): the ingest writes whatever the render node measured, and a member that is
/// missing, renamed or unmeasurable must fail its own check with <see cref="GateDetailCodes.FieldAbsent"/>
/// rather than be read as a zero or a <c>false</c>.
/// </summary>
/// <param name="Pass">The report's own verdict; <c>false</c> ends the render before this gate normally.</param>
/// <param name="DurationSec">The measured container duration (check 3).</param>
/// <param name="TtsDurationSec">The measured narration duration (check 3); zero or negative cannot be judged.</param>
/// <param name="IntegratedLufs">The measured integrated loudness in LUFS (check 4).</param>
/// <param name="Width">The measured frame width (check 5).</param>
/// <param name="Height">The measured frame height (check 5).</param>
/// <param name="SubtitlesPresent">Whether the measured file carried an SRT (check 6).</param>
/// <param name="Sha256">The measured file's SHA-256 (check 9).</param>
/// <param name="SkippedSceneCount">The length of <c>images.skipped</c> (check 7, 03 D7/CR-3).</param>
/// <param name="ModeratedImageCount">How many images the moderation removed (03 CR-3); check 7 adds it to the skipped count.</param>
/// <param name="Hints">The advisory hints of the report; only <c>subtitle_sync_suspect</c> is read (check 10).</param>
public sealed record QualityReportFacts(
    bool? Pass,
    double? DurationSec,
    double? TtsDurationSec,
    double? IntegratedLufs,
    int? Width,
    int? Height,
    bool? SubtitlesPresent,
    string? Sha256,
    int? SkippedSceneCount,
    int? ModeratedImageCount,
    IReadOnlyList<string> Hints);

/// <summary>The gate-relevant facts of the finished <c>render_jobs</c> row.</summary>
/// <param name="Id">The render job id, carried into <see cref="VideoGateResult.RenderJobId"/>.</param>
public sealed record RenderJobFacts(Guid Id);

/// <summary>The gate-relevant facts of the stored <c>assets</c> row (check 9).</summary>
/// <param name="Id">The asset id, carried into <see cref="VideoGateResult.AssetId"/>.</param>
/// <param name="Sha256">
/// The stored file's SHA-256, or <see langword="null"/>/empty when the row is missing or unreadable: the check
/// then fails closed. It is compared with the report's hash ordinally and case-insensitively, because one side
/// may be upper-case hex.
/// </param>
/// <param name="DurationSec">The stored file's duration; it must be above zero.</param>
public sealed record AssetFacts(Guid Id, string? Sha256, decimal? DurationSec);
