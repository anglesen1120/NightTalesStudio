using Microsoft.Extensions.Configuration;
using NightTales.Application.Features.Autopilot.Orchestration;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Autopilot.Orchestration;

/// <summary>
/// <see cref="AutopilotRunOptions"/> (02.8.3 plan §3.4, task file §Config): the shipped defaults are the plan's
/// numbers and valid, every bound rejects what would break the timing chain, the task file's older key names are
/// still honoured as fall-backs with the canonical <c>Autopilot:Run</c> key winning, and the two YouTube timing
/// values are derived rather than duplicated.
/// </summary>
public sealed class AutopilotRunOptionsTests
{
    /// <summary>The plan's numbers (plan §3.4, task file §Config), as the class ships them.</summary>
    [Fact]
    public void Defaults_AreThePlanNumbers_AndValid()
    {
        var options = new AutopilotRunOptions();

        options.MaxProducingRunsPerSeries.ShouldBe(1);
        options.WaitingOwnerExpiryHours.ShouldBe(48);
        options.StuckRunMinutes.ShouldBe(60);
        options.MaxStepsPerTick.ShouldBe(6);
        options.PlanJobMaxRunsPerTick.ShouldBe(200);
        options.PublicationCreateMarginMinutes.ShouldBe(10);
        options.RenderEstimateMinutes.ShouldBe(15);
        options.StoryEstimateMinutes.ShouldBe(10);
        options.MaxBufferDays.ShouldBe(7);
        options.PublishLeadMinutes.ShouldBe(120);
        options.MinVetoWindowHours.ShouldBe(2);

        AutopilotRunOptions.Validate(options).ShouldBeEmpty();
    }

    /// <summary>The canonical section name, so the host's binder, the appsettings block and this class agree.</summary>
    [Fact]
    public void SectionName_IsTheRunSubsection()
    {
        AutopilotRunOptions.SectionName.ShouldBe("Autopilot:Run");
    }

    /// <summary>
    /// The shipped per-tick run budget (200) and its ceiling (2000): the 10 000-series planner needs the headroom
    /// (review finding PF-3), and a value past the ceiling is still refused by name.
    /// </summary>
    /// <summary>
    /// A zero creation margin is legal (plan §3.4: the publication is created exactly at its slot), so the sweep that
    /// rejects zero leaves this value out; a negative margin is still refused by name.
    /// </summary>
    [Fact]
    public void Validate_AcceptsAZeroCreationMargin_AndRejectsANegativeOne()
    {
        var options = new AutopilotRunOptions { PublicationCreateMarginMinutes = 0 };
        AutopilotRunOptions.Validate(options).ShouldBeEmpty();

        options.PublicationCreateMarginMinutes = -1;

        AutopilotRunOptions.Validate(options)
            .ShouldHaveSingleItem()
            .ShouldContain("Autopilot:Run:PublicationCreateMarginMinutes");
    }

    [Fact]
    public void PlanJobMaxRunsPerTick_HasTheWidenedThroughputRange()
    {
        var options = new AutopilotRunOptions { PlanJobMaxRunsPerTick = 2000 };
        AutopilotRunOptions.Validate(options).ShouldBeEmpty();

        options.PlanJobMaxRunsPerTick = 2001;

        AutopilotRunOptions.Validate(options)
            .ShouldHaveSingleItem()
            .ShouldContain("Autopilot:Run:PlanJobMaxRunsPerTick");
    }

