using NightTales.Domain.Series;
using NightTales.Domain.Stories.Review;

namespace NightTales.Domain.Stories.Gates;

/// <summary>
/// Everything the story half of the gate evaluates (02.8.2 plan §3.1), assembled by the Application from the
/// stored <c>story_versions</c> row and its <c>audit_report</c>: no I/O, no <c>JsonElement</c> and no
/// Application type crosses into the Domain, so <see cref="StoryGateRules"/> stays pure and testable.
/// </summary>
/// <param name="Version">The evaluated story version's own facts (checks 11–14).</param>
/// <param name="Audit">The audit report's facts (checks 1–6), already selected per 02.6.2-T1.</param>
/// <param name="Similarity">The two similarity scores (checks 9 and 10).</param>
/// <param name="Moderation">
/// The <c>autopilot_gate</c> moderation outcome (check 7), or <see langword="null"/> when the gate had none to
/// read: a null outcome fails the check closed.
/// </param>
/// <param name="RealWorld">
/// The real-world signal (check 8), or <see langword="null"/> when the review's claims could not be read: a null
/// signal fails the check closed.
/// </param>
/// <param name="Series">The series facts (the <c>true_crime</c> hold).</param>
/// <param name="Length">The D4 band of the version's tier × language cell (check 11).</param>
/// <param name="Attempt">The story-gate attempt of the run, 1-based (D17: at most two generations).</param>
public sealed record StoryGateInput(
    StoryVersionFacts Version,
    AuditFacts Audit,
    SimilaritySignals Similarity,
    ModerationGateOutcome? Moderation,
    RealWorldSignal? RealWorld,
    SeriesGateContext Series,
    LengthBand Length,
    int Attempt = 1);

/// <summary>The gate-relevant facts of one <c>story_versions</c> row.</summary>
/// <param name="Id">The version id, carried into <see cref="StoryGateResult.StoryVersionId"/>.</param>
/// <param name="WordCount">The stored <c>word_count</c> = the D4 token count (check 11).</param>
/// <param name="Keywords">The version's keywords; only their count is read (check 12).</param>
/// <param name="ContentWarnings">The <c>content_warnings</c> of the stored content document (check 13).</param>
/// <param name="KeywordViolationCount">
/// How many keywords break <c>KeywordRules</c> (check 12). Read from the stored audit's local issues, so the
/// gate never re-runs the checker.
/// </param>
/// <param name="KeywordLimit">
/// The keyword cap <c>ceil(D × K)</c> of the episode's tier, K = 6 images per minute (P5/F09), computed by the
/// caller from the planned duration.
/// </param>
/// <param name="FactSheetId">The sheet the version was written from, or <see langword="null"/> (check 14).</param>
/// <param name="ApprovedFactSheetId">
/// The series' current approved sheet id, or <see langword="null"/> (check 14). A null on either side fails
/// <c>fact_sheet_current</c>.
/// </param>
public sealed record StoryVersionFacts(
    Guid Id,
    int WordCount,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> ContentWarnings,
    int KeywordViolationCount,
    int KeywordLimit,
    Guid? FactSheetId,
    Guid? ApprovedFactSheetId);

