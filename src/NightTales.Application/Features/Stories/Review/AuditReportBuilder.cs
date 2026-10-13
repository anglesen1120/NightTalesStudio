using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Review;

namespace NightTales.Application.Features.Stories.Review;

/// <summary>
/// Writes the <c>model_review</c> block of the <c>audit.v1</c> document (02 README D22, 02.6.1-T1). Every other
/// key of the document — <c>local</c>, <c>moderation</c>, <c>revision</c>, <c>similarity</c> — is carried over untouched.
/// </summary>
public static class AuditReportBuilder
{
    /// <summary>The <c>schema_version</c> of the document.</summary>
    public const string SchemaVersion = "audit.v1";

    /// <summary>The key of the block this builder owns.</summary>
    public const string ModelReviewKey = "model_review";

    /// <summary>The key of the block this builder writes on a revision (02.6.2-T1, D22).</summary>
    public const string RevisionKey = "revision";

    /// <summary>The platform-emitted type of the drift finding; it is never one of the model's review types.</summary>
    public const string RevisionDriftType = "revision_drift";

    /// <summary>The longest id, segment address or code this builder stores.</summary>
    public const int RevisionIdMaxChars = 64;

    /// <summary><c>revision.reason</c> when the revision output did not pass validation after the retry.</summary>
    public const string RevisionReasonInvalidOutput = "revision_invalid_output";

    /// <summary><c>revision.reason</c> when the spend cap was reached between the review and the revision.</summary>
    public const string RevisionReasonSpendCapReached = "spend_cap_reached";

    /// <summary>
    /// <c>revision.reason</c> when the revision could not run or its result could not be trusted without failing the
    /// run: the provider was unavailable or timed out, the outcome was unknown, the revised draft could not be
    /// moderated, or the run deadline no longer fits the stage (a revision that is unmoderated or unfinished never
    /// becomes current).
    /// </summary>
    public const string RevisionReasonUnavailable = "revision_unavailable";

    /// <summary>
    /// <c>revision.reason</c> when the local moderator flagged the revised draft while the draft it revises was clean
    /// (02.6.2-T1 A1): a flagged revision never replaces a clean original, and its text is not stored, so no
    /// moderation event is written for it either.
    /// </summary>
    public const string RevisionReasonModerationFlagged = "revision_moderation_flagged";

    /// <summary>
    /// <c>revision.reason</c> when review #2 of the revised draft was unusable (invalid, unavailable, spend cap or
    /// moderated): an unreviewed revision never becomes current, so the generated row is kept.
    /// </summary>
    public const string RevisionReasonReviewUnavailable = "revision_review_unavailable";

    /// <summary>The closed set of stable <c>revision.reason</c> codes (charset <c>[a-z0-9_]</c>); a value outside it is rejected.</summary>
    public static IReadOnlySet<string> RevisionReasons { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        RevisionReasonInvalidOutput,
        RevisionReasonSpendCapReached,
        RevisionReasonUnavailable,
        RevisionReasonModerationFlagged,
        RevisionReasonReviewUnavailable,
    };

    private const string DriftImpact = "The revision changed parts of the story the review did not ask it to change.";
    private const string DriftRepairDirection = "Check the changed segments before approving, or regenerate the story.";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>
    /// Sets <c>model_review { model, prompt_version, stale: false, scores, issues, real_world_claims, ear_rule_answers }</c>
    /// on the document, replacing a previous block. <c>kind</c> of each issue is derived from its type, never read from the model.
    /// </summary>
    /// <param name="auditReportJson">The current <c>audit_report</c>; empty or blank starts a new document.</param>
    /// <param name="validated">The outcome of <see cref="ReviewResultValidator.Validate"/>; only an accepted outcome can be merged.</param>
    /// <param name="model">The model that served the review call.</param>
    /// <param name="promptVersion">The composite prompt version of the call.</param>
    /// <returns>The document with the new <c>model_review</c>.</returns>
    /// <exception cref="InvalidOperationException">The outcome is not accepted: a rejected review is never persisted (02.6.1-T1 S1).</exception>
    /// <exception cref="JsonException">The document is not a JSON object.</exception>
    public static string MergeModelReview(string auditReportJson, ValidatedReview validated, string model, string promptVersion)
    {
        ArgumentNullException.ThrowIfNull(validated);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(promptVersion);

        if (!validated.IsAccepted)
        {
            throw new InvalidOperationException(
                $"A rejected review cannot be merged: {string.Join(", ", validated.Rejections.Select(rejection => rejection.RuleId))}.");
        }

        var review = validated.Review;

        var root = Open(auditReportJson);

        root[ModelReviewKey] = new JsonObject
        {
            ["model"] = model,
            ["prompt_version"] = promptVersion,
            ["stale"] = false,
            ["scores"] = Scores(review.Scores),
            ["issues"] = new JsonArray([.. review.Issues.Select(Issue)]),
            ["real_world_claims"] = new JsonArray([.. review.RealWorldClaims.Select(claim =>
                (JsonNode)new JsonObject { ["segment"] = claim.Segment, ["evidence"] = claim.Evidence })]),
            ["ear_rule_answers"] = new JsonArray([.. review.EarRuleAnswers.Select(answer =>
                (JsonNode)new JsonObject
                {
                    ["question_id"] = answer.QuestionId,
                    ["passed"] = answer.Passed,
                    ["segment"] = answer.Segment,
                    ["evidence"] = answer.Evidence,
                })]),
        };

        return root.ToJsonString(WriteOptions);
    }

