using NightTales.Application.Features.Stories.Quality;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Stories.Quality;

/// <summary>Independent QA of 02.3.4-T1 option validation: every documented bound, both sides.</summary>
public sealed class StoryQualityOptionsQaBoundaryTests
{
    private static StoryQualityOptions Valid() => new()
    {
        LengthTolerance = 0.15,
        MinHookTokens = 3,
        MaxHookSentences = 3,
        KeywordsPerSegment = [1, 2],
        KeywordsPerMinute = 6,
        RepeatedSentenceMinTokens = 12,
        MaxInternalShingleShare = 0.25,
        NameHeuristic = true,
    };

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(0.5, true)]
    [InlineData(-0.0001, false)]
    [InlineData(0.5001, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    public void LengthTolerance_Bounds(double value, bool valid)
    {
        var options = Valid();
        options.LengthTolerance = value;

        StoryQualityOptionsValidator.Validate(options).Count.ShouldBe(valid ? 0 : 1);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(20, true)]
    [InlineData(0, false)]
    [InlineData(21, false)]
    public void KeywordsPerMinute_Bounds(int value, bool valid)
    {
        var options = Valid();
        options.KeywordsPerMinute = value;

        StoryQualityOptionsValidator.Validate(options).Count.ShouldBe(valid ? 0 : 1);
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(3, false)]
    public void RepeatedSentenceMinTokens_Bounds(int value, bool valid)
    {
        var options = Valid();
        options.RepeatedSentenceMinTokens = value;

        StoryQualityOptionsValidator.Validate(options).Count.ShouldBe(valid ? 0 : 1);
    }

    [Theory]
    [InlineData(0.0001, true)]
    [InlineData(0.9999, true)]
    [InlineData(0.0, false)]
    [InlineData(1.0, false)]
    [InlineData(double.NaN, false)]
    public void MaxInternalShingleShare_IsExclusiveZeroToOne(double value, bool valid)
    {
        var options = Valid();
        options.MaxInternalShingleShare = value;

        StoryQualityOptionsValidator.Validate(options).Count.ShouldBe(valid ? 0 : 1);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0, 5, true)]
    [InlineData(5, 5, true)]
    [InlineData(2, 2, true)]
    [InlineData(3, 1, false)]
    [InlineData(-1, 2, false)]
    [InlineData(0, 6, false)]
    [InlineData(6, 6, false)]
    public void KeywordsPerSegment_Range(int min, int max, bool valid)
    {
        var options = Valid();
        options.KeywordsPerSegment = [min, max];

        StoryQualityOptionsValidator.Validate(options).Count.ShouldBe(valid ? 0 : 1);
        if (valid)
        {
            var thresholds = options.ToThresholds();
            thresholds.MinKeywordsPerSegment.ShouldBe(min);
            thresholds.MaxKeywordsPerSegment.ShouldBe(max);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void KeywordsPerSegment_WrongLength_Fails(int length)
    {
        var options = Valid();
        options.KeywordsPerSegment = new int[length];

        StoryQualityOptionsValidator.Validate(options).Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, false)]
    public void MinHookTokens_Bounds(int value, bool valid)
    {
        var options = Valid();
        options.MinHookTokens = value;

        StoryQualityOptionsValidator.Validate(options).Count.ShouldBe(valid ? 0 : 1);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void MaxHookSentences_Bounds(int value, bool valid)
    {
        var options = Valid();
        options.MaxHookSentences = value;

        StoryQualityOptionsValidator.Validate(options).Count.ShouldBe(valid ? 0 : 1);
    }

    [Fact]
    public void EachMissingKey_IsReportedByItsOwnName()
    {
        var removers = new Dictionary<string, Action<StoryQualityOptions>>
        {
            ["LengthTolerance"] = o => o.LengthTolerance = null,
            ["MinHookTokens"] = o => o.MinHookTokens = null,
            ["MaxHookSentences"] = o => o.MaxHookSentences = null,
            ["KeywordsPerSegment"] = o => o.KeywordsPerSegment = null,
            ["KeywordsPerMinute"] = o => o.KeywordsPerMinute = null,
            ["RepeatedSentenceMinTokens"] = o => o.RepeatedSentenceMinTokens = null,
            ["MaxInternalShingleShare"] = o => o.MaxInternalShingleShare = null,
            ["NameHeuristic"] = o => o.NameHeuristic = null,
        };

        foreach (var (key, remove) in removers)
        {
            var options = Valid();
            remove(options);

            var problem = StoryQualityOptionsValidator.Validate(options).ShouldHaveSingleItem();
            problem.ShouldContain("Story:Quality:" + key);
            Should.Throw<InvalidOperationException>(() => options.ToThresholds());
        }
    }

    [Fact]
    public void MultipleProblems_AreAllListed()
    {
        var options = Valid();
        options.LengthTolerance = 9;
        options.KeywordsPerMinute = 99;
        options.MaxInternalShingleShare = 0;

        StoryQualityOptionsValidator.Validate(options).Count.ShouldBe(3);
    }
}
