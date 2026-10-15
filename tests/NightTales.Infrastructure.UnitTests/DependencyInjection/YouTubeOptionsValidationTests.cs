using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightTales.Application.Publishing;
using NightTales.Infrastructure.DependencyInjection;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.DependencyInjection;

/// <summary>
/// 06.3.1-T1 failure table: <c>YouTube:Quota:UnitsPerDay</c> below 1 fails the start-up, while the default of 100 is
/// valid. The options are resolved through the public <c>AddYouTubePublishing</c> path; the ledger is never resolved,
/// so no database context is needed.
/// </summary>
public sealed class YouTubeOptionsValidationTests
{
    private const string UnitsPerDayKey = "YouTube:Quota:UnitsPerDay";

    [Fact]
    public void YouTubeOptions_ZeroUnitsPerDay_FailsValidation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [UnitsPerDayKey] = "0" })
            .Build();
        using var provider = BuildProvider(configuration);

        var exception = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<YouTubeOptions>>().Value);

        exception.Failures.ShouldContain("'YouTube:Quota:UnitsPerDay' must be at least 1.");
    }

    [Fact]
    public void YouTubeOptions_DefaultUnitsPerDay_Is100AndValid()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        using var provider = BuildProvider(configuration);

        var options = provider.GetRequiredService<IOptions<YouTubeOptions>>().Value;

        options.Quota.UnitsPerDay.ShouldBe(100);
    }

    private static ServiceProvider BuildProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddYouTubePublishing();
        return services.BuildServiceProvider();
    }
}
