using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ErrorOr;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Catalog;
using NightTales.Application.Features.Stories.Review;
using NightTales.Domain.Stories.Prompts;

namespace NightTales.Application.Features.Stories.Writing;

public sealed partial class PromptAssembler
{
    private const string ReviewStoryTask = "review_story";
    private const string ReviewStep = "review";
    private const string ReviewKey = "review/v1.md";
    private const string ReviewSchemaKey = "schemas/review.v1.json";

    /// <inheritdoc />
    public ErrorOr<AssembledPrompt> Review(StoryReviewRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PromptPlaceholders.LanguageName] = LanguageName(req.Language),
            [PromptPlaceholders.TierMinutes] = TierMinutes(req.Tier),
            [PromptPlaceholders.TokenRange] = Range(req.Length.MinTokens, req.Length.MaxTokens),
            [PromptPlaceholders.SegmentRange] = Range(req.Length.MinSegments, req.Length.MaxSegments),
            [PromptPlaceholders.EarRules] = catalog.GetShared(PromptFile.EarRules, req.Language),
            [PromptPlaceholders.OverusedPhrases] = catalog.GetShared(PromptFile.Overused, req.Language),
        };
        var systemPrompt = Substitute(catalog.GetStepPrompt(PromptStep.Review), values);

        var promptVersion = catalog.BuildPromptVersion(new PromptVersionParts(SystemKey, req.PresetKey, ReviewSchemaKey, ReviewKey));
        var cacheKey = string.Concat(ReviewStep, ":", Path.GetFileNameWithoutExtension(req.PresetKey));

        var dataJson = JsonSerializer.Serialize(BuildReviewDataObject(req), DataOptions);

        // S4: the review carries the story and its context, so the worst-case estimate bounds it at twice the data budget.
        var limit = 2L * (budget ?? PromptDataBudget.Default).MaxDataObjectBytes;
        if (Encoding.UTF8.GetByteCount(dataJson) > limit)
        {
            LogReviewDataTooLarge(limit);
            return Error.Failure(ErrorCodes.PromptTooLarge, "The review data object exceeds the data budget.");
        }

        return new AssembledPrompt(systemPrompt, dataJson, promptVersion, cacheKey);
    }

    private static ReviewDataObject BuildReviewDataObject(StoryReviewRequest req) =>
        new(
            ReviewStoryTask,
            CatalogWireNames.Spelling(req.Language),
            JsonNamingPolicy.SnakeCaseLower.ConvertName(req.Topic.ToString()),
            req.Tier.ToString().ToLowerInvariant(),
            new ReviewDataLength([req.Length.MinTokens, req.Length.MaxTokens], [req.Length.MinSegments, req.Length.MaxSegments]),
            req.Draft,
            [.. req.Facts.Select(ToReviewData)],
            req.Bible,
            new ReviewDataEarRules([.. req.EarRuleQuestions.Select(question =>
                new ReviewDataQuestion(Guard(question.QuestionId, QuestionIdPattern(), "ear rule question id"), question.Text))]));

    private static ReviewDataFact ToReviewData(FactForWriter fact) =>
        new(Guard(fact.Id, FactIdPattern(), "fact id"), fact.Statement);

    [GeneratedRegex(@"^Q[1-9]\z", RegexOptions.CultureInvariant)]
    private static partial Regex QuestionIdPattern();

    [LoggerMessage(
        EventId = 6001,
        Level = LogLevel.Warning,
        Message = "story.prompt.review-data-too-large limit={Limit}")]
    private partial void LogReviewDataTooLarge(long limit);
}
