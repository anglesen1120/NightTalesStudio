using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Common;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Autopilot.Ideas;

/// <summary>
/// Independent QA of 02.8.1-T2's two raw thresholds at their exact boundaries (task Acceptance 3/5, Failure
/// table rows 1 and 4): the shingle arithmetic that lands a candidate on <c>LexicalMax = 0.35</c> exactly, and
/// the forty-character proximity the true-crime framing rule is written with.
/// </summary>
/// <remarks>
/// Every text is built from a token list, so the window arithmetic is checkable by counting the list: a text of
/// <c>t</c> tokens shingles to <c>t − n + 1</c> windows and the Jaccard of a candidate whose whole shingle set
/// sits inside the corpus' is <c>|A| / |B|</c>. The 0.35 fixtures are therefore 16 tokens against 29 (vi) and
/// 14 against 27 (en), i.e. 7 of 20 windows either way.
/// </remarks>
public sealed class IdeaNoveltyBoundaryQA
{
    private static readonly Guid CorpusId = Guid.Parse("0194f2a5-1f6b-7c4d-8e21-0000000056a1");

    /// <summary>The first <paramref name="count"/> tokens of one distinct sequence.</summary>
    /// <param name="count">How many tokens.</param>
    /// <returns>The tokens, in order.</returns>
    private static string[] Tokens(int count) =>
        [.. Enumerable.Range(1, count).Select(index => $"tok{index}")];

    private static string Text(int count) => string.Join(' ', Tokens(count));

    /// <summary>
    /// The lexical ceiling exactly: a candidate whose shingle set is a strict subset of the corpus' shares 7 of the
    /// corpus' 20 windows, so the Jaccard is 7/20 = 0.35 — the value the planner rejects <b>above</b> only. One
    /// token more (8 windows) is 0.40 and is rejected.
    /// </summary>
    /// <remarks>
    /// The candidate is deliberately the corpus' own opening: the pass' maximum is then the corpus entry, and the
    /// score is the arithmetic of the two token counts rather than of a hand-picked string.
    /// </remarks>
    /// <param name="language">The content language: vi shingles at 10, en at 8.</param>
    [Theory]
    [InlineData(Language.Vi)]
    [InlineData(Language.En)]
    public void LexicalScore_ExactlyAtTheCeiling_IsThirtyFivePercent(Language language)
    {
        var window = language == Language.Vi ? 10 : 8;
        var candidateTokens = 16 - (10 - window);
        var corpusTokens = 29 - (10 - window);

        var corpus = LexicalCorpus.Build(
            [new LexicalCorpusEntry(CorpusId, "episode", Text(corpusTokens))],
            language);

        var atCeiling = corpus.ScoreCandidate(Text(candidateTokens));
        var oneWindowMore = corpus.ScoreCandidate(Text(candidateTokens + 1));

        atCeiling.Score.ShouldBe(0.35m, "7 shared windows of 20 (7 + 20 − 7) is the shipped LexicalMax exactly");
        atCeiling.OverlapId.ShouldBe(CorpusId);
        atCeiling.OverlapKind.ShouldBe("episode");
        oneWindowMore.Score.ShouldBe(0.4m, "8 shared windows of 20 is above the ceiling and is rejected");
        oneWindowMore.Score.ShouldBeGreaterThan(
            0.35m,
            "the planner rejects strictly above the ceiling, so this candidate is the rejected one");
    }

    /// <summary>
    /// The true-crime dating rule's proximity bound is forty characters strictly between the two spans: at exactly
    /// forty the references still count as one mention, at forty-one they do not (task Design "Policy" 3).
    /// </summary>
    /// <remarks>
    /// The gaps below are computed from the fixture itself (<see cref="Gap"/>), so the assertions cannot drift with
    /// a miscounted string.
    /// </remarks>
    [Fact]
    public void TrueCrimeFraming_ProximityBoundary_IsFortyCharacters()
    {
        IReadOnlyList<string> nouns = ["án mạng"];

        var atForty = $"án mạng {new string('x', 38)} 2020";
        var atFortyOne = $"án mạng {new string('x', 39)} 2020";
        var yearFirst = $"2020 án mạng";

        Gap(atForty).ShouldBe(40, "the fixture is the boundary case");
        IdeaTrueCrimeFraming.RequiresResearch(atForty, [], nouns)
            .ShouldBeTrue("forty characters apart is still one mention (the rule is `gap <= 40`)");

        Gap(atFortyOne).ShouldBe(41, "the fixture is one character past the boundary");
        IdeaTrueCrimeFraming.RequiresResearch(atFortyOne, [], nouns)
            .ShouldBeFalse("forty-one characters apart is two mentions and claims nothing");

        Gap(yearFirst).ShouldBe(1);
        IdeaTrueCrimeFraming.RequiresResearch(yearFirst, [], nouns)
            .ShouldBeTrue("the year before the noun is the same proximity rule");

        IdeaTrueCrimeFraming.RequiresResearch("án mạng không có năm nào", [], nouns)
            .ShouldBeFalse("no dated incident, no research flag");
    }

    /// <summary>The characters strictly between the crime noun's last character and the year's first.</summary>
    /// <param name="text">A fixture of the shape <c>&lt;crime noun&gt; … &lt;year&gt;</c> or the reverse.</param>
    /// <returns>The distance, in characters.</returns>
    private static int Gap(string text)
    {
        const string noun = "án mạng";
        const string year = "2020";

        var nounStart = text.IndexOf(noun, StringComparison.Ordinal);
        var nounEnd = nounStart + noun.Length;
        var yearStart = text.IndexOf(year, StringComparison.Ordinal);

        return Math.Max(0, Math.Max(yearStart, nounStart) - Math.Min(yearStart + year.Length, nounEnd));
    }
}