    /// <summary>Each impossible value names its own key and leaves the rest alone (one message per problem).</summary>
    /// <param name="change">The single value to break.</param>
    /// <param name="key">The key the message must name.</param>
    [Theory]
    [InlineData(nameof(AutopilotRunOptions.MaxProducingRunsPerSeries), "Autopilot:Run:MaxProducingRunsPerSeries")]
    [InlineData(nameof(AutopilotRunOptions.WaitingOwnerExpiryHours), "Autopilot:Run:WaitingOwnerExpiryHours")]
    [InlineData(nameof(AutopilotRunOptions.StuckRunMinutes), "Autopilot:Run:StuckRunMinutes")]
    [InlineData(nameof(AutopilotRunOptions.MaxStepsPerTick), "Autopilot:Run:MaxStepsPerTick")]
    [InlineData(nameof(AutopilotRunOptions.PlanJobMaxRunsPerTick), "Autopilot:Run:PlanJobMaxRunsPerTick")]
    [InlineData(nameof(AutopilotRunOptions.RenderEstimateMinutes), "Autopilot:Run:RenderEstimateMinutes")]
    [InlineData(nameof(AutopilotRunOptions.StoryEstimateMinutes), "Autopilot:Run:StoryEstimateMinutes")]
    [InlineData(nameof(AutopilotRunOptions.MaxBufferDays), "Autopilot:Run:MaxBufferDays")]
    public void Validate_RejectsZero_AndNamesTheKey(string change, string key)
    {
        var options = new AutopilotRunOptions();
        typeof(AutopilotRunOptions).GetProperty(change)!.SetValue(options, 0);

        var problems = AutopilotRunOptions.Validate(options);

        problems.ShouldHaveSingleItem().ShouldContain(key);
        problems.ShouldHaveSingleItem().ShouldNotContain("=0", Case.Sensitive);
    }

    /// <summary>
    /// The two derived timing values are validated under their own <c>YouTube</c> keys (06 CR-3), and their
    /// setters are private, so a stray <c>Autopilot:Run:PublishLeadMinutes</c> cannot bind at all.
    /// </summary>
    [Fact]
    public void Validate_RejectsZeroTimingValues_AndTheirSettersArePrivate()
    {
        typeof(AutopilotRunOptions).GetProperty(nameof(AutopilotRunOptions.PublishLeadMinutes))!
            .SetMethod!.IsPublic.ShouldBeFalse();
        typeof(AutopilotRunOptions).GetProperty(nameof(AutopilotRunOptions.MinVetoWindowHours))!
            .SetMethod!.IsPublic.ShouldBeFalse();

        var options = new AutopilotRunOptions();
        var configuration = Configuration(
            ("YouTube:PublishLeadMinutes", "0"), ("YouTube:MinVetoWindowHours", "0"));
        AutopilotRunOptions.ApplyPlatformValues(options, key => configuration[key]);

        var problems = AutopilotRunOptions.Validate(options);

        problems.Count.ShouldBe(2);
        problems.ShouldContain(problem => problem.Contains("YouTube:PublishLeadMinutes", StringComparison.Ordinal));
        problems.ShouldContain(problem => problem.Contains("YouTube:MinVetoWindowHours", StringComparison.Ordinal));
    }

    /// <summary>
    /// A canonical <c>Autopilot:Run:*</c> key wins over the task file's older name: the older name is a migration
    /// path, never a second live source.
    /// </summary>
    [Fact]
    public void ApplyPlatformValues_CanonicalKeyWinsOverTaskFileKey()
    {
        var options = new AutopilotRunOptions();
        var configuration = Configuration(
            ("Autopilot:PlanJob:MaxRunsPerTick", "7"),
            ("Autopilot:Run:PlanJobMaxRunsPerTick", "9"));

        AutopilotRunOptions.ApplyPlatformValues(options, key => configuration[key]);

        options.PlanJobMaxRunsPerTick.ShouldBe(9);
    }

    /// <summary>
    /// A deployment that set only the task file's dotted keys still takes effect: every alias the class documents
    /// is read when its canonical key is absent.
    /// </summary>
    [Fact]
    public void ApplyPlatformValues_TaskFileKeys_AreHonouredWhenCanonicalAbsent()
    {
        var options = new AutopilotRunOptions();
        var configuration = Configuration(
            ("Autopilot:PlanJob:MaxRunsPerTick", "7"),
            ("Autopilot:MaxProducingRunsPerSeries", "2"),
            ("Autopilot:PublicationCreateMarginMinutes", "12"),
            ("Autopilot:MaxBufferDays", "5"),
            ("Autopilot:WaitingOwner:ExpiryHours", "72"),
            ("Autopilot:RenderEstimate:Minutes", "20"),
            ("Autopilot:StoryEstimate:Minutes", "11"));

        AutopilotRunOptions.ApplyPlatformValues(options, key => configuration[key]);

        options.PlanJobMaxRunsPerTick.ShouldBe(7);
        options.MaxProducingRunsPerSeries.ShouldBe(2);
        options.PublicationCreateMarginMinutes.ShouldBe(12);
        options.MaxBufferDays.ShouldBe(5);
        options.WaitingOwnerExpiryHours.ShouldBe(72);
        options.RenderEstimateMinutes.ShouldBe(20);
        options.StoryEstimateMinutes.ShouldBe(11);
    }

