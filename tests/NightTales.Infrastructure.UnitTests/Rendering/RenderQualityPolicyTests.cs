using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightTales.Application.Rendering.Quality;
using NightTales.Infrastructure.DependencyInjection;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Rendering;

/// <summary>
/// The <c>RenderQuality</c> section of 05.5.1-T2 (05 README D10): the D10 defaults, the binding, and the
/// startup validation — a band whose ends are inverted or a share outside 0–1 must fail when the host
/// resolves the options, never at evaluation time (task failure handling).
/// </summary>
public sealed class RenderQualityPolicyTests
{
    [Fact]
    public void Defaults_match_D10()
    {
        var policy = Resolve(Configuration());

        policy.MaxDurationDrift.ShouldBe(0.05);
        policy.MinLufs.ShouldBe(-16);
        policy.MaxLufs.ShouldBe(-14);
        policy.MaxTruePeakDbtp.ShouldBe(-1.0);
        policy.MaxLongestSilenceSeconds.ShouldBe(10);
        policy.MinSubtitleCoverage.ShouldBe(0.50);
        policy.MinCues.ShouldBe(1);
        policy.SyncSuspectHintShare.ShouldBe(0.10);
        policy.PolicyVersion.ShouldBe(1);
    }

    [Fact]
    public void Configuration_binds_the_section()
    {
        var policy = Resolve(Configuration(new Dictionary<string, string?>
        {
            [$"{RenderQualityPolicy.SectionName}:MaxDurationDrift"] = "0.1",
            [$"{RenderQualityPolicy.SectionName}:MinLufs"] = "-15.5",
            [$"{RenderQualityPolicy.SectionName}:MaxLufs"] = "-14.5",
            [$"{RenderQualityPolicy.SectionName}:PolicyVersion"] = "2",
        }));

        policy.MaxDurationDrift.ShouldBe(0.1);
        policy.MinLufs.ShouldBe(-15.5);
        policy.MaxLufs.ShouldBe(-14.5);
        policy.PolicyVersion.ShouldBe(2);
    }

    [Theory]
    [InlineData("MinLufs", "-13")]
    [InlineData("MaxLufs", "-17")]
    [InlineData("MinLufs", "0")]
    [InlineData("MaxDurationDrift", "0")]
    [InlineData("MaxDurationDrift", "0.75")]
    [InlineData("MaxTruePeakDbtp", "0.5")]
    [InlineData("MaxLongestSilenceSeconds", "-1")]
    [InlineData("MinSubtitleCoverage", "1.5")]
    [InlineData("MinCues", "0")]
    [InlineData("SyncSuspectHintShare", "2")]
    [InlineData("PolicyVersion", "0")]
    public void Startup_rejects_out_of_range_threshold(string key, string value)
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            [$"{RenderQualityPolicy.SectionName}:{key}"] = value,
        });

        using var provider = new ServiceCollection().AddRenderQuality(configuration).BuildServiceProvider();

        var exception = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<RenderQualityPolicy>>().Value);

        exception.Message.ShouldContain($"{RenderQualityPolicy.SectionName}:{key}");
    }

    private static RenderQualityPolicy Resolve(IConfiguration configuration)
    {
        using var provider = new ServiceCollection().AddRenderQuality(configuration).BuildServiceProvider();

        return provider.GetRequiredService<IOptions<RenderQualityPolicy>>().Value;
    }

    private static IConfiguration Configuration(Dictionary<string, string?>? values = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(values ?? []).Build();
}
