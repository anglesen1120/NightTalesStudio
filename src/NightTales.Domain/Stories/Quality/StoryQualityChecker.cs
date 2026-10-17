using System.Globalization;
using System.Text;
using NightTales.Domain.Episodes;
using NightTales.Domain.Rendering.Images;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Keywords;

namespace NightTales.Domain.Stories.Quality;

/// <summary>
/// The story quality rules of 02.3.4-T1 (02 README D21): one pure service that turns a <c>story.v1</c> draft plus
/// its <see cref="StoryCheckContext"/> into a deterministic, ordered <see cref="QualityReport"/>. No I/O, no clock, no
/// randomness; issues are ordered by rule id (ordinal), then segment position, so <c>audit_report.local.issues</c>
/// is stable and diffable. The report carries our own draft fragments (≤ 200 chars) and numbers only, never
/// source text, fact statements or URLs. The checker itself never gates approve: it reports what the UI reads,
/// including the 03 D10/D11 blocking objective Q-KW-05 real-name rule and the 03 CR-14 advisory plan hints.
/// </summary>
/// <remarks>
/// It never throws on a malformed draft: every rule treats a null segment, list or text as empty. The
/// markup and metadata rules reuse <see cref="StoryDraftValidator"/> (one definition of D3), the length rules
/// read the same token rule as <see cref="StoryText"/>, and the real-name rule reuses
/// <see cref="RenderKeywordLinter"/> over <see cref="StoryCheckContext.Names"/> (Q-KW-05). Blocking authority
/// stays with <see cref="StoryDraftValidator"/>, the D12 moderation gate, the true-crime gates and the linter
/// step of approve and render.
/// </remarks>
public sealed class StoryQualityChecker
{
    /// <summary>The token count of one shingle (Q-REP-02).</summary>
    public const int ShingleSize = 8;

    /// <summary>Length is more than the tolerance outside the D4 token band.</summary>
    public const string LengthFarOutOfRange = "Q-LEN-01";

    /// <summary>Length is inside the tolerance but outside the D4 token band.</summary>
    public const string LengthOutOfRange = "Q-LEN-02";

    /// <summary>Segment count is outside the D4 band.</summary>
    public const string SegmentCountOutOfRange = "Q-SEG-01";

    /// <summary>The hook is empty or too short.</summary>
    public const string HookMissing = "Q-HOOK-01";

    /// <summary>The hook has too many sentences.</summary>
    public const string HookTooLong = "Q-HOOK-02";

    /// <summary>A segment's keyword count is outside the per-segment range.</summary>
    public const string KeywordCountPerSegment = "Q-KW-01";

    /// <summary>The keyword total exceeds <c>ceil(D × K)</c>.</summary>
    public const string KeywordTotalAboveCap = "Q-KW-02";

    /// <summary>A keyword fails <see cref="KeywordRules"/>.</summary>
    public const string KeywordInvalid = "Q-KW-03";

    /// <summary>A keyword repeats across segments.</summary>
    public const string KeywordDuplicate = "Q-KW-04";

    /// <summary>A keyword repeats two consecutive tokens of a real-person name from the episode's own research (03 D11).</summary>
    public const string KeywordNameDetected = "Q-KW-05";

    /// <summary>A long sentence repeats.</summary>
    public const string RepeatedSentence = "Q-REP-01";

    /// <summary>Too many 8-token shingles repeat inside the draft.</summary>
    public const string RepetitivePhrasing = "Q-REP-02";

    /// <summary>True crime: a capitalised pair that no source title or known name explains.</summary>
    public const string PossibleRealName = "Q-NAME-01";

    /// <summary>True crime: a non-setup paragraph cites no fact.</summary>
    public const string FactRefsMissing = "Q-FACT-01";

    /// <summary>Title, description or tags break a D3 limit.</summary>
    public const string MetadataInvalid = "Q-META-01";

    /// <summary>A segment text carries markdown, brackets, list markers or a URL.</summary>
    public const string FormattingInvalid = "Q-MD-01";

