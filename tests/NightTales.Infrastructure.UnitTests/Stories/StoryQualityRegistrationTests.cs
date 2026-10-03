using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightTales.Domain.Stories.Quality;
using NightTales.Infrastructure.DependencyInjection;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Stories;

public sealed class StoryQualityRegistrationTests
{
    private static readonly Dictionary<string, string?> Valid = new()
    {
        ["Story:Quality:LengthTolerance"] = "0.15",
        ["Story:Quality:MinHookTokens"] = "3",
        ["Story:Quality:MaxHookSentences"] = "3",
        ["Story:Quality:KeywordsPerSegment:0"] = "1",
        ["Story:Quality:KeywordsPerSegment:1"] = "2",
        ["Story:Quality:KeywordsPerMinute"] = "6",
        ["Story:Quality:RepeatedSentenceMinTokens"] = "12",
        ["Story:Quality:MaxInternalShingleShare"] = "0.25",
        ["Story:Quality:NameHeuristic"] = "true",
    };

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddStoryQuality();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddStoryQuality_ValidBlock_ResolvesCheckerWithDefaults()
    {
        using var provider = Build(Valid);

        provider.GetRequiredService<IOptions<NightTales.Application.Features.Stories.Quality.StoryQualityOptions>>()
            .Value.ToThresholds().ShouldBe(StoryQualityThresholds.Defaults);
        provider.GetRequiredService<StoryQualityChecker>().ShouldNotBeNull();
    }

    [Fact]
    public void AddStoryQuality_InvalidKeywordRange_FailsOptionsValidation()
    {
        var settings = new Dictionary<string, string?>(Valid)
        {
            ["Story:Quality:KeywordsPerSegment:0"] = "3",
            ["Story:Quality:KeywordsPerSegment:1"] = "1",
        };
        using var provider = Build(settings);

        Should.Throw<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<NightTales.Application.Features.Stories.Quality.StoryQualityOptions>>().Value);
    }

    [Fact]
    public void AddStoryQuality_MissingBlock_FailsOptionsValidation()
    {
        using var provider = Build([]);

        Should.Throw<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<NightTales.Application.Features.Stories.Quality.StoryQualityOptions>>().Value);
    }
}
