using System.Text.Json;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Stories.Generation;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Generation;

/// <summary>The value object over the D8 job codes and the two platform pause reasons.</summary>
public sealed class GenerationErrorCodeTests
{
    [Fact]
    public void WellKnownCodes_MatchTheErrorCatalogue()
    {
        // The Domain cannot reference ErrorCodes (D1), so it spells the codes itself; this is the join.
        GenerationErrorCode.StoryInvalidOutput.Value.ShouldBe(ErrorCodes.StoryInvalidOutput);
        GenerationErrorCode.StoryModerated.Value.ShouldBe(ErrorCodes.StoryModerated);
        GenerationErrorCode.StoryGenerationFailed.Value.ShouldBe(ErrorCodes.StoryGenerationFailed);
        GenerationErrorCode.StoryGenerationCancelled.Value.ShouldBe(ErrorCodes.StoryGenerationCancelled);
    }

    [Theory]
    [InlineData(GenerationPauseReason.SpendCapReached, "spend_cap_reached")]
    [InlineData(GenerationPauseReason.ProviderFundsExhausted, "provider_funds_exhausted")]
    public void FromPause_MapsToThePlatformCode(GenerationPauseReason reason, string expected)
    {
        GenerationErrorCode.FromPause(reason).Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("fact_sheet_invalid_output")]
    [InlineData("find_sources_generation_failed")]
    public void Create_AcceptsAnyD8Token(string value)
    {
        var created = GenerationErrorCode.Create(value);

        created.IsError.ShouldBeFalse();
        created.Value.Value.ShouldBe(value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Story_Moderated")]
    [InlineData("story moderated")]
    [InlineData("1story")]
    [InlineData("story-moderated")]
    public void Create_RejectsAnythingThatIsNotASnakeCaseToken(string? value)
    {
        GenerationErrorCode.Create(value).IsError.ShouldBeTrue();
    }

    [Fact]
    public void Create_RejectsATokenLongerThan64Characters()
    {
        GenerationErrorCode.Create(new string('a', 65)).IsError.ShouldBeTrue();
        GenerationErrorCode.Create(new string('a', 64)).IsError.ShouldBeFalse();
    }

    [Fact]
    public void Json_RoundTripsAsAPlainString()
    {
        var json = JsonSerializer.Serialize(GenerationErrorCode.StoryModerated);

        json.ShouldBe("\"story_moderated\"");
        JsonSerializer.Deserialize<GenerationErrorCode>(json).ShouldBe(GenerationErrorCode.StoryModerated);
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<GenerationErrorCode>("\"Bad Code\""));
    }
}
