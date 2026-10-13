using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightTales.Application.Features.Stories.Approval;
using NightTales.Infrastructure.DependencyInjection;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Stories;

/// <summary>
/// The <c>Story:TrueCrime</c> block (02.3.3-T1, 02 README T29) through the real registration: both keys
/// carry a default, so the block is optional and no deployment breaks by its absence, while a
/// present-but-invalid value fails the start-up instead of the first approval.
/// </summary>
public sealed class StoryTrueCrimeRegistrationTests
{
    /// <summary>32 characters: the longest accepted marker, one below the limit.</summary>
    private const string MaxLengthVersion = "ta.v1ta.v1ta.v1ta.v1ta.v1ta.v1ta";

    /// <summary>33 characters: one above <see cref="StoryTrueCrimeOptionsValidator.MaxAttestationVersionLength"/>.</summary>
    private const string TooLongVersion = "ta.v1ta.v1ta.v1ta.v1ta.v1ta.v1ta.";

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddStoryTrueCrime();

        return services.BuildServiceProvider();
    }

    [Fact]
    public void StoryTrueCrimeOptions_MissingBlock_UsesTheDefaults()
    {
        using var provider = Build([]);

        var options = provider.GetRequiredService<StoryTrueCrimeOptions>();

        options.AttestationVersion.ShouldBe("ta.v1");
        options.MinCitedSources.ShouldBe(2);
    }

    [Fact]
    public void StoryTrueCrimeOptions_ValidBlock_BindsBothKeys()
    {
        using var provider = Build(new Dictionary<string, string?>
        {
            ["Story:TrueCrime:AttestationVersion"] = "ta.v2",
            ["Story:TrueCrime:MinCitedSources"] = "4",
        });

        var options = provider.GetRequiredService<StoryTrueCrimeOptions>();

        options.AttestationVersion.ShouldBe("ta.v2");
        options.MinCitedSources.ShouldBe(4);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" ta.v1")]
    [InlineData("ta.v1 ")]
    [InlineData("ta\u0001v1")]
    [InlineData(TooLongVersion)]
    public void StoryTrueCrimeOptions_InvalidAttestationVersion_FailsStartupValidation(string version)
    {
        using var provider = Build(new Dictionary<string, string?> { ["Story:TrueCrime:AttestationVersion"] = version });

        var failure = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<StoryTrueCrimeOptions>());

        failure.Message.ShouldContain("Story:TrueCrime:AttestationVersion");
    }

    [Fact]
    public void StoryTrueCrimeOptions_AttestationVersionAtTheLengthLimit_IsAccepted()
    {
        using var provider = Build(new Dictionary<string, string?>
        {
            ["Story:TrueCrime:AttestationVersion"] = MaxLengthVersion,
        });

        provider.GetRequiredService<StoryTrueCrimeOptions>().AttestationVersion.ShouldBe(MaxLengthVersion);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("-1")]
    public void StoryTrueCrimeOptions_MinCitedSourcesOutOfRange_FailsStartupValidation(string floor)
    {
        using var provider = Build(new Dictionary<string, string?> { ["Story:TrueCrime:MinCitedSources"] = floor });

        var failure = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<StoryTrueCrimeOptions>());

        failure.Message.ShouldContain("Story:TrueCrime:MinCitedSources");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("10")]
    public void StoryTrueCrimeOptions_MinCitedSourcesAtTheBounds_IsAccepted(string floor)
    {
        using var provider = Build(new Dictionary<string, string?> { ["Story:TrueCrime:MinCitedSources"] = floor });

        provider.GetRequiredService<StoryTrueCrimeOptions>()
            .MinCitedSources.ShouldBe(int.Parse(floor, System.Globalization.CultureInfo.InvariantCulture));
    }
}
