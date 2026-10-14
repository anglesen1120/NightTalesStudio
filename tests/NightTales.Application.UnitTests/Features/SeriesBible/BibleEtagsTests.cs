using NightTales.Application.Common.Errors;
using NightTales.Application.Features.SeriesBible;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.SeriesBible;

/// <summary>
/// The bible's ETag and the precondition of its save (02.6.3-T1, D8/D9): the value
/// <c>"sb-&lt;version&gt;"</c>, the shared rule that only that exact quoted tag matches, and the 428/412
/// split <c>PUT /api/v1/series/{id}/bible</c> answers before it looks at the body.
/// </summary>
public sealed class BibleEtagsTests
{
    [Fact]
    public void For_QuotesTheVersionWithTheSbPrefix() =>
        BibleEtags.For(2).ShouldBe("\"sb-2\"");

    [Fact]
    public void Matches_NamesTheCurrentVersion() =>
        BibleEtags.Matches("\"sb-2\"", 2).ShouldBeTrue();

    [Theory]
    [InlineData("\"sb-1\"")] // the previous version: stale
    [InlineData("\"sb-3\"")] // a version this row never had
    [InlineData("W/\"sb-2\"")] // weak tags are not a strong match (D9)
    [InlineData("*")] // the shared rule refuses `*`: the client must have read the tag it overwrites
    [InlineData("\"sb-2\", \"sb-3\"")] // a list is not supported on this route
    [InlineData("sb-2")] // unquoted, so not the value we issued
    [InlineData("\"sb-02\"")] // not the value we issued either
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Matches_AnythingElseIsNotAMatch(string? ifMatch) =>
        BibleEtags.Matches(ifMatch, 2).ShouldBeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RequireAndMatch_MissingHeader_IsIfMatchRequired(string? ifMatch)
    {
        var result = BibleEtags.RequireAndMatch(ifMatch, 2);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.IfMatchRequired);
    }

    [Theory]
    [InlineData("\"sb-1\"")]
    [InlineData("\"sb-3\"")]
    [InlineData("W/\"sb-2\"")]
    [InlineData("*")]
    public void RequireAndMatch_StaleWeakOrStarredTag_IsBibleConflict(string ifMatch)
    {
        var result = BibleEtags.RequireAndMatch(ifMatch, 2);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.BibleConflict);
    }

    [Fact]
    public void RequireAndMatch_CurrentVersion_ReturnsThatVersion() =>
        BibleEtags.RequireAndMatch("\"sb-2\"", 2).Value.ShouldBe(2);
}
