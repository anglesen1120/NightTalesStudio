using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Stories;
using NightTales.Application.Features.Stories;
using NightTales.Application.Features.Stories.Approval;
using NightTales.Application.Features.Stories.Commands.ApproveStory;
using NightTales.Application.Features.Stories.Commands.ReopenStory;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Moderation;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.StoryGeneration;

/// <summary>
/// Independent QA regressions for 02.3.2-T1 (negative, boundary and exploratory cases) against the real host and
/// PostgreSQL, including reviewed defect fixes and the explicitly unreachable legacy approval case.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ApproveReopenQaTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Clean = """{"flagged":false,"categories":[],"blocking":[],"stale":false,"unavailable":false}""";
    private const string StaleClean = """{"flagged":false,"categories":[],"blocking":[],"stale":true,"unavailable":false}""";
    private const string StoryTitle = "The house by the river";
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);
    private static readonly string[] InvalidApprovalBodies = ["{", "[]", "{\"attestation\":true}"];
    private static CancellationToken Ct => TestContextToken.Value;

    [Theory]
    [InlineData("approve")]
    [InlineData("reopen")]
    public async Task ForeignEpisode_ApproveOrReopen_Returns404AndLeavesOwnersApprovalUntouched(string action)
    {
        var owner = await SeedAsync();
        var stranger = await SeedAsync();
        using var factory = Factory();
        using var ownerClient = factory.ClientFor(owner.UserId);
        using var strangerClient = factory.ClientFor(stranger.UserId);
        using var approved = await SendAsync(ownerClient, owner.EpisodeId, "approve", Tag(owner));
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(Ct));
        var before = Snapshot(owner);

        foreach (var tag in new[] { null, Tag(owner), "garbage" })
        {
            using var response = await SendAsync(strangerClient, owner.EpisodeId, action, tag);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync(Ct));
            (await CodeAsync(response)).ShouldBe("not_found");
        }

        Snapshot(owner).ShouldBe(before);
        Count(owner, "audit_logs", "action = 'episode.story_approved'").ShouldBe(1);
        Count(owner, "audit_logs", "action = 'episode.story_reopened'").ShouldBe(0);
        Count(owner, "outbox_messages", "type = 'story_reopened.v1'").ShouldBe(0);
        Snapshot(stranger).ShouldStartWith("story_review|");
    }

    [Theory]
    [InlineData("weak")]
    [InlineData("list")]
    [InlineData("listReversed")]
    [InlineData("epTag")]
    [InlineData("star")]
    [InlineData("unquoted")]
    [InlineData("upperPrefix")]
    [InlineData("bareGuid")]
    [InlineData("singleQuoted")]
    [InlineData("trailingJunk")]
    [InlineData("emptyGuid")]
    public async Task Approve_IfMatchVariants_OnlyTheExactStrongStoryTagApproves(string kind)
    {
        var world = await SeedAsync(audit: Audit(StaleClean));
        var moderator = new FakeContentModerator();
        using var factory = Factory(moderator);
        using var client = factory.ClientFor(world.UserId);

        using var response = await SendAsync(client, world.EpisodeId, "approve", Variant(kind, world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed, await response.Content.ReadAsStringAsync(Ct));
        (await CodeAsync(response)).ShouldBe("story_version_conflict");
        moderator.Calls.ShouldBe(0, "ETag validation precedes the moderation refresh");
        Snapshot(world).ShouldBe("story_review|null|null");
        Count(world, "audit_logs", "action = 'episode.story_approved'").ShouldBe(0);
        Count(world, "outbox_messages", "type = 'story_approved.v1'").ShouldBe(0);
    }

    [Fact]
    public async Task Approve_UppercaseGuidInStrongTag_MatchesTheSameVersion()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await SendAsync(client, world.EpisodeId, "approve", $"\"sv-{world.VersionId.ToString("D").ToUpperInvariant()}\"");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        Snapshot(world).ShouldStartWith($"story_approved|{world.VersionId}|");
    }

    [Theory]
    [InlineData("weak")]
    [InlineData("list")]
    [InlineData("epTag")]
    [InlineData("star")]
    [InlineData("unquoted")]
    public async Task Approve_AlreadyApproved_NonExactIfMatch_IsInvalidStateNotIdempotent(string kind)
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var first = await SendAsync(client, world.EpisodeId, "approve", Tag(world));
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        foreach (var tag in new[] { Variant(kind, world.VersionId), null })
        {
            using var response = await SendAsync(client, world.EpisodeId, "approve", tag);
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
            (await CodeAsync(response)).ShouldBe("episode_invalid_state");
        }

        Count(world, "audit_logs", "action = 'episode.story_approved'").ShouldBe(1);
        Count(world, "outbox_messages", "type = 'story_approved.v1'").ShouldBe(1);
    }

    [Fact]
    public async Task Approve_AlreadyApproved_UppercaseGuidOfApprovedTag_IsIdempotentWithoutSecondEvent()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var first = await SendAsync(client, world.EpisodeId, "approve", Tag(world));
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var repeat = await SendAsync(client, world.EpisodeId, "approve", $"\"sv-{world.VersionId.ToString("D").ToUpperInvariant()}\"");

        repeat.StatusCode.ShouldBe(HttpStatusCode.OK, await repeat.Content.ReadAsStringAsync(Ct));
        Count(world, "audit_logs", "action = 'episode.story_approved'").ShouldBe(1);
        Count(world, "outbox_messages", "type = 'story_approved.v1'").ShouldBe(1);
    }

    [Theory]
    [InlineData("weak")]
    [InlineData("list")]
    [InlineData("epTag")]
    [InlineData("star")]
    [InlineData("unquoted")]
    [InlineData("upperPrefix")]
    public async Task Reopen_IfMatchVariants_AreRejectedAndKeepTheApproval(string kind)
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var first = await SendAsync(client, world.EpisodeId, "approve", Tag(world));
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var before = Snapshot(world);

        using var response = await SendAsync(client, world.EpisodeId, "reopen", Variant(kind, world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed, await response.Content.ReadAsStringAsync(Ct));
        (await CodeAsync(response)).ShouldBe("story_version_conflict");
        Snapshot(world).ShouldBe(before);
        Count(world, "audit_logs", "action = 'episode.story_reopened'").ShouldBe(0);
        Count(world, "outbox_messages", "type = 'story_reopened.v1'").ShouldBe(0);
    }

    [Fact]
    public async Task WhitespacePaddedOrBlankIfMatch_ThroughTheMediator_Is412Or428ForBothCommands()
    {
        var world = await SeedAsync();
        using var factory = Factory(userId: world.UserId);
        await using var scope = factory.Services.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var padded = await mediator.Send(new ApproveStoryCommand(world.EpisodeId, $" {Tag(world)} ", null), Ct);
        padded.IsError.ShouldBeTrue();
        padded.FirstError.Code.ShouldBe("story_version_conflict");
        foreach (var blank in new[] { string.Empty, "   ", "\t" })
        {
            var result = await mediator.Send(new ApproveStoryCommand(world.EpisodeId, blank, null), Ct);
            result.IsError.ShouldBeTrue();
            result.FirstError.Code.ShouldBe("if_match_required");
        }

        Snapshot(world).ShouldBe("story_review|null|null");
        var approved = await mediator.Send(new ApproveStoryCommand(world.EpisodeId, Tag(world), null), Ct);
        approved.IsError.ShouldBeFalse();

        var reopenPadded = await mediator.Send(new ReopenStoryCommand(world.EpisodeId, $" {Tag(world)} "), Ct);
        reopenPadded.IsError.ShouldBeTrue();
        reopenPadded.FirstError.Code.ShouldBe("story_version_conflict");
        var reopenBlank = await mediator.Send(new ReopenStoryCommand(world.EpisodeId, "  "), Ct);
        reopenBlank.IsError.ShouldBeTrue();
        reopenBlank.FirstError.Code.ShouldBe("if_match_required");
        Snapshot(world).ShouldStartWith($"story_approved|{world.VersionId}|");
    }

    [Theory]
    [InlineData(EpisodeStatus.Draft)]
    [InlineData(EpisodeStatus.StoryGenerating)]
    [InlineData(EpisodeStatus.StoryApproved)]
    [InlineData(EpisodeStatus.Rendering)]
    [InlineData(EpisodeStatus.VideoReview)]
    [InlineData(EpisodeStatus.Ready)]
    [InlineData(EpisodeStatus.Scheduled)]
    [InlineData(EpisodeStatus.Published)]
    [InlineData(EpisodeStatus.Failed)]
    [InlineData(EpisodeStatus.Cancelled)]
    public async Task Approve_EveryNonReviewStatus_Returns409EvenWithTheCurrentTag_AndChangesNothing(EpisodeStatus status)
    {
        var world = await SeedAsync(status);
        var moderator = new FakeContentModerator();
        using var factory = Factory(moderator);
        using var client = factory.ClientFor(world.UserId);
        var before = Snapshot(world);

        foreach (var tag in new[] { Tag(world), null })
        {
            using var response = await SendAsync(client, world.EpisodeId, "approve", tag);
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
            (await CodeAsync(response)).ShouldBe("episode_invalid_state");
        }

        moderator.Calls.ShouldBe(0);
        Snapshot(world).ShouldBe(before);
        Count(world, "audit_logs", "action = 'episode.story_approved'").ShouldBe(0);
        Count(world, "outbox_messages", "type = 'story_approved.v1'").ShouldBe(0);
    }

    [Theory]
    [InlineData(EpisodeStatus.Draft)]
    [InlineData(EpisodeStatus.StoryGenerating)]
    [InlineData(EpisodeStatus.StoryReview)]
    [InlineData(EpisodeStatus.Rendering)]
    [InlineData(EpisodeStatus.VideoReview)]
    [InlineData(EpisodeStatus.Ready)]
    [InlineData(EpisodeStatus.Scheduled)]
    [InlineData(EpisodeStatus.Published)]
    [InlineData(EpisodeStatus.Failed)]
    [InlineData(EpisodeStatus.Cancelled)]
    public async Task Reopen_EveryNonApprovedStatus_Returns409EvenWithTheCurrentTag_AndChangesNothing(EpisodeStatus status)
    {
        var world = await SeedAsync(status);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var before = Snapshot(world);

        foreach (var tag in new[] { Tag(world), null })
        {
            using var response = await SendAsync(client, world.EpisodeId, "reopen", tag);
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
            (await CodeAsync(response)).ShouldBe("episode_invalid_state");
        }

        Snapshot(world).ShouldBe(before);
        Count(world, "audit_logs", "action = 'episode.story_reopened'").ShouldBe(0);
        Count(world, "outbox_messages", "type = 'story_reopened.v1'").ShouldBe(0);
    }

    [Theory]
    [InlineData("nul", "violence|vio\0lence", 409)]
    [InlineData("c0", "vio\u0001lence", 409)]
    [InlineData("loneSurrogate", "violence|@LONE-SURROGATE@", 409)]
    [InlineData("blank", "   ", 409)]
    [InlineData("upperBlocker", "Sexual/Minors", 409)]
    [InlineData("paddedBlocker", " sexual/minors ", 409)]
    [InlineData("unknownAdvisory", "brand-new-provider-category", 200)]
    public async Task Approve_FreshVerdictCategoryEdgeCases_NeverPersistUnsafeTextAndBlockWhenUncertain(string kind, string categories, int status)
    {
        var world = await SeedAsync(audit: Audit(StaleClean));
        var before = StoredAudit(world);
        var moderator = new FakeContentModerator().EnqueueFlagged(
            ModerationStage.Draft, categories.Replace("@LONE-SURROGATE@", "\uD800", StringComparison.Ordinal).Split('|'));
        using var factory = Factory(moderator);
        using var client = factory.ClientFor(world.UserId);

        using var response = await SendAsync(client, world.EpisodeId, "approve", Tag(world));

        response.StatusCode.ShouldBe((HttpStatusCode)status, $"{kind}: {await response.Content.ReadAsStringAsync(Ct)}");
        moderator.Calls.ShouldBe(1);
        if (status == 409)
        {
            (await CodeAsync(response)).ShouldBe("story_moderated");
            JsonNode.DeepEquals(StoredAudit(world), before).ShouldBeTrue("a rejected verdict must not be written back");
            Snapshot(world).ShouldBe("story_review|null|null");
            Count(world, "audit_logs", "action = 'episode.story_approved'").ShouldBe(0);
            Count(world, "outbox_messages", "type = 'story_approved.v1'").ShouldBe(0);
        }
        else
        {
            var moderation = StoredAudit(world)["moderation"]!.AsObject();
            moderation["stale"]!.GetValue<bool>().ShouldBeFalse();
            moderation["unavailable"]!.GetValue<bool>().ShouldBeFalse();
            moderation["flagged"]!.GetValue<bool>().ShouldBeTrue();
            moderation["blocking"]!.AsArray().Count.ShouldBe(0);
            moderation["categories"]!.AsArray().Select(node => node!.GetValue<string>()).ShouldBe(["brand-new-provider-category"]);
        }

        SqlProbe.Count(Database, $"SELECT count(*) FROM story_versions WHERE id = '{world.VersionId}' AND audit_report::text LIKE '%\\u0000%'").ShouldBe(0);
    }

    [Fact]
    public async Task Approve_StoredBlockingVerdictWithForgedEmptyBlockingArray_IsNotTrustedAndIsRechecked()
    {
        var forged = """{"flagged":true,"categories":["sexual/minors"],"blocking":[],"stale":false,"unavailable":false}""";
        var world = await SeedAsync(audit: Audit(forged));
        var moderator = new FakeContentModerator().EnqueueFlagged(ModerationStage.Draft, "illicit/violent");
        using var factory = Factory(moderator);
        using var client = factory.ClientFor(world.UserId);

        using var response = await SendAsync(client, world.EpisodeId, "approve", Tag(world));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await CodeAsync(response)).ShouldBe("story_moderated");
        moderator.Calls.ShouldBe(1);
        Snapshot(world).ShouldBe("story_review|null|null");
    }

    [Fact]
    public async Task Approve_StoredUppercaseBlockerWithMatchingBlockingArray_StillBlocksWithoutProviderCall()
    {
        var stored = """{"flagged":true,"categories":["SEXUAL/MINORS"],"blocking":["SEXUAL/MINORS"],"stale":false,"unavailable":false}""";
        var world = await SeedAsync(audit: Audit(stored));
        var moderator = new FakeContentModerator();
        using var factory = Factory(moderator);
        using var client = factory.ClientFor(world.UserId);

        using var response = await SendAsync(client, world.EpisodeId, "approve", Tag(world));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await CodeAsync(response)).ShouldBe("story_moderated");
        Snapshot(world).ShouldBe("story_review|null|null");
    }

    [Theory]
    [InlineData("invalidOperation")]
    [InlineData("httpRequest")]
    [InlineData("providerDeadline")]
    [InlineData("nullResult")]
    public async Task Approve_RecheckProviderFailure_FailsClosedAndLeavesStatusAuditAndOutboxUnchanged(string kind)
    {
        var report = Audit(StaleClean);
        var world = await SeedAsync(audit: report);
        var moderator = new ScriptedModerator(kind);
        using var factory = Factory(moderator);
        using var client = factory.ClientFor(world.UserId);

        using var response = await SendAsync(client, world.EpisodeId, "approve", Tag(world));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await CodeAsync(response)).ShouldBe("story_moderated");
        moderator.Calls.ShouldBe(1);
        JsonNode.DeepEquals(StoredAudit(world), JsonNode.Parse(report)).ShouldBeTrue();
        Snapshot(world).ShouldBe("story_review|null|null");
        Count(world, "audit_logs", "action = 'episode.story_approved'").ShouldBe(0);
        Count(world, "outbox_messages", "type = 'story_approved.v1'").ShouldBe(0);
    }

    [Fact]
    public async Task Approve_AlreadyCancelledCaller_ThrowsAndLeavesTheEpisodeInReview()
    {
        var world = await SeedAsync();
        using var factory = Factory(userId: world.UserId);
        await using var scope = factory.Services.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await mediator.Send(new ApproveStoryCommand(world.EpisodeId, Tag(world), null), cancelled.Token));

        Snapshot(world).ShouldBe("story_review|null|null");
        Count(world, "audit_logs", "action = 'episode.story_approved'").ShouldBe(0);
        Count(world, "outbox_messages", "type = 'story_approved.v1'").ShouldBe(0);
    }

    [Fact]
    public async Task ReopenThenRenderPort_OldApprovedIdStillResolvesTheImmutableVersion_ButTheEpisodePointerIsNull()
    {
        var world = await SeedAsync();
        using var factory = Factory(userId: world.UserId);
        using var client = factory.ClientFor(world.UserId);
        using var approved = await SendAsync(client, world.EpisodeId, "approve", Tag(world));
        approved.StatusCode.ShouldBe(HttpStatusCode.OK);
        var approvedId = Guid.Parse(SqlProbe.ScalarText(Database, $"SELECT approved_story_version_id::text FROM episodes WHERE id = '{world.EpisodeId}'")!);
        using var reopened = await SendAsync(client, world.EpisodeId, "reopen", Tag(world));
        reopened.StatusCode.ShouldBe(HttpStatusCode.OK);

        SqlProbe.ScalarText(Database, $"SELECT coalesce(approved_story_version_id::text, 'null') FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("null");
        await using var scope = factory.Services.CreateAsyncScope();
        var port = scope.ServiceProvider.GetRequiredService<IStoryRenderInputProvider>();
        var input = await port.GetAsync(approvedId, Ct);

        input.IsError.ShouldBeFalse("the port is addressed by version id; the caller (09.6.1) must read the null pointer first");
        input.Value.StoryVersionId.ShouldBe(approvedId);
        input.Value.Title.ShouldBe(StoryTitle);
        input.Value.Body.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ApproveReopenApprove_SameVersion_WritesTwoApprovalsOneReopenAndSecondApprovalIsNotIdempotentNoOp()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        (await SendAsync(client, world.EpisodeId, "approve", Tag(world))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(client, world.EpisodeId, "reopen", Tag(world))).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var again = await SendAsync(client, world.EpisodeId, "approve", Tag(world));

        again.StatusCode.ShouldBe(HttpStatusCode.OK, await again.Content.ReadAsStringAsync(Ct));
        Snapshot(world).ShouldStartWith($"story_approved|{world.VersionId}|");
        Count(world, "audit_logs", "action = 'episode.story_approved'").ShouldBe(2);
        Count(world, "audit_logs", "action = 'episode.story_reopened'").ShouldBe(1);
        Count(world, "outbox_messages", "type = 'story_approved.v1'").ShouldBe(2);
        Count(world, "outbox_messages", "type = 'story_reopened.v1'").ShouldBe(1);
    }

    [Fact]
    public async Task AuditAndOutboxRows_ForApproveAndReopen_CarryOnlyIdsAndCodes()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        (await SendAsync(client, world.EpisodeId, "approve", Tag(world))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(client, world.EpisodeId, "reopen", Tag(world))).StatusCode.ShouldBe(HttpStatusCode.OK);

        Keys($"SELECT data::text FROM audit_logs WHERE action = 'episode.story_approved' AND subject_user_id = '{world.UserId}'")
            .ShouldBe(["approval_source", "episode_id", "story_version_id"]);
        Keys($"SELECT payload::text FROM outbox_messages WHERE type = 'story_approved.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(["approvalSource", "episodeId", "occurredAt", "seriesId", "storyVersionId", "userId"]);
        Keys($"SELECT payload::text FROM outbox_messages WHERE type = 'story_reopened.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(["episodeId", "occurredAt", "seriesId", "storyVersionId", "userId"]);
        foreach (var sql in new[]
        {
            $"SELECT string_agg(data::text, ' ') FROM audit_logs WHERE action IN ('episode.story_approved', 'episode.story_reopened') AND subject_user_id = '{world.UserId}'",
            $"SELECT string_agg(payload::text, ' ') FROM outbox_messages WHERE type IN ('story_approved.v1', 'story_reopened.v1') AND user_id = '{world.UserId}'",
        })
        {
            var text = SqlProbe.ScalarText(Database, sql)!;
            text.ShouldNotContain(StoryTitle);
            text.ShouldNotContain("An old house stood");
            text.ShouldNotContain("A fictional story");
        }
    }

    [Fact]
    public async Task ReopenAuditData_UsesTheContractedSnakeCaseKeys()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        (await SendAsync(client, world.EpisodeId, "approve", Tag(world))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(client, world.EpisodeId, "reopen", Tag(world))).StatusCode.ShouldBe(HttpStatusCode.OK);

        Keys($"SELECT data::text FROM audit_logs WHERE action = 'episode.story_reopened' AND subject_user_id = '{world.UserId}'")
            .ShouldBe(["episode_id", "story_version_id"]);
    }

    [Theory]
    [InlineData("foreign", 404)]
    [InlineData("unknown", 404)]
    [InlineData("owned", 400)]
    public async Task Approve_MalformedBody_ReportsOwnershipBeforeBodyValidation(string ownership, int expectedStatus)
    {
        var owner = await SeedAsync();
        var stranger = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(ownership == "foreign" ? stranger.UserId : owner.UserId);
        var episodeId = ownership == "unknown" ? Guid.CreateVersion7() : owner.EpisodeId;

        foreach (var json in InvalidApprovalBodies)
        {
            foreach (var tag in new[] { null, Tag(owner) })
            {
                using var response = await SendAsync(client, episodeId, "approve", tag, Json(json));

                response.StatusCode.ShouldBe((HttpStatusCode)expectedStatus, await response.Content.ReadAsStringAsync(Ct));
                (await CodeAsync(response)).ShouldBe(expectedStatus == 404 ? "not_found" : "story_attestation_required");
            }
        }

        Snapshot(owner).ShouldBe("story_review|null|null");
        Count(owner, "audit_logs", "action = 'episode.story_approved'").ShouldBe(0);
        Count(owner, "outbox_messages", "type = 'story_approved.v1'").ShouldBe(0);
    }

    [Theory]
    [InlineData("bom")]
    [InlineData("whitespaceOnly")]
    public async Task Approve_UnparseableBodies_Return400WithoutSaving(string kind)
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        HttpContent content = kind == "bom"
            ? new ByteArrayContent([0xEF, 0xBB, 0xBF, .. "{}"u8.ToArray()])
            : new StringContent("   ", Encoding.UTF8, "application/json");
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var response = await SendAsync(client, world.EpisodeId, "approve", Tag(world), content);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        Snapshot(world).ShouldBe("story_review|null|null");
    }

    [Fact]
    public async Task LegacyApprovedRowWithoutApprovedAt_IsNotIdempotent_Unreachable()
    {
        // QA-3 owner ruling (2026-10-05): won't fix; no production writer can create story_approved with null
        // approved_at. Keep this manually corrupted/legacy row probe explicit, including its reopen behaviour.
        var world = await SeedAsync(EpisodeStatus.StoryApproved);
        SqlProbe.Execute(Database, $"UPDATE episodes SET approved_story_version_id = '{world.VersionId}', approved_at = NULL WHERE id = '{world.EpisodeId}'");
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var repeat = await SendAsync(client, world.EpisodeId, "approve", Tag(world));

        repeat.StatusCode.ShouldBe(HttpStatusCode.Conflict, await repeat.Content.ReadAsStringAsync(Ct));
        (await CodeAsync(repeat)).ShouldBe("episode_invalid_state");
        using var reopen = await SendAsync(client, world.EpisodeId, "reopen", Tag(world));
        reopen.StatusCode.ShouldBe(HttpStatusCode.OK, await reopen.Content.ReadAsStringAsync(Ct));
        Snapshot(world).ShouldBe("story_review|null|null");
    }

    [Theory]
    [InlineData("manyCategories")]
    [InlineData("hugeCategory")]
    public async Task Approve_OversizedFreshVerdict_IsRejectedWithoutOverwritingAudit(string kind)
    {
        var world = await SeedAsync(audit: Audit(StaleClean));
        var before = StoredAudit(world);
        var names = kind == "manyCategories"
            ? Enumerable.Range(0, 3000).Select(index => $"advisory-{index:D4}").ToArray()
            : [new string('x', 200_000)];
        var moderator = new FakeContentModerator().EnqueueFlagged(ModerationStage.Draft, names);
        using var factory = Factory(moderator);
        using var client = factory.ClientFor(world.UserId);

        using var response = await SendAsync(client, world.EpisodeId, "approve", Tag(world));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await CodeAsync(response)).ShouldBe("story_moderated");
        moderator.Calls.ShouldBe(1);
        Snapshot(world).ShouldBe("story_review|null|null");
        JsonNode.DeepEquals(StoredAudit(world), before).ShouldBeTrue("an oversized verdict must not overwrite audit_report");
        Count(world, "audit_logs", "action = 'episode.story_approved'").ShouldBe(0);
        Count(world, "outbox_messages", "type = 'story_approved.v1'").ShouldBe(0);
    }

    [Fact]
    public async Task TrueCrimeWithTheDefaultNullGate_FailsClosedWithoutAttestationOrFactSheet()
    {
        var world = await SeedAsync(topic: Topic.TrueCrime);
        using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IStoryApprovalGate>().ShouldBeOfType<NullStoryApprovalGate>();
        using var client = factory.ClientFor(world.UserId);

        using var response = await SendAsync(client, world.EpisodeId, "approve", Tag(world));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await CodeAsync(response)).ShouldBe("story_fact_sheet_outdated");
        Snapshot(world).ShouldBe("story_review|null|null");
        Count(world, "audit_logs", "action = 'episode.story_approved'").ShouldBe(0);
        Count(world, "outbox_messages", "type = 'story_approved.v1'").ShouldBe(0);
        Count(world, "audit_logs", "action = 'episode.true_crime_attested'").ShouldBe(0);
    }

    [Fact]
    public async Task ConcurrentApproveAndEdit_NeverApprovesAVersionThatIsNoLongerCurrent()
    {
        var world = await SeedAsync(draft: LongDraft());
        using var factory = Factory();
        using var approveClient = factory.ClientFor(world.UserId);
        using var editClient = factory.ClientFor(world.UserId);
        var edited = LongDraft() with { Title = "Căn nhà ven sông" };
        using var edit = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/episodes/{world.EpisodeId}/story")
        {
            Content = new StringContent(JsonSerializer.Serialize(edited, StoryContentJson.SerializerOptions), Encoding.UTF8, "application/json"),
        };
        edit.Headers.TryAddWithoutValidation("If-Match", Tag(world));

        var approveTask = SendAsync(approveClient, world.EpisodeId, "approve", Tag(world));
        var editTask = editClient.SendAsync(edit, Ct);
        await Task.WhenAll(approveTask, editTask);
        using var approve = await approveTask;
        using var saved = await editTask;

        var outcome = (approve.StatusCode, saved.StatusCode);
        outcome.ShouldBeOneOf(
            (HttpStatusCode.OK, HttpStatusCode.Conflict),
            (HttpStatusCode.PreconditionFailed, HttpStatusCode.OK));
        var invariant = SqlProbe.ScalarText(Database,
            $"SELECT CASE WHEN status = 'story_approved' THEN (approved_story_version_id = current_story_version_id)::text ELSE 'review' END FROM episodes WHERE id = '{world.EpisodeId}'");
        invariant.ShouldBeOneOf("true", "review");
        if (approve.StatusCode == HttpStatusCode.OK)
        {
            invariant.ShouldBe("true");
            SqlProbe.ScalarText(Database, $"SELECT approved_story_version_id::text FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe(world.VersionId.ToString());
        }
    }

    private static string Variant(string kind, Guid versionId)
    {
        var tag = StoryEtag.For(versionId);
        var inner = tag.Trim('"');
        return kind switch
        {
            "weak" => "W/" + tag,
            "list" => $"{tag}, {StoryEtag.For(Guid.CreateVersion7())}",
            "listReversed" => $"{StoryEtag.For(Guid.CreateVersion7())}, {tag}",
            "epTag" => "\"ep-123456\"",
            "star" => "*",
            "unquoted" => inner,
            "upperPrefix" => $"\"SV-{versionId:D}\"",
            "bareGuid" => versionId.ToString("D"),
            "singleQuoted" => $"'{inner}'",
            "trailingJunk" => tag + "x",
            "emptyGuid" => "\"sv-00000000-0000-0000-0000-000000000000\"",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    private static string Tag(World world) => StoryEtag.For(world.VersionId);

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, Guid episodeId, string action, string? tag, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/episodes/{episodeId}/story/{action}") { Content = content };
        if (tag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", tag);
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<string> CodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.GetProperty("code").GetString()!;
    }

    private ApiFactory Factory(IContentModerator? moderator = null, Guid? userId = null) =>
        new ApiFactory(Database, new FakeClock(Start)).WithServices(services =>
        {
            services.RemoveAll<IContentModerator>();
            services.AddSingleton<IContentModerator>(moderator ?? new FakeContentModerator());
            if (userId is { } id)
            {
                services.RemoveAll<ICurrentUser>();
                services.AddSingleton<ICurrentUser>(new TestCurrentUser { Id = id });
            }
        });

    private string Snapshot(World world) => SqlProbe.ScalarText(Database,
        "SELECT status || '|' || coalesce(approved_story_version_id::text, 'null') || '|' || coalesce(approved_at::text, 'null') "
        + $"FROM episodes WHERE id = '{world.EpisodeId}'")!;

    private long Count(World world, string table, string predicate) => SqlProbe.Count(Database,
        $"SELECT count(*) FROM {table} WHERE {predicate} AND {(table == "audit_logs" ? "subject_user_id" : "user_id")} = '{world.UserId}'");

    private string[] Keys(string sql)
    {
        using var document = JsonDocument.Parse(SqlProbe.ScalarText(Database, sql)!);
        return [.. document.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)];
    }

    private JsonObject StoredAudit(World world) =>
        JsonNode.Parse(SqlProbe.ScalarText(Database, $"SELECT audit_report::text FROM story_versions WHERE id = '{world.VersionId}'")!)!.AsObject();

    private static string Audit(string moderation) =>
        $$$"""{"schema_version":"audit.v1","local":{"issues":[]},"moderation":{{{moderation}}},"unknown_future_key":{"keep":true}}""";

    private async Task<World> SeedAsync(
        EpisodeStatus status = EpisodeStatus.StoryReview,
        Topic topic = Topic.Ghost,
        string? audit = null,
        StoryContentV1? draft = null)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, topic: topic, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor)
            .CreateAsync(user.Id, series.Id, lengthTier: LengthTier.Short, status: status, ct: Ct);
        var content = draft ?? SimpleDraft();
        var version = StoryVersion.Create(user.Id, episode.Id, 1, StoryVersionSource.Generated, content,
            StoryContentJson.Write(content, new JsonArray()), "model-test", "rel=1", null, null, null,
            audit ?? Audit(Clean), null, Start);
        await using (var db = Database.CreateAppDbContext(new TestCurrentUser { Id = user.Id }))
        {
            db.StoryVersions.Add(version);
            await db.SaveChangesAsync(Ct);
        }

        SqlProbe.Execute(Database, $"UPDATE episodes SET current_story_version_id = '{version.Id}' WHERE id = '{episode.Id}'");
        return new World(user.Id, episode.Id, version.Id);
    }

    private static StoryContentV1 SimpleDraft() => new(
        StoryTitle,
        new StorySegmentV1("An old house stood by the river.", ["old house"], []),
        [new StorySegmentV1("A visitor heard footsteps in the empty hallway.", ["empty hallway"], [], "setup")],
        new StorySegmentV1("At dawn the visitor left the house.", ["dawn"], []),
        "A fictional story.", ["ghost"], ["supernatural"],
        new StoryStructureV1("ghost", "old house", "footsteps", "visitor", "first_person"));

    private static StoryContentV1 LongDraft()
    {
        var hook = new StorySegmentV1("Đêm ấy người kể ngồi một mình trong căn nhà cũ.", ["old house by the river", "stormy night over the river"], []);
        StorySegmentV1[] paragraphs =
        [
            new("Chủ nhà dặn đừng mở cánh cửa cuối hành lang.", ["empty chair in the hallway"], [], "setup"),
            new("Tiếng gõ vang lên từ chiếc ghế vẫn còn ấm.", ["rain on window"], [], "twist"),
            new("Người hàng xóm im lặng nhìn ra sân.", ["figure at door"], [], "reveal"),
        ];
        var ending = new StorySegmentV1("Anh trả nhà ngay hôm sau.", ["letter on table"], []);
        var count = Words(hook.Text) + Words(ending.Text) + paragraphs.Sum(segment => Words(segment.Text));
        paragraphs[^1] = paragraphs[^1] with
        {
            Text = paragraphs[^1].Text + " " + string.Join(' ', Enumerable.Repeat("rồi", 220 - count)),
        };
        return new StoryContentV1("Căn nhà cuối hành lang", hook, paragraphs, ending,
            "Một chuyện ma nhẹ nhàng.", ["truyện ma", "tâm linh"], ["supernatural"],
            new StoryStructureV1("truyện ma tâm linh", "ngôi nhà cũ", "một cuộc điện thoại lúc nửa đêm", "người kể là nguyên nhân", "first_person"));
    }

    private static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private sealed record World(Guid UserId, Guid EpisodeId, Guid VersionId);

    private sealed class ScriptedModerator(string kind) : IContentModerator
    {
        public int Calls { get; private set; }

        public ValueTask<ModerationResult> CheckAsync(IReadOnlyList<string> texts, ModerationStage stage, CancellationToken ct)
        {
            Calls++;
            return kind switch
            {
                "invalidOperation" => throw new InvalidOperationException("provider exploded"),
                "httpRequest" => throw new HttpRequestException("provider down"),
                "providerDeadline" => throw new TaskCanceledException("provider deadline"),
                _ => ValueTask.FromResult<ModerationResult>(null!),
            };
        }
    }
}