    /// <summary>Moderation flagged a D12 blocking category, or no moderation verdict is available (fails closed).</summary>
    public const string ModerationBlocked = "Q-MOD-01";

    /// <summary>The similarity verdict is <see cref="SimilarityVerdict.Warn"/>.</summary>
    public const string SimilarityWarn = "Q-SIM-01";

    /// <summary>The similarity verdict is <see cref="SimilarityVerdict.Block"/>.</summary>
    public const string SimilarityBlocked = "Q-SIM-02";

    /// <summary>The model review is stale (F25).</summary>
    public const string ModelReviewStale = "Q-REVIEW-01";

    /// <summary>The episode has no length tier.</summary>
    public const string LengthTierMissing = "Q-TIER-01";

    /// <summary>The render plan trimmed scenes (03 CR-14, <c>scenes_trimmed</c>).</summary>
    public const string ScenesTrimmed = "Q-SCENE-01";

    /// <summary>The render plan is sparse (03 CR-14, <c>scenes_sparse</c>).</summary>
    public const string ScenesSparse = "Q-SCENE-02";

    /// <summary>
    /// The category recorded when the draft-stage moderation flagged the text but named no category. It fails closed: an
    /// uncategorised flag blocks approval like a blocking category.
    /// </summary>
    public const string UnspecifiedModerationCategory = "unspecified";

