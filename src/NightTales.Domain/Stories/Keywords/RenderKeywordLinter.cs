using System.Globalization;
using NightTales.Domain.Stories.Content;

namespace NightTales.Domain.Stories.Keywords;

/// <summary>
/// The render keyword linter of 03 README D10, D11, F10 and G4 (03.2.2-T1): it runs the 02 <see cref="KeywordRules"/>
/// over every keyword of every segment <em>and</em> checks the keyword against the episode's real-person
/// <see cref="NameCorpus"/>, so a scene that names a real victim or suspect is blocked before any image is paid for.
/// Same assembly as the rules, pure: no I/O, no clock, no randomness.
/// </summary>
/// <remarks>
/// <para>
/// The rules run first and per keyword: the first <see cref="KeywordRules"/> violation becomes the blocking objective
/// <see cref="StoryDraftValidator.KeywordInvalid"/> issue of 02 (one error shape the writer already knows), and a
/// keyword that repeats two consecutive tokens of a corpus run then adds the blocking objective
/// <see cref="NameDetected"/> issue (D10, D11). Both carry the 02 D22 segment address of the keyword's segment (02
/// CR-5: <see cref="KeywordRules"/> itself stays untouched; 03.2.2 only adds this check).
/// </para>
/// <para>
/// The corpus check runs for a keyword that passes the rules and for every <see cref="KeywordRules"/> violation
/// except R2 (<see cref="KeywordRules.NotEnglish"/>): a keyword whose only violation is the non-ASCII alphabet
/// rule must be rewritten anyway, so the check is deferred to the next lint. That keeps the R2 path linear and
/// never hands a non-ASCII flood to the corpus.
/// </para>
/// <para>
/// Issues never carry the keyword, the matched name or any source text — only the rule id, the D22 address, the
/// keyword's zero-based index inside its segment and, for the name rule, <c>run:&lt;n tokens&gt;</c> (D11; 03 §8 and
/// 02 D19 keep keywords, titles and fact statements out of logs and params). The index rides as a final
/// <c>;index:&lt;i&gt;</c> token of <see cref="ValidationIssue.Evidence"/> — the <see cref="ValidationIssue"/> record
/// has no index field — and is read back by <see cref="TryGetKeywordIndex"/>; that carrier is how the 03.2.2-T2
/// wiring and 09.6.1 recover D10's <c>errors[].index</c>. The issue list still keeps the order of the caller's
/// <c>segments</c> and, inside a segment, the keyword index, with the <see cref="KeywordRules"/> issue before the
/// name issue of the same keyword.
/// </para>
/// <para>
/// It never throws on a malformed list: a null element, a null keyword list, a null or blank keyword, and an empty
/// corpus are all handled (a null or blank keyword fails <see cref="KeywordRules"/> R1, which is the same issue the
/// validator emits for it).
/// </para>
/// </remarks>
public static class RenderKeywordLinter
{
    /// <summary>D10/D11: the keyword repeats a name from the episode's own research (blocking, objective).</summary>
    public const string NameDetected = "keyword_name_detected";

    /// <summary>
    /// The run's rules for the 2-argument overload: <see cref="StoryRules"/> defaults. One shared, immutable
    /// instance, so a call does not pay for <see cref="StoryRules"/>' length resolution.
    /// </summary>
    private static readonly StoryRules DefaultRules = new();

    /// <summary>
    /// Lints every keyword of every segment against the 02 rules and the corpus, with the default
    /// <see cref="StoryRules"/> (empty known person/place lists).
    /// </summary>
    /// <param name="segments">The keywords per segment, in story order; the caller's order is preserved.</param>
    /// <param name="names">The episode's name corpus, or <see cref="NameCorpus.Empty"/> for fiction or with the
    /// name rule off.</param>
    /// <returns>The blocking issues, in segment order and then keyword index; empty when every keyword passes.</returns>
    public static IReadOnlyList<ValidationIssue> Lint(IReadOnlyList<SegmentKeywords> segments, NameCorpus names) =>
        Lint(segments, names, DefaultRules);

    /// <summary>
    /// Lints every keyword of every segment against the 02 rules and the corpus, with the run's own
    /// <see cref="StoryRules"/>.
    /// </summary>
    /// <param name="segments">The keywords per segment, in story order; the caller's order is preserved.</param>
    /// <param name="names">The episode's name corpus, or <see cref="NameCorpus.Empty"/> for fiction or with the
    /// name rule off.</param>
    /// <param name="rules">The run's rules; <see cref="StoryRules.KnownPersonNames"/> and
    /// <see cref="StoryRules.KnownPlaceNames"/> feed R5 and R12, exactly as in the validator.</param>
    /// <returns>The blocking issues, in segment order and then keyword index; empty when every keyword passes.</returns>
    /// <exception cref="ArgumentNullException">A list or the corpus is null.</exception>
    public static IReadOnlyList<ValidationIssue> Lint(
        IReadOnlyList<SegmentKeywords> segments,
        NameCorpus names,
        StoryRules rules)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(rules);

