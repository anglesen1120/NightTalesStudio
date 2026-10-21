using NightTales.Application.Features.Autopilot.Ideas;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Infrastructure.Autopilot;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Autopilot;

/// <summary>
/// 02.8.1-T2 task Design: the three <c>Autopilot:Ideas</c> sections of T2 validated on start — the novelty
/// thresholds with the corpus cap, the pick weights and the policy pre-check. Each validator is called the way
/// the options pipeline calls it, so the assertions cover the message the boot prints too.
/// </summary>
public sealed class IdeaNoveltyOptionsValidatorTests
{
    [Fact]
    public void Novelty_Defaults_AreValid()
    {
        var result = new IdeaNoveltyOptionsValidator().Validate(null, new IdeaNoveltyOptions());

        result.Succeeded.ShouldBeTrue();
        new IdeaNoveltyOptions().CorpusEpisodes.ShouldBe(IdeaNoveltyOptions.DefaultCorpusEpisodes);

        // The corpus cap may reach the reader's own ceiling.
        new IdeaNoveltyOptionsValidator()
            .Validate(null, new IdeaNoveltyOptions { CorpusEpisodes = IdeaCorpusProvider.MaxCorpusEpisodes })
            .Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(201)]
    public void Novelty_CorpusEpisodes_OutsideOneToTwoHundred_Fails(int corpusEpisodes)
    {
        var result = new IdeaNoveltyOptionsValidator()
            .Validate(null, new IdeaNoveltyOptions { CorpusEpisodes = corpusEpisodes });

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull().ShouldContain(failure => failure.Contains("CorpusEpisodes", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1.5, 0.70)]
    [InlineData(-0.1, 0.70)]
    [InlineData(0.35, 1.5)]
    [InlineData(0.35, -0.1)]
    public void Novelty_Thresholds_OutsideZeroToOne_Fail(double lexicalMax, double judgeMax)
    {
        var result = new IdeaNoveltyOptionsValidator().Validate(
            null,
            new IdeaNoveltyOptions { LexicalMax = (decimal)lexicalMax, JudgeMax = (decimal)judgeMax });

        result.Failed.ShouldBeTrue();
    }

    [Fact]
    public void Novelty_BothThresholdsAtOne_AreValid_TheRollbackRecipe()
    {
        // The task's failure table: LexicalMax = JudgeMax = 1.0 degenerates the pipeline to "pick the first valid
        // candidate" without a code change, so the boundary value must stay legal.
        new IdeaNoveltyOptionsValidator()
            .Validate(null, new IdeaNoveltyOptions { LexicalMax = 1m, JudgeMax = 1m })
            .Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Policy_TheWordMatchMode_IsTheOnlyAcceptedValue()
    {
        new IdeaPolicyOptionsValidator().Validate(null, new IdeaPolicyOptions()).Succeeded.ShouldBeTrue();

        var result = new IdeaPolicyOptionsValidator()
            .Validate(null, new IdeaPolicyOptions { BannedThemeMatchMode = "substring" });

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull().ShouldContain(failure => failure.Contains("BannedThemeMatchMode", StringComparison.Ordinal));
    }

    [Fact]
    public void Weights_EveryWeight_IsARatio()
    {
        new IdeaWeightsValidator().Validate(null, new IdeaWeightsOptions()).Succeeded.ShouldBeTrue();

        // Zero disables a component (the rollback recipe sets Variety to 0), so it stays legal.
        new IdeaWeightsValidator().Validate(null, new IdeaWeightsOptions { Variety = 0m }).Succeeded.ShouldBeTrue();

        new IdeaWeightsValidator().Validate(null, new IdeaWeightsOptions { Variety = -0.01m }).Failed.ShouldBeTrue();
        new IdeaWeightsValidator().Validate(null, new IdeaWeightsOptions { Novelty = 1.01m }).Failed.ShouldBeTrue();
        new IdeaWeightsValidator()
            .Validate(null, new IdeaWeightsOptions { Fit = 2m })
            .Failures.ShouldNotBeNull()
            .ShouldContain(failure => failure.Contains("Fit", StringComparison.Ordinal));
    }
}
