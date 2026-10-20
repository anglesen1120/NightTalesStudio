using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Domain.Common;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Gates;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Autopilot.Gates;

/// <summary>
/// <see cref="RealWorldSignalService"/> (02.8.2-T2 step 4, plan §3.1 check 8): it composes the detector's inputs from
/// the stored content, the release lexicon of the series language and the review model's claims, and it answers
/// "unknown" — never "clean" — whenever one of them could not be read.
/// </summary>
public sealed class RealWorldSignalServiceTests
{
    private static readonly string[] PhraseVi = ["dựa trên sự kiện có thật"];

    private static readonly string[] PhraseEn = ["based on real events"];

    private static readonly RealWorldOptions Options = RealWorldOptions.Defaults;

    /// <summary>The Vietnamese phrase fires for a Vietnamese series, and the lexicon is chosen by language.</summary>
    [Fact]
    public void Detect_VietnamesePhrase_FiresForVietnameseSeries()
    {
        var service = Service();

        var signal = service.Detect(Content("Câu chuyện này dựa trên sự kiện có thật."), Language.Vi, [], Options);

        signal.ShouldNotBeNull().Hit.ShouldBeTrue();
        signal!.Hits.ShouldHaveSingleItem();
        signal.Hits[0].Kind.ShouldBe(RealWorldSignalKind.FramingLexicon);
        signal.Hits[0].Segment.ShouldBe("p:0");
        signal.Hits[0].MatchedPhrase.ShouldBe("dựa trên sự kiện có thật");
    }

    /// <summary>The English lexicon is not consulted for a Vietnamese series: a phrase of another release is no hit.</summary>
    [Fact]
    public void Detect_EnglishPhraseIsNotInTheVietnameseLexicon()
    {
        var service = Service();

        var signal = service.Detect(Content("This story is based on real events."), Language.Vi, [], Options);

        signal.ShouldNotBeNull().Hit.ShouldBeFalse();
        signal!.Hits.ShouldBeEmpty();
    }

    /// <summary>The same holding for English: each series reads its own release lexicon.</summary>
    [Fact]
    public void Detect_EnglishPhrase_FiresForEnglishSeries()
    {
        var service = Service();

        var signal = service.Detect(Content("This story is based on real events."), Language.En, [], Options);

        signal.ShouldNotBeNull().Hit.ShouldBeTrue();
        signal!.Hits.ShouldHaveSingleItem();
        signal.Hits[0].Kind.ShouldBe(RealWorldSignalKind.FramingLexicon);
        signal.Hits[0].MatchedPhrase.ShouldBe("based on real events");
    }

    /// <summary>A persisted review claim is a <see cref="RealWorldSignalKind.ModelClaim"/> hit on its own address.</summary>
    [Fact]
    public void Detect_ModelClaim_IsAHitWithoutMatchingText()
    {
        var service = Service();

        var signal = service.Detect(
            Content("Một câu chuyện hoàn toàn hư cấu."),
            Language.Vi,
            [new RealWorldClaim("p:0")],
            Options);

        signal.ShouldNotBeNull().Hit.ShouldBeTrue();
        signal!.Hits.ShouldHaveSingleItem();
        signal.Hits[0].Kind.ShouldBe(RealWorldSignalKind.ModelClaim);
        signal.Hits[0].Segment.ShouldBe("p:0");
    }

    /// <summary>Clean fiction with no claim is no hit: the gate must not fail an ordinary story.</summary>
    [Fact]
    public void Detect_CleanFiction_IsNoHit()
    {
        var service = Service();

        var signal = service.Detect(Content("Ngọn hải đăng đứng im trong sương."), Language.Vi, [], Options);

        signal.ShouldNotBeNull().Hit.ShouldBeFalse();
        signal!.Hits.ShouldBeEmpty();
    }

    /// <summary>
    /// F31: the detector reads generated text only. A phrase the owner wrote elsewhere (a keyword, a brief) never
    /// reaches it — the service has no owner input to read, and a keyword is not part of the content document.
    /// </summary>
    [Fact]
    public void Detect_OwnerInputsNeverReachTheDetector()
    {
        var service = Service();
        var content = Content("Ngọn hải đăng đứng im trong sương.");

        var signal = service.Detect(content, Language.Vi, [], Options);

        signal.ShouldNotBeNull().Hit.ShouldBeFalse();
        signal!.Hits.ShouldBeEmpty();
        StoryTextSegments.From(content).ShouldNotContain(segment =>
            segment.Text.Contains("dựa trên sự kiện có thật", StringComparison.Ordinal));
    }

    /// <summary>Claims that could not be read are unknown, not empty (F6): the gate fails check 8 closed.</summary>
    [Fact]
    public void Detect_UnreadableClaims_AreUnknown()
    {
        Service().Detect(Content("Ngọn hải đăng đứng im trong sương."), Language.Vi, null, Options)
            .ShouldBeNull("a review whose member is unreadable asserts nothing, so the check cannot pass");
    }

    /// <summary>A content document with no text is unknown: a detector over nothing cannot certify fiction.</summary>
    [Fact]
    public void Detect_UnreadableContent_IsUnknown()
    {
        Service().Detect(null, Language.Vi, [], Options).ShouldBeNull();
        Service().Detect(BlankContent(), Language.Vi, [], Options).ShouldBeNull("whitespace is no text");
    }

    /// <summary>The options are required: the profile always resolves a <c>RealWorld</c> block, so a null is a defect.</summary>
    [Fact]
    public void Detect_NullOptions_Throws()
    {
        Should.Throw<ArgumentNullException>(() => Service().Detect(Content("x"), Language.Vi, [], null!));
    }

    private static RealWorldSignalService Service() => new(new FakeLexicon());

    /// <summary>
    /// A story with <paramref name="body"/> as its only <c>p:0</c>. The hook, the ending, the title and the
    /// description carry their own text, so a phrase in the body fires once — on the one segment that holds it
    /// (one hit per segment, F31) — instead of on every segment a single shared segment object would fill.
    /// </summary>
    private static StoryContentV1 Content(string body)
    {
        var paragraph = new StorySegmentV1(body, [], []);

        return new StoryContentV1(
            "title",
            new StorySegmentV1("The lantern was already lit.", [], []),
            [paragraph],
            new StorySegmentV1("The pier emptied again.", [], []),
            "description",
            [],
            [],
            null!);
    }

    /// <summary>A document with no text at all: every field is blank, so the detector has nothing to read.</summary>
    private static StoryContentV1 BlankContent()
    {
        var blank = new StorySegmentV1("   ", [], []);

        return new StoryContentV1(" ", blank, [blank], blank, " ", [], [], null!);
    }

    /// <summary>The release lexicon of both languages, one phrase each.</summary>
    private sealed class FakeLexicon : IRealWorldLexicon
    {
        public IReadOnlyList<string> PhrasesFor(Language language) => language switch
        {
            Language.Vi => PhraseVi,
            Language.En => PhraseEn,
            _ => throw new InvalidOperationException($"No lexicon ships for {language}."),
        };
    }
}