/// <summary>
/// The gate-relevant facts of one <c>audit_report</c> (<c>audit.v1</c>, D22).
/// </summary>
/// <remarks>
/// 02.6.2-T1: when <c>revision.applied</c> is true the current state is review #2, so
/// <paramref name="Scores"/> and <paramref name="Issues"/> must be the ones read from
/// <c>revision.review_after</c>; otherwise they come from <c>model_review</c>. The caller selects the block
/// (the same rule as <c>StoryReadProjection.Review</c>) and maps it here; the rules never choose.
/// </remarks>
/// <param name="ModelReviewPresent">Whether <c>model_review</c> is present and readable (check 1).</param>
/// <param name="ModelReviewStale">
/// Whether <c>model_review.stale</c> is set; a missing flag is stale, so an edited story fails closed (check 1).
/// </param>
/// <param name="Scores">
/// The selected review's D16 scores, or <see langword="null"/> when the block is absent or incomplete: checks 2
/// and 3 then fail with <see cref="GateDetailCodes.FieldAbsent"/>.
/// </param>
/// <param name="Issues">
/// <c>local.issues</c> ∪ the selected review's issues, as (severity, kind) pairs, or <see langword="null"/> when
/// the issues could not be read (check 4 fails closed).
/// </param>
/// <param name="ModerationFlagged">
/// <c>audit_report.moderation.flagged</c>, or <see langword="null"/> when the block is absent (check 6).
/// </param>
/// <param name="RevisionApplied">
/// <c>audit_report.revision.applied</c>, or <see langword="null"/> when the block is absent (check 5).
/// </param>
public sealed record AuditFacts(
    bool ModelReviewPresent,
    bool ModelReviewStale,
    ReviewScores? Scores,
    IReadOnlyList<GateIssueFacts>? Issues,
    bool? ModerationFlagged,
    bool? RevisionApplied);

/// <summary>
/// One issue of <c>local.issues</c> or of the selected review, reduced to the two fields check 4 reads. An issue
/// of the model review carries the kind the server derives from its type (<c>ReviewIssueTypes.KindOf</c>); a
/// local one carries the kind the checker wrote.
/// </summary>
/// <param name="Severity"><c>blocking</c> or <c>advisory</c>.</param>
/// <param name="Kind">
/// <c>objective</c> or <c>taste</c>. A value outside those two is counted as objective when the severity is
/// blocking, so a malformed entry cannot open the gate.
/// </param>
public sealed record GateIssueFacts(string Severity, string Kind);

/// <summary>The two similarity signals of checks 9 and 10 (plan §3.1, §3.4's <c>similarity</c> block).</summary>
/// <param name="Sources">
/// <c>story_versions.similarity_score</c>, the score against the episode's loaded sources (02.5.6), or
/// <see langword="null"/> when it was never computed: the check then fails closed.
/// </param>
/// <param name="PastEpisodes">
/// The similarity against the series' own past episodes, or <see langword="null"/> when the corpus query
/// returned nothing usable: the check then fails closed (plan §3.2 "no pass on unknown").
/// </param>
/// <param name="CorpusMax">How many past episodes the score was computed against (<c>corpus_max</c>).</param>
public sealed record SimilaritySignals(decimal? Sources, decimal? PastEpisodes, int CorpusMax);

/// <summary>
/// The 02.8.2-T2 content gate's verdict over the narration, the metadata and the scene prompts (check 7).
/// </summary>
/// <param name="Flagged">
/// True when a category is above its profile threshold or is blocked outright. Unavailability sets it too, so
/// the check can never pass on an unknown (plan §7).
/// </param>
/// <param name="UnavailableDetail">
/// <see cref="GateDetailCodes.ModeratorUnavailable"/> when the moderator failed after the D7 retry budget; the
/// check fails closed and reports it.
/// </param>
public sealed record ModerationGateOutcome(bool Flagged, string? UnavailableDetail);

/// <summary>The series facts the story gate needs.</summary>
/// <param name="Topic">
/// The series topic. <see cref="Topic.TrueCrime"/> adds checks 14 and 15 and always holds the story.
/// </param>
public sealed record SeriesGateContext(Topic Topic);

/// <summary>
/// The D4 word-count band of one tier × language cell, as the story gate reads it (check 11). The Application
/// maps the resolved price-version limits onto it (the same <c>min_tokens</c>/<c>max_tokens</c> the writer got),
/// so the rule stays pure.
/// </summary>
/// <param name="Min">The lower bound of the D4 band, inclusive.</param>
/// <param name="Max">The upper bound of the D4 band, inclusive.</param>
/// <param name="AdvisoryTolerance">
/// The share outside the band that still counts as a pass (F07/D4: 15 %). It matches
/// <c>StoryQualityThresholds.LengthTolerance</c>, whose Q-LEN-02 advisory is the same rule.
/// </param>
public sealed record LengthBand(int Min, int Max, decimal AdvisoryTolerance = 0.15m);
