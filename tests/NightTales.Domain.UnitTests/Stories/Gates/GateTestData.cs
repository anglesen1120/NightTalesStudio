using NightTales.Domain.Series;
using NightTales.Domain.Stories.Gates;
using NightTales.Domain.Stories.Review;
using Shouldly;

namespace NightTales.Domain.UnitTests.Stories.Gates;

/// <summary>
/// The all-green inputs the gate tests start from (02.8.2-T1): one place that says what "nothing is wrong"
/// means, so a failing test proves the one thing it changed. The rules are pure, so there is no mock, no
/// clock and no database here.
/// </summary>
/// <remarks>
/// A member whose <em>absence</em> is the case under test is expressed on the record itself, never through a
/// builder parameter: <c>Audit() with { Scores = null }</c>, <c>StoryInput() with { Moderation = null }</c>.
/// A <see langword="null"/> passed to a builder parameter means "use the green default", because that is what
/// the builders are for.
/// </remarks>
internal static class GateTestData
{
    /// <summary>A 64-character hex digest, the shape <c>quality_report.sha256</c> and the asset row both carry.</summary>
    public const string ReportSha = "2c26b46b68ffc68ff99b453c1d30413413422d706483bfa0f98a5e886266e7ae";

    public static readonly Guid StoryVersionId = new("018f0000-0000-7000-8000-000000000001");
    public static readonly Guid FactSheetId = new("018f0000-0000-7000-8000-000000000002");
    public static readonly Guid RenderJobId = new("018f0000-0000-7000-8000-000000000003");
    public static readonly Guid AssetId = new("018f0000-0000-7000-8000-000000000004");

    private static readonly IReadOnlyList<string> GreenKeywords = ["abandoned house at dusk"];
    private static readonly IReadOnlyList<string> GreenWarnings = ["violence"];
    private static readonly IReadOnlyList<string> NoHints = [];
    private static readonly IReadOnlyList<GateIssueFacts> NoIssues = [];

    /// <summary>
    /// A story input whose fifteen checks all pass for a non-true-crime topic: a 4-scored review with no
    /// issues, low similarity, an in-band length, no keyword problem and allowed warnings.
    /// </summary>
    /// <param name="version">The version facts, or the green defaults.</param>
    /// <param name="audit">The audit facts, or the green defaults.</param>
    /// <param name="similarity">The similarity signals, or the green defaults.</param>
    /// <param name="moderation">The <c>autopilot_gate</c> outcome, or the green "not flagged".</param>
    /// <param name="realWorld">The real-world signal, or the green "no hit".</param>
    /// <param name="series">The series facts, or a ghost-topic series.</param>
    /// <param name="length">The D4 band, or the 1600–1800 band with the 15 % advisory tolerance.</param>
    /// <param name="attempt">The run's story-gate attempt.</param>
    /// <returns>The assembled input.</returns>
    public static StoryGateInput StoryInput(
        StoryVersionFacts? version = null,
        AuditFacts? audit = null,
        SimilaritySignals? similarity = null,
        ModerationGateOutcome? moderation = null,
        RealWorldSignal? realWorld = null,
        SeriesGateContext? series = null,
        LengthBand? length = null,
        int attempt = 1) => new(
            Version: version ?? Version(),
            Audit: audit ?? Audit(),
            Similarity: similarity ?? new SimilaritySignals(Sources: 0.01m, PastEpisodes: 0.02m, CorpusMax: 50),
            Moderation: moderation ?? new ModerationGateOutcome(Flagged: false, UnavailableDetail: null),
            RealWorld: realWorld ?? new RealWorldSignal(Hit: false, Hits: []),
            Series: series ?? new SeriesGateContext(Topic.Ghost),
            Length: length ?? new LengthBand(Min: 1600, Max: 1800),
            Attempt: attempt);

    /// <summary>Green version facts: 1700 words (inside 1360–2070), one keyword, one allowed warning, no violation, no sheet.</summary>
    /// <param name="wordCount">The D4 token count.</param>
    /// <param name="keywords">The version's keywords.</param>
    /// <param name="contentWarnings">The declared content warnings.</param>
    /// <param name="keywordViolationCount">How many keywords break the keyword rules, or null when the stored local block is unreadable.</param>
    /// <param name="keywordLimit">The <c>ceil(D × K)</c> cap of the episode's tier.</param>
    /// <param name="factSheetId">The sheet the version was written from.</param>
    /// <param name="approvedFactSheetId">The series' current approved sheet id.</param>
    /// <returns>The version facts.</returns>
    public static StoryVersionFacts Version(
        int wordCount = 1700,
        IReadOnlyList<string>? keywords = null,
        IReadOnlyList<string>? contentWarnings = null,
        int? keywordViolationCount = 0,
        int keywordLimit = 6,
        Guid? factSheetId = null,
        Guid? approvedFactSheetId = null) => new(
            StoryVersionId,
            wordCount,
            keywords ?? GreenKeywords,
            contentWarnings ?? GreenWarnings,
            keywordViolationCount,
            keywordLimit,
            factSheetId,
            approvedFactSheetId);

