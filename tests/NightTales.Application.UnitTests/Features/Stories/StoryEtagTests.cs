using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Stories;

public sealed class StoryEtagTests
{
    private static readonly Guid VersionId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    [Fact]
    public void For_QuotesTheVersionIdWithTheSvPrefix() =>
        StoryEtag.For(VersionId).ShouldBe("\"sv-0f8fad5b-d9cb-469f-a165-70867728950e\"");

    [Fact]
    public void TryParse_RoundTripsAnEtagItIssued()
    {
        StoryEtag.TryParse(StoryEtag.For(VersionId), out var parsed).ShouldBeTrue();
        parsed.ShouldBe(VersionId);
    }

    [Theory]
    [InlineData("W/\"sv-0f8fad5b-d9cb-469f-a165-70867728950e\"")]
    [InlineData("0f8fad5b-d9cb-469f-a165-70867728950e")]
    [InlineData("\"ep-12345\"")]
    [InlineData("\"sv-not-a-guid\"")]
    [InlineData("\"sv-00000000-0000-0000-0000-000000000000\"")]
    [InlineData("")]
    public void TryParse_RejectsMalformedHeaders(string header) =>
        StoryEtag.TryParse(header, out _).ShouldBeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RequireAndMatch_MissingHeader_IsIfMatchRequired(string? header)
    {
        var result = StoryEtag.RequireAndMatch(header, VersionId);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.IfMatchRequired);
    }

    [Fact]
    public void RequireAndMatch_StaleOrMalformedOrNoCurrent_IsVersionConflict()
    {
        StoryEtag.RequireAndMatch(StoryEtag.For(Guid.NewGuid()), VersionId).FirstError.Code
            .ShouldBe(ErrorCodes.StoryVersionConflict);
        StoryEtag.RequireAndMatch("W/" + StoryEtag.For(VersionId), VersionId).FirstError.Code
            .ShouldBe(ErrorCodes.StoryVersionConflict);
        StoryEtag.RequireAndMatch(StoryEtag.For(VersionId), null).FirstError.Code
            .ShouldBe(ErrorCodes.StoryVersionConflict);
    }

    [Fact]
    public void RequireAndMatch_CurrentEtag_ReturnsTheId() =>
        StoryEtag.RequireAndMatch(StoryEtag.For(VersionId), VersionId).Value.ShouldBe(VersionId);
}
