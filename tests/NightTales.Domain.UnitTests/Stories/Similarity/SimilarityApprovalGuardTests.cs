using NightTales.Domain.Common;
using NightTales.Domain.Stories.Quality;
using NightTales.Domain.Stories.Similarity;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Similarity;

public sealed class SimilarityApprovalGuardTests
{
    private static readonly SimilarityOptions Options = SimilarityOptions.For(Language.Vi, new SimilaritySettings());

    [Fact]
    public void Guard_ScoreAboveBlock_IsBlock() =>
        SimilarityApprovalGuard.Classify(0.1501, Options).ShouldBe(SimilarityVerdict.Block);

    [Fact]
    public void Guard_ScoreAtBlock_IsWarn() =>
        SimilarityApprovalGuard.Classify(0.15, Options).ShouldBe(SimilarityVerdict.Warn);

    [Fact]
    public void Guard_ScoreInWarnBand_IsWarn() =>
        SimilarityApprovalGuard.Classify(0.0501, Options).ShouldBe(SimilarityVerdict.Warn);

    [Fact]
    public void Guard_NullScore_IsPass() =>
        SimilarityApprovalGuard.Classify(null, Options).ShouldBe(SimilarityVerdict.Pass);

    [Fact]
    public void Guard_ScoreAtWarn_IsPass() =>
        SimilarityApprovalGuard.Classify(0.05, Options).ShouldBe(SimilarityVerdict.Pass);

    [Fact]
    public void Guard_BlockAboveOne_AllowsWarnOnlyRollback() =>
        SimilarityApprovalGuard.Classify(1, Options with { BlockAbove = 1 }).ShouldBe(SimilarityVerdict.Warn);
}
