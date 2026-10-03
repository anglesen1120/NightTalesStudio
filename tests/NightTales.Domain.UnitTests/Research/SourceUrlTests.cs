using NightTales.Domain.Research;
using NightTales.TestKit.Research;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Research;

/// <summary>The URL rules of 02.5.1 plan §3.1 and 02 D11: the single place a pasted URL is judged.</summary>
public sealed class SourceUrlTests
{
    private static readonly FakePublicSuffixList Psl = new();

    private static void ShouldBeInvalid(string input)
    {
        var result = SourceUrl.Parse(input, Psl);

        result.IsError.ShouldBeTrue(input);
        result.FirstError.Code.ShouldBe(SourceErrors.SourceUrlInvalidCode, input);
    }

    [Fact]
    public void Parse_Https_ReturnsNormalisedValue()
    {
        var url = SourceUrl.Parse("https://www.Example.com/a/b?x=1", Psl).Value;

        url.Value.ShouldBe("https://www.example.com/a/b?x=1");
        url.Host.ShouldBe("www.example.com");
        url.RegistrableDomain.ShouldBe("example.com");
        url.IsHttps.ShouldBeTrue();
    }

    [Theory]
    [InlineData("https://example.com:8443/a")]
    [InlineData("http://example.com:8080/")]
    [InlineData("https://example.com:22/")]
    public void Parse_ExplicitPortNot80Or443_Invalid(string input) => ShouldBeInvalid(input);

    [Theory]
    [InlineData("https://user:pw@example.com/")]
    [InlineData("https://user@example.com/")]
    [InlineData("https://@example.com/")]
    public void Parse_UserInfo_Invalid(string input) => ShouldBeInvalid(input);

    [Fact]
    public void Parse_DottedQuadIp_Invalid() => ShouldBeInvalid("http://127.0.0.1/");

    [Fact]
    public void Parse_DecimalIpLiteral_Invalid() => ShouldBeInvalid("http://2130706433/");

    [Fact]
    public void Parse_OctalIpLiteral_Invalid() => ShouldBeInvalid("http://0177.0.0.1/");

    [Fact]
    public void Parse_HexIpLiteral_Invalid()
    {
        ShouldBeInvalid("http://0x7f.0.0.1/");
        ShouldBeInvalid("http://0x7f000001/");
    }

    [Fact]
    public void Parse_BracketedIpv6_Invalid() => ShouldBeInvalid("http://[::1]/");

    [Fact]
    public void Parse_Ipv4MappedIpv6_Invalid() => ShouldBeInvalid("http://[::ffff:127.0.0.1]/");

    [Fact]
    public void Parse_TrailingDot_StrippedThenAccepted()
    {
        var url = SourceUrl.Parse("https://example.com./a", Psl).Value;

        url.Host.ShouldBe("example.com");
        url.Value.ShouldBe("https://example.com/a");
    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://intranet/")]
    [InlineData("http://intranet./")]
    public void Parse_SingleLabelHost_Invalid(string input) => ShouldBeInvalid(input);

    [Theory]
    [InlineData("https://com/")]
    [InlineData("https://co.uk/")]
    public void Parse_BareTld_Invalid(string input) => ShouldBeInvalid(input);

    [Fact]
    public void Parse_IdnHost_BecomesPunycode()
    {
        var url = SourceUrl.Parse("https://bücher.example.com/x", Psl).Value;

        url.Host.ShouldBe("xn--bcher-kva.example.com");
        url.RegistrableDomain.ShouldBe("example.com");
    }

    [Fact]
    public void Parse_UnicodeSingleLabel_Invalid() => ShouldBeInvalid("https://bücher/");

    [Fact]
    public void Parse_FragmentDropped_QueryKept()
    {
        SourceUrl.Parse("https://example.com/a?b=1#frag", Psl).Value.Value.ShouldBe("https://example.com/a?b=1");
    }

    [Theory]
    [InlineData("https://example.com:443/a", "https://example.com/a")]
    [InlineData("http://example.com:80/a", "http://example.com/a")]
    [InlineData("https://example.com", "https://example.com/")]
    public void Parse_DefaultPortDropped_AndEmptyPathBecomesSlash(string input, string expected)
        => SourceUrl.Parse(input, Psl).Value.Value.ShouldBe(expected);

    [Fact]
    public void Parse_Over2048_Invalid() => ShouldBeInvalid("https://example.com/" + new string('a', 2_048));

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/a")]
    [InlineData("javascript:alert(1)")]
    [InlineData("example.com/a")]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NotHttp_Invalid(string input) => ShouldBeInvalid(input);

    [Fact]
    public void Parse_UpperCaseHost_Lowercased()
        => SourceUrl.Parse("HTTPS://EXAMPLE.COM/Path", Psl).Value.Value.ShouldBe("https://example.com/Path");

    [Fact]
    public void Parse_NumericTld_Invalid() => ShouldBeInvalid("http://example.123/");
}