    /// <summary>
    /// Sets <c>revision { applied: true, fixed_issue_ids, parent_version_id, drift { segment_count_changed,
    /// changed_segments, added_findings }, review_after { scores, issues } }</c> on the document (02.6.2-T1,
    /// D22) and appends the drift finding to <c>review_after.issues</c> as an <c>advisory</c> <c>objective</c>
    /// <c>revision_drift</c> issue. Every other key of the document is carried over untouched; the
    /// <c>revision</c> block is rebuilt, so merging twice is idempotent. The drift is advisory only: it never
    /// blocks and never changes the episode state. Every model string written here is cleaned and length-capped;
    /// <c>fixed_issue_ids</c> is the exception, because an id is not free text — an entry outside
    /// <see cref="ReviewResultValidator"/>'s id contract is dropped, never rewritten into a different id.
    /// </summary>
    /// <param name="auditReportJson">The current <c>audit_report</c>; empty or blank starts a new document.</param>
    /// <param name="parentVersionId">The id of the generated <c>story_versions</c> row the revision descends from.</param>
    /// <param name="fixedIssueIds">The ids of the review issues the revision call was asked to fix.</param>
    /// <param name="reviewAfter">The accepted outcome of review #2 (hints only).</param>
    /// <param name="drift">The segment-drift check of the revision.</param>
    /// <returns>The document with the new <c>revision</c> block.</returns>
    /// <exception cref="InvalidOperationException">Review #2 is not accepted: a rejected review is never persisted.</exception>
    /// <exception cref="JsonException">The document is not a JSON object.</exception>
    public static string MergeRevision(
        string auditReportJson,
        Guid parentVersionId,
        IReadOnlyList<string> fixedIssueIds,
        ValidatedReview reviewAfter,
        RevisionDrift drift)
    {
        ArgumentNullException.ThrowIfNull(fixedIssueIds);
        ArgumentNullException.ThrowIfNull(reviewAfter);
        ArgumentNullException.ThrowIfNull(drift);

        if (!reviewAfter.IsAccepted)
        {
            throw new InvalidOperationException(
                $"A rejected review cannot be merged: {string.Join(", ", reviewAfter.Rejections.Select(rejection => rejection.RuleId))}.");
        }

        var review = CleanReview(reviewAfter.Review);

        var issues = new JsonArray([.. review.Issues.Select(Issue)]);
        if (drift.HasDrift)
        {
            issues.Add(DriftIssue(drift));
        }

        var root = Open(auditReportJson);

        root[RevisionKey] = new JsonObject
        {
            ["applied"] = true,
            ["fixed_issue_ids"] = FixedIssueIds(fixedIssueIds),
            ["parent_version_id"] = parentVersionId.ToString("D"),
            ["drift"] = new JsonObject
            {
                ["segment_count_changed"] = drift.SegmentCountChanged,
                ["changed_segments"] = Ids(drift.ChangedSegments),
                ["added_findings"] = Ids(drift.AddedFindings),
            },
            ["review_after"] = new JsonObject
            {
                ["scores"] = Scores(review.Scores),
                ["issues"] = issues,
            },
        };

        return root.ToJsonString(WriteOptions);
    }

    /// <summary>
    /// Sets <c>revision { applied: false, reason }</c> on the document (02.6.2-T1, D22): the revision was not
    /// applied — its output was invalid, or the spend cap was reached — and the generated row stays current.
    /// Every other key of the document is carried over untouched; merging twice is idempotent.
    /// </summary>
    /// <param name="auditReportJson">The current <c>audit_report</c>; empty or blank starts a new document.</param>
    /// <param name="reason">A code of <see cref="RevisionReasons"/>.</param>
    /// <returns>The document with the new <c>revision</c> block.</returns>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is not a stable code of <see cref="RevisionReasons"/>.</exception>
    /// <exception cref="JsonException">The document is not a JSON object.</exception>
    public static string MergeRevisionNotApplied(string auditReportJson, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var code = Cap(Clean(reason), RevisionIdMaxChars);
        if (!RevisionReasons.Contains(code))
        {
            throw new ArgumentException($"The revision reason '{code}' is not a stable code.", nameof(reason));
        }

        var root = Open(auditReportJson);

        root[RevisionKey] = new JsonObject
        {
            ["applied"] = false,
            ["reason"] = code,
        };

        return root.ToJsonString(WriteOptions);
    }

