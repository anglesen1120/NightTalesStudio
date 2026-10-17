using System.Text;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Keywords;

namespace NightTales.Application.Features.Rendering;

/// <summary>
/// The two content-free mappings of the 03 D10 keyword lint: the linter's blocking issues become the
/// <c>errors[]</c> rows of a <c>keywords_flagged</c> 409, and the approved version's keywords are sanitised
/// before they reach the linter and the planner.
/// </summary>
/// <remarks>
/// <para>
/// Both the approve command (02.3.2-T1) and the render command (09.6.1-T1) run the same lint over the same
/// version with the same corpus, so the 409 body and the sanitiser live here once and the two paths cannot
/// drift apart.
/// </para>
/// <para>
/// Nothing here carries story text: a flag is the D22 segment address, the keyword's index inside that segment
/// and the rule id, and the sanitiser only replaces a noncharacter with a space (SEC2). No keyword and no
/// person's name can therefore reach an error body, a log line or <c>render_jobs.params</c> (03 §8, 02 D19).
/// </para>
/// </remarks>
internal static class KeywordLintMapping
{
    /// <summary>
    /// Maps the linter's blocking issues to the D10 <c>errors[]</c> rows, reading the keyword index the linter
    /// carries in <see cref="ValidationIssue.Evidence"/> back out (03.2.2-T1, CR-18b). No issue text, keyword
    /// or name leaves this method: a row is the D22 segment address, the index and the rule id.
    /// </summary>
    /// <param name="issues">The blocking issues of one lint, in segment and then keyword order.</param>
    /// <returns>One row per issue; a missing index carrier becomes 0.</returns>
    public static KeywordFlag[] ToFlags(IReadOnlyList<ValidationIssue> issues)
    {
        var flags = new KeywordFlag[issues.Count];
        for (var index = 0; index < issues.Count; index++)
        {
            var issue = issues[index];
            _ = RenderKeywordLinter.TryGetKeywordIndex(issue, out var keywordIndex);
            flags[index] = new KeywordFlag(issue.Segment, keywordIndex, issue.RuleId);
        }

        return flags;
    }

    /// <summary>
    /// The segments with every noncharacter replaced by a space in each keyword, so the linter and the planner
    /// never normalise one; ICU throws on a noncharacter (SEC2).
    /// </summary>
    /// <param name="segments">The approved version's ordered scene keywords.</param>
    /// <returns>The same segments with each keyword sanitised.</returns>
    public static List<SegmentKeywords> SanitiseKeywords(IReadOnlyList<SegmentKeywords> segments) =>
        segments.Select(segment => segment with
        {
            Keywords = segment.Keywords.Select(ReplaceNoncharacters).ToList(),
        }).ToList();

    /// <summary>
    /// <paramref name="text"/> with every noncharacter replaced by a space. Unpaired surrogates stay: the Domain
    /// normaliser already replaces them, and a space there would split a token that counts as one today (SEC2).
    /// Returns the input itself when there is no noncharacter.
    /// </summary>
    /// <param name="text">The text to sanitise.</param>
    /// <returns>The text without a noncharacter.</returns>
    public static string ReplaceNoncharacters(string text)
    {
        StringBuilder? builder = null;
        for (var index = 0; index < text.Length; index++)
        {
            var width = char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])
                ? 2
                : 1;
            var codePoint = width == 2 ? char.ConvertToUtf32(text[index], text[index + 1]) : text[index];
            if (BibleOpNormalizer.IsNoncharacter(codePoint))
            {
                builder ??= new StringBuilder(text.Length).Append(text, 0, index);
                builder.Append(' ');
            }
            else
            {
                builder?.Append(text, index, width);
            }

            index += width - 1;
        }

        return builder?.ToString() ?? text;
    }
}
