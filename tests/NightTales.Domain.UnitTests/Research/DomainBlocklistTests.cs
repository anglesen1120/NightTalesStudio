using NightTales.Domain.Research;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Research;

/// <summary>The blocklist of 02 D11: registrable-domain and subdomain match, never a lookalike.</summary>
public sealed class DomainBlocklistTests
{
    private static readonly DomainBlocklist Blocklist = new(new HashSet<string>(StringComparer.Ordinal) { "youtube.com", "youtu.be" });

    [Fact]
    public void IsBlocked_Exact_True() => Blocklist.IsBlocked("youtube.com").ShouldBeTrue();

    [Fact]
    public void IsBlocked_SubdomainRegistrable_True()
    {
        Blocklist.IsBlocked("m.youtube.com").ShouldBeTrue();
        Blocklist.IsBlocked("A.B.YouTube.com.").ShouldBeTrue();
    }

    [Fact]
    public void IsBlocked_Lookalike_False() => Blocklist.IsBlocked("notyoutube.com").ShouldBeFalse();

    [Fact]
    public void IsBlocked_SuffixConfusion_False() => Blocklist.IsBlocked("youtube.com.evil.net").ShouldBeFalse();

    [Fact]
    public void IsBlocked_PunycodeConfusable_False() => Blocklist.IsBlocked("xn--youtub-rta.com").ShouldBeFalse();
}
