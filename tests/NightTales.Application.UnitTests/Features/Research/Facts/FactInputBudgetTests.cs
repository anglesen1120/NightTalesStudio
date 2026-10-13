using System.Text;
using System.Text.Json;
using NightTales.Application.Features.Research.Facts;
using NightTales.Domain.Common;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Research.Facts;

/// <summary>
/// 02.5.3-T1: the deterministic input budget of a fact-extraction call — alias order, exclusion, per-source
/// head+tail truncation, the total-budget loop, the user-note exemption and the token estimate.
/// </summary>
public sealed class FactInputBudgetTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string Marker = "\n[…truncated…]\n";

    private static FactInputLimits Limits(int maxSources = 10, int perSourceCharCap = 20_000, int maxInputTokens = 60_000) =>
        new(maxSources, perSourceCharCap, maxInputTokens);

    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:000000000000}");

    private static FactSourceCandidate Url(int n, string text, int ageMinutes = 0) =>
        new(Id(n), $"Title {n}", FactSourceKind.Url, text, BaseTime.AddMinutes(-ageMinutes));

    private static FactSourceCandidate Note(int n, string text, int ageMinutes = 0) =>
        new(Id(n), $"Note {n}", FactSourceKind.UserNote, text, BaseTime.AddMinutes(-ageMinutes));

    [Fact]
    public void FactInputBudget_AssignsAliasesS1ToS10_InCreatedOrder()
    {
        // Candidate n is created n minutes ago, so candidate 10 is the oldest and takes s1.
        var candidates = Enumerable.Range(1, 10).Select(n => Url(n, $"text {n}", n)).ToArray();

        var plan = FactInputBudget.Plan(Language.Vi, candidates, Limits());

        plan.Sources.Select(source => source.Alias)
            .ShouldBe(["s1", "s2", "s3", "s4", "s5", "s6", "s7", "s8", "s9", "s10"]);
        plan.Sources[0].SourceId.ShouldBe(Id(10));
        plan.Sources[9].SourceId.ShouldBe(Id(1));
        plan.ExcludedSourceIds.ShouldBeEmpty();
        plan.WithinBudget.ShouldBeTrue();
    }

    [Fact]
    public void FactInputBudget_AssignsAliasById_WhenCreatedAtTies()
    {
        var candidates = new[]
        {
            Url(2, "second", 0),
            Url(1, "first", 0),
        };

        var plan = FactInputBudget.Plan(Language.En, candidates, Limits());

        plan.Sources[0].SourceId.ShouldBe(Id(1));
        plan.Sources[1].SourceId.ShouldBe(Id(2));
    }

    [Fact]
    public void FactInputBudget_ExcludesSources_AboveMaxSources()
    {
        var candidates = Enumerable.Range(1, 12).Select(n => Url(n, $"text {n}", n)).ToArray();

        var plan = FactInputBudget.Plan(Language.En, candidates, Limits(maxSources: 10));

        plan.Sources.Count.ShouldBe(10);
        plan.Sources[^1].Alias.ShouldBe("s10");
        plan.Sources[^1].SourceId.ShouldBe(Id(3));
        plan.ExcludedSourceIds.ShouldBe([Id(1), Id(2)], ignoreOrder: true);
    }

    [Fact]
    public void FactInputBudget_ExcludesUrlWithEmptyText_AndReportsIt()
    {
        var candidates = new[]
        {
            Url(1, "has text", 1),
            Url(2, "   ", 2),
            new FactSourceCandidate(Id(3), "null text", FactSourceKind.Url, null, BaseTime.AddMinutes(-3)),
        };

        var plan = FactInputBudget.Plan(Language.En, candidates, Limits());

        plan.Sources.ShouldHaveSingleItem().SourceId.ShouldBe(Id(1));
        plan.ExcludedSourceIds.ShouldBe([Id(2), Id(3)], ignoreOrder: true);
    }

    [Fact]
    public void FactInputBudget_TruncatesPerSource_HeadAndTail_WhenOverCharCap()
    {
        var head = new string('H', 150);
        var tail = new string('T', 150);
        var candidates = new[] { Url(1, head + tail, 1) };

        var plan = FactInputBudget.Plan(Language.En, candidates, Limits(perSourceCharCap: 100));

        var text = plan.Sources.Single().Text;
        text.ShouldContain(Marker);
        text.ShouldStartWith("H");
        text.ShouldEndWith("T");
        text.Split(Marker)[0].ShouldBe(new string('H', 60));
        text.Split(Marker)[1].ShouldBe(new string('T', 40));
        plan.TruncatedAliases.ShouldBe(["s1"]);
        plan.InputTruncated.ShouldBeTrue();
    }

    [Fact]
    public void FactInputBudget_KeepsSourceWhole_AtOrUnderCharCap()
    {
        var text = new string('x', 100);
        var plan = FactInputBudget.Plan(Language.En, [Url(1, text, 1)], Limits(perSourceCharCap: 100));

        plan.Sources.Single().Text.ShouldBe(text);
        plan.TruncatedAliases.ShouldBeEmpty();
        plan.InputTruncated.ShouldBeFalse();
    }

    [Fact]
    public void FactInputBudget_ReducesTotal_WhenOverTokenBudget_NeverExceedsCap()
    {
        // Two long sources blow the 5,000-token ceiling; the loop must shrink them until the object fits.
        var limits = Limits(perSourceCharCap: 20_000, maxInputTokens: 5_000);
        var candidates = new[] { Url(1, new string('a', 20_000), 1), Url(2, new string('b', 20_000), 2) };

        var plan = FactInputBudget.Plan(Language.Vi, candidates, limits);

        plan.EstimatedTokens.ShouldBeLessThanOrEqualTo(limits.MaxInputTokens);
        plan.WithinBudget.ShouldBeTrue();
        plan.TruncatedAliases.Count.ShouldBe(2);
        FactInputBudget.EstimateTokens(FactDataObjectJson.Serialize(Language.Vi, plan.Sources))
            .ShouldBe(plan.EstimatedTokens);
    }

    [Fact]
    public void FactInputBudget_KeepsUserNoteUntruncated()
    {
        var note = new string('n', 300);
        var limits = Limits(perSourceCharCap: 100);
        var candidates = new[] { Url(1, new string('u', 300), 2), Note(2, note, 1) };

        var plan = FactInputBudget.Plan(Language.En, candidates, limits);

        var userNote = plan.Sources.Single(source => source.IsUserNote);
        userNote.Alias.ShouldBe("s2");
        userNote.Text.ShouldBe(note);
        plan.TruncatedAliases.ShouldBe(["s1"]);
    }

    [Fact]
    public void FactInputBudget_ExcludesUserNoteWithEmptyText()
    {
        var candidates = new[] { Note(1, "  ", 1), Url(2, "text", 2) };

        var plan = FactInputBudget.Plan(Language.En, candidates, Limits());

        plan.Sources.ShouldHaveSingleItem().Alias.ShouldBe("s1");
        plan.ExcludedSourceIds.ShouldBe([Id(1)]);
    }

    [Fact]
    public void FactInputBudget_EstimatesTokens_FromUtf8Bytes()
    {
        FactInputBudget.EstimateTokens("").ShouldBe(0);
        FactInputBudget.EstimateTokens("abcd").ShouldBe(1);
        FactInputBudget.EstimateTokens("abcde").ShouldBe(2);
        // é is two UTF-8 bytes; a Vietnamese word is multi-byte, so the estimate is byte based, not char based.
        FactInputBudget.EstimateTokens("é").ShouldBe(1);
        Encoding.UTF8.GetByteCount("ếế").ShouldBe(6);
        FactInputBudget.EstimateTokens("ếế").ShouldBe(2);
    }

    [Fact]
    public void FactInputBudget_EmptyCandidates_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => FactInputBudget.Plan(Language.En, [], Limits()));
    }

    [Fact]
    public void FactInputBudget_EveryCandidateEmpty_Throws()
    {
        var candidates = new[] { Url(1, "", 1), Url(2, "  ", 2) };

        Should.Throw<ArgumentOutOfRangeException>(() => FactInputBudget.Plan(Language.En, candidates, Limits()));
    }

    [Fact]
    public void FactInputBudget_MaxSourcesAboveTheAliasCeiling_Throws()
    {
        var candidates = new[] { Url(1, "text", 1) };

        Should.Throw<ArgumentOutOfRangeException>(() => FactInputBudget.Plan(Language.En, candidates, Limits(maxSources: 11)));
        Should.Throw<ArgumentOutOfRangeException>(() => FactInputBudget.Plan(Language.En, candidates, Limits(maxSources: 0)));
        Should.Throw<ArgumentOutOfRangeException>(() => FactInputBudget.Plan(Language.En, candidates, Limits(perSourceCharCap: 0)));
        Should.Throw<ArgumentOutOfRangeException>(() => FactInputBudget.Plan(Language.En, candidates, Limits(maxInputTokens: 0)));
    }

    [Fact]
    public void FactInputBudget_FlagsOverBudget_WhenOnlyUserNotesRemain()
    {
        // A note is never truncated, so when it alone exceeds the ceiling the plan is flagged, never silently sent.
        var candidates = new[] { Note(1, new string('n', 3_000), 1) };

        var plan = FactInputBudget.Plan(Language.Vi, candidates, Limits(perSourceCharCap: 100, maxInputTokens: 50));

        plan.Status.ShouldBe(FactInputBudgetStatus.OverBudget);
        plan.WithinBudget.ShouldBeFalse();
        plan.EstimatedTokens.ShouldBeGreaterThan(50);
        plan.TruncatedAliases.ShouldBeEmpty();
    }

    [Fact]
    public void FactInputBudget_TerminatesAndFits_WithManyLargeSourcesAndAUserNote()
    {
        var candidates = new List<FactSourceCandidate> { Note(1, new string('n', 3_000), 1) };
        candidates.AddRange(Enumerable.Range(2, 9).Select(n => Url(n, new string('a', 20_000), n)));
        var limits = Limits(maxInputTokens: 20_000);

        var plan = FactInputBudget.Plan(Language.Vi, candidates, limits);

        plan.WithinBudget.ShouldBeTrue();
        plan.EstimatedTokens.ShouldBeLessThanOrEqualTo(limits.MaxInputTokens);
        plan.Sources.Single(source => source.IsUserNote).Text.Length.ShouldBe(3_000);
    }

    [Fact]
    public void FactInputBudget_IsDeterministic()
    {
        var candidates = Enumerable.Range(1, 6).Select(n => Url(n, new string((char)('a' + n), 30_000), n)).ToArray();

        var first = FactInputBudget.Plan(Language.Vi, candidates, Limits(maxInputTokens: 30_000));
        var second = FactInputBudget.Plan(Language.Vi, candidates, Limits(maxInputTokens: 30_000));

        second.Sources.Select(source => (source.Alias, source.SourceId, source.Text))
            .ShouldBe(first.Sources.Select(source => (source.Alias, source.SourceId, source.Text)));
        second.TruncatedAliases.ShouldBe(first.TruncatedAliases);
        second.EstimatedTokens.ShouldBe(first.EstimatedTokens);
        second.Status.ShouldBe(first.Status);
        FactDataObjectJson.Serialize(Language.Vi, second.Sources)
            .ShouldBe(FactDataObjectJson.Serialize(Language.Vi, first.Sources));
    }

    [Fact]
    public void FactInputBudget_SurrogateSafeCut_NeverSplitsAPairOrATextElement()
    {
        // 2 chars per astral glyph: a naive char-count cut would leave a lone surrogate.
        var text = string.Concat(Enumerable.Repeat("\U0001F600", 200)) + new string('z', 200);
        var candidates = new[] { Url(1, text, 1) };

        var plan = FactInputBudget.Plan(Language.En, candidates, Limits(perSourceCharCap: 201));

        var cut = plan.Sources.Single().Text;
        cut.ShouldContain(Marker);
        IsWellFormedUtf16(cut).ShouldBeTrue();
        cut.ShouldNotContain("\uFFFD");
        // The head holds only whole astral pairs: 120 chars, sixty glyphs.
        var head = cut.Split(Marker)[0];
        head.Length.ShouldBe(120);
        head.ShouldBe(string.Concat(Enumerable.Repeat("\U0001F600", 60)));
    }

    [Fact]
    public void FactInputBudget_SurrogateSafeCut_NeverSplitsACombiningSequence()
    {
        // "x" + U+0301 (combining acute) is one text element of two chars and has no NFC precomposed form,
        // so the cut must not separate the mark from its base.
        var text = string.Concat(Enumerable.Repeat("x\u0301", 300));
        var candidates = new[] { Url(1, text, 1) };

        var plan = FactInputBudget.Plan(Language.En, candidates, Limits(perSourceCharCap: 101));

        var cut = plan.Sources.Single().Text;
        var parts = cut.Split(Marker);
        parts[0].ShouldBe(string.Concat(Enumerable.Repeat("x\u0301", 30)));
        parts[0].ShouldNotEndWith("x");
        parts[1].ShouldBe(string.Concat(Enumerable.Repeat("x\u0301", 21)));
        IsWellFormedUtf16(cut).ShouldBeTrue();
    }

    [Fact]
    public void FactDataObjectJson_StripsControlCharacters_AndKeepsNewlineAndTab()
    {
        var source = new FactSourceInput("s1", Id(1), "title\u0001", "a\u0000b\tc\nd", false);

        var json = FactDataObjectJson.Serialize(Language.En, [source]);

        json.ShouldNotContain('\u0000');
        json.ShouldNotContain('\u0001');
        var text = JsonDocument.Parse(json).RootElement.GetProperty("sources")[0].GetProperty("text").GetString();
        text.ShouldBe("a b\tc\nd");
        JsonDocument.Parse(json).RootElement.GetProperty("sources")[0].GetProperty("title").GetString().ShouldBe("title ");
    }

    [Fact]
    public void FactDataObjectJson_ReplacesLoneSurrogates_AndKeepsValidPairs()
    {
        var source = new FactSourceInput("s1", Id(1), "t", "ok \uD83D lone \U0001F600 pair", false);

        var json = FactDataObjectJson.Serialize(Language.En, [source]);

        var text = JsonDocument.Parse(json).RootElement.GetProperty("sources")[0].GetProperty("text").GetString();
        text.ShouldBe("ok \uFFFD lone \U0001F600 pair");
        IsWellFormedUtf16(text!).ShouldBeTrue();
    }

    [Fact]
    public void FactDataObjectJson_NeverSerialisesSourceIdOrUserNoteFlag()
    {
        var source = new FactSourceInput("s1", Id(7), "Title", "Text", true);

        var json = FactDataObjectJson.Serialize(Language.En, [source]);

        json.ShouldNotContain("source_id");
        json.ShouldNotContain("is_user_note");
        json.ShouldNotContain(Id(7).ToString());
        var root = JsonDocument.Parse(json).RootElement;
        root.EnumerateObject().Select(property => property.Name).ShouldBe(["task", "language", "sources"]);
        root.GetProperty("task").GetString().ShouldBe("extract_facts");
        root.GetProperty("language").GetString().ShouldBe("en");
        root.GetProperty("sources")[0].EnumerateObject().Select(property => property.Name).ShouldBe(["alias", "title", "text"]);
    }

    private static bool IsWellFormedUtf16(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(value[i]))
            {
                return false;
            }
        }

        return true;
    }
}