    /// <summary>
    /// The two timing values come from <c>YouTube</c> (06 CR-3: no second <c>Autopilot:</c> key to drift), and an
    /// absent or unparsable one keeps the shipped default rather than failing a Worker that does not bind it.
    /// </summary>
    [Fact]
    public void ApplyPlatformValues_ReadsYouTubeTiming_AndFallsBackToDefaults()
    {
        var options = new AutopilotRunOptions();
        var configuration = Configuration(("YouTube:PublishLeadMinutes", "90"), ("YouTube:MinVetoWindowHours", "3"));
        AutopilotRunOptions.ApplyPlatformValues(options, key => configuration[key]);

        options.PublishLeadMinutes.ShouldBe(90);
        options.MinVetoWindowHours.ShouldBe(3);
        options.PublishLeadMinutes.ShouldNotBe(AutopilotRunOptions.DefaultPublishLeadMinutes);

        var untouched = new AutopilotRunOptions();
        var strayConfiguration = Configuration(
            ("YouTube:PublishLeadMinutes", "not-a-number"),
            ("Autopilot:Run:PublishLeadMinutes", "45"));
        AutopilotRunOptions.ApplyPlatformValues(untouched, key => strayConfiguration[key]);

        untouched.PublishLeadMinutes.ShouldBe(AutopilotRunOptions.DefaultPublishLeadMinutes);
        untouched.MinVetoWindowHours.ShouldBe(AutopilotRunOptions.DefaultMinVetoWindowHours);
    }

    /// <summary>The domain's timing view carries every value the chain computes with, in one place.</summary>
    [Fact]
    public void ToTimingOptions_CarriesEveryTimingValue()
    {
        var options = new AutopilotRunOptions
        {
            PublicationCreateMarginMinutes = 11,
            RenderEstimateMinutes = 16,
            StoryEstimateMinutes = 9,
            MaxBufferDays = 3,
        };
        var configuration = Configuration(("YouTube:PublishLeadMinutes", "100"), ("YouTube:MinVetoWindowHours", "4"));
        AutopilotRunOptions.ApplyPlatformValues(options, key => configuration[key]);

        var timing = options.ToTimingOptions();

        timing.PublishLeadMinutes.ShouldBe(100);
        timing.MinVetoWindowHours.ShouldBe(4);
        timing.PublicationCreateMarginMinutes.ShouldBe(11);
        timing.RenderEstimateMinutes.ShouldBe(16);
        timing.StoryEstimateMinutes.ShouldBe(9);
        timing.MaxBufferDays.ShouldBe(3);
    }

    /// <summary>
    /// The pipeline the host actually runs (<c>BindConfiguration</c> then <c>Configure&lt;IConfiguration&gt;</c>):
    /// a stray <c>Autopilot:Run:PublishLeadMinutes</c> is ignored and the YouTube value is what the options carry.
    /// </summary>
    [Fact]
    public void BinderPipeline_YouTubeIsTheOnlySourceOfTheTimingValues()
    {
        var configuration = Configuration(
            ("Autopilot:Run:PublishLeadMinutes", "45"),
            ("Autopilot:Run:MinVetoWindowHours", "9"),
            ("Autopilot:Run:PlanJobMaxRunsPerTick", "12"),
            ("YouTube:PublishLeadMinutes", "90"),
            ("YouTube:MinVetoWindowHours", "3"));

        var options = new AutopilotRunOptions();
        configuration.GetSection(AutopilotRunOptions.SectionName).Bind(options);
        AutopilotRunOptions.ApplyPlatformValues(options, key => configuration[key]);

        options.PublishLeadMinutes.ShouldBe(90);
        options.MinVetoWindowHours.ShouldBe(3);
        options.PlanJobMaxRunsPerTick.ShouldBe(12);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value))
            .Build();
}
