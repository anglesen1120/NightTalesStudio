using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Common;
using NightTales.Domain.Stories.Similarity;
using NightTales.Domain.Stories.Structure;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Autopilot.Ideas;

/// <summary>
/// The lexical novelty pass of 02.8.1-T2 (task Acceptance 1, Tests <c>Jaccard_*</c>): the shingle rule
/// 02.5.6-T1 fixes (vi 10, en 8), the Jaccard maximum over a corpus prepared once, and the two ways the
/// pass declines to claim an overlap (a text shorter than the window, and an empty corpus).
/// </summary>
/// <remarks>
/// The texts are built from token lists, so every case's token count is the length of a list the reader can
/// check — the window arithmetic is what decides the expected score, never a hand-counted string.
/// </remarks>
public sealed class IdeaNoveltyLexicalScorerTests
{
    private static readonly Guid EpisodeId = Guid.Parse("0194f2a5-1f6b-7c4d-8e21-000000000001");
    private static readonly Guid OtherEpisodeId = Guid.Parse("0194f2a5-1f6b-7c4d-8e21-000000000002");
    private static readonly Guid IdeaId = Guid.Parse("0194f2a5-1f6b-7c4d-8e21-000000000003");

    private static readonly IReadOnlyList<string> ViPrefix = ["căn", "nhà", "bên", "sông", "mở", "một", "cánh", "cửa", "lúc", "nửa"];
    private static readonly IReadOnlyList<string> ViMiddle = ["đêm", "tiếng", "gõ", "cất", "lên", "khi", "đèn", "tắt", "người", "trong"];
    private static readonly IReadOnlyList<string> ViOtherMiddle = ["thuyền", "trôi", "xa", "bờ", "biển", "gió", "thổi", "cát", "dài", "mãi"];
    private static readonly IReadOnlyList<string> ViSuffix = ["nhà", "vẫn", "nghe", "tiếng", "gọi", "mỗi", "sáng", "trên", "bến", "cũ"];

    private static readonly IReadOnlyList<string> EnPrefix = ["the", "house", "beside", "the", "river", "opens", "one", "door"];
    private static readonly IReadOnlyList<string> EnMiddle = ["at", "midnight", "a", "knocking", "starts", "when", "the", "lamps"];
    private static readonly IReadOnlyList<string> EnOtherMiddle = ["far", "out", "at", "sea", "the", "wind", "moves", "sand"];
    private static readonly IReadOnlyList<string> EnSuffix = ["and", "the", "family", "hears", "a", "voice", "each", "morning"];

    /// <summary>An episode text and a paraphrase of it: the same opening and closing windows, a new middle.</summary>
    [Theory]
    [InlineData(Language.Vi)]
    [InlineData(Language.En)]
    public void Jaccard_IdenticalTexts_IsOne_ParaphraseBelowThreshold(Language language)
    {
        var (original, paraphrase) = EpisodeAndParaphrase(language);
        var corpus = LexicalCorpus.Build([new LexicalCorpusEntry(EpisodeId, "episode", original)], language);

        var identical = corpus.ScoreCandidate(original);
        var rewritten = corpus.ScoreCandidate(paraphrase);

        identical.Score.ShouldBe(1m);
        identical.OverlapId.ShouldBe(EpisodeId);
        identical.OverlapKind.ShouldBe("episode");
        rewritten.Score.ShouldBeLessThan(0.35m, "the configured LexicalMax a candidate is rejected above");
        rewritten.Score.ShouldBeGreaterThan(
            0m,
            "a paraphrase that keeps its opening and closing windows shares at least those two shingles");
    }

    /// <summary>The window the plan fixes per language: Vietnamese ten tokens, English eight.</summary>
    [Fact]
    public void Jaccard_VietnameseTitles_UsesN10_EnglishUsesN8()
    {
        SimilarityOptions.NFor(Language.Vi).ShouldBe(10);
        SimilarityOptions.NFor(Language.En).ShouldBe(8);

        // A nine-token text is one token short of the Vietnamese window and shingles to two windows in
        // English, so identical to its own corpus entry it scores nothing in vi and one in en.
        var vi = Text(["căn", "nhà", "bên", "sông", "mở", "một", "cánh", "cửa", "lúc"]);
        var en = Text(["the", "house", "beside", "the", "river", "opens", "one", "door", "at"]);

        LexicalCorpus.Build([new LexicalCorpusEntry(EpisodeId, "episode", vi)], Language.Vi)
            .ScoreCandidate(vi).Score.ShouldBe(0m);
        LexicalCorpus.Build([new LexicalCorpusEntry(EpisodeId, "episode", en)], Language.En)
            .ScoreCandidate(en).Score.ShouldBe(1m);
    }

