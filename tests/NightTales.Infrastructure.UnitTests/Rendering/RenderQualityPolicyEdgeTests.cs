using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NightTales.Application.Rendering.Quality;
using NightTales.Infrastructure.DependencyInjection;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Rendering;

/// <summary>
/// QA edge pass over the <c>RenderQuality</c> startup validation (05.5.1-T2, 05 README D10): the
/// validator must reject every out-of-range value — including non-finite numbers the in-memory
/// binder accepts — and a generic host must actually run the validation on start.
/// </summary>
public sealed class RenderQualityPolicyEdgeTests
{
    private static readonly string[] NonFinite = ["NaN", "Infinity", "-Infinity"];

    [Theory]
    [InlineData("MinLufs", "NaN")]
    [InlineData("MinLufs", "-Infinity")]
    [InlineData("MaxLufs", "NaN")]
    [InlineData("MaxLufs", "Infinity")]
    [InlineData("MaxTruePeakDbtp", "NaN")]
    [InlineData("MaxTruePeakDbtp", "Infinity")]
    [InlineData("MaxDurationDrift", "NaN")]
    [InlineData("MaxDurationDrift", "Infinity")]
    [InlineData("MaxLongestSilenceSeconds", "NaN")]
    [InlineData("MaxLongestSilenceSeconds", "Infinity")]
    [InlineData("MaxLongestSilenceSeconds", "-Infinity")]
    [InlineData("MinSubtitleCoverage", "NaN")]
    [InlineData("MinSubtitleCoverage", "Infinity")]
    [InlineData("SyncSuspectHintShare", "NaN")]
    [InlineData("SyncSuspectHintShare", "Infinity")]
    public void Startup_rejects_non_finite_threshold(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{RenderQualityPolicy.SectionName}:{key}"] = value,
            })
            .Build();

        using var provider = new ServiceCollection().AddRenderQuality(configuration).BuildServiceProvider();

        var exception = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<RenderQualityPolicy>>().Value);
        exception.Message.ShouldContain($"{RenderQualityPolicy.SectionName}:{key}");
    }

    [Fact]
    public async Task Generic_host_with_defaults_starts_and_validates()
    {
        // Acceptance 5/Notes: the wiring must be a real ValidateOnStart — a host that defaults the
        // section starts cleanly, and an out-of-range section fails the start, not the resolution.
        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddRenderQuality(new ConfigurationBuilder().Build()))
            .Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        host.Services.GetRequiredService<IOptions<RenderQualityPolicy>>().Value.PolicyVersion.ShouldBe(1);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Generic_host_fails_to_start_on_out_of_range_section()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{RenderQualityPolicy.SectionName}:MinLufs"] = "-13",
            })
            .Build();

        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddRenderQuality(configuration))
            .Build();

        await Should.ThrowAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
    }
}
