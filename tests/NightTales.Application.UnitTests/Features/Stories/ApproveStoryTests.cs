using System.Text.Json;
using NightTales.Application.Common;
using NightTales.Application.Features.Stories.Commands.ApproveStory;
using NightTales.Domain.Stories;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Stories;

/// <summary>Pure command shape, no-op audit projection and response contract checks.</summary>
public sealed class ApproveStoryTests
{
    [Fact]
    public void ApproveStory_NoOutcome_SuppressesAuditForIdempotentReplay()
    {
        var command = new ApproveStoryCommand(Guid.NewGuid(), "", null);
        command.ToAuditData().ShouldBeNull();
    }

    [Theory]
    [InlineData(ApprovalSource.User, "user")]
    [InlineData(ApprovalSource.Autopilot, "autopilot")]
    public void ApproveStory_AuditDataContainsNoStoryText(ApprovalSource source, string wire)
    {
        var episodeId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var command = new ApproveStoryCommand(episodeId, "untrusted header", new(true, "untrusted attestation"), source)
        {
            ApprovedVersionId = versionId,
        };
        var data = command.ToAuditData();
        data.ShouldNotBeNull();
        data.EntityId.ShouldBe(episodeId);
        data.Summary.Keys.Order().ShouldBe(["approval_source", "episode_id", "story_version_id"]);
        data.Summary["episode_id"].ShouldBe(episodeId);
        data.Summary["story_version_id"].ShouldBe(versionId);
        data.Summary["approval_source"].ShouldBe(wire);
        JsonSerializer.Serialize(data.Summary).ShouldNotContain("untrusted");
    }

    [Fact]
    public void ApproveStory_ValidatorLeavesEtagAndAttestationToOrderedHandlerChecks()
    {
        var validator = new ApproveStoryCommandValidator();
        validator.Validate(new ApproveStoryCommand(Guid.NewGuid(), "", null)).IsValid.ShouldBeTrue();
        validator.Validate(new ApproveStoryCommand(Guid.NewGuid(), "malformed", new(false, ""))).IsValid.ShouldBeTrue();
        validator.Validate(new ApproveStoryCommand(Guid.Empty, "", null)).IsValid.ShouldBeFalse();
        validator.Validate(new ApproveStoryCommand(Guid.NewGuid(), "", null, (ApprovalSource)100)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void ApprovedStoryDto_UsesOneEtagBodyMemberAndHeaderInterface()
    {
        var dto = new ApprovedStoryDto(Guid.NewGuid(), Guid.NewGuid(), "\"sv-example\"", "story_approved", DateTimeOffset.UtcNow);
        ((IHasETag)dto).ETag.ShouldBe(dto.Etag);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(dto, JsonSerializerOptions.Web));
        json.RootElement.GetProperty("etag").GetString().ShouldBe(dto.Etag);
        json.RootElement.EnumerateObject().Count(property => property.Name.Equals("etag", StringComparison.OrdinalIgnoreCase)).ShouldBe(1);
    }
}