    /// <summary>A text shorter than the window claims no overlap — not even against an identical entry.</summary>
    [Theory]
    [InlineData(Language.Vi)]
    [InlineData(Language.En)]
    public void Jaccard_TextShorterThanN_ReturnsZero(Language language)
    {
        var shortText = Text(["căn", "nhà", "bên", "sông"]);
        var corpus = LexicalCorpus.Build([new LexicalCorpusEntry(EpisodeId, "episode", shortText)], language);

        var match = corpus.ScoreCandidate(shortText);

        match.ShouldBe(LexicalMatch.None);
        match.Score.ShouldBe(0m);
        match.OverlapId.ShouldBeNull("no overlap is claimed, so no entry is named");
        match.OverlapKind.ShouldBeNull();
    }

    /// <summary>The reported maximum is the candidate's highest similarity, with the entry that produced it.</summary>
    [Fact]
    public void LexicalCorpus_MaxOverlap_IsTheHighestScoreAndItsEntry()
    {
        var candidate = Candidate(
            title: "Căn nhà bên sông",
            logline: "Người trở về nghe tiếng gõ lúc nửa đêm",
            setting: "Bến cũ trên sông");
        var text = IdeaNoveltyLexicalScorer.CandidateText(candidate);
        var corpus = LexicalCorpus.Build(
            [
                new LexicalCorpusEntry(EpisodeId, "episode", Text(ViOtherMiddle)),
                new LexicalCorpusEntry(IdeaId, "pending_idea", text),
            ],
            Language.Vi);

        var match = corpus.ScoreCandidate(text);

        match.Score.ShouldBe(1m);
        match.OverlapId.ShouldBe(IdeaId);
        match.OverlapKind.ShouldBe("pending_idea");
    }

    /// <summary>Two entries with the same score: the one the corpus was built with first keeps the id.</summary>
    [Fact]
    public void LexicalCorpus_Tie_NamesTheFirstPreparedEntry()
    {
        var text = Text(ViPrefix, ViMiddle, ViSuffix);
        var corpus = LexicalCorpus.Build(
            [
                new LexicalCorpusEntry(EpisodeId, "episode", text),
                new LexicalCorpusEntry(OtherEpisodeId, "episode", text),
            ],
            Language.Vi);

        corpus.ScoreCandidate(text).OverlapId.ShouldBe(EpisodeId);
    }

    /// <summary>One corpus serves the whole batch: its shingles are prepared in Build, never re-read.</summary>
    [Fact]
    public void LexicalCorpus_BuiltOnce_ScoresEveryCandidate()
    {
        var entries = new List<LexicalCorpusEntry> { new(EpisodeId, "episode", Text(ViPrefix, ViMiddle, ViSuffix)) };
        var corpus = LexicalCorpus.Build(entries, Language.Vi);
        var identical = corpus.ScoreCandidate(Text(ViPrefix, ViMiddle, ViSuffix));

        entries.Clear();

        corpus.Count.ShouldBe(1);
        identical.Score.ShouldBe(1m);
        corpus.ScoreCandidate(Text(ViPrefix, ViMiddle, ViSuffix)).ShouldBe(
            identical,
            "the prepared corpus is the whole state of a pass");
        corpus.ScoreCandidate(Text(ViPrefix, ViOtherMiddle, ViSuffix)).Score.ShouldBeLessThan(identical.Score);
    }

    /// <summary>The shingle text is the title, the logline and the setting — never the hook.</summary>
    [Fact]
    public void IdeaNoveltyLexicalScorer_CandidateText_JoinsTitleLoglineAndSetting()
    {
        var candidate = Candidate(
            title: "Căn nhà bên sông",
            logline: "Người trở về nghe tiếng gõ lúc nửa đêm",
            setting: "Bến cũ trên sông");

        IdeaNoveltyLexicalScorer.CandidateText(candidate).ShouldBe(
            "Căn nhà bên sông Người trở về nghe tiếng gõ lúc nửa đêm Bến cũ trên sông");
    }

    private static IdeaCandidate Candidate(string title, string logline, string setting) =>
        new(title, logline, "Tiếng gõ chỉ cất lên khi đèn tắt", setting, StoryStructureTags.Empty, []);

    private static (string Original, string Rewritten) EpisodeAndParaphrase(Language language) =>
        language == Language.Vi
            ? (Text(ViPrefix, ViMiddle, ViSuffix), Text(ViPrefix, ViOtherMiddle, ViSuffix))
            : (Text(EnPrefix, EnMiddle, EnSuffix), Text(EnPrefix, EnOtherMiddle, EnSuffix));

    private static string Text(IEnumerable<string> tokens) => string.Join(' ', tokens);

    private static string Text(
        IReadOnlyList<string> prefix,
        IReadOnlyList<string> middle,
        IReadOnlyList<string> suffix) =>
        Text([.. prefix, .. middle, .. suffix]);
}
