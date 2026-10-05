using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Features.Research.Sources;
using NightTales.Fetcher.Core.Fetching;
using NightTales.Infrastructure.DependencyInjection;
using NightTales.Fetcher.Core.Research;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Research;

/// <summary>The start-up validators of 02.5.1 T1 Config: a bad blocklist or cap fails the boot, not the request.</summary>
public sealed class FetcherOptionsValidatorTests
{
    private static readonly FetcherOptionsValidator Validator = new(new PublicSuffixListProvider(NullLogger<PublicSuffixListProvider>.Instance));

    private static bool Fails(params string[] domains) =>
        Validator.Validate(null, new FetcherOptions { BlockedDomains = [.. domains] }).Failed;

    [Fact]
    public void ValidList_Succeeds() => Fails("youtube.com", "youtu.be", "t.co", "x.com", "zingmp3.vn").ShouldBeFalse();

    [Theory]
    [InlineData("https://youtube.com")]
    [InlineData("youtube.com/watch")]
    [InlineData("*.youtube.com")]
    [InlineData("YouTube.com")]
    public void BlockedDomainWithSchemePathWildcardOrUppercase_Fails(string entry) => Fails(entry).ShouldBeTrue(entry);

    [Theory]
    [InlineData("localhost")]
    [InlineData("com")]
    [InlineData("co.uk")]
    [InlineData("127.0.0.1")]
    [InlineData("")]
    public void BlockedDomainSingleLabelSuffixOrIp_Fails(string entry) => Fails(entry).ShouldBeTrue(entry);

    [Fact]
    public void EmptyBlocklist_Fails() => Fails().ShouldBeTrue();

    [Theory]
    [InlineData(0, 30, 3000)]
    [InlineData(10, 0, 3000)]
    [InlineData(10, 30, 0)]
    [InlineData(10, 30, 10_001)]
    public void ZeroCap_Fails(int perEpisode, int perDay, int noteChars)
    {
        var result = new SourcesOptionsValidator().Validate(
            null,
            new SourcesOptions { MaxPerEpisode = perEpisode, MaxUrlSourcesPerUserPerDay = perDay, MaxNoteChars = noteChars });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Sources:");
    }

    [Fact]
    public void DefaultCaps_Succeed() => new SourcesOptionsValidator().Validate(null, new SourcesOptions()).Succeeded.ShouldBeTrue();
}
