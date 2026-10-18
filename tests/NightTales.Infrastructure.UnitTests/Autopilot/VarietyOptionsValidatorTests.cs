using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightTales.Application.Features.Autopilot.Ideas.Abstractions;
using NightTales.Domain.Stories.Structure;
using NightTales.Infrastructure.Autopilot;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Autopilot;

/// <summary>
/// 02.8.4-T1 plan "Config keys": <c>Autopilot:Variety</c> — the section binds <em>and</em> validates on start
/// (through <c>AddAutopilotIdeas</c>, the one path both hosts compose), its defaults are the Domain's own, and an
/// impossible window, cap or rotation floor fails the boot instead of silently changing every rejection and every
/// steering suggestion of a paid run.
/// </summary>
public sealed class VarietyOptionsValidatorTests
{
    private const string SectionName = VarietyOptionsSection.SectionName;

    [Fact]
    public void Variety_Defaults_AreTheDomainDefaults_AndValid()
    {
        var section = new VarietyOptionsSection();

        new VarietyOptionsValidator().Validate(null, section).Succeeded.ShouldBeTrue();
        section.ToOptions().ShouldBe(VarietyOptions.Default);

        // The plan's first guesses (plan §10 item 10), pinned here so a drift shows up as a failing boot test.
        VarietyOptions.Default.WindowK.ShouldBe(10);
        VarietyOptions.Default.TemplateWindowK.ShouldBe(5);
        VarietyOptions.Default.TagShareMax.ShouldBe(0.40m);
        VarietyOptions.Default.RotationMinCount.ShouldBe(3);
        VarietyOptions.Default.Enabled.ShouldBeTrue();
    }

    [Fact]
    public void Variety_Section_BindsAndRegistersTheDomainOptions()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            [$"{SectionName}:Enabled"] = "false",
            [$"{SectionName}:WindowK"] = "4",
            [$"{SectionName}:TemplateWindowK"] = "2",
            [$"{SectionName}:TagShareMax"] = "0.25",
            [$"{SectionName}:RotationMinCount"] = "2",
        });

        provider.GetRequiredService<VarietyOptions>().ShouldBe(new VarietyOptions(4, 2, 0.25m, 2, false));
    }

    [Fact]
    public void Variety_InvalidConfiguration_FailsTheOptionsPipeline()
    {
        using var provider = BuildProvider(new Dictionary<string, string?> { [$"{SectionName}:WindowK"] = "0" });

        var exception = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<VarietyOptionsSection>>().Value);

        exception.Failures.ShouldContain(failure => failure.Contains("WindowK", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Variety_WindowK_BelowOne_Fails(int windowK)
    {
        var result = new VarietyOptionsValidator().Validate(null, new VarietyOptionsSection { WindowK = windowK });

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull().ShouldContain(failure => failure.Contains("WindowK", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(51)]
    [InlineData(1_000_000)]
    public void Variety_WindowK_AboveTheHistoryCap_Fails(int windowK)
    {
        // The idea step assesses exactly the rows its context carries (IdeaContextProvider.HistoryCount), so a
        // wider window would silently assess fewer entries than the story-time plan reads.
        var result = new VarietyOptionsValidator().Validate(null, new VarietyOptionsSection { WindowK = windowK });

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull().ShouldContain(failure => failure.Contains("WindowK", StringComparison.Ordinal));
    }

    [Fact]
    public void Variety_WindowK_AtTheHistoryCap_IsLegal()
    {
        var result = new VarietyOptionsValidator()
            .Validate(null, new VarietyOptionsSection { WindowK = IdeaContextProvider.HistoryCount });

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Variety_TemplateWindowK_WiderThanTheWindow_Fails()
    {
        var result = new VarietyOptionsValidator()
            .Validate(null, new VarietyOptionsSection { WindowK = 5, TemplateWindowK = 6 });

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull().ShouldContain(failure => failure.Contains("TemplateWindowK", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Variety_TagShareMax_OutsideZeroToOne_Fails(double tagShareMax)
    {
        var result = new VarietyOptionsValidator()
            .Validate(null, new VarietyOptionsSection { TagShareMax = (decimal)tagShareMax });

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull().ShouldContain(failure => failure.Contains("TagShareMax", StringComparison.Ordinal));
    }

    [Fact]
    public void Variety_TagShareMax_AtOne_IsLegal()
    {
        new VarietyOptionsValidator().Validate(null, new VarietyOptionsSection { TagShareMax = 1m }).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Variety_RotationMinCount_BelowOne_Fails(int rotationMinCount)
    {
        var result = new VarietyOptionsValidator()
            .Validate(null, new VarietyOptionsSection { RotationMinCount = rotationMinCount });

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull().ShouldContain(failure => failure.Contains("RotationMinCount", StringComparison.Ordinal));
    }

    [Fact]
    public void AddAutopilotIdeas_RegistersTheStructurePlanProviderScoped()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build());
        services.AddLogging();
        services.AddAutopilotIdeas();

        services.ShouldContain(
            descriptor => descriptor.ServiceType == typeof(IStructurePlanProvider)
                && descriptor.ImplementationType == typeof(StructurePlanProvider)
                && descriptor.Lifetime == ServiceLifetime.Scoped);
    }

    private static ServiceProvider BuildProvider(IReadOnlyDictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddAutopilotIdeas();

        return services.BuildServiceProvider();
    }
}
