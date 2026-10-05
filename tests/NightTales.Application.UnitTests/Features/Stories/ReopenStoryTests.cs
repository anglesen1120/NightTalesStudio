using System.Text.Json;
using NightTales.Application.Common;
using NightTales.Application.Features.Stories;
using NightTales.Application.Features.Stories.Commands.ReopenStory;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Stories;

/// <summary>Pure reopen contracts; state/locking/transaction cases live in ReopenStoryEndpointTests.</summary>
public sealed class ReopenStoryTests
{
    private static readonly string[] AuditKeys = ["episode_id", "story_version_id"];

    [Fact]
    public void ReopenStory_AuditDataBeforeSuccess_IsNull()
    {
        var command = new ReopenStoryCommand(Guid.NewGuid(), "not-story-text");
        command.ToAuditData().ShouldBeNull();
    }

    [Fact]
    public void ReopenStory_AuditDataContainsOnlyEpisodeAndUnlockedVersionIds()
    {
        var episodeId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var command = new ReopenStoryCommand(episodeId, StoryEtag.For(versionId))
        {
            ReopenedStoryVersionId = versionId,
        };

        var data = command.ToAuditData();

        data.ShouldNotBeNull();
        data.EntityType.ShouldBe("episode");
        data.EntityId.ShouldBe(episodeId);
        data.SubjectUserId.ShouldBeNull();
        data.Summary.Keys.Order(StringComparer.Ordinal).ShouldBe(AuditKeys);
        data.Summary["episode_id"].ShouldBe(episodeId.ToString("D", System.Globalization.CultureInfo.InvariantCulture));
        data.Summary["story_version_id"].ShouldBe(versionId.ToString("D", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("")]
    [InlineData("malformed")]
    public void ReopenStory_ValidatorLeavesIfMatchToOrderedPreconditions(string tag)
    {
        var command = new ReopenStoryCommand(Guid.NewGuid(), tag);
        new ReopenStoryCommandValidator().Validate(command).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void ReopenStory_ValidatorRejectsEmptyEpisodeId()
    {
        var command = new ReopenStoryCommand(Guid.Empty, string.Empty);
        new ReopenStoryCommandValidator().Validate(command).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void ReopenStory_DtoExposesOneEtagAndImplementsHeaderContract()
    {
        var versionId = Guid.NewGuid();
        var dto = new EpisodeStoryStateDto(Guid.NewGuid(), versionId, StoryEtag.For(versionId), "story_review");

        ((IHasETag)dto).ETag.ShouldBe(StoryEtag.For(versionId));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(dto, JsonSerializerOptions.Web));
        document.RootElement.GetProperty("etag").GetString().ShouldBe(StoryEtag.For(versionId));
        document.RootElement.EnumerateObject().Count(property => property.Name.Equals("etag", StringComparison.OrdinalIgnoreCase))
            .ShouldBe(1);
    }
}
