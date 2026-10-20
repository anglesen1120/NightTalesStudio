using System.Text;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Gates;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Gates;

/// <summary>
/// The real-world detector of 02.8.2-T2 (F31): the three signals, the D22 addressing and evidence rules, and the
/// hostile-input cases. Pure Domain code, so there is no mock, no clock and no database here.
/// </summary>
public sealed class RealWorldSignalDetectorTests
{
    private static readonly IReadOnlyList<string> ViFraming =
    [
        "chuyện có thật",
        "dựa trên sự kiện có thật",
        "người thật việc thật",
    ];

    private static readonly IReadOnlyList<string> EnFraming =
    [
        "based on real events",
        "based on a true story",
        "true story",
    ];

    private static readonly IReadOnlyList<RealWorldClaim> NoClaims = [];

    [Fact]
    public void RealWorldSignalDetector_VietnameseFramingPhrase_Hits()
    {
        var segments = StoryTextSegments.Create(
            "Một đêm ở Hà Nội",
            "Trần nhà thấp và ẩm.",
            [
                "Một người đàn ông đi vào ngõ nhỏ.",
                "Chiếc đèn nhấp nháy rồi tắt.",
                "Câu chuyện này dựa trên sự kiện có thật mà tôi từng đọc.",
            ],
            "Anh ta không bao giờ quay lại.",
            "Một câu chuyện về Hà Nội.");

        var signal = RealWorldSignalDetector.Detect(segments, ViFraming, NoClaims, RealWorldOptions.Defaults);

        signal.Hit.ShouldBeTrue();
        var hit = signal.Hits.ShouldHaveSingleItem();
        hit.Segment.ShouldBe("p:2");
        hit.Kind.ShouldBe(RealWorldSignalKind.FramingLexicon);
        hit.MatchedPhrase.ShouldBe("dựa trên sự kiện có thật");
        hit.Evidence.ShouldContain("dựa trên sự kiện có thật");
    }

    [Fact]
    public void RealWorldSignalDetector_EnglishFramingPhrase_Hits()
    {
        var segments = StoryTextSegments.Create(
            "The Last Bus",
            "The podcast is based on real events from a small town.",
            ["The driver counted the passengers twice.", "Nobody answered."],
            "The bus left at dawn.",
            "A short mystery.");

        var signal = RealWorldSignalDetector.Detect(segments, EnFraming, NoClaims, RealWorldOptions.Defaults);

        signal.Hit.ShouldBeTrue();
        var hit = signal.Hits.ShouldHaveSingleItem();
        hit.Segment.ShouldBe("hook");
        hit.Kind.ShouldBe(RealWorldSignalKind.FramingLexicon);
        hit.MatchedPhrase.ShouldBe("based on real events");
    }

    [Fact]
    public void RealWorldSignalDetector_DateWithoutIncidentWord_NoHit()
    {
        // Task §"Incident words": a bare year co-occurs with ordinary fiction dates and is never a hit on its own.
        var segments = StoryTextSegments.Create(
            "Năm 2020",
            "Năm 2020 là một năm khó khăn.",
            ["Gia đình tôi chuyển đến thành phố."],
            "Mọi thứ dần ổn hơn.",
            null);

        var signal = RealWorldSignalDetector.Detect(segments, ViFraming, NoClaims, RealWorldOptions.Defaults);

        signal.Hit.ShouldBeFalse();
        signal.Hits.ShouldBeEmpty();
    }

    [Fact]
    public void RealWorldSignalDetector_BareYearWithIncidentWordSameSegment_Hits()
    {
        // Task §"Incident words": a bare year is never a hit alone, but in a segment that also carries an incident
        // word it counts — the other half of the co-occurrence rule the NoHit test above pins.
        var segments = StoryTextSegments.Create(
            null,
            "Năm 2020, cảnh sát mở lại hồ sơ và xác định đây là một vụ án.",
            [],
            null,
            null);

        var signal = RealWorldSignalDetector.Detect(segments, ViFraming, NoClaims, RealWorldOptions.Defaults);

        var hit = signal.Hits.ShouldHaveSingleItem();
        hit.Kind.ShouldBe(RealWorldSignalKind.DatedIncident);
        hit.Segment.ShouldBe("hook");
        hit.MatchedPhrase.ShouldBe("vụ án");
    }

