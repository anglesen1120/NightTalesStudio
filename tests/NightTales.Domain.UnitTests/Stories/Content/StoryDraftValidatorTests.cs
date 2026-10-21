using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Length;
using NightTales.Domain.Usage;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Content;

/// <summary>
/// <c>StoryDraftValidator</c> (02.2.1-T1 Tests list, 02 README D3/D4/D22): one test per rule id of the
/// task's table, plus the D22 addressing, the immutability of the draft and the schema-shape check.
/// </summary>
/// <remarks>
/// The valid fixture is built inline: <c>prompts/fixtures/</c> holds only the briefs, the true-crime fact
/// sheet and the rubric (the sample draft belongs to another task's fixtures), so the short/vi draft is
/// assembled here and padded to exactly <see cref="ValidBodyTokens"/> tokens, inside the D4 195–260 band.
/// </remarks>
public sealed class StoryDraftValidatorTests
{
    /// <summary>The short/vi D4 band is 195–260 tokens; this fixture sits in the middle of it.</summary>
    private const int ValidBodyTokens = 220;

    private static readonly JsonSerializerOptions DraftJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] Vocabulary =
    [
        "truyện ma tâm linh",
        "một cuộc điện thoại lúc nửa đêm",
        "người kể là nguyên nhân",
    ];

    [Fact]
    public void Validate_TitleOver100Chars_TitleTooLong()
    {
        var result = StoryDraftValidator.Validate(ValidDraft() with { Title = new string('t', 101) }, Rules());

        var issue = result.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("title_too_long");
        issue.Severity.ShouldBe(IssueSeverity.Blocking);
        issue.Kind.ShouldBe(IssueKind.Objective);
        issue.Segment.ShouldBe("title");

        // The boundary itself is inside the limit.
        StoryDraftValidator.Validate(ValidDraft() with { Title = new string('t', 100) }, Rules())
            .BlockingRuleIds.ShouldNotContain("title_too_long");
    }

    [Fact]
    public void Validate_TitleWithUrl_MetadataHasMarkup()
    {
        var result = StoryDraftValidator.Validate(
            ValidDraft() with { Title = "Bí mật ngôi nhà https://x.test" },
            Rules());

        var issue = result.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("metadata_has_markup");
        issue.Severity.ShouldBe(IssueSeverity.Blocking);
        issue.Segment.ShouldBe("title");
    }

    [Fact]
    public void Validate_DescriptionWithAngleBracket_MetadataHasMarkup()
    {
        var result = StoryDraftValidator.Validate(
            ValidDraft() with { Description = "Một câu chuyện <bí ẩn> bên sông." },
            Rules());

        var issue = result.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("metadata_has_markup");
        issue.Segment.ShouldBe("meta");
    }

    [Fact]
    public void Validate_TagWithComma_TagsInvalid()
    {
        var result = StoryDraftValidator.Validate(ValidDraft() with { Tags = ["truyện ma, tâm linh"] }, Rules());

        var issue = result.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("tags_invalid");
        issue.Segment.ShouldBe("meta");
    }

    [Fact]
    public void Validate_TextWithParentheses_TextHasMarkup()
    {
        // 02 T72 added ( and ) to the rejected set.
        var result = StoryDraftValidator.Validate(
            WithParagraphText(0, "Anh nghe thấy tiếng gõ (rất khẽ) trong đêm."),
            Rules());

        var issue = result.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("text_has_markup");
        issue.Segment.ShouldBe("p:0");
    }

    [Fact]
    public void Validate_UnmappedStructureOver40Chars_StructureTextInvalid()
    {
        var structure = DefaultStructure() with { SubGenre = new string('s', 41) };

        var result = StoryDraftValidator.Validate(Draft(structure: structure), Rules());

        result.BlockingRuleIds.ShouldContain("structure_text_invalid");
        result.IsValid.ShouldBeFalse();
        result.Issues.ShouldContain(issue =>
            issue.RuleId == "structure_text_invalid"
            && issue.Severity == IssueSeverity.Blocking
            && issue.Segment == "meta");

        // The value is also outside the vocabulary, and that part stays advisory (02 T71).
        result.Issues.ShouldContain(issue =>
            issue.RuleId == "structure_vocab_unknown" && issue.Severity == IssueSeverity.Advisory);
    }

    [Fact]
    public void Validate_StructureWithOtherPrefix_StripsPrefixBeforeMapping()
    {
        // 02 T71: only the mapper writes `other:`, so a model-supplied prefix is stripped before mapping.
        var structure = DefaultStructure() with { SubGenre = "other:truyện ma tâm linh" };

        var mapped = StructureVocabMapper.MapTerm(structure.SubGenre, Rules());
        mapped.Value.ShouldBe("truyện ma tâm linh");
        mapped.Issues.ShouldBeEmpty();

        var result = StoryDraftValidator.Validate(Draft(structure: structure), Rules());

        result.Issues.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_HookFourSentences_HookTooLong()
    {
        var result = StoryDraftValidator.Validate(
            Draft(hook: new StorySegmentV1("Một. Hai. Ba. Bốn", ["old house by the river"], [])),
            Rules());

        var issue = result.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("hook_too_long");
        issue.Segment.ShouldBe("hook");
    }

    [Fact]
    public void Validate_HookThreeSentences_NoIssue()
    {
        var result = StoryDraftValidator.Validate(
            Draft(hook: new StorySegmentV1("Một. Hai. Ba.", ["old house by the river"], [])),
            Rules());

        result.Issues.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_KeywordWithPersonName_KeywordInvalid()
    {
        var rules = Rules() with { KnownPersonNames = new HashSet<string> { "linh" } };

        var result = StoryDraftValidator.Validate(WithParagraphKeywords(0, ["portrait of linh"]), rules);

        var issue = result.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("keyword_invalid");
        issue.Segment.ShouldBe("p:0");
    }

    [Fact]
    public void Validate_KeywordWithGoreTerm_KeywordInvalid()
    {
        var result = StoryDraftValidator.Validate(WithParagraphKeywords(1, ["pool of blood"]), Rules());

        var issue = result.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("keyword_invalid");
        issue.Segment.ShouldBe("p:1");
    }

    [Fact]
    public void Validate_KeywordWithVietnameseDiacritics_KeywordInvalid()
    {
        var result = StoryDraftValidator.Validate(WithParagraphKeywords(0, ["ngôi nhà cũ"]), Rules());

        var issue = result.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("keyword_invalid");
        issue.Segment.ShouldBe("p:0");
    }

    [Fact]
    public void Validate_SegmentWithThreeKeywords_KeywordCountOutOfRange()
    {
        var draft = WithParagraphKeywords(
            0,
            ["old house by the river", "empty chair in the hallway", "rain on the window"]);

        var result = StoryDraftValidator.Validate(draft, Rules());

        var issue = result.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("keyword_count_out_of_range");
        issue.Severity.ShouldBe(IssueSeverity.Blocking);
        issue.Segment.ShouldBe("p:0");
    }

    [Fact]
    public void Validate_SegmentWithZeroKeywords_KeywordCountOutOfRange()
    {
        var result = StoryDraftValidator.Validate(WithParagraphKeywords(0, []), Rules());

        var issue = result.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("keyword_count_out_of_range");
        issue.Segment.ShouldBe("p:0");
    }

    [Fact]
    public void Validate_ThreeParagraphs_StandardTier_SegmentCountOutOfRange()
    {
        // The D4 standard band is 20–32 segments; the five-segment short draft is far below it.
        var result = StoryDraftValidator.Validate(ValidDraft(), StandardVi());

        var issue = result.Issues
            .Where(candidate => candidate.RuleId == "segment_count_out_of_range")
            .ShouldHaveSingleItem();
        issue.Severity.ShouldBe(IssueSeverity.Blocking);
        issue.Segment.ShouldBe("meta");
    }

    [Fact]
    public void Validate_Tokens15PercentOver_LengthOutOfRange()
    {
        // 260 × 1.15 = 299: 320 tokens is more than 15 % outside the short/vi band.
        var result = StoryDraftValidator.Validate(Draft(bodyTokens: 320), Rules());

        var issue = result.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("length_out_of_range");
        issue.Severity.ShouldBe(IssueSeverity.Blocking);
        issue.Segment.ShouldBe("meta");
    }

    [Fact]
    public void Validate_Tokens10PercentOver_LengthAdvisory()
    {
        // 280 is outside the band (195–260) but inside its 15 % tolerance.
        var result = StoryDraftValidator.Validate(Draft(bodyTokens: 280), Rules());

        var issue = result.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("length_advisory");
        issue.Severity.ShouldBe(IssueSeverity.Advisory);
        issue.Segment.ShouldBe("meta");
        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_TokensAtUpperBound_NoIssue()
    {
        var result = StoryDraftValidator.Validate(Draft(bodyTokens: 260), Rules());

        result.Issues.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_TextWithMarkdown_TextHasMarkup()
    {
        var result = StoryDraftValidator.Validate(
            WithParagraphText(0, "Anh thấy **một bóng người** trong đêm."),
            Rules());

        var issue = result.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("text_has_markup");
        issue.Segment.ShouldBe("p:0");
    }

    [Fact]
    public void Validate_TextWithUrl_TextHasMarkup()
    {
        var result = StoryDraftValidator.Validate(
            WithParagraphText(1, "Anh đọc thấy https://x.test trong đêm."),
            Rules());

        var issue = result.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("text_has_markup");
        issue.Segment.ShouldBe("p:1");
    }

    [Fact]
    public void Validate_TextWithListMarker_TextHasMarkup()
    {
        var result = StoryDraftValidator.Validate(
            WithParagraphText(0, "Anh ghi lại:\n- vật thứ nhất"),
            Rules());

        var issue = result.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("text_has_markup");
        issue.Segment.ShouldBe("p:0");
    }

    [Fact]
    public void Validate_DescriptionOver1000Chars_DescriptionTooLong()
    {
        var result = StoryDraftValidator.Validate(ValidDraft() with { Description = new string('d', 1_001) }, Rules());

        var issue = result.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("description_too_long");
        issue.Segment.ShouldBe("meta");

        // The boundary itself is inside the limit.
        StoryDraftValidator.Validate(ValidDraft() with { Description = new string('d', 1_000) }, Rules())
            .Issues.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_SixteenTags_TagsInvalid()
    {
        var result = StoryDraftValidator.Validate(ValidDraft() with { Tags = SixteenTags() }, Rules());

        var issue = result.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("tags_invalid");
        issue.Segment.ShouldBe("meta");
    }

    [Fact]
    public void Validate_TagOver30Chars_TagsInvalid()
    {
        var result = StoryDraftValidator.Validate(ValidDraft() with { Tags = [new string('t', 31)] }, Rules());

        var issue = result.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("tags_invalid");
        issue.Segment.ShouldBe("meta");
    }

    [Fact]
    public void Validate_TagsTotalOver400Chars_TagsInvalid()
    {
        // 14 tags of exactly 30 characters: the count and each length are inside the limits, the total is not.
        var tags = Enumerable.Repeat(new string('a', 30), 14).ToArray();

        var result = StoryDraftValidator.Validate(ValidDraft() with { Tags = tags }, Rules());

        var issue = result.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("tags_invalid");
        issue.Segment.ShouldBe("meta");
    }

    [Fact]
    public void Validate_FactRefNotInApprovedSheet_FactRefUnknown()
    {
        var rules = Rules() with { AllowedFactRefs = new HashSet<string> { "f1" } };
        var paragraphs = DefaultParagraphs();
        paragraphs[0] = paragraphs[0] with { FactRefs = ["f9"] };

        var result = StoryDraftValidator.Validate(Draft(paragraphs: paragraphs), rules);

        var issue = result.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("fact_ref_unknown");
        issue.Segment.ShouldBe("p:0");
    }

    [Fact]
    public void Validate_FactRefPresentWithNoSheet_FactRefUnknown()
    {
        // A fiction run has no approved sheet, so the allowed set is empty and any fact_ref fails (D3).
        var paragraphs = DefaultParagraphs();
        paragraphs[0] = paragraphs[0] with { FactRefs = ["f1"] };

        var result = StoryDraftValidator.Validate(Draft(paragraphs: paragraphs), Rules());

        var issue = result.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("fact_ref_unknown");
        issue.Segment.ShouldBe("p:0");
    }

    [Fact]
    public void Validate_InvalidBeat_BeatInvalid()
    {
        var paragraphs = DefaultParagraphs();
        paragraphs[0] = paragraphs[0] with { Beat = "wiggle" };

        var result = StoryDraftValidator.Validate(Draft(paragraphs: paragraphs), Rules());

        var issue = result.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("beat_invalid");
        issue.Segment.ShouldBe("p:0");
    }

    [Fact]
    public void Validate_InvalidNarratorMode_NarratorModeInvalid()
    {
        var structure = DefaultStructure() with { NarratorMode = "second_person" };

        var result = StoryDraftValidator.Validate(Draft(structure: structure), Rules());

        var issue = result.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("narrator_mode_invalid");
        issue.Segment.ShouldBe("meta");
    }

    [Fact]
    public void Validate_InvalidContentWarning_WarningInvalid()
    {
        var result = StoryDraftValidator.Validate(Draft(warnings: ["kittens"]), Rules());

        var issue = result.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("warning_invalid");
        issue.Segment.ShouldBe("meta");
    }

    [Fact]
    public void Validate_NullTagElement_TagsInvalid()
    {
        // SEC-02.2.1 (QA): a deserialised draft can carry a null tag element; the validator reports it
        // and never dereferences it (the pre-fix code read tag.Length and threw an NRE).
        var result = StoryDraftValidator.Validate(Draft(tags: [null!]), Rules());

        result.IsValid.ShouldBeFalse();
        result.BlockingRuleIds.ShouldBe(["tags_invalid"]);
        result.Issues.ShouldContain(issue => issue.RuleId == "tags_invalid" && issue.Segment == "meta");
    }

    [Fact]
    public void Validate_NullStructure_BlockingNarratorMode()
    {
        // SEC-02.2.1-STRUCTURE-FAIL-OPEN: a missing structure block is malformed, never a valid draft.
        var draft = Draft() with { Structure = null! };

        var result = StoryDraftValidator.Validate(draft, Rules());

        result.IsValid.ShouldBeFalse();
        result.BlockingRuleIds.ShouldBe(["narrator_mode_invalid"]);
    }

    [Fact]
    public void Validate_OversizedEvidence_IsBoundedTo200()
    {
        // CR-04 / D22: evidence quotes at most 200 chars, snipped before the interpolated string is built.
        var keywordResult = StoryDraftValidator.Validate(WithParagraphKeywords(0, [new string('k', 500)]), Rules());
        keywordResult.Issues.ShouldContain(issue => issue.RuleId == "keyword_invalid");
        keywordResult.Issues.ShouldAllBe(issue => issue.Evidence.Length <= ValidationIssue.MaxEvidenceChars);

        var tagResult = StoryDraftValidator.Validate(Draft(tags: [new string('t', 500)]), Rules());
        tagResult.Issues.ShouldContain(issue => issue.RuleId == "tags_invalid");
        tagResult.Issues.ShouldAllBe(issue => issue.Evidence.Length <= ValidationIssue.MaxEvidenceChars);
    }

    /// <summary>SEC-02.2.1-UNICODE-DOMAIN: IDN labels and the IDNA dot equivalents are domains too.</summary>
    [Theory]
    [InlineData("example.com")]         // an ASCII domain (control)
    [InlineData("EXAMPLE.COM")]         // an all-uppercase ASCII domain is still a domain
    [InlineData("example.中国")]        // a Unicode (IDN) TLD
    [InlineData("example.xn--p1ai")]    // a punycode TLD
    [InlineData("example．com")]         // U+FF0E fullwidth full stop
    [InlineData("example。com")]         // U+3002 ideographic full stop
    [InlineData("example｡com")]         // U+FF61 halfwidth ideographic full stop
    public void Validate_UnicodeOrEquivalentDomain_MetadataHasMarkup(string title)
    {
        var result = StoryDraftValidator.Validate(Draft(title: title), Rules());

        result.BlockingRuleIds.ShouldContain("metadata_has_markup");
    }

    /// <summary>NEW-02.2.1-DOMAIN-PROSE: a dot before a capitalised word is sentence punctuation.</summary>
    [Theory]
    [InlineData("end.Then")]
    [InlineData("end。Then")]
    [InlineData("end. Then")]
    public void Validate_SentenceProseDomain_NoMetadataMarkup(string title)
    {
        var result = StoryDraftValidator.Validate(Draft(title: title), Rules());

        result.BlockingRuleIds.ShouldNotContain("metadata_has_markup");
        result.IsValid.ShouldBeTrue();
    }

    /// <summary>NEW-02.2.1-EVIDENCE-SURROGATE: the 200-char cut never splits a surrogate pair.</summary>
    [Fact]
    public void ValidationIssue_Snip_DoesNotSplitSurrogatePair()
    {
        // 199 ASCII then an emoji straddles the cut; the snip must back off one code unit.
        var evidence = new string('a', 199) + "😀" + new string('b', 10);

        var issue = ValidationIssue.Blocking("rule", SegmentAddress.Meta, evidence, "fix");

        issue.Evidence.Length.ShouldBeLessThanOrEqualTo(ValidationIssue.MaxEvidenceChars);
        char.IsHighSurrogate(issue.Evidence[^1]).ShouldBeFalse();
        char.IsLowSurrogate(issue.Evidence[0]).ShouldBeFalse();
    }

    [Fact]
    public void Validate_UnknownStructureTerm_MapsToOtherPrefixAndAdvisory()
    {
        var structure = DefaultStructure() with { SubGenre = "quiet haunting" };

        var mapped = StructureVocabMapper.MapTerm(structure.SubGenre, Rules());
        mapped.Value.ShouldBe("other:quiet haunting");

        var result = StoryDraftValidator.Validate(Draft(structure: structure), Rules());

        var issue = result.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("structure_vocab_unknown");
        issue.Severity.ShouldBe(IssueSeverity.Advisory);
        issue.Segment.ShouldBe("meta");
        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ValidDraft_NoBlockingIssues()
    {
        var draft = ValidDraft();

        var result = StoryDraftValidator.Validate(draft, Rules());

        result.IsValid.ShouldBeTrue();
        result.BlockingRuleIds.ShouldBeEmpty();
        result.Issues.ShouldBeEmpty();

        StoryText.Tokenize(DerivedStoryFields.From(draft).Body).Count.ShouldBeInRange(195, 260);

        AssertSchemaShape(draft);
    }

    [Fact]
    public void Validate_BlockingRuleIds_ContainsEveryBlockingRule()
    {
        var result = StoryDraftValidator.Validate(BrokenDraft(), Rules());

        string[] expected =
        [
            "title_too_long",
            "metadata_has_markup",
            "hook_too_long",
            "keyword_invalid",
            "keyword_count_out_of_range",
            "segment_count_out_of_range",
            "length_out_of_range",
            "text_has_markup",
            "description_too_long",
            "tags_invalid",
            "fact_ref_unknown",
            "beat_invalid",
            "narrator_mode_invalid",
            "warning_invalid",
            "structure_text_invalid",
            "control_characters_invalid",
        ];

        result.BlockingRuleIds.ShouldBe(expected, ignoreOrder: true);
        result.IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Validate_DoesNotMutateDraft()
    {
        var draft = ValidDraft() with { Title = new string('t', 130) };
        var before = Snapshot(draft);

        var first = StoryDraftValidator.Validate(draft, Rules());
        var second = StoryDraftValidator.Validate(draft, Rules());

        first.BlockingRuleIds.ShouldContain("title_too_long");
        first.Issues.Count.ShouldBeGreaterThan(0);
        first.BlockingRuleIds.ShouldBe(second.BlockingRuleIds);

        Snapshot(draft).ShouldBe(before, "the validator reports, never repairs (02.2.1 DECISION)");
        draft.Title.Length.ShouldBe(130);
        draft.Paragraphs.Count.ShouldBe(3);
    }

    [Fact]
    public void Validate_AllIssuesCarryD22SegmentAddress()
    {
        var result = StoryDraftValidator.Validate(BrokenDraft(), Rules());

        result.Issues.ShouldNotBeEmpty();
        foreach (var issue in result.Issues)
        {
            IsD22Address(issue.Segment).ShouldBeTrue($"'{issue.Segment}' is not a D22 address");
        }

        var addresses = result.Issues.Select(issue => issue.Segment).ToArray();
        addresses.ShouldContain("title");
        addresses.ShouldContain("hook");
        addresses.ShouldContain("p:0");
        addresses.ShouldContain("meta");
    }

    /// <summary>The 15 % band edges (D4): inside [195×0.85, 260×1.15] is advisory at worst, outside is blocking.</summary>
    [Theory]
    [InlineData(165, "length_out_of_range")]   // below 195 × 0.85 = 165.75
    [InlineData(166, "length_advisory")]       // inside the tolerance, below the band
    [InlineData(194, "length_advisory")]
    [InlineData(195, null)]                    // the lower bound itself is inside
    [InlineData(261, "length_advisory")]
    [InlineData(299, "length_advisory")]       // 260 × 1.15 = 299 exactly: inside, not outside
    [InlineData(300, "length_out_of_range")]
    public void Validate_TokensAtBandEdges_ExpectedSeverity(int bodyTokens, string? expectedRule)
    {
        var result = StoryDraftValidator.Validate(Draft(bodyTokens: bodyTokens), Rules());

        var issues = result.Issues
            .Where(issue => issue.RuleId is "length_out_of_range" or "length_advisory")
            .ToArray();

        if (expectedRule is null)
        {
            issues.ShouldBeEmpty();
        }
        else
        {
            var issue = issues.ShouldHaveSingleItem();
            issue.RuleId.ShouldBe(expectedRule);
            issue.Severity.ShouldBe(
                expectedRule == "length_out_of_range" ? IssueSeverity.Blocking : IssueSeverity.Advisory);
        }
    }

    /// <summary>The segment band edge (D4): 4 segments is inside the short band, 3 is below it.</summary>
    [Fact]
    public void Validate_ParagraphCountsAtSegmentBandEdge_ExpectedSeverity()
    {
        var three = DefaultParagraphs();
        var two = new[] { three[0], three[1] };
        var one = new[] { three[0] };

        // 2 paragraphs + hook + ending = 4 segments: the short band's lower edge (D4: 4-5).
        var inside = StoryDraftValidator.Validate(Draft(paragraphs: two), Rules());
        inside.Issues.ShouldNotContain(issue => issue.RuleId == "segment_count_out_of_range");

        // 1 paragraph → 3 segments: below the band.
        var below = StoryDraftValidator.Validate(Draft(paragraphs: one), Rules());
        below.Issues.ShouldContain(issue =>
            issue.RuleId == "segment_count_out_of_range" && issue.Severity == IssueSeverity.Blocking);
    }

    /// <summary>The D3 keyword-count rule is 1-2 per segment: two valid keywords are inside, four are out.</summary>
    [Fact]
    public void Validate_SegmentWithTwoOrFourKeywords_ExpectedSeverity()
    {
        var inside = StoryDraftValidator.Validate(
            WithParagraphKeywords(0, ["old house by the river", "empty chair in the hallway"]),
            Rules());
        inside.Issues.ShouldNotContain(issue => issue.RuleId == "keyword_count_out_of_range");

        var outside = StoryDraftValidator.Validate(
            WithParagraphKeywords(
                0,
                ["old house by the river", "empty chair in the hallway", "rain on the window", "cold tea on the table"]),
            Rules());
        var issue = outside.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("keyword_count_out_of_range");
        issue.Segment.ShouldBe("p:0");
    }

    /// <summary>NFC normalisation happens before the character-count and markup rules (02 T70, D4).</summary>
    [Fact]
    public void Validate_DecomposedDiacritics_SameVerdictAsComposed()
    {
        // "nhà" decomposed is one more UTF-16 unit than "nhà" precomposed; the limit must count NFC.
        var pad = new string('t', 97);
        var nfdTitle = "nha\u0300" + pad;   // 3+97 = 100 NFC chars, 101 UTF-16 units

        var nfd = StoryDraftValidator.Validate(ValidDraft() with { Title = nfdTitle }, Rules());
        nfd.Issues.ShouldNotContain(issue => issue.RuleId == "title_too_long");

        var pad2 = new string('t', 98);
        var over = StoryDraftValidator.Validate(ValidDraft() with { Title = "nha\u0300" + pad2 }, Rules());
        over.Issues.ShouldContain(issue => issue.RuleId == "title_too_long");

        // Decomposed diacritics in a segment text are prose, not markup, and tokens agree with NFC.
        var text = "Anh mo\u0309 ca\u0301nh cư\u0309a cuô\u0301i ha\u0300nh lang va\u0300 lă\u0301ng nghe.";
        var inText = StoryDraftValidator.Validate(WithParagraphText(0, text), Rules());
        inText.Issues.ShouldNotContain(issue => issue.RuleId == "text_has_markup");
    }

    /// <summary>An ordered-list marker at a line start is markup too (02 T72).</summary>
    [Fact]
    public void Validate_TextWithNumberedListMarker_TextHasMarkup()
    {
        var result = StoryDraftValidator.Validate(
            WithParagraphText(0, "Anh ghi lại:\n1. vật thứ nhất"),
            Rules());

        var issue = result.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("text_has_markup");
        issue.Segment.ShouldBe("p:0");
    }

    /// <summary>The markup set and its edges (02 T72): the rejected characters, and a bare period that is prose.</summary>
    [Theory]
    [InlineData("Đêm đầy *sao* rơi xuống sân nhà cũ")]
    [InlineData("Câu nói _chìm_ vào im lặng của đêm")]
    [InlineData("Tiếng gọi vọng lại `từ đâu` không rõ")]
    [InlineData("Anh đếm [1] chiếc ghế trong bóng tối")]
    [InlineData("Bức ảnh > cạnh cửa đã bị ai đó bỏ đi")]
    public void Validate_TextWithMarkupChar_TextHasMarkup(string text)
    {
        var result = StoryDraftValidator.Validate(WithParagraphText(0, text), Rules());

        result.Issues.ShouldContain(issue => issue.RuleId == "text_has_markup" && issue.Segment == "p:0");
    }

    /// <summary>An empty segment text is prose-count zero, not a crash: the length and count rules report it.</summary>
    [Fact]
    public void Validate_EmptySegmentText_DoesNotThrowAndReports()
    {
        var result = StoryDraftValidator.Validate(WithParagraphText(0, string.Empty), Rules());

        // PadBody re-fills the last paragraph, so the draft still sits in the band: an empty text is
        // markup-free and raises no spurious issue — the spec's "treated as 0 tokens" path.
        result.Issues.ShouldBeEmpty();
    }

    /// <summary>A null optional block is a deserialiser edge, not a crash: the validator reports, never throws.</summary>
    [Fact]
    public void Validate_NullOptionalBlocks_DoesNotThrow()
    {
        var draft = new StoryContentV1(
            "Tựa",
            new StorySegmentV1("Mở đầu.", null!, null!),
            [new StorySegmentV1("Đoạn.", ["old house by the river"], null!, null)],
            new StorySegmentV1("Kết.", [], []),
            "Mô tả",
            null!,
            null!,
            null!);

        var result = StoryDraftValidator.Validate(draft, Rules());

        // A null keyword list counts as zero keywords on the hook and the ending, and the paragraph's
        // null beat is invalid; the null metadata lists raise nothing.
        result.Issues.Count(issue => issue.RuleId == "keyword_count_out_of_range").ShouldBe(2);
        result.Issues.ShouldContain(issue => issue.RuleId == "beat_invalid" && issue.Segment == "p:0");
        result.Issues.ShouldNotContain(issue => issue.RuleId == "tags_invalid" || issue.RuleId == "warning_invalid");
        result.BlockingRuleIds.ShouldContain("keyword_count_out_of_range");
    }

    /// <summary>
    /// A deserialised draft with a null structural member must still be reported, never thrown (the
    /// task's contract: the validator "never throws for a malformed draft"). Currently fails:
    /// <c>DerivedStoryFields.From</c> dereferences <c>Hook</c>/<c>Ending</c>/<c>Paragraphs</c> without a
    /// null check, so <c>Validate</c> raises a <see cref="NullReferenceException"/> instead of issues.
    /// </summary>
    [Fact]
    public void Validate_NullHookEndingOrParagraphs_DoesNotThrow()
    {
        var valid = ValidDraft();

        var withNullHook = valid with { Hook = null! };
        var withNullEnding = valid with { Ending = null! };
        var withNullParagraphs = valid with { Paragraphs = null! };

        StoryDraftValidator.Validate(withNullHook, Rules()).IsValid.ShouldBeFalse();
        StoryDraftValidator.Validate(withNullEnding, Rules()).IsValid.ShouldBeFalse();

        var result = StoryDraftValidator.Validate(withNullParagraphs, Rules());
        result.BlockingRuleIds.ShouldContain("segment_count_out_of_range");
    }

    /// <summary>Hostile input is a report, not an exception: huge strings, control chars, a lone surrogate.</summary>
    [Fact]
    public void Validate_HostileInput_DoesNotThrow()
    {
        var draft = new StoryContentV1(
            new string('t', 2_000_000),
            new StorySegmentV1("\u0001\u0007\t\u000B\u000C\r\n", null!, []),
            [],
            new StorySegmentV1(new string('x', 500), [], []),
            new string('d', 500_000),
            [new string('q', 500), "tag\u0000nul"],
            ["\u0000"],
            new StoryStructureV1("\uFFFD\u0007", "set\u0085ting", "\uD800", "twist", "first_person"));

        var result = StoryDraftValidator.Validate(draft, Rules());

        result.IsValid.ShouldBeFalse();
        result.BlockingRuleIds.ShouldContain("title_too_long");
        result.BlockingRuleIds.ShouldContain("description_too_long");
        result.BlockingRuleIds.ShouldContain("segment_count_out_of_range");
        result.BlockingRuleIds.ShouldContain("length_out_of_range");
    }

    /// <summary>
    /// QA-1: NUL (and any other C0 control character but LF, CR, TAB) in any free-text field is a blocking
    /// issue that names the field and carries the D22 address, so no path that validates can persist it.
    /// </summary>
    [Theory]
    [InlineData("title", "title")]
    [InlineData("description", "meta")]
    [InlineData("tags[1]", "meta")]
    [InlineData("hook.text", "hook")]
    [InlineData("hook.beat", "hook")]
    [InlineData("paragraphs[1].text", "p:1")]
    [InlineData("paragraphs[1].beat", "p:1")]
    [InlineData("ending.text", "ending")]
    [InlineData("ending.beat", "ending")]
    [InlineData("structure.sub_genre", "meta")]
    [InlineData("structure.setting", "meta")]
    [InlineData("structure.opening", "meta")]
    [InlineData("structure.twist_type", "meta")]
    public void Validate_NulInTextField_ControlCharactersInvalid_NamingTheField(string field, string address)
    {
        var result = StoryDraftValidator.Validate(WithControlCharacter(ValidDraft(), field, "\u0000"), Rules());

        result.IsValid.ShouldBeFalse();
        result.BlockingRuleIds.ShouldContain("control_characters_invalid");
        var issue = result.Issues.Single(found => found.RuleId == "control_characters_invalid");
        issue.Severity.ShouldBe(IssueSeverity.Blocking);
        issue.Segment.ShouldBe(address);
        issue.Evidence.ShouldBe($"U+0000 in {field}");
        issue.RepairDirection.ShouldContain(field);
    }

    /// <summary>Every C0 control character except LF, CR and TAB is refused, not only NUL.</summary>
    [Theory]
    [InlineData("\u0000")]
    [InlineData("\u0001")]
    [InlineData("\u0007")]
    [InlineData("\u000B")]
    [InlineData("\u000C")]
    [InlineData("\u001B")]
    [InlineData("\u001F")]
    public void Validate_C0ControlCharacterInParagraph_ControlCharactersInvalid(string control)
    {
        var result = StoryDraftValidator.Validate(
            WithParagraphText(0, "Anh nghe tiếng gõ" + control + " từ trong phòng."),
            Rules());

        result.Issues.ShouldContain(issue =>
            issue.RuleId == "control_characters_invalid" && issue.Segment == "p:0" && issue.Severity == IssueSeverity.Blocking);
    }

    /// <summary>A line feed, a carriage return and a tab are prose: the editor canonicalises CR and CRLF to LF.</summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    [InlineData("\t")]
    public void Validate_LineBreaksAndTabs_NoControlCharactersIssue(string whitespace)
    {
        var result = StoryDraftValidator.Validate(
            WithParagraphText(0, "Anh nghe tiếng gõ" + whitespace + "từ trong phòng."),
            Rules());

        result.Issues.ShouldNotContain(issue => issue.RuleId == "control_characters_invalid");
    }

    /// <summary>One issue per offending field: two fields give two issues, a clean draft gives none.</summary>
    [Fact]
    public void Validate_NulInTwoFields_OneIssuePerField_CleanDraftNone()
    {
        var dirty = WithControlCharacter(WithControlCharacter(ValidDraft(), "title", "\u0000"), "ending.text", "\u0002");

        StoryDraftValidator.Validate(dirty, Rules()).Issues
            .Where(issue => issue.RuleId == "control_characters_invalid")
            .Select(issue => issue.Segment)
            .ShouldBe(["title", "ending"], ignoreOrder: true);
        StoryDraftValidator.Validate(ValidDraft(), Rules()).Issues
            .ShouldNotContain(issue => issue.RuleId == "control_characters_invalid");
    }

    /// <summary>A null segment, tag or structure is reported by its own rule; the control-character scan never dereferences it.</summary>
    [Fact]
    public void Validate_NullMembers_ControlCharacterScanDoesNotThrow()
    {
        var valid = ValidDraft();

        var result = StoryDraftValidator.Validate(
            valid with { Hook = null!, Ending = null!, Structure = null!, Tags = [null!, "tag"] },
            Rules());

        result.Issues.ShouldNotContain(issue => issue.RuleId == "control_characters_invalid");
    }

    /// <summary>A structure free string at exactly 40 characters is inside the 02 T71 limit.</summary>
    [Fact]
    public void Validate_UnmappedStructureAt40Chars_NoStructureTextInvalid()
    {
        var structure = DefaultStructure() with { SubGenre = new string('s', 40) };

        var result = StoryDraftValidator.Validate(Draft(structure: structure), Rules());

        var issue = result.Issues.ShouldHaveSingleItem();
        issue.RuleId.ShouldBe("structure_vocab_unknown");
        issue.Severity.ShouldBe(IssueSeverity.Advisory);
    }

    /// <summary>A URL inside a structure value is a blocking text rule, advisory only is not enough (02 T71).</summary>
    [Fact]
    public void Validate_StructureValueWithUrl_StructureTextInvalid()
    {
        var structure = DefaultStructure() with { Setting = "nhà ven sông www.x.test" };

        var result = StoryDraftValidator.Validate(Draft(structure: structure), Rules());

        result.Issues.ShouldContain(issue =>
            issue.RuleId == "structure_text_invalid" && issue.Severity == IssueSeverity.Blocking);
    }

    /// <summary>A paragraph beat that is null is invalid, not silently skipped (D3: every paragraph has a beat).</summary>
    [Fact]
    public void Validate_ParagraphWithoutBeat_BeatInvalid()
    {
        var paragraphs = DefaultParagraphs();
        paragraphs[2] = paragraphs[2] with { Beat = null };

        var result = StoryDraftValidator.Validate(Draft(paragraphs: paragraphs), Rules());

        result.Issues.ShouldContain(issue =>
            issue.RuleId == "beat_invalid" && issue.Segment == "p:2");
    }

    /// <summary>CR-01 (D3): the hook and the ending have no beat, so a non-null one is invalid whatever it says.</summary>
    [Theory]
    [InlineData("hook", "setup")]
    [InlineData("hook", "wiggle")]
    [InlineData("hook", "")]
    [InlineData("ending", "aftermath")]
    [InlineData("ending", "wiggle")]
    [InlineData("ending", "")]
    public void Validate_NonNullBeatOnHookOrEnding_BeatInvalid(string segment, string beat)
    {
        var draft = ValidDraft();
        draft = segment == "hook"
            ? draft with { Hook = draft.Hook with { Beat = beat } }
            : draft with { Ending = draft.Ending with { Beat = beat } };

        var result = StoryDraftValidator.Validate(draft, Rules());

        result.IsValid.ShouldBeFalse();
        var issue = result.Issues.Single(found => found.RuleId == "beat_invalid");
        issue.Severity.ShouldBe(IssueSeverity.Blocking);
        issue.Segment.ShouldBe(segment);
    }

    /// <summary>A hook and an ending without a beat, as D3 defines them, raise no beat issue.</summary>
    [Fact]
    public void Validate_NullBeatOnHookAndEnding_NoBeatIssue()
    {
        var draft = ValidDraft();

        draft.Hook.Beat.ShouldBeNull();
        draft.Ending.Beat.ShouldBeNull();
        StoryDraftValidator.Validate(draft, Rules()).Issues.ShouldNotContain(issue => issue.RuleId == "beat_invalid");
    }

    /// <summary>A draft that breaks every blocking rule of the table, each once.</summary>
    private static StoryContentV1 BrokenDraft()
    {
        StorySegmentV1[] paragraphs =
        [
            // Three keywords (one invalid), a fact id outside the empty allowed set, an invalid beat and markup.
            new("Một đoạn văn có (dấu ngoặc).", ["a b", "old house by the river", "rain on the window"], ["f9"], "wiggle"),
            new("Đoạn hai của câu chuyện.", ["old house by the river"], [], "setup"),
            new("Đoạn ba của câu chuyện.", ["old house by the river"], [], "clue"),
            new("Đoạn bốn của câu chuyện.", ["old house by the river"], [], "reveal"),
        ];

        return Draft(
            title: new string('t', 101) + " https://x.test",
            hook: new StorySegmentV1("Một. Hai. Ba. Bốn.", ["old house by the river"], []),
            paragraphs: paragraphs,
            description: new string('d', 1_001) + "\u0000",
            tags: SixteenTags(),
            warnings: ["kittens"],
            structure: new StoryStructureV1(
                new string('s', 41),
                "ngôi nhà cũ",
                "một cuộc điện thoại lúc nửa đêm",
                "người kể là nguyên nhân",
                "second_person"),
            bodyTokens: 320);
    }

    private static StoryContentV1 ValidDraft() => Draft();

    private static StoryContentV1 Draft(
        string? title = null,
        StorySegmentV1? hook = null,
        StorySegmentV1[]? paragraphs = null,
        StorySegmentV1? ending = null,
        string? description = null,
        string[]? tags = null,
        string[]? warnings = null,
        StoryStructureV1? structure = null,
        int bodyTokens = ValidBodyTokens)
    {
        var draft = new StoryContentV1(
            title ?? "Căn nhà cuối hành lang",
            hook ?? DefaultHook(),
            paragraphs ?? DefaultParagraphs(),
            ending ?? DefaultEnding(),
            description ?? "Một chuyện ma nhẹ nhàng về căn nhà bên sông.",
            tags ?? DefaultTags(),
            warnings ?? DefaultWarnings(),
            structure ?? DefaultStructure());

        return PadBody(draft, bodyTokens);
    }

    private static StorySegmentV1 DefaultHook() =>
        new("Đêm ấy mưa rất to, người kể ngồi một mình trong căn nhà cũ ven sông.", ["old house by the river"], []);

    private static StorySegmentV1[] DefaultParagraphs()
    {
        StorySegmentV1[] paragraphs =
        [
            new("Căn nhà thuê rẻ bất thường, chủ nhà chỉ dặn đừng mở cánh cửa cuối hành lang.", ["empty chair in the hallway"], [], "setup"),
            new("Tiếng gõ vang lên từ trong đó, và khi anh mở ra thì thấy chiếc ghế cũ vẫn còn ấm.", ["rain on the window"], [], "twist"),
            new("Anh kể lại chuyện ấy cho người hàng xóm, và người ấy chỉ im lặng nhìn ra sân.", ["figure at the door"], [], "reveal"),
        ];

        return paragraphs;
    }

    private static StorySegmentV1 DefaultEnding() =>
        new("Anh trả nhà ngay hôm sau, không dám kể cho ai nghe chuyện mình đã thấy.", ["letter on the table"], []);

    private static string[] DefaultTags() => ["truyện ma", "tâm linh"];

    private static string[] DefaultWarnings() => ["supernatural"];

    private static StoryStructureV1 DefaultStructure() => new(
        "truyện ma tâm linh",
        "ngôi nhà cũ",
        "một cuộc điện thoại lúc nửa đêm",
        "người kể là nguyên nhân",
        "first_person");

    /// <summary>The same draft with <paramref name="control"/> appended to one named free-text field.</summary>
    private static StoryContentV1 WithControlCharacter(StoryContentV1 draft, string field, string control)
    {
        var paragraphs = draft.Paragraphs.ToArray();
        var tags = draft.Tags.ToArray();
        switch (field)
        {
            case "title":
                return draft with { Title = draft.Title + control };
            case "description":
                return draft with { Description = draft.Description + control };
            case "tags[1]":
                tags[1] += control;
                return draft with { Tags = tags };
            case "hook.beat":
                return draft with { Hook = draft.Hook with { Beat = (draft.Hook.Beat ?? string.Empty) + control } };
            case "paragraphs[1].beat":
                paragraphs[1] = paragraphs[1] with { Beat = paragraphs[1].Beat + control };
                return draft with { Paragraphs = paragraphs };
            case "ending.beat":
                return draft with { Ending = draft.Ending with { Beat = (draft.Ending.Beat ?? string.Empty) + control } };
            case "hook.text":
                return draft with { Hook = draft.Hook with { Text = draft.Hook.Text + control } };
            case "paragraphs[1].text":
                paragraphs[1] = paragraphs[1] with { Text = paragraphs[1].Text + control };
                return draft with { Paragraphs = paragraphs };
            case "ending.text":
                return draft with { Ending = draft.Ending with { Text = draft.Ending.Text + control } };
            case "structure.sub_genre":
                return draft with { Structure = draft.Structure with { SubGenre = draft.Structure.SubGenre + control } };
            case "structure.setting":
                return draft with { Structure = draft.Structure with { Setting = draft.Structure.Setting + control } };
            case "structure.opening":
                return draft with { Structure = draft.Structure with { Opening = draft.Structure.Opening + control } };
            case "structure.twist_type":
                return draft with { Structure = draft.Structure with { TwistType = draft.Structure.TwistType + control } };
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown field.");
        }
    }

    /// <summary>The same draft with one paragraph's text replaced; the body is re-padded to the band.</summary>
    private static StoryContentV1 WithParagraphText(int index, string text)
    {
        var paragraphs = DefaultParagraphs();
        paragraphs[index] = paragraphs[index] with { Text = text };
        return Draft(paragraphs: paragraphs);
    }

    /// <summary>The same draft with one paragraph's keywords replaced.</summary>
    private static StoryContentV1 WithParagraphKeywords(int index, string[] keywords)
    {
        var paragraphs = DefaultParagraphs();
        paragraphs[index] = paragraphs[index] with { Keywords = keywords };
        return Draft(paragraphs: paragraphs);
    }

    /// <summary>
    /// Pads the last paragraph with the filler syllable so the body holds exactly <paramref name="tokens"/>
    /// D4 tokens — the fixture must sit inside the short/vi band of 195–260, not merely pass the validator.
    /// </summary>
    private static StoryContentV1 PadBody(StoryContentV1 draft, int tokens)
    {
        var paragraphs = draft.Paragraphs.ToArray();
        var others = Words(draft.Hook.Text) + Words(draft.Ending.Text);
        for (var index = 0; index < paragraphs.Length - 1; index++)
        {
            others += Words(paragraphs[index].Text);
        }

        var last = paragraphs.Length - 1;
        paragraphs[last] = paragraphs[last] with { Text = Fill(paragraphs[last].Text, tokens - others) };
        return draft with { Paragraphs = paragraphs };
    }

    /// <summary>Appends the filler syllable until the text holds exactly <paramref name="tokens"/> words.</summary>
    private static string Fill(string text, int tokens)
    {
        var words = Words(text);
        return words >= tokens
            ? text
            : text + " " + string.Join(' ', Enumerable.Repeat("rồi", tokens - words));
    }

    /// <summary>A whitespace word count kept independent of <see cref="StoryText.Tokenize"/> for the fixture.</summary>
    private static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string[] SixteenTags()
    {
        return Enumerable
            .Range(0, 16)
            .Select(index => string.Concat("tag", index.ToString(CultureInfo.InvariantCulture)))
            .ToArray();
    }

    /// <summary>A whitespace-free dump of every member, so a mutation cannot hide.</summary>
    private static string Snapshot(StoryContentV1 draft) => string.Join(
        '|',
        draft.Title,
        draft.Hook.Text,
        string.Join(',', draft.Hook.Keywords),
        string.Join(';', draft.Paragraphs.Select(paragraph => paragraph.Text)),
        string.Join(',', draft.Paragraphs.SelectMany(paragraph => paragraph.Keywords)),
        string.Join(',', draft.Paragraphs.SelectMany(paragraph => paragraph.FactRefs)),
        string.Join(',', draft.Paragraphs.Select(paragraph => paragraph.Beat)),
        draft.Ending.Text,
        draft.Description,
        string.Join(',', draft.Tags),
        string.Join(',', draft.ContentWarnings),
        draft.Structure.SubGenre,
        draft.Structure.Setting,
        draft.Structure.Opening,
        draft.Structure.TwistType,
        draft.Structure.NarratorMode);

    private static bool IsD22Address(string segment) =>
        segment is "title" or "hook" or "ending" or "meta"
        || (segment.StartsWith("p:", StringComparison.Ordinal)
            && int.TryParse(segment.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out _));

    /// <summary>
    /// Serialises the draft with the persistence layer's snake_case policy and asserts the property names
    /// are exactly the schema's <c>required</c> sets, so a renamed member cannot leave
    /// <c>prompts/schemas/story.v1.json</c> behind.
    /// </summary>
    private static void AssertSchemaShape(StoryContentV1 draft)
    {
        var json = JsonSerializer.Serialize(draft, DraftJson);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.EnumerateObject().Select(member => member.Name)
            .ShouldBe(SchemaKeys("story.v1.json"), ignoreOrder: true);
        root.GetProperty("hook").EnumerateObject().Select(member => member.Name)
            .ShouldBe(SchemaKeys("story.v1.json", "$defs", "segment"), ignoreOrder: true);
        root.GetProperty("paragraphs")[0].EnumerateObject().Select(member => member.Name)
            .ShouldBe(SchemaKeys("story.v1.json", "$defs", "paragraph"), ignoreOrder: true);
        root.GetProperty("structure").EnumerateObject().Select(member => member.Name)
            .ShouldBe(SchemaKeys("story.v1.json", "properties", "structure"), ignoreOrder: true);
    }

    /// <summary>The <c>required</c> keys of one object of the committed schema file.</summary>
    private static string[] SchemaKeys(string fileName, params string[] path)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "prompts", "schemas", fileName)));
        var element = document.RootElement;
        foreach (var step in path)
        {
            element = element.GetProperty(step);
        }

        return [.. element.GetProperty("required").EnumerateArray().Select(value => value.GetString() ?? string.Empty)];
    }

    /// <summary>The repository root, found by walking up from the test output to <c>NightTales.sln</c>.</summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NightTales.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root (NightTales.sln) above the test output.");
    }

    private static StoryRules Rules()
    {
        return new StoryRules
        {
            StructureVocab = new HashSet<string>(Vocabulary, StringComparer.Ordinal),
        };
    }

    private static StoryRules StandardVi()
    {
        return Rules() with
        {
            Length = LengthTargetResolver.Resolve(LengthTier.Standard, Language.Vi, PriceLimits.D4Defaults),
        };
    }
}
