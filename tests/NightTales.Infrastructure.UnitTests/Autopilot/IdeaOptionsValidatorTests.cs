using Microsoft.Extensions.Options;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Infrastructure.Autopilot;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Autopilot;

/// <summary>
/// 02.8.1-T1: <see cref="IdeaOptionsValidator"/> — the <c>Autopilot:Ideas</c> start-up contract: the
/// candidate count and its ceiling inside 1..<see cref="IdeaOptions.MaxCountCeiling"/> (the schema's
/// <c>maxItems</c>), the count never above the ceiling, and usable Domain limits. An impossible section
/// fails the boot instead of reaching a paid call (plan §7.1).
/// </summary>
public sealed class IdeaOptionsValidatorTests
{
    [Fact]
    public void Validate_ThePlanDefaults_Succeed_AndMatchTheSchemaCeiling()
    {
        new IdeaOptionsValidator().Validate(null, new IdeaOptions()).Succeeded.ShouldBeTrue();

        IdeaOptions.DefaultCount.ShouldBe(5);
        IdeaOptions.DefaultMaxCount.ShouldBe(10);
        // The schema's `ideas` maxItems: a configured count can never ask for more than a valid answer may carry.
        IdeaOptions.MaxCountCeiling.ShouldBe(10);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public void Validate_CountInsideTheBand_Succeeds(int count)
    {
        var options = new IdeaOptions { Count = count, MaxCount = count };

        new IdeaOptionsValidator().Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(11)]
    public void Validate_CountOutsideTheBand_Fails(int count)
    {
        var options = new IdeaOptions { Count = count };

        var result = new IdeaOptionsValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain($"{nameof(IdeaOptions.Count)}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Validate_MaxCountOutsideTheBand_Fails(int maxCount)
    {
        var options = new IdeaOptions { Count = 1, MaxCount = maxCount };

        var result = new IdeaOptionsValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain($"{nameof(IdeaOptions.MaxCount)}");
    }

    [Fact]
    public void Validate_CountAboveMaxCount_Fails()
    {
        // 6 is a legal count and 6 a legal ceiling, but asking for six while capping at five is a contradiction.
        var options = new IdeaOptions { Count = 6, MaxCount = 5 };

        var result = new IdeaOptionsValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("cannot exceed");
    }

    [Fact]
    public void Validate_TheBoundaryPair_Succeeds()
    {
        // Count == MaxCount is a real configuration (every run asks for the full allowed batch).
        new IdeaOptionsValidator()
            .Validate(null, new IdeaOptions { Count = IdeaOptions.MaxCountCeiling, MaxCount = IdeaOptions.MaxCountCeiling })
            .Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Validate_UnusableLimits_Fail()
    {
        var result = new IdeaOptionsValidator()
            .Validate(null, new IdeaOptions { Limits = new IdeaLimits(TitleChars: 0) });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Limits");
    }

    [Fact]
    public void Validate_AnInvertedBeatBand_Fails()
    {
        var result = new IdeaOptionsValidator()
            .Validate(null, new IdeaOptions { Limits = new IdeaLimits(MinBeats: 8, MaxBeats: 3) });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Limits");
    }

    [Fact]
    public void Validate_SeveralViolations_NamesEveryProblem()
    {
        var result = new IdeaOptionsValidator()
            .Validate(null, new IdeaOptions { Count = 0, MaxCount = 0, Limits = new IdeaLimits(TitleChars: -1) });

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull().Count().ShouldBe(3);
        result.FailureMessage.ShouldContain($"{nameof(IdeaOptions.Count)}");
        result.FailureMessage.ShouldContain($"{nameof(IdeaOptions.MaxCount)}");
        result.FailureMessage.ShouldContain("Limits");
    }

    [Fact]
    public void Validate_WidthsAtThePlanDefault_Succeed()
    {
        // The plan's numbers are the widest a host may configure (they are the pinned episode_ideas columns).
        var limits = new IdeaLimits(TitleChars: 100, LoglineChars: 300, HookChars: 300, SettingChars: 120, MinBeats: 3, MaxBeats: 8);

        new IdeaOptionsValidator().Validate(null, new IdeaOptions { Limits = limits }).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Validate_TightenedWidthsAndBand_Succeed()
    {
        // QA defect D4: tighter than the plan is always legal — a shorter field and a shorter outline still fit
        // the columns and the schema.
        var limits = new IdeaLimits(TitleChars: 80, LoglineChars: 200, HookChars: 250, SettingChars: 60, MinBeats: 4, MaxBeats: 6);

        new IdeaOptionsValidator().Validate(null, new IdeaOptions { Limits = limits }).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(101, 300, 300, 120, "TitleChars")]
    [InlineData(100, 301, 300, 120, "LoglineChars")]
    [InlineData(100, 300, 301, 120, "HookChars")]
    [InlineData(100, 300, 300, 121, "SettingChars")]
    public void Validate_WidthOneAboveTheColumn_Fails(int title, int logline, int hook, int setting, string key)
    {
        // One above the pinned column width would insert-fail with 22001 *after* the paid call (QA defect D4).
        var limits = new IdeaLimits(TitleChars: title, LoglineChars: logline, HookChars: hook, SettingChars: setting);

        var result = new IdeaOptionsValidator().Validate(null, new IdeaOptions { Limits = limits });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain(key);
        result.FailureMessage.ShouldContain("may only tighten");
    }

    [Fact]
    public void Validate_MinBeatsBelowTheSchemaFloor_Fails()
    {
        // The ideas.v1 schema's beats minItems is 3: asking for fewer requires an answer the schema forbids.
        var result = new IdeaOptionsValidator()
            .Validate(null, new IdeaOptions { Limits = new IdeaLimits(MinBeats: 2, MaxBeats: 8) });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain(nameof(IdeaLimits.MinBeats));
    }

    [Fact]
    public void Validate_MaxBeatsAboveTheSchemaCeiling_Fails()
    {
        // The ideas.v1 schema's beats maxItems is 8: a wider band can never be honoured by a valid answer.
        var result = new IdeaOptionsValidator()
            .Validate(null, new IdeaOptions { Limits = new IdeaLimits(MinBeats: 3, MaxBeats: 9) });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain(nameof(IdeaLimits.MaxBeats));
    }

    [Fact]
    public void Validate_NullSection_Throws() =>
        Should.Throw<ArgumentNullException>(() => new IdeaOptionsValidator().Validate(null, null!));
}