    [Fact]
    public void RealWorldSignalDetector_DateAndIncidentWordSameSegment_Hits()
    {
        var segments = StoryTextSegments.Create(
            "Hồ sơ cũ",
            "Căn nhà vẫn còn đó.",
            [
                "Buổi sáng trời mưa.",
                "Ngày 12/3/2019, cảnh sát kết luận đây là một vụ án chưa có lời giải.",
            ],
            "Không ai nhắc lại chuyện đó.",
            null);

        var signal = RealWorldSignalDetector.Detect(segments, ViFraming, NoClaims, RealWorldOptions.Defaults);

        signal.Hit.ShouldBeTrue();
        var hit = signal.Hits.ShouldHaveSingleItem();
        hit.Segment.ShouldBe("p:1");
        hit.Kind.ShouldBe(RealWorldSignalKind.DatedIncident);
        hit.MatchedPhrase.ShouldBe("vụ án");
        hit.Evidence.ShouldBe(segments[3].Text);
    }

    [Fact]
    public void RealWorldSignalDetector_DateInOneSegmentIncidentInAnother_NoHit()
    {
        // The co-occurrence rule is per segment: a date in one paragraph and an incident word in the next is not a hit.
        var segments = StoryTextSegments.Create(
            "Một ngày mưa",
            "Trời mưa từ sáng.",
            [
                "Ngày 12/3/2019 trời mưa rất to.",
                "Cảnh sát đã bắt giữ một người đàn ông.",
            ],
            "Anh ta được thả sau đó.",
            null);

        var signal = RealWorldSignalDetector.Detect(segments, ViFraming, NoClaims, RealWorldOptions.Defaults);

        signal.Hit.ShouldBeFalse();
        signal.Hits.ShouldBeEmpty();
    }

    [Fact]
    public void RealWorldSignalDetector_ModelClaimsNonEmpty_Hits()
    {
        var segments = StoryTextSegments.Create(
            "The Long Night",
            "The phone rang once.",
            ["She wrote the address on her hand.", "The house was empty."],
            "She never called back.",
            null);

        IReadOnlyList<RealWorldClaim> claims = [new("p:1")];

        var signal = RealWorldSignalDetector.Detect(segments, EnFraming, claims, RealWorldOptions.Defaults);

        signal.Hit.ShouldBeTrue();
        var hit = signal.Hits.ShouldHaveSingleItem();
        hit.Segment.ShouldBe("p:1");
        hit.Kind.ShouldBe(RealWorldSignalKind.ModelClaim);
        hit.MatchedPhrase.ShouldBe(string.Empty);
        hit.Evidence.ShouldBe("The house was empty.");
    }

    [Fact]
    public void RealWorldSignalDetector_CleanFiction_NoHit()
    {
        var segments = StoryTextSegments.Create(
            "Ngọn đèn cuối ngõ",
            "Gió thổi qua mái tôn.",
            [
                "Bà cụ bán bánh mì dọn hàng lúc chín giờ.",
                "Đứa trẻ ngồi đợi bên vệ đường.",
                "Một chiếc xe đạp dừng lại rồi đi.",
            ],
            "Ngọn đèn tắt, con ngõ chìm vào bóng tối.",
            "Truyện ngắn về một con ngõ nhỏ.");

        var signal = RealWorldSignalDetector.Detect(segments, ViFraming, NoClaims, RealWorldOptions.Defaults);

        signal.Hit.ShouldBeFalse();
        signal.Hits.ShouldBeEmpty();
    }

    [Fact]
    public void RealWorldSignalDetector_DiacriticsInsensitive_Match()
    {
        // The task §Notes fold rule: owners type without diacritics, so both sides are folded onto the base letters.
        var segments = StoryTextSegments.Create(
            null,
            "Chuyen co that, toi da doc ve no.",
            [],
            null,
            null);

        var signal = RealWorldSignalDetector.Detect(segments, ViFraming, NoClaims, RealWorldOptions.Defaults);

        signal.Hit.ShouldBeTrue();
        var hit = signal.Hits.ShouldHaveSingleItem();
        hit.Segment.ShouldBe("hook");
        hit.MatchedPhrase.ShouldBe("chuyện có thật");
        hit.Evidence.ShouldBe(segments[0].Text);
    }

    [Fact]
    public void RealWorldSignalDetector_DecomposedInput_MatchesAfterNfc()
    {
        // NFD input must gate on the same rule as NFC input: the detector composes before matching.
        var decomposed = "Đây là chuyện có thật.".Normalize(NormalizationForm.FormD);
        var segments = StoryTextSegments.Create(null, decomposed, [], null, null);

        var signal = RealWorldSignalDetector.Detect(segments, ViFraming, NoClaims, RealWorldOptions.Defaults);

        signal.Hit.ShouldBeTrue();
        signal.Hits.ShouldHaveSingleItem().Segment.ShouldBe("hook");
    }

