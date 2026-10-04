using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
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

        var root = string.IsNullOrWhiteSpace(auditReportJson)
            ? new JsonObject { ["schema_version"] = SchemaVersion }
            : JsonNode.Parse(auditReportJson) as JsonObject
                ?? throw new JsonException("The audit report is not a JSON object.");

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
}
