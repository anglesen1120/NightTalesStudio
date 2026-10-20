using System.Diagnostics;
using System.Text;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Gates;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Gates;

/// <summary>
/// QA probes of <see cref="RealWorldSignalDetector"/> (02.8.2-T2, written by the QA agent, not by the
/// implementer): hostile input size and codepoints, mixed normalisation forms, the year-only and cross-segment
/// negatives the co-occurrence rule exists for, and case folding outside ASCII.
/// </summary>
/// <remarks>
/// Every case is pure Domain code, so the only budget here is time: the huge-input cases assert the detector
/// stays linear (source-generated <c>NonBacktracking</c> patterns and an ordinal phrase search), which is the
/// class of regression no correctness assertion would catch.
/// </remarks>
public sealed class RealWorldSignalDetectorQaTests
{
    private static readonly IReadOnlyList<string> ViFraming = ["chuyện có thật", "dựa trên sự kiện có thật"];

    private static readonly IReadOnlyList<string> EnFraming = ["based on real events", "true story"];

    private static readonly IReadOnlyList<RealWorldClaim> NoClaims = [];

    /// <summary>
    /// A one-megabyte segment of a date pattern plus one incident word must still answer in linear time and
    /// quote at most 200 characters of evidence — the hostile input a generated paragraph can reach.
    /// </summary>
    [Fact]
    public void RealWorldSignalDetectorQa_HugeRepeatedDateSegment_IsLinearAndQuotesAtMost200Chars()
    {
        var block = string.Concat(Enumerable.Repeat("ngay 5 thang 3 ", 60_000));
        block.Length.ShouldBeGreaterThanOrEqualTo(900_000, "this probe is about a megabyte-scale segment");

        var segments = StoryTextSegments.Create(
            null,
            null,
            ["Cảnh sát mở lại hồ sơ vụ án. " + block],
            null,
            null);

        var watch = Stopwatch.StartNew();
        var signal = RealWorldSignalDetector.Detect(segments, ViFraming, NoClaims, RealWorldOptions.Defaults);
        watch.Stop();

        signal.Hit.ShouldBeTrue();
        var hit = signal.Hits.ShouldHaveSingleItem();
        hit.Kind.ShouldBe(RealWorldSignalKind.DatedIncident);
        hit.Segment.ShouldBe("p:0");
        hit.Evidence.Length.ShouldBeLessThanOrEqualTo(ValidationIssue.MaxEvidenceChars);
        watch.Elapsed.ShouldBeLessThan(
            TimeSpan.FromSeconds(20),
            "the date patterns are NonBacktracking and the phrase search is ordinal, so a megabyte is a single pass");
    }

    /// <summary>
    /// A repeated framing phrase over two million characters yields one hit (the first occurrence), not one hit
    /// per occurrence, and stays linear — otherwise a loop of prose would multiply the stored evidence.
    /// </summary>
    [Fact]
    public void RealWorldSignalDetectorQa_TwoMillionCharRepeatedPhrase_YieldsOneHitPerPhrase()
    {
        var block = string.Concat(Enumerable.Repeat("chuyện có thật. ", 150_000));
        block.Length.ShouldBeGreaterThanOrEqualTo(2_000_000);

        var segments = StoryTextSegments.Create(null, null, [block], null, null);

        var watch = Stopwatch.StartNew();
        var signal = RealWorldSignalDetector.Detect(segments, ViFraming, NoClaims, RealWorldOptions.Defaults);
        watch.Stop();

        signal.Hits.Count.ShouldBe(1, "one phrase of the lexicon matched, and only its first occurrence is reported");
        signal.Hits[0].Kind.ShouldBe(RealWorldSignalKind.FramingLexicon);
        signal.Hits[0].Evidence.Length.ShouldBeLessThanOrEqualTo(ValidationIssue.MaxEvidenceChars);
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
    }

