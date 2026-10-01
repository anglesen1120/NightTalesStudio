using NightTales.Domain.Stories.Keywords;

namespace NightTales.Domain.Stories.Content;

/// <summary>
/// The D3 draft validator (02 README D3, D21 step 2): every objective rule of the 02.2.1 rule table as an
/// issue with a stable rule id. It never throws for a malformed draft and never repairs one — the
/// corrective note (02.2.3-T2) and the job's single retry (02.2.2-T3) are the repair path, so
/// <c>story_versions</c> never stores a draft the validator silently edited.
/// </summary>
/// <remarks>
/// The length rule is objective exactly as D4 states it: more than 15 % outside the token band blocks,
/// inside the 15 % it is advisory; widening the band is a D4 change, not a validator change. Every issue
/// carries the D22 address (<see cref="SegmentAddress"/>) that the editor, the checker and the review
/// share, and the same issue list is returned for the same input (nothing is mutated).
/// </remarks>
public static class StoryDraftValidator
{
    /// <summary>The title is longer than <see cref="StoryRules.TitleMaxChars"/>.</summary>
    public const string TitleTooLong = "title_too_long";

    /// <summary>The title or description carries markup, a URL, an @handle, a #hashtag, an e-mail or a phone number (02 T70).</summary>
    public const string MetadataHasMarkup = "metadata_has_markup";

    /// <summary>The hook is longer than <see cref="StoryRules.HookMaxSentences"/> sentences.</summary>
    public const string HookTooLong = "hook_too_long";

    /// <summary>A keyword fails <see cref="KeywordRules"/>.</summary>
    public const string KeywordInvalid = "keyword_invalid";

    /// <summary>A segment has fewer than <see cref="StoryRules.MinKeywordsPerSegment"/> or more than <see cref="StoryRules.MaxKeywordsPerSegment"/> keywords.</summary>
    public const string KeywordCountOutOfRange = "keyword_count_out_of_range";

    /// <summary>The draft is outside the D4 segment band.</summary>
    public const string SegmentCountOutOfRange = "segment_count_out_of_range";

    /// <summary>The draft is more than 15 % outside the D4 token band.</summary>
    public const string LengthOutOfRange = "length_out_of_range";

    /// <summary>The draft is inside the 15 % tolerance but outside the D4 token band.</summary>
    public const string LengthAdvisory = "length_advisory";

    /// <summary>A segment text carries markdown, brackets, a list marker or a URL (02 T72).</summary>
    public const string TextHasMarkup = "text_has_markup";

    /// <summary>The description is longer than <see cref="StoryRules.DescriptionMaxChars"/>.</summary>
    public const string DescriptionTooLong = "description_too_long";

    /// <summary>A tag limit is broken, or a tag carries a URL, <c>&lt;</c>, <c>&gt;</c>, <c>,</c> or <c>#</c> (F36, 02 T70).</summary>
    public const string TagsInvalid = "tags_invalid";

    /// <summary>A <c>fact_ref</c> is not in the approved sheet's ids (or the sheet is absent).</summary>
    public const string FactRefUnknown = "fact_ref_unknown";

    /// <summary>A paragraph beat is outside <see cref="StoryEnums.Beats"/>.</summary>
    public const string BeatInvalid = "beat_invalid";

    /// <summary>The narrator mode is outside <see cref="StoryEnums.NarratorModes"/>.</summary>
    public const string NarratorModeInvalid = "narrator_mode_invalid";

    /// <summary>A content warning is outside <see cref="StoryEnums.ContentWarnings"/>.</summary>
    public const string WarningInvalid = "warning_invalid";

    /// <summary>An unmapped <c>sub_genre</c>/<c>opening</c>/<c>twist_type</c> value is knowable but only advisory (02 T71).</summary>
    public const string StructureVocabUnknown = "structure_vocab_unknown";

    /// <summary>An unmapped structure free string is over 40 characters or carries a URL/markup (02 T71).</summary>
    public const string StructureTextInvalid = "structure_text_invalid";

    /// <summary>The sentence punctuation a bare-domain token may carry at its end (02 T70).</summary>
    private static readonly char[] TokenTrailingPunctuation =
        ['.', ',', ';', ':', '!', '?', ')', '(', '"', '\'', '\u2026', '\u201d', '\u00bb'];

    /// <summary>Validates one draft (02 README D21 step 2).</summary>
    /// <param name="content">The draft as the writer returned it; the draft is not modified.</param>
    /// <param name="rules">The run's rules: limits, the resolved length target and the run's data lists.</param>
    /// <returns>Every issue found, in rule order; <see cref="ValidationResult.BlockingRuleIds"/> carries the distinct blocking ids.</returns>
    public static ValidationResult Validate(StoryContentV1 content, StoryRules rules)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(rules);

