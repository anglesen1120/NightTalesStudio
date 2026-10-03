using NightTales.Domain.Research;
using NightTales.TestKit.Research;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Research;

/// <summary>
/// QA probes for 02.5.1-T1 (written by an agent that did not write the code): SSRF-relevant URL syntax, IP-literal
/// notations, normalisation and blocklist look-alikes, with expectations taken from 02.5.1 plan §3.1 and 02 D11.
/// </summary>
public sealed class SourceUrlQaProbeTests
{
    private static readonly FakePublicSuffixList Psl = new();

    private static readonly string[] D11Domains =
    [
        "youtube.com", "youtu.be", "youtube-nocookie.com", "tiktok.com", "facebook.com", "fb.watch", "fb.com",
        "instagram.com", "threads.net", "x.com", "twitter.com", "t.co", "reddit.com", "vimeo.com", "dailymotion.com",
        "twitch.tv", "bilibili.com", "zalo.me", "zingmp3.vn",
    ];

    private static void ShouldBeInvalid(string? input)
    {
        var result = SourceUrl.Parse(input, Psl);

        result.IsError.ShouldBeTrue($"'{input}' must be rejected, got {(result.IsError ? string.Empty : result.Value.Value)}");
        result.FirstError.Code.ShouldBe(SourceErrors.SourceUrlInvalidCode, input);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt://example.com/%0aalert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("file://example.com/etc/passwd")]
    [InlineData("ftp://example.com/a")]
    [InlineData("gopher://example.com/")]
    [InlineData("mailto:a@example.com")]
    [InlineData("data:text/html,<b>x</b>")]
    [InlineData("//example.com/a")]
    [InlineData("example.com/a")]
    [InlineData("www.example.com")]
    [InlineData("https:/example.com")]
    [InlineData("https://")]
    [InlineData("https:///a")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Parse_NonHttpOrSchemeless_Invalid(string? input) => ShouldBeInvalid(input);

    [Theory]
    [InlineData("http://127.1/")]
    [InlineData("http://0/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://1.1.1.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://0x7f.1/")]
    [InlineData("http://0x7f.0x0.0x0.0x1/")]
    [InlineData("http://017700000001/")]
    [InlineData("http://4294967295/")]
    [InlineData("http://99999999999/")]
    [InlineData("http://2130706433./")]
    [InlineData("http://127.0.0.1./")]
    [InlineData("http://1.2.3/")]
    [InlineData("http://1.2.3.4.5/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[::]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://[::ffff:7f00:1]/")]
    [InlineData("http://[0:0:0:0:0:ffff:7f00:1]/")]
    [InlineData("http://[2001:db8::1]/")]
    [InlineData("http://[fe80::1%25eth0]/")]
    [InlineData("http://[::ffff:127.0.0.1]:80/")]
    [InlineData("https://[::1]:443/a")]
    [InlineData("http://\u2460\u2467\u2467.0.0.1/")]
    public void Parse_IpLiteralAnyNotation_Invalid(string input) => ShouldBeInvalid(input);

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://localhost./")]
    [InlineData("http://LOCALHOST/")]
    [InlineData("http://example/")]
    [InlineData("http://example./")]
    [InlineData("http://com/")]
    [InlineData("http://co.uk/")]
    [InlineData("http://.com/")]
    [InlineData("http://example..com/")]
    [InlineData("http://example.com../")]
    [InlineData("http://.example.com/")]
    [InlineData("http://-.com/")]
    [InlineData("http://\u65E5\u672C/")]
    public void Parse_NoRegistrableDomainOrMalformedHost_Invalid(string input) => ShouldBeInvalid(input);

    [Theory]
    [InlineData("https://example.com@evil.com/")]
    [InlineData("https://example.com:80@evil.com/")]
    [InlineData("https://a:b@example.com/")]
    [InlineData("https://:@example.com/")]
    [InlineData("https://@@example.com/")]
    [InlineData("https://example.com%40evil.com/")]
    [InlineData("https://example.com\u2044@evil.com/")]
    public void Parse_CredentialsOrAuthorityTricks_Invalid(string input) => ShouldBeInvalid(input);

    [Theory]
    [InlineData("https://evil.com\\@example.com/", "evil.com")]
    [InlineData("https://evil.com#@example.com/", "evil.com")]
    [InlineData("https://evil.com?@example.com/", "evil.com")]
    [InlineData("https://evil.com/@example.com", "evil.com")]
    [InlineData("https://exa%6Dple.com/", "example.com")]
    [InlineData("https://example\u3002com/", "example.com")]
    public void Parse_AuthorityConfusion_NeverYieldsADifferentHostThanTheFetcherWillUse(string input, string expectedHost)
    {
        var result = SourceUrl.Parse(input, Psl);

        if (result.IsError)
        {
            result.FirstError.Code.ShouldBe(SourceErrors.SourceUrlInvalidCode);
            return;
        }

        result.Value.Host.ShouldBe(expectedHost);
        new Uri(result.Value.Value).Host.ShouldBe(expectedHost, "the stored Value must re-parse to the same host");
        new Uri(result.Value.Value).UserInfo.ShouldBeEmpty("no credentials survive in the stored authority");
    }

    [Theory]
    [InlineData("https://example.com:0/")]
    [InlineData("https://example.com:65536/")]
    [InlineData("https://example.com:99999/")]
    [InlineData("https://example.com:-1/")]
    [InlineData("https://example.com:abc/")]
    [InlineData("https://example.com:8080/")]
    [InlineData("http://example.com:8443/")]
    [InlineData("https://example.com:4430/")]
    [InlineData("https://example.com:1443/")]
    public void Parse_PortOutsideAllowList_Invalid(string input) => ShouldBeInvalid(input);

    [Theory]
    [InlineData("http://example.com:80/a", "http://example.com/a")]
    [InlineData("https://example.com:443/a", "https://example.com/a")]
    [InlineData("http://example.com:443/a", "http://example.com:443/a")]
    [InlineData("https://example.com:80/a", "https://example.com:80/a")]
    public void Parse_AllowedExplicitPorts_DefaultDroppedOtherKept(string input, string expected) =>
        SourceUrl.Parse(input, Psl).Value.Value.ShouldBe(expected);

    [Theory]
    [InlineData("HTTPS://EXAMPLE.COM:443/A?X=1#frag", "https://example.com/A?X=1")]
    [InlineData("http://example.com", "http://example.com/")]
    [InlineData("https://example.com./a", "https://example.com/a")]
    [InlineData("https://Example.Com/a#", "https://example.com/a")]
    [InlineData("https://example.com/a#one#two", "https://example.com/a")]
    [InlineData("  https://example.com/a  ", "https://example.com/a")]
    public void Parse_Normalisation_MatchesSpec(string input, string expected) =>
        SourceUrl.Parse(input, Psl).Value.Value.ShouldBe(expected);

    [Fact]
    public void Parse_FragmentCaseAndDefaultPortVariants_NormaliseToTheSameValue()
    {
        var baseline = SourceUrl.Parse("https://example.com/a", Psl).Value.Value;

        foreach (var variant in new[]
        {
            "https://example.com/a#frag",
            "HTTPS://EXAMPLE.COM/a",
            "https://example.com:443/a",
            "https://example.com./a",
            "https://EXAMPLE.com:443/a#x",
        })
        {
            SourceUrl.Parse(variant, Psl).Value.Value.ShouldBe(baseline, variant);
        }

        SourceUrl.Parse("https://example.com/a?x=1", Psl).Value.Value.ShouldNotBe(baseline, "the query is kept (task Design)");
        SourceUrl.Parse("https://example.com/A", Psl).Value.Value.ShouldNotBe(baseline, "the path is case-sensitive");
    }

    [Theory]
    [InlineData("https://exa mple.com/")]
    [InlineData("https://example.com/a\nb")]
    [InlineData("https://example.com/a\rb")]
    [InlineData("https://example.com/a\tb")]
    [InlineData("https://example.com/\u0000")]
    [InlineData("https://exam\u0000ple.com/")]
    [InlineData("https://example.com\u0085/")]
    [InlineData("https://ex\u200Bample.com/")]
    [InlineData("https://example.com/a b\nc")]
    public void Parse_WhitespaceOrControlInsideTheUrl_NeverAccepted(string input)
    {
        var result = SourceUrl.Parse(input, Psl);

        if (!result.IsError)
        {
            // A framework that percent-encodes an inner space in the *path* is acceptable; anything in the host is not.
            result.Value.Host.ShouldBe("example.com");
            result.Value.Value.ShouldNotContain("\n");
            result.Value.Value.ShouldNotContain("\r");
            result.Value.Value.ShouldNotContain("\t");
            result.Value.Value.ShouldNotContain("\0");
            result.Value.Value.ShouldNotContain(" ");
        }
    }

    [Fact]
    public void Parse_Length2048_ValidAnd2049_Invalid()
    {
        const string prefix = "https://example.com/";
        var exact = prefix + new string('a', 2048 - prefix.Length);
        var over = exact + "a";

        exact.Length.ShouldBe(2048);
        SourceUrl.Parse(exact, Psl).IsError.ShouldBeFalse("exactly 2,048 characters is allowed");
        ShouldBeInvalid(over);
    }

    [Fact]
    public void Parse_HostLimits_Label64And254Characters_Invalid()
    {
        var label63 = new string('a', 63);
        SourceUrl.Parse($"https://{label63}.com/", Psl).IsError.ShouldBeFalse("a 63-character label is legal");
        ShouldBeInvalid($"https://{label63}a.com/");

        var longHost = string.Join('.', Enumerable.Repeat(new string('b', 60), 5)) + ".com";
        ShouldBeInvalid($"https://{longHost}/");
    }

    [Theory]
    [InlineData("https://m\u00FCnchen.de/", "xn--mnchen-3ya.de", "xn--mnchen-3ya.de")]
    [InlineData("https://WWW.M\u00DCNCHEN.DE/a", "www.xn--mnchen-3ya.de", "xn--mnchen-3ya.de")]
    [InlineData("https://xn--mnchen-3ya.de/", "xn--mnchen-3ya.de", "xn--mnchen-3ya.de")]
    public void Parse_IdnHost_BecomesPunycodeWithPunycodeRegistrableDomain(string input, string host, string registrable)
    {
        var url = SourceUrl.Parse(input, Psl).Value;

        url.Host.ShouldBe(host);
        url.RegistrableDomain.ShouldBe(registrable);
    }

    [Theory]
    [InlineData("https://xn--/")]
    [InlineData("https://xn--a.com/")]
    [InlineData("https://xn--zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz.com/")]
    public void Parse_MalformedPunycode_InvalidOrNormalised(string input)
    {
        var result = SourceUrl.Parse(input, Psl);

        if (!result.IsError)
        {
            result.Value.Host.ShouldBe(result.Value.Host.ToLowerInvariant());
            result.Value.Host.ShouldAllBe(character => character < 128);
        }
    }

    [Fact]
    public void Parse_ResultHost_AlwaysAsciiLowercase_ForMixedInputs()
    {
        foreach (var input in new[]
        {
            "https://B\u00DCCHER.example.com/",
            "https://\uFF45\uFF58\uFF41\uFF4D\uFF50\uFF4C\uFF45.com/",
            "https://EXAMPLE.COM/",
        })
        {
            var result = SourceUrl.Parse(input, Psl);
            if (result.IsError)
            {
                continue;
            }

            result.Value.Host.ShouldAllBe(character => character < 128, input);
            result.Value.Host.ShouldBe(result.Value.Host.ToLowerInvariant(), input);
            result.Value.Value.ShouldStartWith("http", Case.Sensitive);
        }
    }

    [Theory]
    [InlineData("https://youtube.com/watch?v=x", true)]
    [InlineData("https://www.youtube.com/watch?v=x", true)]
    [InlineData("https://m.youtube.com/", true)]
    [InlineData("https://YOUTUBE.COM./", true)]
    [InlineData("https://youtu.be/abc", true)]
    [InlineData("https://a.b.c.d.youtube.com/", true)]
    [InlineData("https://old.reddit.com/r/x", true)]
    [InlineData("https://vm.tiktok.com/x", true)]
    [InlineData("https://fb.watch/x", true)]
    [InlineData("https://l.facebook.com/l.php?u=x", true)]
    [InlineData("https://mobile.twitter.com/x", true)]
    [InlineData("https://t.co/abc", true)]
    [InlineData("https://zingmp3.vn/", true)]
    [InlineData("https://www.zalo.me/", true)]
    [InlineData("https://youtube.com.evil.com/", false)]
    [InlineData("https://youtube.com.evil.net/", false)]
    [InlineData("https://notyoutube.com/", false)]
    [InlineData("https://evilyoutube.com/", false)]
    [InlineData("https://youtube.co/", false)]
    [InlineData("https://youtube.xyz/", false)]
    [InlineData("https://youtube-com.com/", false)]
    [InlineData("https://box.com/", false)]
    [InlineData("https://max.com/", false)]
    [InlineData("https://dropbox.com/", false)]
    [InlineData("https://fbi.com/", false)]
    [InlineData("https://tt.co/", false)]
    [InlineData("https://example.com/?u=https://youtube.com/watch", false)]
    [InlineData("https://example.com/youtube.com", false)]
    [InlineData("https://x.com.example.org/", false)]
    [InlineData("https://xn--80ak6aa92e.com/", false)]
    [InlineData("https://y\u043Eutube.com/", false)]
    [InlineData("https://youtube\u3002com.evil.com/", false)]
    public void Pipeline_ParseThenBlocklist_MatchesRegistrableDomainOnly(string input, bool blocked)
    {
        var blocklist = new DomainBlocklist(new HashSet<string>(D11Domains, StringComparer.Ordinal));

        var parsed = SourceUrl.Parse(input, Psl);
        parsed.IsError.ShouldBeFalse(input);

        blocklist.IsBlocked(parsed.Value.RegistrableDomain).ShouldBe(blocked, $"{input} -> {parsed.Value.RegistrableDomain}");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("com")]
    [InlineData("youtube")]
    public void DomainBlocklist_EmptyOrBareInputs_NeverBlocked(string host)
    {
        var blocklist = new DomainBlocklist(new HashSet<string>(D11Domains, StringComparer.Ordinal));

        blocklist.IsBlocked(host).ShouldBeFalse();
    }

    [Fact]
    public void DomainBlocklist_EmptySet_BlocksNothing() =>
        new DomainBlocklist(new HashSet<string>()).IsBlocked("youtube.com").ShouldBeFalse();
}
