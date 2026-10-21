using System.Text;
using NightTales.Domain.Stories.Similarity;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Similarity;

public sealed class XxHash64Tests
{
    [Theory]
    [InlineData("", 0xEF46DB3751D8E999UL)]
    [InlineData("a", 0xD24EC4F1A98C6E5BUL)]
    [InlineData("abc", 0x44BC2CF5AD770999UL)]
    [InlineData("Nobody inspects the spammish repetition", 0xFBCEA83C8A378BF1UL)]
    [InlineData("tôi\u001fđã\u001fđi", 0xE3A5A9C3AA0D02B7UL)]
    public void HashToUInt64_SeedZero_MatchesReferenceVector(string text, ulong expected) =>
        XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(text)).ShouldBe(expected);
}
