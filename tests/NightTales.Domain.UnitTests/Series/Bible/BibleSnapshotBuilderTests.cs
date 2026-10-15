using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Structure;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Series.Bible;

/// <summary>
/// The writer-budget builder (02.6.3 plan §4 U4): newest-first summaries, the S0..S7 truncation order, the narrator
/// never dropped, and the size measure that matches the writer's data object.
/// </summary>
public sealed class BibleSnapshotBuilderTests
{
    /// <summary>Dense Vietnamese text, about 1.2 UTF-8 bytes per character.</summary>
    private const string Phrase = "Căn nhà bên sông lặng im đêm nay. ";

    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 30, 0, TimeSpan.Zero);

    private static readonly StoryStructureTags Structure = new(
        "haunted_house", "rural_house", "night", "revenant", "first_person", ["setup", "clue", "twist", "reveal"]);

    /// <summary>The writer budget: <c>Bible:Prompt:*</c> defaults, with MaxChars in UTF-8 bytes.</summary>
    private static readonly BibleSnapshotLimits Defaults = new(
        MaxSummaries: 50,
        MaxChars: 36_000,
        MaxDescriptionChars: 150,
        MaxVoiceNotesChars: 500,
        KeepFullSummaryCount: 25,
        MaxArrayEntries: 15);

    private static readonly BibleSnapshotLimits NoBudget = Defaults with { MaxChars = int.MaxValue };

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private static readonly string[] NewestFirstTitles = ["Tập 5", "Tập 2", "Tập 1"];
    private static readonly string[] InputOrderTitles = ["Tập A", "Tập B"];

    [Fact]
    public void BibleSnapshotBuilder_51Summaries_KeepsNewest50()
    {
        var summaries = Enumerable.Range(1, 51).Select(index => Summary(index, "Một tập.")).ToList();

        var snapshot = BibleSnapshotBuilder.Build(Content(summaries: summaries), 1, Defaults);

        snapshot.EpisodeSummaries.Count.ShouldBe(50);
        snapshot.EpisodeSummaries[0].Title.ShouldBe("Tập 51");
        snapshot.EpisodeSummaries[^1].Title.ShouldBe("Tập 2");
        snapshot.EpisodeSummaries.Select(summary => summary.Title).ShouldNotContain("Tập 1");
        snapshot.Truncated.ShouldBeTrue();
    }

    [Fact]
    public void BibleSnapshotBuilder_ExactlyFiftySummaries_AreNotCut()
    {
        var summaries = Enumerable.Range(1, 50).Select(index => Summary(index, "Một tập.")).ToList();

        var snapshot = BibleSnapshotBuilder.Build(Content(summaries: summaries), 1, Defaults);

        snapshot.EpisodeSummaries.Count.ShouldBe(50);
        snapshot.Truncated.ShouldBeFalse();
    }

    [Fact]
    public void BibleSnapshotBuilder_Summaries_AreOrderedNewestFirstWhateverTheInputOrder()
    {
        var summaries = new[] { Summary(2, "Hai."), Summary(5, "Năm."), Summary(1, "Một.") };

        var snapshot = BibleSnapshotBuilder.Build(Content(summaries: summaries), 1, Defaults);

        snapshot.EpisodeSummaries.Select(summary => summary.Title).ShouldBe(NewestFirstTitles);
    }

    [Fact]
    public void BibleSnapshotBuilder_EqualApprovalTimes_KeepInputOrder()
    {
        var summaries = new[]
        {
            new BibleEpisodeSummary(EpisodeAt(1), "Tập A", "A.", Structure, Now),
            new BibleEpisodeSummary(EpisodeAt(2), "Tập B", "B.", Structure, Now),
        };

        var snapshot = BibleSnapshotBuilder.Build(Content(summaries: summaries), 1, Defaults);

        snapshot.EpisodeSummaries.Select(summary => summary.Title).ShouldBe(InputOrderTitles);
    }

    [Fact]
    public void BibleSnapshotBuilder_FitsBudget_TruncatedFalse()
    {
        var content = Content(
            characters: [new BibleCharacter("ch1", "Bà Tư", "Căn nhà bên sông.", null)],
            summaries: [Summary(1, "Một người trở về.")]);

        var snapshot = BibleSnapshotBuilder.Build(content, 1, Defaults);

        snapshot.Truncated.ShouldBeFalse();
        snapshot.Narrator.ShouldNotBeNull();
        snapshot.Narrator!.VoiceNotes.ShouldBe("Giọng trầm, chậm.");
        Wire(snapshot).ShouldBe(Wire(BibleSnapshotBuilder.Build(content, 1, NoBudget)));
    }

    [Fact]
    public void BibleSnapshotBuilder_BlankVoiceNotes_BecomeNull()
    {
        var snapshot = BibleSnapshotBuilder.Build(Content(voiceNotes: "   "), 1, Defaults);

        snapshot.Narrator.ShouldNotBeNull();
        snapshot.Narrator!.VoiceNotes.ShouldBeNull();
        Wire(snapshot).ShouldNotContain("voice_notes");
    }

    [Fact]
    public void BibleSnapshotBuilder_OverBudget_AppliesTruncationOrderS1ToS7()
    {
        // Each step changes something the earlier steps left alone, so a budget equal to the bytes after step k is met
        // exactly by step k: the builder must stop there, not earlier and not later.
        var content = new SeriesBibleContent(
            SeriesBibleContent.V1,
            new BibleNarrator("Người kể", "Kể đêm.", new string('v', 600)),
            Enumerable.Range(1, 3).Select(i => new BibleCharacter($"ch{i}", $"Nhân vật {i}", new string(Letter(i), 300), null)).ToList(),
            Enumerable.Range(1, 3).Select(i => new BiblePlace($"pl{i}", $"Nơi {i}", new string(Letter(i + 3), 300))).ToList(),
            [new BibleRule("r1", "rule1"), new BibleRule("r2", "rule2"), new BibleRule("r3", "rule3")],
            Enumerable.Range(1, 27).Select(i => Summary(i, $"Tập {i} có một bí mật.")).ToList());

        for (var step = 1; step <= 7; step++)
        {
            var expected = LadderExpected(step);
            var limits = Defaults with { MaxArrayEntries = 2, MaxChars = BibleSnapshotBuilder.MeasureUtf8Bytes(expected) };

            var actual = BibleSnapshotBuilder.Build(content, 1, limits);

            Wire(actual).ShouldBe(Wire(expected), $"budget equals the bytes after step S{step}");
        }
    }

    [Fact]
    public void BibleSnapshotBuilder_KeepsOnlyNewest25FullSummaries()
    {
        // 50 summaries of 200 three-byte characters are 30,000 bytes before any cut, so the full bible is over the
        // 36,000 budget and S3 is the step that brings it under.
        var summaries = Enumerable.Range(1, 50).Select(index => Summary(index, new string('ạ', 200))).ToList();
        var characters = Enumerable.Range(1, 15)
            .Select(index => new BibleCharacter($"ch{index}", $"Nhân vật {index}", new string('ạ', 150), null))
            .ToList();

        var snapshot = BibleSnapshotBuilder.Build(Content(characters: characters, summaries: summaries), 1, Defaults);

        snapshot.Truncated.ShouldBeTrue();
        var newest = snapshot.EpisodeSummaries.Take(25).ToList();
        var older = snapshot.EpisodeSummaries.Skip(25).ToList();
        newest.Count.ShouldBe(25);
        newest.ShouldAllBe(summary => summary.Summary == new string('ạ', 200));
        older.Count.ShouldBe(25);
        older.ShouldAllBe(summary => summary.Summary == null);
        older[0].Title.ShouldBe("Tập 25");
        older[^1].Title.ShouldBe("Tập 1");
        // S4 to S7 must not run once S3 fits: the characters and the rule are untouched.
        snapshot.RecurringCharacters.Count.ShouldBe(15);
        snapshot.RecurringCharacters.ShouldAllBe(character => character.Description.Length == 150);
        snapshot.Rules.Single().ShouldBe("Không nói tên người chết.");
        snapshot.Narrator.ShouldNotBeNull();
        snapshot.Narrator!.VoiceNotes.ShouldBe("Giọng trầm, chậm.");
        BibleSnapshotBuilder.MeasureUtf8Bytes(snapshot).ShouldBeLessThanOrEqualTo(36_000);
    }

    [Fact]
    public void BibleSnapshotBuilder_MaximalDenseBible_FitsTheBudget()
    {
        // The maximal T1 limits in dense Vietnamese: the builder must cut it to fit, never throw.
        var content = new SeriesBibleContent(
            SeriesBibleContent.V1,
            new BibleNarrator(Dense(80), Dense(1000), Dense(1000)),
            Enumerable.Range(1, 30).Select(i => new BibleCharacter($"ch{i}", Dense(60), Dense(300), null)).ToList(),
            Enumerable.Range(1, 30).Select(i => new BiblePlace($"pl{i}", Dense(60), Dense(300))).ToList(),
            Enumerable.Range(1, 30).Select(i => new BibleRule($"r{i}", Dense(300))).ToList(),
            Enumerable.Range(1, 50).Select(i => Entry(i, Dense(100), Dense(200))).ToList());

        var snapshot = BibleSnapshotBuilder.Build(content, 1, Defaults);

        snapshot.Truncated.ShouldBeTrue();
        BibleSnapshotBuilder.MeasureUtf8Bytes(snapshot).ShouldBeLessThanOrEqualTo(36_000);
        snapshot.Narrator.ShouldNotBeNull();
        snapshot.Narrator!.Persona.Length.ShouldBe(1000);
        snapshot.EpisodeSummaries.Count.ShouldBe(50);
    }

    [Fact]
    public void BibleSnapshotBuilder_MaximalThreeByteBible_ThrowsTooLarge()
    {
        // Three-byte text at the maximal limits: even after S0 to S7 the bible is over 36,000 bytes, so it is refused.
        var content = new SeriesBibleContent(
            SeriesBibleContent.V1,
            new BibleNarrator(new string('ạ', 80), new string('ạ', 1000), new string('ạ', 1000)),
            Enumerable.Range(1, 30).Select(i => new BibleCharacter($"ch{i}", new string('ạ', 60), new string('ạ', 300), null)).ToList(),
            Enumerable.Range(1, 30).Select(i => new BiblePlace($"pl{i}", new string('ạ', 60), new string('ạ', 300))).ToList(),
            Enumerable.Range(1, 30).Select(i => new BibleRule($"r{i}", new string('ạ', 300))).ToList(),
            Enumerable.Range(1, 50).Select(i => Entry(i, new string('ạ', 100), new string('ạ', 200))).ToList());

        var exception = Should.Throw<BibleSnapshotTooLargeException>(
            () => BibleSnapshotBuilder.Build(content, 1, Defaults));

        exception.MaxChars.ShouldBe(36_000);
        exception.MeasuredBytes.ShouldBeGreaterThan(36_000);
        exception.Message.ShouldNotContain("ạ");
    }

    [Fact]
    public void BibleSnapshotBuilder_TruncationNeverLeavesALoneHighSurrogate()
    {
        var description = new string('a', 149) + "\U0001F600" + "b";
        var content = Content(characters: [new BibleCharacter("ch1", "Bà Tư", description, null)]);
        var fullBytes = BibleSnapshotBuilder.MeasureUtf8Bytes(BibleSnapshotBuilder.Build(content, 1, NoBudget));

        var snapshot = BibleSnapshotBuilder.Build(content, 1, Defaults with { MaxChars = fullBytes - 1 });

        // The cut at 150 units would end on the emoji's high surrogate; it is dropped, leaving the 149 letters.
        snapshot.RecurringCharacters.Single().Description.ShouldBe(new string('a', 149));
    }

    [Fact]
    public void BibleSnapshotBuilder_NarratorNeverDropped_ThrowsInstead()
    {
        const string secret = "BÍ MẬT";
        var content = Content(persona: secret + " " + new string('a', 1000));

        var exception = Should.Throw<BibleSnapshotTooLargeException>(
            () => BibleSnapshotBuilder.Build(content, 1, Defaults with { MaxChars = 200 }));

        exception.MaxChars.ShouldBe(200);
        exception.MeasuredBytes.ShouldBeGreaterThan(200);
        exception.Message.ShouldNotContain(secret);
    }

    [Fact]
    public void BibleSnapshotBuilder_MeasuredSize_EqualsTheBibleBytesOfTheDataObject()
    {
        var snapshot = BibleSnapshotBuilder.Build(
            Content(
                characters: [new BibleCharacter("ch1", "Bà Tư", "Căn nhà bên sông.", null)],
                summaries: [Summary(1, "Một người trở về.")]),
            3,
            Defaults);

        // Same options as PromptAssembler.DataOptions (snake_case, omit nulls, UnicodeRanges.All). The true
        // cross-project equality test against the real data object belongs in Application tests.
        var options = WireOptions;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { bible = snapshot }, options));
        var bibleText = document.RootElement.GetProperty("bible").GetRawText();

        BibleSnapshotBuilder.MeasureUtf8Bytes(snapshot).ShouldBe(Encoding.UTF8.GetByteCount(bibleText));
        bibleText.ShouldContain("Căn nhà bên sông.");
    }

    /// <summary>The bible after step S0 and steps S1 to <paramref name="step"/>, written out from the content of the ladder test.</summary>
    private static BibleSnapshot LadderExpected(int step)
    {
        var descriptionChars = step >= 2 ? 150 : 300;
        var fullSummaries = step >= 3 ? 25 : 27;
        var placeCount = step >= 5 ? 2 : 3;
        var characterCount = step >= 6 ? 2 : 3;
        string[] rules = step >= 7 ? [] : step >= 4 ? ["rule1", "rule2"] : ["rule1", "rule2", "rule3"];

        return new BibleSnapshot(
            1,
            new BibleNarratorSnapshot("Người kể", "Kể đêm.", new string('v', step >= 1 ? 500 : 600)),
            Enumerable.Range(1, characterCount)
                .Select(i => new BibleCharacterSnapshot($"Nhân vật {i}", new string(Letter(i), descriptionChars)))
                .ToList(),
            Enumerable.Range(1, placeCount)
                .Select(i => new BiblePlaceSnapshot($"Nơi {i}", new string(Letter(i + 3), descriptionChars)))
                .ToList(),
            rules,
            Enumerable.Range(1, 27)
                .Reverse()
                .Select(n => new BibleEpisodeSummarySnapshot($"Tập {n}", n > 27 - fullSummaries ? $"Tập {n} có một bí mật." : null))
                .ToList(),
            step > 0);
    }

    private static SeriesBibleContent Content(
        string persona = "Kể chuyện đêm khuya cho người lớn.",
        string voiceNotes = "Giọng trầm, chậm.",
        IReadOnlyList<BibleCharacter>? characters = null,
        IReadOnlyList<BibleEpisodeSummary>? summaries = null) => new(
        SeriesBibleContent.V1,
        new BibleNarrator("Người kể chuyện", persona, voiceNotes),
        characters ?? Array.Empty<BibleCharacter>(),
        Array.Empty<BiblePlace>(),
        [new BibleRule("rl1", "Không nói tên người chết.")],
        summaries ?? Array.Empty<BibleEpisodeSummary>());

    private static BibleEpisodeSummary Summary(int index, string summary) => Entry(index, $"Tập {index}", summary);

    private static BibleEpisodeSummary Entry(int index, string title, string summary) => new(
        EpisodeAt(index), title, summary, Structure, Now.AddDays(index));

    private static Guid EpisodeAt(int index) => new(index, 0, 0, new byte[8]);

    /// <summary>Vietnamese text of exactly <paramref name="length"/> UTF-16 units.</summary>
    private static string Dense(int length) =>
        string.Concat(Enumerable.Repeat(Phrase, length / Phrase.Length + 1))[..length];

    private static char Letter(int index) => (char)('a' + index - 1);

    private static string Wire(BibleSnapshot snapshot) => JsonSerializer.Serialize(snapshot, WireOptions);
}
