using NightTales.Domain.Common;
using NightTales.Domain.Stories.Similarity;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Similarity;

public sealed class ScoreFactTests
{
    [Theory]
    [InlineData(Language.En)]
    [InlineData(Language.Vi)]
    public void ScoreFact_StatementCopiedFromItsSource_IsAboveThreshold(Language language)
    {
        var source = language == Language.Vi ? SimilarityFixtures.VietnameseSource : SimilarityFixtures.EnglishSource;
        var score = SimilarityScorer.ScoreFact(source, SimilarityFixtures.Corpus(source), SimilarityFixtures.Options(language));
        score.ShouldBe(1);
        score.ShouldBeGreaterThan(0.6);
    }

    [Fact]
    public void ScoreFact_StatementShorterThanN_IsZero() =>
        SimilarityScorer.ScoreFact("one two three", SimilarityFixtures.Corpus(SimilarityFixtures.EnglishSource), SimilarityFixtures.Options(Language.En)).ShouldBe(0);

    [Fact]
    public void ScoreFact_StatementFromAnotherSource_IsLow() =>
        SimilarityScorer.ScoreFact(SimilarityFixtures.EnglishNew, SimilarityFixtures.Corpus(SimilarityFixtures.EnglishSource), SimilarityFixtures.Options(Language.En)).ShouldBe(0);

    [Fact]
    public void ScoreFact_NoSources_IsZero() =>
        SimilarityScorer.ScoreFact(SimilarityFixtures.EnglishSource, [], SimilarityFixtures.Options(Language.En)).ShouldBe(0);

    [Fact]
    public void ScoreFact_MatchesAcrossCitedSources_AreUnioned()
    {
        var options = SimilarityFixtures.Options(Language.En) with { N = 2 };
        SimilarityScorer.ScoreFact("alpha beta gamma", SimilarityFixtures.Corpus("alpha beta", "beta gamma"), options).ShouldBe(1);
    }

    [Fact]
    public void ScoreFact_InvalidUtf16InSource_IsSanitizedBeforeNormalization()
    {
        var options = SimilarityFixtures.Options(Language.En) with { N = 2 };
        SimilarityScorer.ScoreFact("alpha beta gamma", SimilarityFixtures.Corpus("alpha\0beta\ud800gamma"), options).ShouldBe(1);
    }
}