    /// <summary>Green audit facts: a current review scoring 4 on all six non-true-crime dimensions, no issues, un-flagged, un-revised.</summary>
    /// <param name="modelReviewPresent">Whether <c>model_review</c> is present and readable.</param>
    /// <param name="modelReviewStale">Whether the review's <c>stale</c> flag is set.</param>
    /// <param name="scores">The selected review's D16 scores.</param>
    /// <param name="issues">The selected review's issues plus <c>local.issues</c>.</param>
    /// <param name="moderationFlagged">The draft-time moderation flag.</param>
    /// <param name="revisionApplied">Whether a revision was applied.</param>
    /// <returns>The audit facts.</returns>
    public static AuditFacts Audit(
        bool modelReviewPresent = true,
        bool modelReviewStale = false,
        ReviewScores? scores = null,
        IReadOnlyList<GateIssueFacts>? issues = null,
        bool? moderationFlagged = false,
        bool? revisionApplied = false) => new(
            modelReviewPresent,
            modelReviewStale,
            scores ?? new ReviewScores(Hook: 4, Pacing: 4, Atmosphere: 4, NaturalLanguage: 4, Originality: 4, FactualCare: null, LengthFit: 4),
            issues ?? NoIssues,
            moderationFlagged,
            revisionApplied);

    /// <summary>Audit facts whose every block is absent: checks 1–6 all fail closed.</summary>
    /// <returns>The audit facts.</returns>
    public static AuditFacts AuditAbsent() => new(
        ModelReviewPresent: false,
        ModelReviewStale: true,
        Scores: null,
        Issues: null,
        ModerationFlagged: null,
        RevisionApplied: null);

    /// <summary>A video input whose ten checks all pass: landscape 1920×1080, −15 LUFS, subtitles on and present, no hint.</summary>
    /// <param name="subtitlesEnabled">Whether subtitles were requested.</param>
    /// <param name="aspect">The series aspect.</param>
    /// <param name="imageModerationEvents">The render job's <c>openai_image</c> moderation-event count.</param>
    /// <param name="asset">The stored asset row.</param>
    /// <param name="report">The stored quality report.</param>
    /// <returns>The assembled input.</returns>
    public static VideoGateInput VideoInput(
        bool subtitlesEnabled = true,
        Aspect aspect = Aspect.Landscape,
        int imageModerationEvents = 0,
        AssetFacts? asset = null,
        QualityReportFacts? report = null) => new(
            Report: report ?? Report(),
            RenderJob: new RenderJobFacts(RenderJobId),
            Asset: asset ?? Asset(),
            SubtitlesEnabled: subtitlesEnabled,
            Aspect: aspect,
            ImageModerationEvents: imageModerationEvents);

    /// <summary>The same input with no quality report at all: check 1 and every dependent check fail closed.</summary>
    /// <param name="subtitlesEnabled">Whether subtitles were requested.</param>
    /// <returns>The assembled input.</returns>
    public static VideoGateInput AbsentReportInput(bool subtitlesEnabled = true) => new(
        Report: null,
        RenderJob: new RenderJobFacts(RenderJobId),
        Asset: Asset(),
        SubtitlesEnabled: subtitlesEnabled,
        Aspect: Aspect.Landscape,
        ImageModerationEvents: 0);

    /// <summary>Green quality-report facts: pass, 600 s container and narration, −15 LUFS, 1920×1080, an SRT, the report's own hash, nothing skipped.</summary>
    /// <param name="pass">The report's own verdict.</param>
    /// <param name="durationSec">The container duration.</param>
    /// <param name="ttsDurationSec">The narration duration.</param>
    /// <param name="integratedLufs">The integrated loudness.</param>
    /// <param name="width">The frame width.</param>
    /// <param name="height">The frame height.</param>
    /// <param name="subtitlesPresent">Whether an SRT was measured.</param>
    /// <param name="sha256">The measured file's hash.</param>
    /// <param name="skippedSceneCount">The <c>images.skipped</c> count.</param>
    /// <param name="moderatedImageCount">The <c>images.moderated_count</c>.</param>
    /// <param name="hints">The report's advisory hints.</param>
    /// <returns>The report facts.</returns>
    public static QualityReportFacts Report(
        bool? pass = true,
        double? durationSec = 600,
        double? ttsDurationSec = 600,
        double? integratedLufs = -15,
        int? width = 1920,
        int? height = 1080,
        bool? subtitlesPresent = true,
        string? sha256 = ReportSha,
        int? skippedSceneCount = 0,
        int? moderatedImageCount = 0,
        IReadOnlyList<string>? hints = null) => new(
            pass,
            durationSec,
            ttsDurationSec,
            integratedLufs,
            width,
            height,
            subtitlesPresent,
            sha256,
            skippedSceneCount,
            moderatedImageCount,
            hints ?? NoHints);

    /// <summary>A stored asset row that matches the report.</summary>
    /// <param name="sha256">The stored file's hash.</param>
    /// <param name="durationSec">The stored file's duration.</param>
    /// <returns>The asset facts.</returns>
    public static AssetFacts Asset(string? sha256 = ReportSha, decimal? durationSec = 600m) =>
        new(AssetId, sha256, durationSec);

    /// <summary>The single check with <paramref name="id"/>; a missing or a duplicated id fails the calling test.</summary>
    /// <param name="checks">The result's checks.</param>
    /// <param name="id">The check id.</param>
    /// <returns>The check.</returns>
    public static GateCheck Find(IReadOnlyList<GateCheck> checks, string id)
    {
        var matches = checks.Where(check => check.Id == id).ToList();
        matches.Count.ShouldBe(1, $"expected exactly one '{id}' check");
        return matches[0];
    }
}