        var issues = new List<ValidationIssue>();
        foreach (var segment in segments)
        {
            if (segment is null)
            {
                continue;
            }

            var keywords = segment.Keywords;
            if (keywords is null)
            {
                continue;
            }

            var address = segment.Segment ?? string.Empty;
            for (var index = 0; index < keywords.Count; index++)
            {
                var keyword = keywords[index];

                var violation = KeywordRules.Check(keyword, rules);
                if (!violation.IsValid)
                {
                    // The validator's 02 mapping (02.2.1-T1, 02 README D3), with the failing rule id as the
                    // Evidence instead of the quoted keyword: the render path must not carry keyword text
                    // (03 §8, 02 D19), and the D10 error shape needs the rule ids only. The keyword index
                    // rides as a final ;index:<i> token (D10's errors[].index).
                    issues.Add(ValidationIssue.Blocking(
                        KeywordInvalidRule,
                        address,
                        WithKeywordIndex(violation.RuleId, index),
                        KeywordRepairDirection));
                }

                // S4 (skip the corpus check when R2 already rejected the keyword): a keyword that fails the
                // non-ASCII alphabet rule must be rewritten anyway and the check re-runs on the next lint.
                // It also keeps a hostile combining-mark flood out of NameCorpus.Matches' NFD canonical
                // reordering, which is quadratic in the number of combining marks of one word. Any other
                // violation, and a valid keyword (RuleId stays empty), still get the corpus check.
                if (violation.RuleId != KeywordRules.NotEnglish
                    && names.Matches(keyword, out var runTokens))
                {
                    issues.Add(ValidationIssue.Blocking(
                        NameDetected,
                        address,
                        WithKeywordIndex(FormattableString.Invariant($"run:{runTokens}"), index),
                        NameRepairDirection));
                }
            }
        }

        return issues;
    }

    /// <summary>
    /// Reads the keyword index this linter appended to an issue's <see cref="ValidationIssue.Evidence"/> as the
    /// final <c>;index:&lt;i&gt;</c> token — the carrier of D10's <c>errors[].index</c> (the 03.2.2-T2 wiring and
    /// 09.6.1 consume it; a <see cref="ValidationIssue"/> has no index field of its own).
    /// </summary>
    /// <param name="issue">An issue produced by <see cref="Lint(IReadOnlyList{SegmentKeywords}, NameCorpus, StoryRules)"/>.</param>
    /// <param name="index">The zero-based keyword index inside its segment, when <see langword="true"/>; otherwise 0.</param>
    /// <returns><see langword="true"/> for a trailing <c>;index:&lt;n&gt;</c> with invariant digits and a non-negative
    /// <see cref="int"/> value; <see langword="false"/> for a null issue, a missing or negative or non-numeric or
    /// overflowing index, or any trailing junk. It never throws.</returns>
    public static bool TryGetKeywordIndex(ValidationIssue issue, out int index)
    {
        index = 0;
        if (issue is null)
        {
            return false;
        }

        var evidence = issue.Evidence;
        if (evidence is null)
        {
            return false;
        }

        const string marker = ";index:";
        var at = evidence.LastIndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return false;
        }

        var digits = evidence.AsSpan(at + marker.Length);
        if (digits.IsEmpty)
        {
            return false;
        }

        foreach (var ch in digits)
        {
            if (ch is < '0' or > '9')
            {
                return false;
            }
        }

        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    /// <summary>
    /// The Evidence of one keyword issue: the 02 evidence with the keyword's zero-based index appended as the final
    /// <c>;index:&lt;i&gt;</c> token (D10's <c>errors[].index</c>, read back by <see cref="TryGetKeywordIndex"/>).
    /// </summary>
    /// <param name="evidence">The 02 evidence: a <see cref="KeywordRules"/> rule id or <c>run:&lt;n&gt;</c>.</param>
    /// <param name="index">The zero-based keyword index inside its segment.</param>
    /// <returns>The evidence plus the trailing index token. The argument is only ever a rule id (the longest,
    /// <c>keyword_meta_instruction</c>, is 24 chars) or <c>run:&lt;n&gt;</c>, and the suffix is at most 17 chars
    /// (<c>;index:</c> plus ten digits), so the result stays far under
    /// <see cref="ValidationIssue.MaxEvidenceChars"/> (200): the index can never be cut, and
    /// <see cref="ValidationIssue.Blocking"/>'s clamp is a no-op here.</returns>
    private static string WithKeywordIndex(string evidence, int index) =>
        evidence + FormattableString.Invariant($";index:{index}");

    /// <summary>The 02 rule id the validator uses for a keyword that fails <see cref="KeywordRules"/> (one id, one
    /// mapping — 02 CR-5).</summary>
    private const string KeywordInvalidRule = StoryDraftValidator.KeywordInvalid;

    /// <summary>The corrective note of the 02 mapping, unchanged, so the two paths say the same thing.</summary>
    private const string KeywordRepairDirection = "Rewrite the keyword as an English noun phrase of 3 to 12 words.";

    /// <summary>The D11 guidance of the name rule, from the <c>quality.keyword_name_detected</c> copy (plan.md §6).</summary>
    private const string NameRepairDirection = "Describe the place or object instead of naming a real person.";
}
