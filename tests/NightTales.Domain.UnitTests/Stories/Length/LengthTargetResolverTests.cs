using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories.Length;
using NightTales.Domain.Usage;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Length;

/// <summary>
/// <c>LengthTargetResolver</c> (02.2.1-T1 Tests list, 02 README D4/D21, T103): the per-language bands of
/// the D4 table, resolved from a price version's <c>limits.story</c>.
/// </summary>
public sealed class LengthTargetResolverTests
{
    [Fact]
    public void LengthTargetResolver_StandardVi_Returns1300To2080And20To32Segments()
    {
        var target = LengthTargetResolver.Resolve(LengthTier.Standard, Language.Vi, PriceLimits.D4Defaults);

        target.MinTokens.ShouldBe(1_300);
        target.MaxTokens.ShouldBe(2_080);
        target.MinSegments.ShouldBe(20);
        target.MaxSegments.ShouldBe(32);
        target.WriterMaxOutput.ShouldBe(6_000);
        target.ReviewMaxOutput.ShouldBe(2_500);
        target.RevisionMaxOutput.ShouldBe(6_000);
    }

    [Fact]
    public void LengthTargetResolver_ShortVi_Returns195To260()
    {
        var target = LengthTargetResolver.Resolve(LengthTier.Short, Language.Vi, PriceLimits.D4Defaults);

        target.MinTokens.ShouldBe(195);
        target.MaxTokens.ShouldBe(260);
        target.MinSegments.ShouldBe(4);
        target.MaxSegments.ShouldBe(5);
        target.WriterMaxOutput.ShouldBe(2_500, "02 T66 raised the short writer cap for reasoning headroom");
        target.ReviewMaxOutput.ShouldBe(1_200);
        target.RevisionMaxOutput.ShouldBe(1_500);
    }

    [Fact]
    public void LengthTargetResolver_LongEn_Returns2000To3000And40To60Segments()
    {
        var target = LengthTargetResolver.Resolve(LengthTier.Long, Language.En, PriceLimits.D4Defaults);

        target.MinTokens.ShouldBe(2_000);
        target.MaxTokens.ShouldBe(3_000);
        target.MinSegments.ShouldBe(40);
        target.MaxSegments.ShouldBe(60);
        target.WriterMaxOutput.ShouldBe(10_000);
        target.ReviewMaxOutput.ShouldBe(3_500);
        target.RevisionMaxOutput.ShouldBe(10_000);
    }

    /// <summary>Every cell of the D4 table, not just the three named cases: a swapped row fails here.</summary>
    [Theory]
    [InlineData(LengthTier.Short, Language.Vi, 195, 260, 4, 5, 2_500, 1_200, 1_500)]
    [InlineData(LengthTier.Short, Language.En, 150, 200, 4, 5, 2_500, 1_200, 1_500)]
    [InlineData(LengthTier.Standard, Language.Vi, 1_300, 2_080, 20, 32, 6_000, 2_500, 6_000)]
    [InlineData(LengthTier.Standard, Language.En, 1_000, 1_600, 20, 32, 6_000, 2_500, 6_000)]
    [InlineData(LengthTier.Long, Language.Vi, 2_600, 3_900, 40, 60, 10_000, 3_500, 10_000)]
    [InlineData(LengthTier.Long, Language.En, 2_000, 3_000, 40, 60, 10_000, 3_500, 10_000)]
    public void LengthTargetResolver_EveryD4Cell_MatchesTheTable(
        LengthTier tier,
        Language language,
        int minTokens,
        int maxTokens,
        int minSegments,
        int maxSegments,
        int writerMaxOutput,
        int reviewMaxOutput,
        int revisionMaxOutput)
    {
        var target = LengthTargetResolver.Resolve(tier, language, PriceLimits.D4Defaults);

        target.ShouldBe(new LengthTarget(
            minTokens, maxTokens, minSegments, maxSegments, writerMaxOutput, reviewMaxOutput, revisionMaxOutput));
    }

    [Fact]
    public void LengthTargetResolver_UnknownTier_Throws()
    {
        var limits = new PriceLimits(new Dictionary<LengthTier, IReadOnlyDictionary<Language, LengthLimit>>
        {
            [LengthTier.Short] = new Dictionary<Language, LengthLimit>
            {
                [Language.Vi] = new(195, 260, 4, 5, 2_500, 1_200, 1_500),
            },
        });

        // A tier the table does not carry, and a language the carried tier does not carry.
        Should.Throw<KeyNotFoundException>(() => LengthTargetResolver.Resolve(LengthTier.Standard, Language.Vi, limits));
        Should.Throw<KeyNotFoundException>(() => LengthTargetResolver.Resolve(LengthTier.Short, Language.En, limits));
    }
}