    [Fact]
    public void RealWorldSignalDetector_EvidenceTruncatedTo200Chars()
    {
        var paragraph = string.Concat(Enumerable.Repeat("Một đoạn văn dài không có dấu hiệu nào. ", 12))
            + "Cuối cùng thì đây là chuyện có thật.";

        var segments = StoryTextSegments.Create(null, null, [paragraph], null, null);

        var signal = RealWorldSignalDetector.Detect(segments, ViFraming, NoClaims, RealWorldOptions.Defaults);

        var hit = signal.Hits.ShouldHaveSingleItem();
        hit.Segment.ShouldBe("p:0");
        hit.Evidence.Length.ShouldBeLessThanOrEqualTo(ValidationIssue.MaxEvidenceChars);
        hit.Evidence.ShouldContain("chuyện có thật");
        paragraph.ShouldContain(hit.Evidence);
    }

    [Fact]
    public void RealWorldSignalDetector_SegmentAddressing_UsesD22Ids()
    {
        var segments = StoryTextSegments.Create(
            "Chuyện có thật",
            "Chuyện có thật, phần mở đầu.",
            ["Đoạn văn thứ nhất.", "Đoạn văn thứ hai.", "Đoạn văn thứ ba.", "Chuyện có thật, phần thân."],
            "Chuyện có thật, phần kết.",
            "Chuyện có thật, phần mô tả.");

        var signal = RealWorldSignalDetector.Detect(segments, ViFraming, NoClaims, RealWorldOptions.Defaults);

        signal.Hit.ShouldBeTrue();
        signal.Hits.Select(hit => hit.Segment).ShouldBe(["title", "hook", "p:3", "ending", "meta"]);
        signal.Hits.ShouldAllBe(hit => hit.Kind == RealWorldSignalKind.FramingLexicon);
    }

    [Fact]
    public void RealWorldSignalDetector_EnglishPhrase_IsCaseInsensitive()
    {
        var segments = StoryTextSegments.Create(
            null,
            "BASED ON REAL EVENTS, they said, and nobody checked.",
            [],
            null,
            null);

        var signal = RealWorldSignalDetector.Detect(segments, EnFraming, NoClaims, RealWorldOptions.Defaults);

        signal.Hit.ShouldBeTrue();
        signal.Hits.ShouldHaveSingleItem().MatchedPhrase.ShouldBe("based on real events");
    }

    [Fact]
    public void RealWorldSignalDetector_PhraseInsideLongerWord_NoHit()
    {
        // "untrue story" contains "true story": a whole-word match is what keeps that out.
        var insideWord = StoryTextSegments.Create(null, "This is an untrue story about a boat.", [], null, null);

        RealWorldSignalDetector.Detect(insideWord, ["true story"], NoClaims, RealWorldOptions.Defaults)
            .Hit.ShouldBeFalse();

        // ... and the same phrase still hits when the boundary is real, so the negative cannot pass for the wrong reason.
        var wholeWord = StoryTextSegments.Create(null, "This is a true story about a boat.", [], null, null);

        RealWorldSignalDetector.Detect(wholeWord, ["true story"], NoClaims, RealWorldOptions.Defaults)
            .Hit.ShouldBeTrue();
    }

    [Fact]
    public void RealWorldSignalDetector_NoncharacterAndLoneSurrogate_DoesNotThrow()
    {
        // ICU refuses U+FFFE on Linux and a lone surrogate is not a scalar value: the detector drops both and still
        // reports the hit, instead of throwing on hostile text (02.6.3 gate precedent).
        var segments = StoryTextSegments.Create(
            null,
            "Đây là chuyện có thật\uFFFE và \uD800 kết thúc.",
            ["\uD800\uD800"],
            null,
            null);

        var signal = RealWorldSignalDetector.Detect(segments, ViFraming, NoClaims, RealWorldOptions.Defaults);

        signal.Hit.ShouldBeTrue();
        signal.Hits.ShouldHaveSingleItem().Segment.ShouldBe("hook");
        signal.Hits.ShouldNotContain(hit => hit.Segment == "p:0");
    }

    [Fact]
    public void RealWorldSignalDetector_EmptyInput_NoHit()
    {
        var blank = StoryTextSegments.Create(null, "   ", [], null, null);

        blank.Count.ShouldBe(0);
        RealWorldSignalDetector.Detect(blank, ViFraming, NoClaims, RealWorldOptions.Defaults).Hit.ShouldBeFalse();
        RealWorldSignalDetector.Detect(StoryTextSegments.Empty, [], [], RealWorldOptions.Defaults).Hit.ShouldBeFalse();
    }

