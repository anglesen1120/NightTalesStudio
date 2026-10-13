using System.Text;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Catalog;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Application.Features.Stories.Review;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Prompts;
using NightTales.Domain.Stories.Review;

namespace NightTales.Application.Features.Stories.Writing;

public sealed partial class PromptAssembler
{
    private const string ReviseStoryTask = "revise_story";
    private const string RevisionStep = "revision";
    private const string RevisionKey = "revise/v1.md";

    /// <summary>The most issues the revision data object carries; the same cap as <c>review.v1</c> and the review prompt.</summary>
    private const int MaxRevisionIssues = ReviewResultValidator.MaxIssues;

    /// <summary>The longest issue id the revision data object carries; the review's own <c>^[a-z][a-z0-9_]{0,31}$</c> cap.</summary>
    private const int MaxRevisionIssueIdChars = 32;

    /// <summary>The <c>severity</c> value that marks an issue as blocking (02.6.1-T1).</summary>
    private const string Blocking = "blocking";

    /// <inheritdoc />
    public ErrorOr<AssembledPrompt> Revise(StoryRevisionRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PromptPlaceholders.LanguageName] = LanguageName(req.Language),
            [PromptPlaceholders.TierMinutes] = TierMinutes(req.Tier),
            [PromptPlaceholders.TokenRange] = Range(req.Length.MinTokens, req.Length.MaxTokens),
            [PromptPlaceholders.SegmentRange] = Range(req.Length.MinSegments, req.Length.MaxSegments),
            [PromptPlaceholders.EarRules] = catalog.GetShared(PromptFile.EarRules, req.Language),
        };
        var systemPrompt = Substitute(catalog.GetStepPrompt(PromptStep.Revision), values);

        var promptVersion = catalog.BuildPromptVersion(new PromptVersionParts(SystemKey, req.PresetKey, SchemaKey, RevisionKey: RevisionKey));
        var cacheKey = string.Concat(RevisionStep, ":", Path.GetFileNameWithoutExtension(req.PresetKey));

        var dataJson = JsonSerializer.Serialize(BuildRevisionDataObject(req), DataOptions);

        // The worst-case estimate bounds the revision input at three times the data budget — the story, the
        // review findings and their context — so the assembler enforces the same bound (S4).
        var limit = 3L * (budget ?? PromptDataBudget.Default).MaxDataObjectBytes;
        if (Encoding.UTF8.GetByteCount(dataJson) > limit)
        {
            LogRevisionDataTooLarge(limit);
            return Error.Failure(ErrorCodes.PromptTooLarge, "The revision data object exceeds the data budget.");
        }

        return new AssembledPrompt(systemPrompt, dataJson, promptVersion, cacheKey);
    }

    private static RevisionDataObject BuildRevisionDataObject(StoryRevisionRequest req) =>
        new(
            ReviseStoryTask,
            CatalogWireNames.Spelling(req.Language),
            req.Tier.ToString().ToLowerInvariant(),
            new ReviewDataLength([req.Length.MinTokens, req.Length.MaxTokens], [req.Length.MinSegments, req.Length.MaxSegments]),
            [.. BlockingObjectiveIssues(req.Issues).Take(MaxRevisionIssues).Select(ToRevisionData)],
            req.Draft,
            [.. req.Facts.Select(ToReviewData)],
            req.Bible);

    /// <summary>
    /// The issues the revision call may fix (02.6.2-T1): <c>blocking</c> objective ones only. A taste or
    /// advisory issue the caller passed is dropped here as a second guard, and an issue whose type is
    /// outside the closed list is treated as non-objective rather than throwing — a validated review never
    /// carries one, so this only bounds a caller's slip.
    /// </summary>
    private static IEnumerable<ReviewIssueV1> BlockingObjectiveIssues(IReadOnlyList<ReviewIssueV1> issues) =>
        issues.Where(IsBlockingObjective);

    private static bool IsBlockingObjective(ReviewIssueV1 issue) =>
        string.Equals(issue.Severity, Blocking, StringComparison.Ordinal)
        && (string.Equals(issue.Type, ReviewIssueTypes.FactualCareMissing, StringComparison.Ordinal)
            || (ReviewIssueTypes.TryParse(issue.Type, out var type) && ReviewIssueTypes.IsObjective(type)));

    /// <summary>One issue as wire data: every model string is cleaned of C0 controls and lone surrogates and clamped on a code-point boundary.</summary>
    private static RevisionDataIssue ToRevisionData(ReviewIssueV1 issue) =>
        new(
            Clamp(TextSafety.Clean(issue.Id), MaxRevisionIssueIdChars),
            TextSafety.Clean(issue.Type),
            TextSafety.Clean(issue.Segment),
            Clamp(TextSafety.Clean(issue.Evidence), ValidationIssue.MaxEvidenceChars),
            Clamp(TextSafety.Clean(issue.RepairDirection), ReviewResultValidator.MaxFieldChars));

    /// <summary>
    /// Bounds a string to <paramref name="max"/> UTF-16 code units without splitting a surrogate pair, so the
    /// serialised data object never carries a lone high surrogate. Same rule as <see cref="ValidationIssue.Snip"/>,
    /// for the caps that are not the 200-character evidence cap.
    /// </summary>
    private static string Clamp(string value, int max)
    {
        if (value.Length <= max)
        {
            return value;
        }

        var length = max;
        if (char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length]))
        {
            length--;
        }

        return value[..length];
    }

    [LoggerMessage(
        EventId = 6002,
        Level = LogLevel.Warning,
        Message = "story.prompt.revision-data-too-large limit={Limit}")]
    private partial void LogRevisionDataTooLarge(long limit);
}
