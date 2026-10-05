using System.Text;
using NightTales.Domain.Stories.Similarity;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Similarity;

public sealed class ShinglerTests
{
    private static readonly string[] RepeatedTokens = ["a", "a", "a", "a"];
    private static readonly string[] VietnameseTokens = ["tôi", "đã", "đi", "về", "nhà"];

    [Fact]
    public void Hash_FewerTokensThanN_ReturnsEmpty() => Shingler.Hash("one two", 3).ShouldBeEmpty();

    [Fact]
    public void Hash_WindowBoundary_IsUnambiguous()
    {
        var first = Shingler.Hash("ab c", 2).ShouldHaveSingleItem();
        var second = Shingler.Hash("a bc", 2).ShouldHaveSingleItem();
        first.ShouldNotBe(second);
        first.ShouldBe(XxHash64.HashToUInt64(Encoding.UTF8.GetBytes("ab\u001fc")));
    }

    [Fact]
    public void Hash_SameTextTwice_IsDeterministic() =>
        Shingler.Hash("tôi đã đi về nhà", 3).ShouldBe(Shingler.Hash("tôi đã đi về nhà", 3));

    [Fact]
    public void Shingles_DuplicateWindow_IsDeduplicated()
    {
        Shingler.Hash("a a a a", 2).Length.ShouldBe(3);
        Shingler.Shingles(RepeatedTokens, 2).Count.ShouldBe(1);
        ShingleSet.From(Shingler.Hash("a a a a", 2)).Count.ShouldBe(1);
    }

    [Fact]
    public void Shingles_AndHash_UseIdenticalUtf8Windows() =>
        Shingler.Shingles(VietnameseTokens, 3).SetEquals(Shingler.Hash("tôi đã đi về nhà", 3)).ShouldBeTrue();

    [Fact]
    public void Hash_UnicodeWhitespace_IsTokenBoundary() =>
        Shingler.Hash("one\t two\nthree\u00a0four", 2).ShouldBe(Shingler.Hash("one two three four", 2));

    [Fact]
    public void Hash_InvalidWindowSize_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => Shingler.Hash("one two", 0));
}
