using NightTales.Domain.Research;
using NightTales.TestKit.Research;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Research;

/// <summary>SEC-3 of the 02.5.1-T1 review: names under reserved or special-use suffixes never parse.</summary>
public sealed class SourceUrlReservedSuffixTests
{
    private static readonly FakePublicSuffixList Psl = new();

    [Theory]
    [InlineData("https://a.localhost/")]
    [InlineData("https://printer.local/")]
    [InlineData("https://nas.localdomain/")]
    [InlineData("https://db.internal/")]
    [InlineData("https://router.home.arpa/")]
    [InlineData("https://home.arpa/")]
    [InlineData("https://box.lan/")]
    [InlineData("https://wiki.intranet/")]
    [InlineData("https://hr.corp/")]
    [InlineData("https://x.invalid/")]
    [InlineData("https://site.test/")]
    [InlineData("https://site.example/")]
    [InlineData("https://abcdef.onion/")]
    [InlineData("https://SITE.TEST/")]
    [InlineData("https://site.test./")]
    [InlineData("https://deep.sub.site.internal/a")]
    public void Parse_ReservedSuffix_Invalid(string input)
    {
        var result = SourceUrl.Parse(input, Psl);

        result.IsError.ShouldBeTrue(input);
        result.FirstError.Code.ShouldBe(SourceErrors.SourceUrlInvalidCode, input);
    }

    [Theory]
    [InlineData("https://test.com/", "test.com")]
    [InlineData("https://example.com/", "example.com")]
    [InlineData("https://example.org/a", "example.org")]
    [InlineData("https://local.example.com/", "example.com")]
    [InlineData("https://internal.vnexpress.net/", "vnexpress.net")]
    [InlineData("https://arpa-home.net/", "arpa-home.net")]
    public void Parse_ReservedWordNotTheLastLabel_Accepted(string input, string registrable)
    {
        var result = SourceUrl.Parse(input, Psl);

        result.IsError.ShouldBeFalse(input);
        result.Value.RegistrableDomain.ShouldBe(registrable);
    }
}