        var issues = new List<ValidationIssue>();

        ValidateMetadata(content, rules, issues);
        ValidateHook(content.Hook, rules, issues);
        ValidateSegments(content, rules, issues);
        ValidateWarnings(content, issues);
        ValidateStructure(content.Structure, rules, issues);
        ValidateSegmentCount(content, rules, issues);
        ValidateLength(content, rules, issues);

        return new ValidationResult(issues);
    }

    /// <summary>The title/description rules: length plus the T70 metadata check (02 T70).</summary>
    private static void ValidateMetadata(StoryContentV1 content, StoryRules rules, List<ValidationIssue> issues)
    {
        var title = StoryText.Normalize(content.Title);
        if (title.Length > rules.TitleMaxChars)
        {
            issues.Add(ValidationIssue.Blocking(
                TitleTooLong,
                SegmentAddress.Title,
                FormattableString.Invariant($"{title.Length} characters, limit {rules.TitleMaxChars}"),
                "Shorten the title to the D3 limit."));
        }

        if (FindMetadataViolation(title) is { } titleViolation)
        {
            issues.Add(ValidationIssue.Blocking(
                MetadataHasMarkup,
                SegmentAddress.Title,
                titleViolation,
                "Remove markup, URLs, handles, hashtags, e-mail addresses and phone numbers from the title."));
        }

        var description = StoryText.Normalize(content.Description);
        if (description.Length > rules.DescriptionMaxChars)
        {
            issues.Add(ValidationIssue.Blocking(
                DescriptionTooLong,
                SegmentAddress.Meta,
                FormattableString.Invariant($"{description.Length} characters, limit {rules.DescriptionMaxChars}"),
                "Shorten the description to the D3 limit."));
        }

        if (FindMetadataViolation(description) is { } descriptionViolation)
        {
            issues.Add(ValidationIssue.Blocking(
                MetadataHasMarkup,
                SegmentAddress.Meta,
                descriptionViolation,
                "Remove markup, URLs, handles, hashtags, e-mail addresses and phone numbers from the description."));
        }

        ValidateTags(content, rules, issues);
    }

    /// <summary>The F36 tag limits plus the T70 per-tag character rule (F36, 02 T70).</summary>
    private static void ValidateTags(StoryContentV1 content, StoryRules rules, List<ValidationIssue> issues)
    {
        var tags = OrEmpty(content.Tags);
        if (tags.Count > rules.MaxTags)
        {
            issues.Add(ValidationIssue.Blocking(
                TagsInvalid,
                SegmentAddress.Meta,
                FormattableString.Invariant($"{tags.Count} tags, limit {rules.MaxTags}"),
                "Use no more tags than the F36 limit."));
        }

        var total = 0;
        foreach (var tag in tags)
        {
            total += tag.Length;

            if (tag.Length > rules.TagMaxChars)
            {
                issues.Add(ValidationIssue.Blocking(
                    TagsInvalid,
                    SegmentAddress.Meta,
                    FormattableString.Invariant($"tag '{tag}' is {tag.Length} characters, limit {rules.TagMaxChars}"),
                    "Shorten the tag."));
            }

            if (FindTagViolation(tag) is { } hit)
            {
                issues.Add(ValidationIssue.Blocking(
                    TagsInvalid,
                    SegmentAddress.Meta,
                    FormattableString.Invariant($"tag '{tag}' carries '{hit}'"),
                    "Remove URLs, angle brackets, commas and hashtags from the tags."));
            }
        }

        if (total > rules.TagsTotalMaxChars)
        {
            issues.Add(ValidationIssue.Blocking(
                TagsInvalid,
                SegmentAddress.Meta,
                FormattableString.Invariant($"{total} total tag characters, limit {rules.TagsTotalMaxChars}"),
                "Use fewer or shorter tags."));
        }
    }

    /// <summary>The hook length rule (D3: at most three sentences).</summary>
    private static void ValidateHook(StorySegmentV1? hook, StoryRules rules, List<ValidationIssue> issues)
    {
        var sentences = StoryText.CountSentences(hook?.Text);
        if (sentences > rules.HookMaxSentences)
        {
            issues.Add(ValidationIssue.Blocking(
                HookTooLong,
                SegmentAddress.Hook,
                FormattableString.Invariant($"{sentences} sentences, limit {rules.HookMaxSentences}"),
                "Shorten the hook to three sentences or fewer."));
        }
    }

    /// <summary>The per-segment rules, in D3 order: hook, paragraphs, ending (D22 addressing).</summary>
    private static void ValidateSegments(StoryContentV1 content, StoryRules rules, List<ValidationIssue> issues)
    {
        ValidateSegment(content.Hook, SegmentAddress.Hook, isParagraph: false, rules, issues);

        var paragraphs = OrEmpty(content.Paragraphs);
        for (var index = 0; index < paragraphs.Count; index++)
        {
            ValidateSegment(paragraphs[index], SegmentAddress.Paragraph(index), isParagraph: true, rules, issues);
        }

        ValidateSegment(content.Ending, SegmentAddress.Ending, isParagraph: false, rules, issues);
    }

    /// <summary>One segment: text markup, keyword count and validity, fact refs, and the paragraph beat.</summary>
    private static void ValidateSegment(
        StorySegmentV1? segment,
        string address,
        bool isParagraph,
        StoryRules rules,
        List<ValidationIssue> issues)
    {
        if (StoryText.FindMarkup(segment?.Text) is { } markup)
        {
            issues.Add(ValidationIssue.Blocking(
                TextHasMarkup,
                address,
                FormattableString.Invariant($"'{markup}' in the text"),
                "Remove markdown, brackets, list markers and URLs from the segment text."));
        }

        var keywords = OrEmpty(segment?.Keywords);
        if (keywords.Count < rules.MinKeywordsPerSegment || keywords.Count > rules.MaxKeywordsPerSegment)
        {
            issues.Add(ValidationIssue.Blocking(
                KeywordCountOutOfRange,
                address,
                FormattableString.Invariant($"{keywords.Count} keywords, allowed {rules.MinKeywordsPerSegment}-{rules.MaxKeywordsPerSegment}"),
                "Return one or two keywords for the segment."));
        }

        foreach (var keyword in keywords)
        {
            var violation = KeywordRules.Check(keyword, rules);
            if (!violation.IsValid)
            {
                issues.Add(ValidationIssue.Blocking(
                    KeywordInvalid,
                    address,
                    FormattableString.Invariant($"'{keyword}' ({violation.RuleId})"),
                    "Rewrite the keyword as an English noun phrase of 3 to 12 words."));
            }
        }

        foreach (var factRef in OrEmpty(segment?.FactRefs))
        {
            if (!rules.AllowedFactRefs.Contains(factRef))
            {
                issues.Add(ValidationIssue.Blocking(
                    FactRefUnknown,
                    address,
                    factRef,
                    "Cite only fact ids from the approved fact sheet."));
            }
        }

        if (isParagraph && !StoryEnums.Beats.Contains(segment?.Beat ?? string.Empty))
        {
            issues.Add(ValidationIssue.Blocking(
                BeatInvalid,
                address,
                segment?.Beat,
                "Use one of the six D3 beats: setup, build_up, clue, twist, reveal, aftermath."));
        }
    }

    /// <summary>The D3 content-warning enum rule.</summary>
    private static void ValidateWarnings(StoryContentV1 content, List<ValidationIssue> issues)
    {
        foreach (var warning in OrEmpty(content.ContentWarnings))
        {
            if (!StoryEnums.ContentWarnings.Contains(warning))
            {
                issues.Add(ValidationIssue.Blocking(
                    WarningInvalid,
                    SegmentAddress.Meta,
                    warning,
                    "Use only the D3 content warnings."));
            }
        }
    }

    /// <summary>
    /// The structure rules: the vocabulary check and the <c>other:</c> mapping via
    /// <see cref="StructureVocabMapper"/> (02 T71) plus the narrator-mode enum (D3).
    /// </summary>
    private static void ValidateStructure(StoryStructureV1? structure, StoryRules rules, List<ValidationIssue> issues)
    {
        if (structure is null)
        {
            return;
        }

        issues.AddRange(StructureVocabMapper.MapTerm(structure.SubGenre, rules).Issues);
        issues.AddRange(StructureVocabMapper.MapTerm(structure.Opening, rules).Issues);
        issues.AddRange(StructureVocabMapper.MapTerm(structure.TwistType, rules).Issues);
        issues.AddRange(StructureVocabMapper.MapFreeText(structure.Setting, rules).Issues);

        if (!StoryEnums.NarratorModes.Contains(structure.NarratorMode))
        {
            issues.Add(ValidationIssue.Blocking(
                NarratorModeInvalid,
                SegmentAddress.Meta,
                structure.NarratorMode,
                "Use first_person or third_person."));
        }
    }

    /// <summary>The D4 segment band (hook + paragraphs + ending).</summary>
    private static void ValidateSegmentCount(StoryContentV1 content, StoryRules rules, List<ValidationIssue> issues)
    {
        var segments = OrEmpty(content.Paragraphs).Count + 2;
        if (segments < rules.Length.MinSegments || segments > rules.Length.MaxSegments)
        {
            issues.Add(ValidationIssue.Blocking(
                SegmentCountOutOfRange,
                SegmentAddress.Meta,
                FormattableString.Invariant($"{segments} segments, D4 band {rules.Length.MinSegments}-{rules.Length.MaxSegments}"),
                "Write a draft inside the D4 segment band; add a scene beat, never filler."));
        }
    }

    /// <summary>
    /// The D4 token rule with its 15 % tolerance: outside <c>[MinTokens × 0.85, MaxTokens × 1.15]</c> is
    /// blocking, inside that band but outside <c>[MinTokens, MaxTokens]</c> is advisory (D4).
    /// </summary>
    private static void ValidateLength(StoryContentV1 content, StoryRules rules, List<ValidationIssue> issues)
    {
        var tokens = DerivedStoryFields.From(content).WordCount;
        var min = rules.Length.MinTokens;
        var max = rules.Length.MaxTokens;

        if (tokens < min * 0.85m || tokens > max * 1.15m)
        {
            issues.Add(ValidationIssue.Blocking(
                LengthOutOfRange,
                SegmentAddress.Meta,
                FormattableString.Invariant($"{tokens} tokens, D4 band {min}-{max}, more than 15 % outside"),
                "Reach the D4 token band: add a scene beat, never filler."));
            return;
        }

        if (tokens < min || tokens > max)
        {
            issues.Add(ValidationIssue.Advisory(
                LengthAdvisory,
                SegmentAddress.Meta,
                FormattableString.Invariant($"{tokens} tokens, D4 band {min}-{max}, inside the 15 % tolerance"),
                "Reach the D4 token band: add a scene beat, never filler."));
        }
    }

    /// <summary>
    /// The first T70 metadata violation in a title or description, or null: markup, a URL (scheme or bare
    /// domain), an <c>@</c> handle or e-mail, or a phone number (seven digits in one letter-free token).
    /// </summary>
    private static string? FindMetadataViolation(string? value)
    {
        var text = StoryText.Normalize(value);
        if (text.Length == 0)
        {
            return null;
        }

        if (StoryText.FindMarkup(text) is { } markup)
        {
            return markup;
        }

        if (text.Contains('@'))
        {
            return "@";
        }

        foreach (var token in StoryText.Tokenize(text))
        {
            if (CountDigits(token) >= 7 && !HasLetter(token))
            {
                return "phone number";
            }

            if (BareDomain(token) is { } domain)
            {
                return domain;
            }
        }

        return null;
    }

    /// <summary>The first T70 tag violation: <c>&lt;</c>, <c>&gt;</c>, <c>,</c>, <c>#</c> or a URL.</summary>
    private static string? FindTagViolation(string value)
    {
        if (value.Contains('<'))
        {
            return "<";
        }

        if (value.Contains('>'))
        {
            return ">";
        }

        if (value.Contains(','))
        {
            return ",";
        }

        if (value.Contains('#'))
        {
            return "#";
        }

        return value.Contains("://", StringComparison.Ordinal)
            || value.Contains("www.", StringComparison.OrdinalIgnoreCase)
            ? "URL"
            : null;
    }

    /// <summary>The bare-domain form of one token (<c>example.com</c>), or null (a 2–24 letter TLD).</summary>
    private static string? BareDomain(string token)
    {
        var trimmed = token.TrimEnd(TokenTrailingPunctuation);
        var lastDot = trimmed.LastIndexOf('.');
        if (lastDot <= 0 || lastDot == trimmed.Length - 1)
        {
            return null;
        }

        var suffix = trimmed[(lastDot + 1)..];
        if (suffix.Length is < 2 or > 24)
        {
            return null;
        }

        foreach (var character in suffix)
        {
            if (!char.IsAsciiLetter(character))
            {
                return null;
            }
        }

        return trimmed;
    }

    /// <summary>Counts the ASCII digits of one token.</summary>
    private static int CountDigits(string text)
    {
        var digits = 0;
        foreach (var character in text)
        {
            if (char.IsAsciiDigit(character))
            {
                digits++;
            }
        }

        return digits;
    }

    /// <summary>Whether one token holds any letter at all.</summary>
    private static bool HasLetter(string text)
    {
        foreach (var character in text)
        {
            if (char.IsLetter(character))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>An empty list for a null member; records are non-nullable, so this only guards a deserialiser.</summary>
    private static IReadOnlyList<T> OrEmpty<T>(IReadOnlyList<T>? values) => values ?? [];
}