    /// <summary>
    /// A phrase written partly in NFC and partly in NFD (the two forms a Vietnamese editor produces) must still
    /// hit, and so must the mirror case: a decomposed lexicon line against composed text.
    /// </summary>
    [Fact]
    public void RealWorldSignalDetectorQa_MixedNfcNfdText_StillHits()
    {
        // The first two words are composed, the rest decomposed: e + circumflex + dot below stays three codepoints.
        var mixed = "dựa trên " + "sự kiện có thật".Normalize(NormalizationForm.FormD);
        mixed.Length.ShouldBeGreaterThan(
            mixed.Normalize(NormalizationForm.FormC).Length,
            "the input must really be mixed: the decomposed half carries combining marks");

        var segments = StoryTextSegments.Create(null, "Đoạn này " + mixed + ".", [], null, null);

        var signal = RealWorldSignalDetector.Detect(segments, ViFraming, NoClaims, RealWorldOptions.Defaults);

        var hit = signal.Hits.ShouldHaveSingleItem();
        hit.MatchedPhrase.ShouldBe("dựa trên sự kiện có thật");
        hit.Segment.ShouldBe("hook");
    }

    /// <summary>The lexicon side may be decomposed too: the release file is NFC, but the contract is not form-bound.</summary>
    [Fact]
    public void RealWorldSignalDetectorQa_DecomposedLexiconPhrase_MatchesComposedText()
    {
        var decomposedPhrase = "dựa trên sự kiện có thật".Normalize(NormalizationForm.FormD);
        var segments = StoryTextSegments.Create(null, "Đây là câu chuyện dựa trên sự kiện có thật.", [], null, null);

        var signal = RealWorldSignalDetector.Detect(segments, [decomposedPhrase], NoClaims, RealWorldOptions.Defaults);

        signal.Hit.ShouldBeTrue();
        signal.Hits.ShouldHaveSingleItem().Segment.ShouldBe("hook");
    }

    /// <summary>
    /// A year alone is never a signal: "in 1999 the ship" is ordinary fiction, with and without an incident word,
    /// in both co-occurrence modes.
    /// </summary>
    [Fact]
    public void RealWorldSignalDetectorQa_EnglishYearOnly_NoHit()
    {
        var yearOnly = StoryTextSegments.Create("In 1999 the ship", "In 1999 the ship left the harbour.", [], null, null);
        RealWorldSignalDetector.Detect(yearOnly, EnFraming, NoClaims, RealWorldOptions.Defaults)
            .Hit.ShouldBeFalse("a bare year co-occurs with ordinary fiction dates");

        RealWorldSignalDetector.Detect(
                yearOnly,
                EnFraming,
                NoClaims,
                RealWorldOptions.Defaults with { RequireIncidentCoOccurrence = false })
            .Hit.ShouldBeFalse("a bare year is never a specific date, in either mode");
    }

    /// <summary>
    /// The co-occurrence rule with an explicit English day: "12 March 2019 … the murder case" is the signal the rule
    /// exists for, and the whole date shape decides it.
    /// </summary>
    [Fact]
    public void RealWorldSignalDetectorQa_EnglishDateWithDayAndIncidentWord_Hits()
    {
        var segments = StoryTextSegments.Create(
            null,
            "On 12 March 2019 the murder case was reopened.",
            [],
            null,
            null);

        var hit = RealWorldSignalDetector.Detect(segments, EnFraming, NoClaims, RealWorldOptions.Defaults)
            .Hits.ShouldHaveSingleItem();

        hit.Kind.ShouldBe(RealWorldSignalKind.DatedIncident);
        hit.MatchedPhrase.ShouldBe("murder case");
    }