    /// <summary>The D12 draft-stage categories that block approval on the human path.</summary>
    public static readonly IReadOnlySet<string> BlockingModerationCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "sexual/minors",
        "self-harm/instructions",
        "illicit/violent",
        "hate/threatening",
        UnspecifiedModerationCategory,
    };

    /// <summary>Every rule id with the i18n key its message uses; the UI's <c>quality</c> files must define each key.</summary>
    public static readonly IReadOnlyDictionary<string, string> MessageKeys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [LengthFarOutOfRange] = "quality.length_far_out_of_range",
        [LengthOutOfRange] = "quality.length_out_of_range",
        [SegmentCountOutOfRange] = "quality.segment_count_out_of_range",
        [HookMissing] = "quality.hook_missing",
        [HookTooLong] = "quality.hook_too_long",
        [KeywordCountPerSegment] = "quality.keyword_count_per_segment",
        [KeywordTotalAboveCap] = "quality.keyword_total_above_cap",
        [KeywordInvalid] = "quality.keyword_invalid",
        [KeywordDuplicate] = "quality.keyword_duplicate",
        [KeywordNameDetected] = "quality.keyword_name_detected",
        [RepeatedSentence] = "quality.repeated_sentence",
        [RepetitivePhrasing] = "quality.repetitive_phrasing",
        [PossibleRealName] = "quality.possible_real_name",
        [FactRefsMissing] = "quality.fact_refs_missing",
        [MetadataInvalid] = "quality.metadata_invalid",
        [FormattingInvalid] = "quality.formatting_invalid",
        [ModerationBlocked] = "quality.moderation_blocked",
        [SimilarityWarn] = "quality.similarity_warn",
        [SimilarityBlocked] = "quality.similarity_blocked",
        [ModelReviewStale] = "quality.model_review_stale",
        [LengthTierMissing] = "quality.length_tier_missing",
        [ScenesTrimmed] = "quality.scenes_trimmed",
        [ScenesSparse] = "quality.scenes_sparse",
    };

    private const int EvidenceMax = ValidationIssue.MaxEvidenceChars;

    /// <summary>Common capitalised sentence openers that are not the first half of a name (Q-NAME-01 heuristic).</summary>
    private static readonly HashSet<string> SentenceStarters = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "then", "but", "and", "so", "when", "while", "after", "before", "as", "if", "it", "he", "she",
        "they", "we", "i", "you", "this", "that", "there", "here", "now", "later", "soon", "once", "suddenly", "nobody",
        "everyone", "someone", "no", "not", "one", "in", "on", "at", "by", "with", "for", "from", "to", "of",
    };

    private static readonly HashSet<string> MetadataRuleIds = new(StringComparer.Ordinal)
    {
        StoryDraftValidator.TitleTooLong,
        StoryDraftValidator.MetadataHasMarkup,
        StoryDraftValidator.DescriptionTooLong,
        StoryDraftValidator.TagsInvalid,
    };

    private readonly StoryQualityThresholds _thresholds;

    /// <summary>Creates a checker with the rule-table defaults.</summary>
    public StoryQualityChecker()
        : this(StoryQualityThresholds.Defaults)
    {
    }

    /// <summary>Creates a checker with explicit thresholds.</summary>
    /// <param name="thresholds">The config-driven thresholds.</param>
    public StoryQualityChecker(StoryQualityThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        _thresholds = thresholds;
    }

    /// <summary>Checks one draft.</summary>
    /// <param name="content">The <c>story.v1</c> draft; it is not modified.</param>
    /// <param name="ctx">The check context.</param>
    /// <returns>The ordered issues and the counters; never throws for a malformed draft.</returns>
    public QualityReport Check(StoryContentV1 content, StoryCheckContext ctx)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(ctx);

        var segments = Segments(content);
        var paragraphCount = segments.Count - 2;
        var tokens = 0;
        var keywordTotal = 0;
        foreach (var segment in segments)
        {
            tokens += StoryText.CountTokens(segment.Segment?.Text);
            keywordTotal += segment.Segment?.Keywords?.Count ?? 0;
        }

        var issues = new List<(QualityIssue Issue, int Order)>();

        CheckLength(ctx, tokens, paragraphCount + 2, issues);
        CheckHook(content, issues);
        CheckKeywords(segments, ctx, keywordTotal, issues);
        CheckRenderKeywordNames(segments, ctx, issues);
        CheckRepeatedSentences(segments, issues);
        var shingleShare = CheckShingles(segments, issues);
        CheckTrueCrime(segments, ctx, issues);
        CheckValidatorBackedRules(content, ctx, issues);
        CheckContext(ctx, issues);

        var ordered = issues
            .OrderBy(entry => entry.Issue.RuleId, StringComparer.Ordinal)
            .ThenBy(entry => entry.Order)
            .Select(entry => entry.Issue)
            .ToList();

        return new QualityReport(
            ordered,
            new QualityCounters(tokens, paragraphCount + 2, keywordTotal, paragraphCount, ctx.SimilarityScore, shingleShare));
    }

    private static List<SegmentView> Segments(StoryContentV1 content)
    {
        var list = new List<SegmentView> { new(SegmentAddress.Hook, 1, content.Hook, false) };
        var paragraphs = content.Paragraphs ?? [];
        for (var index = 0; index < paragraphs.Count; index++)
        {
            list.Add(new SegmentView(SegmentAddress.Paragraph(index), 2 + index, paragraphs[index], true));
        }

        list.Add(new SegmentView(SegmentAddress.Ending, 1_000_000, content.Ending, false));
        return list;
    }

    private const int MetaOrder = 2_000_000;

    private static readonly System.Text.RegularExpressions.Regex UrlPattern = new(
        @"(?:https?://|www\.)\S*",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static string Bound(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return ValidationIssue.Snip(value);
    }

    private static QualityIssue Make(
        string ruleId,
        QualitySeverity severity,
        QualityKind kind,
        string segment,
        string evidence,
        string? repair,
        decimal? measured = null,
        decimal? threshold = null) =>
        new(ruleId, severity, kind, segment, MessageKeys[ruleId], Bound(evidence), repair, measured, threshold);

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    private static int TierMaxMinutes(LengthTier tier) => tier switch
    {
        LengthTier.Short => 1,
        LengthTier.Standard => 8,
        LengthTier.Long => 15,
        _ => 0,
    };

    private void CheckLength(StoryCheckContext ctx, int tokens, int segmentCount, List<(QualityIssue, int)> issues)
    {
        if (ctx.Tier is null)
        {
            issues.Add((Make(LengthTierMissing, QualitySeverity.Advisory, QualityKind.Objective, SegmentAddress.Meta, "no length tier", "Choose a length tier for the episode."), MetaOrder));
            return;
        }

        if (ctx.Length is not { } length)
        {
            return;
        }

        var tolerance = (decimal)_thresholds.LengthTolerance;
        var lowFar = length.MinTokens * (1m - tolerance);
        var highFar = length.MaxTokens * (1m + tolerance);
        if (tokens < lowFar || tokens > highFar)
        {
            var bound = tokens < lowFar ? lowFar : highFar;
            issues.Add((Make(
                LengthFarOutOfRange,
                QualitySeverity.Blocking,
                QualityKind.Objective,
                SegmentAddress.Meta,
                Inv($"{tokens} tokens, band {length.MinTokens}-{length.MaxTokens}"),
                "Reach the token range of this tier: add a scene beat, never filler.",
                tokens,
                bound), MetaOrder));
        }
        else if (tokens < length.MinTokens || tokens > length.MaxTokens)
        {
            issues.Add((Make(
                LengthOutOfRange,
                QualitySeverity.Advisory,
                QualityKind.Objective,
                SegmentAddress.Meta,
                Inv($"{tokens} tokens, band {length.MinTokens}-{length.MaxTokens}"),
                "Move the length into the token range of this tier.",
                tokens,
                tokens < length.MinTokens ? length.MinTokens : length.MaxTokens), MetaOrder));
        }

        if (segmentCount < length.MinSegments || segmentCount > length.MaxSegments)
        {
            issues.Add((Make(
                SegmentCountOutOfRange,
                QualitySeverity.Blocking,
                QualityKind.Objective,
                SegmentAddress.Meta,
                Inv($"{segmentCount} segments, band {length.MinSegments}-{length.MaxSegments}"),
                "Use the segment count of this tier.",
                segmentCount,
                segmentCount < length.MinSegments ? length.MinSegments : length.MaxSegments), MetaOrder));
        }
    }

    private void CheckHook(StoryContentV1 content, List<(QualityIssue, int)> issues)
    {
        var hookTokens = StoryText.CountTokens(content.Hook?.Text);
        if (hookTokens < _thresholds.MinHookTokens)
        {
            issues.Add((Make(
                HookMissing,
                QualitySeverity.Blocking,
                QualityKind.Objective,
                SegmentAddress.Hook,
                Inv($"{hookTokens} tokens"),
                "Write an opening hook.",
                hookTokens,
                _thresholds.MinHookTokens), 1));
        }

        var sentences = StoryText.SplitSentences(content.Hook?.Text).Count;
        if (sentences > _thresholds.MaxHookSentences)
        {
            issues.Add((Make(
                HookTooLong,
                QualitySeverity.Advisory,
                QualityKind.Objective,
                SegmentAddress.Hook,
                Inv($"{sentences} sentences"),
                "Cut the hook to three sentences or fewer.",
                sentences,
                _thresholds.MaxHookSentences), 1));
        }
    }

    private void CheckKeywords(List<SegmentView> segments, StoryCheckContext ctx, int keywordTotal, List<(QualityIssue, int)> issues)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var reportedDuplicates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var view in segments)
        {
            var keywords = view.Segment?.Keywords ?? [];
            if (keywords.Count < _thresholds.MinKeywordsPerSegment || keywords.Count > _thresholds.MaxKeywordsPerSegment)
            {
                issues.Add((Make(
                    KeywordCountPerSegment,
                    QualitySeverity.Advisory,
                    QualityKind.Objective,
                    view.Address,
                    Inv($"{keywords.Count} keywords"),
                    keywords.Count < _thresholds.MinKeywordsPerSegment ? "Add 1-2 keywords." : "Cut to 1-2 keywords.",
                    keywords.Count,
                    keywords.Count < _thresholds.MinKeywordsPerSegment ? _thresholds.MinKeywordsPerSegment : _thresholds.MaxKeywordsPerSegment), view.Order));
            }

            foreach (var keyword in keywords)
            {
                var violation = KeywordRules.Check(keyword, ctx.Rules);
                if (!violation.IsValid)
                {
                    issues.Add((Make(
                        KeywordInvalid,
                        QualitySeverity.Advisory,
                        QualityKind.Objective,
                        view.Address,
                        Inv($"'{ValidationIssue.Snip(keyword)}' ({violation.RuleId})"),
                        "Rewrite the keyword as an English noun phrase of 3 to 12 words."), view.Order));
                }

                var key = StoryText.Normalize(keyword).Trim().ToLowerInvariant();
                if (key.Length == 0)
                {
                    continue;
                }

                if (seen.TryAdd(key, view.Address) || seen[key] == view.Address)
                {
                    continue;
                }

                if (reportedDuplicates.Add(key))
                {
                    issues.Add((Make(
                        KeywordDuplicate,
                        QualitySeverity.Advisory,
                        QualityKind.Objective,
                        SegmentAddress.Meta,
                        Inv($"'{ValidationIssue.Snip(keyword)}' repeats"),
                        "Use a different keyword for each segment."), MetaOrder));
                }
            }
        }

        if (ctx.Tier is { } tier)
        {
            var cap = (int)Math.Ceiling(TierMaxMinutes(tier) * (double)_thresholds.KeywordsPerMinute);
            if (keywordTotal > cap)
            {
                issues.Add((Make(
                    KeywordTotalAboveCap,
                    QualitySeverity.Advisory,
                    QualityKind.Objective,
                    SegmentAddress.Meta,
                    Inv($"{keywordTotal} keywords, cap {cap}"),
                    "Cut keywords so the scene budget is not trimmed.",
                    keywordTotal,
                    cap), MetaOrder));
            }
        }
    }

    private static void CheckRenderKeywordNames(List<SegmentView> segments, StoryCheckContext ctx, List<(QualityIssue, int)> issues)
    {
        // 03 D10/D11 and CR-14: the render linter runs over the draft's keywords in story order. Only its name rule
        // becomes a quality issue: the 02 KeywordRules condition is already Q-KW-03 (advisory) here and blocks the
        // save in the validator, so re-emitting keyword_invalid would duplicate it with a different severity. The
        // linter's Evidence (a rule id, or run:<n>, plus ;index:<i>) is carried verbatim — never keyword or name
        // text (03 §8, 02 D19).
        var keywords = new List<SegmentKeywords>(segments.Count);
        var orders = new Dictionary<string, int>(segments.Count, StringComparer.Ordinal);
        foreach (var view in segments)
        {
            keywords.Add(new SegmentKeywords(view.Address, view.Segment?.Keywords ?? []));
            orders[view.Address] = view.Order;
        }

        // The approve and render paths lint with the default rules (the 2-argument overload); this call passes the
        // run's rules, but the rules reach it only through the R2 alphabet skip, and that alphabet is fixed, so the
        // name verdicts read below cannot drift from the ones those two callers see.
        foreach (var linted in RenderKeywordLinter.Lint(keywords, ctx.Names, ctx.Rules))
        {
            if (linted.RuleId != RenderKeywordLinter.NameDetected)
            {
                continue;
            }

            // The rule id, message and severity are Q-KW-05's own; the D22 address and the Evidence come from the
            // linter, so the chip, D10's errors[] and this issue agree on segment and keyword index.
            issues.Add((Make(
                KeywordNameDetected,
                QualitySeverity.Blocking,
                QualityKind.Objective,
                linted.Segment,
                linted.Evidence,
                linted.RepairDirection),
                orders.TryGetValue(linted.Segment, out var order) ? order : MetaOrder));
        }
    }

    private void CheckRepeatedSentences(List<SegmentView> segments, List<(QualityIssue, int)> issues)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var view in segments)
        {
            foreach (var sentence in StoryText.SplitSentences(view.Segment?.Text))
            {
                var tokens = NormalizedTokens(sentence);
                if (tokens.Count < _thresholds.RepeatedSentenceMinTokens)
                {
                    continue;
                }

                if (!seen.Add(string.Join(' ', tokens)))
                {
                    issues.Add((Make(
                        RepeatedSentence,
                        QualitySeverity.Advisory,
                        QualityKind.Objective,
                        view.Address,
                        ValidationIssue.Snip(sentence),
                        "Rewrite or remove the repeated sentence.",
                        tokens.Count,
                        _thresholds.RepeatedSentenceMinTokens), view.Order));
                }
            }
        }
    }

    private const ulong WindowBase = 1_099_511_628_211UL;

    /// <summary>WindowBase^(ShingleSize-1): the weight of the oldest token of a full window.</summary>
    private static readonly ulong WindowLeadWeight = LeadWeight();

    private static ulong LeadWeight()
    {
        ulong weight = 1;
        unchecked
        {
            for (var index = 1; index < ShingleSize; index++)
            {
                weight *= WindowBase;
            }
        }

        return weight;
    }

    /// <summary>FNV-1a over the token's UTF-16 units; the 64-bit window hashes stand in for the joined shingle text.</summary>
    private static ulong TokenHash(string token)
    {
        var hash = 14_695_981_039_346_656_037UL;
        unchecked
        {
            foreach (var character in token)
            {
                hash = (hash ^ character) * 1_099_511_628_211UL;
            }
        }

        return hash;
    }

    private double? CheckShingles(List<SegmentView> segments, List<(QualityIssue, int)> issues)
    {
        var seen = new HashSet<ulong>();
        var total = 0;
        var repeated = 0;
        SegmentView? worst = null;
        var worstRepeated = 0;
        foreach (var view in segments)
        {
            var tokens = NormalizedTokens(view.Segment?.Text);
            var segmentRepeated = 0;
            var hashes = new ulong[tokens.Count];
            for (var index = 0; index < hashes.Length; index++)
            {
                hashes[index] = TokenHash(tokens[index]);
            }

            ulong window = 0;
            unchecked
            {
                for (var index = 0; index < hashes.Length; index++)
                {
                    window = index >= ShingleSize
                        ? ((window - (hashes[index - ShingleSize] * WindowLeadWeight)) * WindowBase) + hashes[index]
                        : (window * WindowBase) + hashes[index];
                    if (index < ShingleSize - 1)
                    {
                        continue;
                    }

                    total++;
                    if (!seen.Add(window))
                    {
                        repeated++;
                        segmentRepeated++;
                    }
                }
            }

            if (segmentRepeated > worstRepeated)
            {
                worstRepeated = segmentRepeated;
                worst = view;
            }
        }

        if (total == 0)
        {
            return null;
        }

        var share = (double)repeated / total;
        if (share > _thresholds.MaxInternalShingleShare && worst is { } at)
        {
            issues.Add((Make(
                RepetitivePhrasing,
                QualitySeverity.Advisory,
                QualityKind.Objective,
                at.Address,
                Inv($"{share.ToString("0.00", CultureInfo.InvariantCulture)} of phrases repeat"),
                "Vary the phrasing.",
                (decimal)share,
                (decimal)_thresholds.MaxInternalShingleShare), at.Order));
        }

        return share;
    }

    private void CheckTrueCrime(List<SegmentView> segments, StoryCheckContext ctx, List<(QualityIssue, int)> issues)
    {
        if (ctx.Topic != Topic.TrueCrime)
        {
            return;
        }

        if (_thresholds.NameHeuristic)
        {
            var titles = (ctx.FactSheetTitles ?? [])
                .Select(title => StoryText.Normalize(title).ToLowerInvariant())
                .ToList();
            foreach (var view in segments)
            {
                var reported = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pair in CapitalisedPairs(view.Segment?.Text))
                {
                    var lowered = pair.ToLowerInvariant();
                    if (!reported.Add(lowered) || IsExplained(pair, lowered, titles, ctx.Rules))
                    {
                        continue;
                    }

                    issues.Add((Make(
                        PossibleRealName,
                        QualitySeverity.Advisory,
                        QualityKind.Taste,
                        view.Address,
                        ValidationIssue.Snip(pair),
                        "Check the name against the sources; do not name a real person."), view.Order));
                }
            }
        }

        if (ctx.FactCount >= 1)
        {
            foreach (var view in segments)
            {
                if (view.IsParagraph
                    && !string.Equals(view.Segment?.Beat, "setup", StringComparison.Ordinal)
                    && (view.Segment?.FactRefs ?? []).Count == 0)
                {
                    issues.Add((Make(
                        FactRefsMissing,
                        QualitySeverity.Advisory,
                        QualityKind.Objective,
                        view.Address,
                        "no fact_refs",
                        "Cite the fact this paragraph states, or fold it into a cited one."), view.Order));
                }
            }
        }
    }

    private static bool IsExplained(string pair, string lowered, List<string> titles, StoryRules rules)
    {
        foreach (var title in titles)
        {
            if (title.Contains(lowered, StringComparison.Ordinal))
            {
                return true;
            }
        }

        var parts = pair.Split(' ');
        return parts.Any(part => rules.KnownPersonNames.Contains(part) || rules.KnownPlaceNames.Contains(part))
            || rules.KnownPersonNames.Contains(pair)
            || rules.KnownPlaceNames.Contains(pair);
    }

    private static List<string> CapitalisedPairs(string? text)
    {
        var pairs = new List<string>();
        foreach (var sentence in StoryText.SplitSentences(text))
        {
            var tokens = StoryText.Tokenize(sentence);
            for (var index = 0; index + 1 < tokens.Count; index++)
            {
                var first = index == 0 && SentenceStarters.Contains(tokens[index].Trim('.', ',', '"').ToLowerInvariant())
                    ? null
                    : Capitalised(tokens[index]);
                var second = Capitalised(tokens[index + 1]);
                if (first is not null && second is not null)
                {
                    pairs.Add(first + " " + second);
                    index++;
                }
            }
        }

        return pairs;
    }

    private static string? Capitalised(string token)
    {
        var word = token.Trim('.', ',', ';', ':', '!', '?', '"', '\'', '\u2026', '\u201c', '\u201d');
        if (word.Length < 2 || !char.IsUpper(word[0]))
        {
            return null;
        }

        for (var index = 1; index < word.Length; index++)
        {
            if (!char.IsLetter(word[index]) || char.IsUpper(word[index]))
            {
                return null;
            }
        }

        return word;
    }

    private static void CheckValidatorBackedRules(StoryContentV1 content, StoryCheckContext ctx, List<(QualityIssue, int)> issues)
    {
        foreach (var found in StoryDraftValidator.Validate(content, ctx.Rules).Issues)
        {
            if (MetadataRuleIds.Contains(found.RuleId))
            {
                issues.Add((Make(
                    MetadataInvalid,
                    QualitySeverity.Blocking,
                    QualityKind.Objective,
                    SegmentAddress.Meta,
                    found.Evidence,
                    found.RepairDirection), MetaOrder));
            }
            else if (found.RuleId == StoryDraftValidator.TextHasMarkup)
            {
                issues.Add((Make(
                    FormattingInvalid,
                    QualitySeverity.Blocking,
                    QualityKind.Objective,
                    found.Segment,
                    UrlPattern.Replace(found.Evidence, "<url>"),
                    found.RepairDirection), SegmentOrder(found.Segment)));
            }
        }
    }

    private static int SegmentOrder(string address)
    {
        if (address == SegmentAddress.Hook)
        {
            return 1;
        }

        if (address == SegmentAddress.Ending)
        {
            return 1_000_000;
        }

        if (address.StartsWith("p:", StringComparison.Ordinal)
            && int.TryParse(address.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            return 2 + index;
        }

        return MetaOrder;
    }

    private static void CheckContext(StoryCheckContext ctx, List<(QualityIssue, int)> issues)
    {
        var flagged = (ctx.ModerationCategories ?? []).Where(BlockingModerationCategories.Contains).ToList();
        if (flagged.Count > 0)
        {
            issues.Add((Make(
                ModerationBlocked,
                QualitySeverity.Blocking,
                QualityKind.Objective,
                SegmentAddress.Meta,
                string.Join(", ", flagged),
                "Remove the flagged content."), MetaOrder));
        }
        else if (ctx.ModerationUnavailable)
        {
            // Fail closed: no verdict is not a clean verdict, so the text counts as not moderated until it is re-checked.
            issues.Add((Make(
                ModerationBlocked,
                QualitySeverity.Blocking,
                QualityKind.Objective,
                SegmentAddress.Meta,
                "moderation unavailable",
                "Run the content moderation again; approval is blocked until it has passed."), MetaOrder));
        }

        if (ctx.HasUrlSources || ctx.Topic == Topic.TrueCrime)
        {
            if (ctx.SimilarityVerdict == SimilarityVerdict.Warn)
            {
                issues.Add((Make(
                    SimilarityWarn,
                    QualitySeverity.Advisory,
                    QualityKind.Objective,
                    SegmentAddress.Meta,
                    SimilarityEvidence(ctx),
                    "Rewrite the passages that follow a source closely.",
                    ctx.SimilarityScore is { } score ? (decimal)score : null), MetaOrder));
            }
            else if (ctx.SimilarityVerdict == SimilarityVerdict.Block)
            {
                issues.Add((Make(
                    SimilarityBlocked,
                    QualitySeverity.Blocking,
                    QualityKind.Objective,
                    SegmentAddress.Meta,
                    SimilarityEvidence(ctx),
                    "Rewrite the story in your own words; approval is blocked.",
                    ctx.SimilarityScore is { } blockScore ? (decimal)blockScore : null), MetaOrder));
            }
        }

        if (ctx.ModelReviewStale)
        {
            issues.Add((Make(
                ModelReviewStale,
                QualitySeverity.Advisory,
                QualityKind.Taste,
                SegmentAddress.Meta,
                "review is stale",
                "Run the review again."), MetaOrder));
        }

        if (ctx.Plan is { } plan)
        {
            if (plan.HintCodes.Contains(RenderImagePlanner.ScenesTrimmed))
            {
                issues.Add((Make(
                    ScenesTrimmed,
                    QualitySeverity.Advisory,
                    QualityKind.Objective,
                    SegmentAddress.Meta,
                    Inv($"{plan.TrimmedCount} scenes trimmed"),
                    "Cut keywords so every scene fits the image budget.",
                    plan.TrimmedCount), MetaOrder));
            }

            if (plan.HintCodes.Contains(RenderImagePlanner.ScenesSparse))
            {
                issues.Add((Make(
                    ScenesSparse,
                    QualitySeverity.Advisory,
                    QualityKind.Objective,
                    SegmentAddress.Meta,
                    Inv($"{plan.Terms.Count} of {plan.NeededImages} scenes"),
                    "Add keywords so the video has enough scenes.",
                    plan.Terms.Count,
                    plan.NeededImages), MetaOrder));
            }
        }
    }

    private static string SimilarityEvidence(StoryCheckContext ctx) =>
        ctx.SimilarityScore is { } score
            ? Inv($"score {score.ToString("0.000", CultureInfo.InvariantCulture)}")
            : "no score";

    /// <summary>NFC, lower-case, punctuation stripped; the token rule of D4 otherwise (whitespace-separated).</summary>
    private static List<string> NormalizedTokens(string? text)
    {
        var tokens = new List<string>();
        var builder = new StringBuilder();
        foreach (var raw in StoryText.Tokenize(text))
        {
            builder.Clear();
            foreach (var character in raw)
            {
                if (char.IsLetterOrDigit(character) || character == '\'')
                {
                    builder.Append(char.ToLowerInvariant(character));
                }
            }

            if (builder.Length > 0)
            {
                tokens.Add(builder.ToString());
            }
        }

        return tokens;
    }

    private sealed record SegmentView(string Address, int Order, StorySegmentV1? Segment, bool IsParagraph);
}
