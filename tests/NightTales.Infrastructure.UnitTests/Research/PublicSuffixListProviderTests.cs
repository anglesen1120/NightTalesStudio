using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Fetcher.Core.Research;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Research;

/// <summary>The embedded list and the longest-match algorithm of <see cref="PublicSuffixListProvider"/>.</summary>
public sealed class PublicSuffixListProviderTests
{
    private static readonly PublicSuffixListProvider Real = new(NullLogger<PublicSuffixListProvider>.Instance);

    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("www.vnexpress.net", "vnexpress.net")]
    [InlineData("a.b.example.co.uk", "example.co.uk")]
    [InlineData("tuoitre.com.vn", "tuoitre.com.vn")]
    [InlineData("m.youtube.com", "youtube.com")]
    [InlineData("youtu.be", "youtu.be")]
    [InlineData("t.co", "t.co")]
    public void RegistrableDomain_OfRealHosts(string host, string expected)
        => Real.GetRegistrableDomain(host).ShouldBe(expected);

    [Theory]
    [InlineData("com")]
    [InlineData("co.uk")]
    [InlineData("com.vn")]
    public void PublicSuffixItself_HasNoRegistrableDomain(string host) => Real.GetRegistrableDomain(host).ShouldBeNull();

    [Fact]
    public void UnlistedTld_FallsBackToLastTwoLabels_AndNeverThrows()
        => Real.GetRegistrableDomain("a.b.notarealtld").ShouldBe("b.notarealtld");

    [Fact]
    public void IdnTld_IsMatchedInPunycode()
        => Real.GetRegistrableDomain("xn--80a1acny.xn--p1ai").ShouldBe("xn--80a1acny.xn--p1ai");

    [Fact]
    public void WildcardAndExceptionRules_FollowTheListAlgorithm()
    {
        var list = new PublicSuffixListProvider(
            "// ===BEGIN ICANN DOMAINS===\ncom\nck\n*.ck\n!www.ck\n// ===END ICANN DOMAINS===\n// ===BEGIN PRIVATE DOMAINS===\nblogspot.com\n// ===END PRIVATE DOMAINS===\n",
            NullLogger<PublicSuffixListProvider>.Instance);

        list.GetRegistrableDomain("foo.bar.ck").ShouldBe("foo.bar.ck", "*.ck makes bar.ck the suffix");
        list.GetRegistrableDomain("www.ck").ShouldBe("www.ck", "the exception rule overrides the wildcard");
        list.GetRegistrableDomain("x.www.ck").ShouldBe("www.ck");
        list.GetRegistrableDomain("a.blogspot.com").ShouldBe("blogspot.com", "only the ICANN section counts");
    }

    [Fact]
    public void RepeatedLookups_AreServedFromTheCache()
        => Real.GetRegistrableDomain("cached.example.com").ShouldBeSameAs(Real.GetRegistrableDomain("cached.example.com"));
}