    /// <summary>
    /// The task file's own English example — "In March 2019 … the murder case" (task §"Real-world detection",
    /// <c>DatedIncident</c> row) — must hit. A month and a year without a day is the most common real-world framing
    /// there is, so this probe pins the contract; a red verdict here is the defect, not a wrong expectation.
    /// </summary>
    [Fact]
    public void RealWorldSignalDetectorQa_TaskFileEnglishExample_MonthAndYearWithIncidentWord_Hits()
    {
        var segments = StoryTextSegments.Create(
            null,
            "In March 2019 the murder case was reopened.",
            [],
            null,
            null);

        var signal = RealWorldSignalDetector.Detect(segments, EnFraming, NoClaims, RealWorldOptions.Defaults);

        var hit = signal.Hits.ShouldHaveSingleItem();
        hit.Kind.ShouldBe(RealWorldSignalKind.DatedIncident);
    }

    /// <summary>The co-occurrence rule is per segment in English too: a date in the title, the incident in the hook.</summary>
    [Fact]
    public void RealWorldSignalDetectorQa_EnglishDateAndIncidentInDifferentSegments_NoHit()
    {
        var segments = StoryTextSegments.Create(
            "The night of March 12, 2019",
            "The harbour was quiet.",
            ["A man walked to the pier.", "The lamps went out."],
            "Nothing was ever proven.",
            "A short mystery.");

        RealWorldSignalDetector.Detect(segments, EnFraming, NoClaims, RealWorldOptions.Defaults)
            .Hit.ShouldBeFalse();
    }

    /// <summary>
    /// Hostile lexicon data cannot throw and cannot match: an unpaired surrogate and a noncharacter fold to
    /// nothing and are dropped, while the usable line still matches.
    /// </summary>
    [Fact]
    public void RealWorldSignalDetectorQa_HostileLexiconEntries_AreDroppedNotThrown()
    {
        IReadOnlyList<string> phrases = ["\uD800", "\uFFFE", "   ", "\uDC00\uD800", "chuyện có thật"];
        var segments = StoryTextSegments.Create(null, "Đây là chuyện có thật.", [], null, null);

        var signal = RealWorldSignalDetector.Detect(segments, phrases, NoClaims, RealWorldOptions.Defaults);

        signal.Hits.ShouldHaveSingleItem().MatchedPhrase.ShouldBe("chuyện có thật");
    }

    /// <summary>
    /// Upper-case Vietnamese (the owner's phone keyboard capitalises) matches, and a Turkish capital İ in the
    /// segment neither throws nor stops the ASCII phrase from matching.
    /// </summary>
    [Fact]
    public void RealWorldSignalDetectorQa_UppercaseVietnameseAndTurkishCapital_StillHits()
    {
        var vietnamese = StoryTextSegments.Create(null, "CHUYỆN CÓ THẬT, ANH TA NÓI.", [], null, null);
        RealWorldSignalDetector.Detect(vietnamese, ViFraming, NoClaims, RealWorldOptions.Defaults)
            .Hits.ShouldHaveSingleItem().Segment.ShouldBe("hook");

        var turkish = StoryTextSegments.Create(null, "BASED ON REAL EVENTS — İSTANBUL'DA BİR GECE.", [], null, null);
        RealWorldSignalDetector.Detect(turkish, EnFraming, NoClaims, RealWorldOptions.Defaults)
            .Hits.ShouldHaveSingleItem().MatchedPhrase.ShouldBe("based on real events");
    }

    /// <summary>
    /// A lone surrogate or a noncharacter in the segment itself is dropped before matching, and the hit on the
    /// remaining text survives — the ICU path on Linux refuses such input (02.6.3 precedent).
    /// </summary>
    [Fact]
    public void RealWorldSignalDetectorQa_LoneSurrogateHeavySegment_StillHitsTheCleanPart()
    {
        var text = string.Concat(Enumerable.Repeat("\uD800", 50)) + "dựa trên sự kiện có thật" + new string('\uDC00', 50);
        var segments = StoryTextSegments.Create(null, text, [], null, null);

        var signal = RealWorldSignalDetector.Detect(segments, ViFraming, NoClaims, RealWorldOptions.Defaults);

        signal.Hits.ShouldHaveSingleItem().Segment.ShouldBe("hook");
    }
}