    private static JsonObject Open(string auditReportJson) =>
        string.IsNullOrWhiteSpace(auditReportJson)
            ? new JsonObject { ["schema_version"] = SchemaVersion }
            : JsonNode.Parse(auditReportJson) as JsonObject
                ?? throw new JsonException("The audit report is not a JSON object.");

    private static JsonObject Scores(ReviewScores scores) => new()
    {
        ["hook"] = scores.Hook,
        ["pacing"] = scores.Pacing,
        ["atmosphere"] = scores.Atmosphere,
        ["natural_language"] = scores.NaturalLanguage,
        ["originality"] = scores.Originality,
        ["factual_care"] = scores.FactualCare,
        ["length_fit"] = scores.LengthFit,
    };

    private static JsonNode Issue(ReviewIssueV1 issue) => new JsonObject
    {
        ["id"] = issue.Id,
        ["severity"] = issue.Severity,
        ["kind"] = ReviewIssueTypes.KindOf(issue.Type) == IssueKind.Objective ? "objective" : "taste",
        ["segment"] = issue.Segment,
        ["evidence"] = issue.Evidence,
        ["impact"] = issue.Impact,
        ["repair_direction"] = issue.RepairDirection,
    };

    private static JsonArray Ids(IReadOnlyList<string> values) =>
        new([.. values.Select(value => (JsonNode)JsonValue.Create(Cap(Clean(value), RevisionIdMaxChars))!)]);

    /// <summary>
    /// <c>fixed_issue_ids</c> carries ids, not free text, so an entry outside
    /// <see cref="ReviewResultValidator"/>'s id contract (the shape of every review issue id) is dropped:
    /// cleaning it would produce a different id that matches no issue and reads as a fabricated one.
    /// </summary>
    /// <param name="ids">The ids of the issues the revision was asked to fix.</param>
    /// <returns>The array of the ids the contract accepts.</returns>
    private static JsonArray FixedIssueIds(IReadOnlyList<string> ids) =>
        new([.. ids.Where(ReviewResultValidator.IsIssueId).Select(value => (JsonNode)JsonValue.Create(value)!)]);

    private static StoryReviewV1 CleanReview(StoryReviewV1 review) =>
        review with { Issues = [.. review.Issues.Select(CleanIssue)] };

    private static ReviewIssueV1 CleanIssue(ReviewIssueV1 issue) => issue with
    {
        Id = Cap(Clean(issue.Id), RevisionIdMaxChars),
        Segment = Cap(Clean(issue.Segment), RevisionIdMaxChars),
        Evidence = Cap(Clean(issue.Evidence), ValidationIssue.MaxEvidenceChars),
        Impact = Cap(Clean(issue.Impact), ReviewResultValidator.MaxFieldChars),
        RepairDirection = Cap(Clean(issue.RepairDirection), ReviewResultValidator.MaxFieldChars),
    };

    private static JsonObject DriftIssue(RevisionDrift drift) => new JsonObject
    {
        ["type"] = RevisionDriftType,
        ["severity"] = "advisory",
        ["kind"] = "objective",
        ["segment"] = Cap(Clean(drift.ChangedSegments.Count > 0 ? drift.ChangedSegments[0] : SegmentAddress.Meta), RevisionIdMaxChars),
        ["evidence"] = Cap(Clean(DriftEvidence(drift)), ValidationIssue.MaxEvidenceChars),
        ["impact"] = DriftImpact,
        ["repair_direction"] = DriftRepairDirection,
    };

    private static string DriftEvidence(RevisionDrift drift)
    {
        var parts = new List<string>(3);
        if (drift.SegmentCountChanged)
        {
            parts.Add("segment count changed");
        }

        if (drift.ChangedSegments.Count > 0)
        {
            parts.Add("changed segments: " + string.Join(", ", drift.ChangedSegments));
        }

        if (drift.AddedFindings.Count > 0)
        {
            parts.Add("new blocking findings: " + string.Join(", ", drift.AddedFindings));
        }

        return parts.Count == 0 ? "no drift" : string.Join("; ", parts);
    }

    // Nothing from the model reaches jsonb uncleaned (02.2.2-T3 failure class): strip C0 controls and lone surrogates.
    private static string Clean(string? value) => TextSafety.Clean(value);

    private static string Cap(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..SafeLength(value, maxChars)];

    // Never split a surrogate pair when cutting: a dangling high surrogate would be unstorable.
    private static int SafeLength(string value, int maxChars) =>
        char.IsHighSurrogate(value[maxChars - 1]) ? maxChars - 1 : maxChars;
}