    [Fact]
    public void RealWorldSignalDetector_ClaimWithUnknownSegment_HitsWithEmptyEvidence()
    {
        var segments = StoryTextSegments.Create("Title", "Hook.", ["Body."], "Ending.", null);

        IReadOnlyList<RealWorldClaim> claims = [new("p:99")];

        var signal = RealWorldSignalDetector.Detect(segments, EnFraming, claims, RealWorldOptions.Defaults);

        var hit = signal.Hits.ShouldHaveSingleItem();
        hit.Kind.ShouldBe(RealWorldSignalKind.ModelClaim);
        hit.Segment.ShouldBe("p:99");
        hit.Evidence.ShouldBeEmpty();
    }

    [Fact]
    public void RealWorldSignalDetector_CoOccurrenceOff_BareYearStillNoHit()
    {
        // Even with the co-occurrence requirement switched off a bare year never hits, and an incident word does not
        // change that: only a specific date does.
        var options = RealWorldOptions.Defaults with { RequireIncidentCoOccurrence = false };
        var segments = StoryTextSegments.Create(
            null,
            "Năm 2020 cảnh sát đã bắt giữ hung thủ.",
            [],
            null,
            null);

        RealWorldSignalDetector.Detect(segments, ViFraming, NoClaims, options).Hit.ShouldBeFalse();
    }

    [Fact]
    public void RealWorldSignalDetector_CoOccurrenceOff_SpecificDateIsEnough()
    {
        var options = RealWorldOptions.Defaults with { RequireIncidentCoOccurrence = false };
        var segments = StoryTextSegments.Create(null, "Ngày 12/3/2019 trời mưa rất to.", [], null, null);

        var signal = RealWorldSignalDetector.Detect(segments, ViFraming, NoClaims, options);

        var hit = signal.Hits.ShouldHaveSingleItem();
        hit.Kind.ShouldBe(RealWorldSignalKind.DatedIncident);
        hit.MatchedPhrase.ShouldBe("Ngày 12/3/2019");
    }

    [Fact]
    public void RealWorldSignalDetector_HitsOrderedBySegmentThenKind()
    {
        var segments = StoryTextSegments.Create(
            "Chuyện có thật",
            "Một buổi sáng.",
            ["Ngày 12/3/2019 cảnh sát kết luận đây là một vụ án, và đó là chuyện có thật."],
            null,
            null);

        var signal = RealWorldSignalDetector.Detect(segments, ViFraming, NoClaims, RealWorldOptions.Defaults);

        signal.Hits.Select(hit => (hit.Segment, hit.Kind)).ShouldBe(
        [
            ("title", RealWorldSignalKind.FramingLexicon),
            ("p:0", RealWorldSignalKind.FramingLexicon),
            ("p:0", RealWorldSignalKind.DatedIncident),
        ]);
    }

    [Fact]
    public void StoryTextSegments_Create_SkipsBlankTextsAndKeepsTheParagraphIndex()
    {
        var segments = StoryTextSegments.Create("Title", "Hook.", ["một", "   ", "ba"], "Ending.", null);

        segments.Select(segment => segment.Id).ShouldBe(["title", "hook", "p:0", "p:2", "ending"]);
        segments.Count.ShouldBe(5);
    }

    [Fact]
    public void StoryTextSegments_From_StoryContent_IncludesDescriptionAndTagsAsMeta()
    {
        var content = new StoryContentV1(
            "Title",
            new StorySegmentV1("Hook.", ["a keyword"], []),
            [new StorySegmentV1("Body one.", ["a keyword"], []), new StorySegmentV1("Body two.", ["a keyword"], [])],
            new StorySegmentV1("Ending.", ["a keyword"], []),
            "The description.",
            ["tag one", "tag two"],
            ["violence"],
            new StoryStructureV1("mystery", "a small town", "cold open", "reveal", "first_person"));

        var segments = StoryTextSegments.From(content);

        segments.Select(segment => segment.Id).ShouldBe(["title", "hook", "p:0", "p:1", "ending", "meta"]);
        segments[^1].Text.ShouldBe("The description.\ntag one\ntag two");
        StoryTextSegments.From(null).Count.ShouldBe(0);
    }

    [Fact]
    public void RealWorldOptions_Defaults_RequireCoOccurrenceAndCarryBothLanguages()
    {
        RealWorldOptions.Defaults.RequireIncidentCoOccurrence.ShouldBeTrue();
        RealWorldOptions.Defaults.IncidentWords.ShouldContain("vụ án");
        RealWorldOptions.Defaults.IncidentWords.ShouldContain("murder case");
        RealWorldOptions.Defaults.IncidentWords.Count.ShouldBe(23);
    }
}
