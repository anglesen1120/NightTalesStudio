using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Features.Stories;
using NightTales.Application.Features.Stories.Approval;
using NightTales.Application.Features.Stories.Commands.ApproveStory;
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
using StorySeries = NightTales.Domain.Series.Series;

namespace NightTales.Api.IntegrationTests.StoryGeneration;

/// <summary>Approval ordering, refresh and atomic writes against the real host and PostgreSQL.</summary>
[Trait("Category", "Integration")]
public sealed partial class ApproveStoryEndpointTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Action = "episode.story_approved";
    private const string Clean = """{"flagged":false,"categories":[],"blocking":[],"stale":false,"unavailable":false}""";
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task ApproveStory_ReviewStateCurrentEtag_SetsApprovedVersionAndStatus_ExposesCurrentStoryEtag()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await ApproveAsync(client, world);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await BodyAsync(response);
        body.GetProperty("episodeId").GetGuid().ShouldBe(world.EpisodeId);
        body.GetProperty("storyVersionId").GetGuid().ShouldBe(world.VersionId);
        body.GetProperty("status").GetString().ShouldBe("story_approved");
        body.GetProperty("approvedAt").GetDateTimeOffset().ShouldBe(Start);
        body.GetProperty("etag").GetString().ShouldBe(StoryEtag.For(world.VersionId));
        response.Headers.ETag!.Tag.ShouldBe(StoryEtag.For(world.VersionId));

        using var detail = await client.GetAsync($"/api/v1/episodes/{world.EpisodeId}", Ct);
        detail.StatusCode.ShouldBe(HttpStatusCode.OK, await detail.Content.ReadAsStringAsync(Ct));
        var episodeBody = await BodyAsync(detail);
        episodeBody.GetProperty("currentStory").GetProperty("etag").GetString()
            .ShouldBe(StoryEtag.For(world.VersionId));
        var episodeEtag = $"\"ep-{SqlProbe.ScalarText(Database, $"SELECT xmin::text FROM episodes WHERE id='{world.EpisodeId}'")}\"";
        detail.Headers.ETag!.Tag.ShouldBe(episodeEtag);
        episodeBody.GetProperty("etag").GetString().ShouldBe(episodeEtag);

        AssertApproved(world);
        AuditCount(world).ShouldBe(1);
        OutboxCount(world).ShouldBe(1);
        SqlProbe.ScalarText(Database,
            $"SELECT data->>'episode_id' || '|' || (data->>'story_version_id') || '|' || (data->>'approval_source') FROM audit_logs WHERE action='{Action}' AND subject_user_id='{world.UserId}'")
            .ShouldBe($"{world.EpisodeId}|{world.VersionId}|user");
        SqlProbe.ScalarText(Database,
            $"SELECT string_agg(key, ',' ORDER BY key) FROM audit_logs, jsonb_object_keys(data) AS key WHERE action='{Action}' AND subject_user_id='{world.UserId}'")
            .ShouldBe("approval_source,episode_id,story_version_id");
        var audit = SqlProbe.ScalarText(Database, $"SELECT data::text FROM audit_logs WHERE action='{Action}' AND subject_user_id='{world.UserId}'");
        audit.ShouldNotContain(Draft().Title);
        audit.ShouldNotContain(Draft().Hook.Text);
        using var payload = JsonDocument.Parse(SqlProbe.ScalarText(Database,
            $"SELECT payload::text FROM outbox_messages WHERE type='story_approved.v1' AND user_id='{world.UserId}'"));
        payload.RootElement.GetProperty("episodeId").GetGuid().ShouldBe(world.EpisodeId);
        payload.RootElement.GetProperty("storyVersionId").GetGuid().ShouldBe(world.VersionId);
        payload.RootElement.GetProperty("approvalSource").GetString().ShouldBe("user");
    }

    [Fact]
    public async Task ApproveStory_AlreadyApprovedSameEtag_IsIdempotent200_NoSecondAudit()
    {
        var world = await SeedAsync();
        var gate = new RecordingGate();
        var moderator = new FakeContentModerator();
        using var factory = Factory(moderator, gate);
        using var client = factory.ClientFor(world.UserId);
        using var first = await ApproveAsync(client, world);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var repeat = await ApproveAsync(client, world);
        repeat.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyAsync(repeat)).GetRawText().ShouldBe((await BodyAsync(first)).GetRawText());
        AuditCount(world).ShouldBe(1);
        OutboxCount(world).ShouldBe(1);
        gate.Calls.ShouldBe(1);
        moderator.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData(null, 428, "if_match_required")]
    [InlineData("stale", 412, "story_version_conflict")]
    [InlineData("W/\"sv-00000000-0000-0000-0000-000000000001\"", 412, "story_version_conflict")]
    [InlineData("*", 412, "story_version_conflict")]
    [InlineData("\"ep-1234\"", 412, "story_version_conflict")]
    public async Task ApproveStory_MissingStaleOrMalformedEtag_FailsBeforeGate(string? tag, int status, string code)
    {
        var world = await SeedAsync(audit: Audit(Clean, stale: true));
        var gate = new RecordingGate();
        var moderator = new FakeContentModerator();
        using var factory = Factory(moderator, gate);
        using var client = factory.ClientFor(world.UserId);
        using var response = await PostAsync(client, world.EpisodeId, tag == "stale" ? StoryEtag.For(Guid.CreateVersion7()) : tag, ct: Ct);
        await AssertProblemAsync(response, status, code);
        gate.Calls.ShouldBe(0);
        moderator.Calls.ShouldBe(0);
        AssertNothingApproved(world);
    }

    [Theory]
    [InlineData(EpisodeStatus.Draft)]
    [InlineData(EpisodeStatus.StoryGenerating)]
    [InlineData(EpisodeStatus.Rendering)]
    public async Task ApproveStory_NonReviewState_PrecedesMissingOrStaleEtag(EpisodeStatus status)
    {
        var world = await SeedAsync(status: status);
        var gate = new RecordingGate();
        using var factory = Factory(gate: gate, actorId: world.UserId);
        using var client = factory.ClientFor(world.UserId);
        using var missing = await PostAsync(client, world.EpisodeId, null, ct: Ct);
        await AssertProblemAsync(missing, 409, "episode_invalid_state");
        // The public route and the command both preserve state-before-ETag ordering.
        await using var scope = factory.Services.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var direct = await mediator.Send(new ApproveStoryCommand(world.EpisodeId, "", null), Ct);
        direct.IsError.ShouldBeTrue();
        direct.FirstError.Code.ShouldBe("episode_invalid_state");
        using var stale = await PostAsync(client, world.EpisodeId, StoryEtag.For(Guid.CreateVersion7()), ct: Ct);
        await AssertProblemAsync(stale, 409, "episode_invalid_state");
        gate.Calls.ShouldBe(0);
        AuditCount(world).ShouldBe(0);
        OutboxCount(world).ShouldBe(0);
    }

    [Fact]
    public async Task ApproveStory_AlreadyApprovedDifferentEtag_Returns409InvalidState()
    {
        var world = await SeedAsync();
        using var factory = Factory(actorId: world.UserId);
        using var client = factory.ClientFor(world.UserId);
        using var first = await ApproveAsync(client, world);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var wrong = await PostAsync(client, world.EpisodeId, StoryEtag.For(Guid.CreateVersion7()), ct: Ct);
        await AssertProblemAsync(wrong, 409, "episode_invalid_state");
        using var missing = await PostAsync(client, world.EpisodeId, null, ct: Ct);
        await AssertProblemAsync(missing, 409, "episode_invalid_state");
        await using var scope = factory.Services.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var direct = await mediator.Send(new ApproveStoryCommand(world.EpisodeId, "", null), Ct);
        direct.IsError.ShouldBeTrue();
        direct.FirstError.Code.ShouldBe("episode_invalid_state");
        AuditCount(world).ShouldBe(1);
        OutboxCount(world).ShouldBe(1);
    }

    [Fact]
    public async Task ForeignEpisode_ApproveReturns404()
    {
        var owner = await SeedAsync();
        var stranger = await SeedAsync();
        var gate = new RecordingGate();
        using var factory = Factory(gate: gate, actorId: stranger.UserId);
        using var client = factory.ClientFor(stranger.UserId);
        using var response = await PostAsync(client, owner.EpisodeId, StoryEtag.For(owner.VersionId), ct: Ct);
        await AssertProblemAsync(response, 404, "not_found");
        gate.Calls.ShouldBe(0);
        AssertNothingApproved(owner);
        using var absent = await PostAsync(client, Guid.CreateVersion7(), null, ct: Ct);
        await AssertProblemAsync(absent, 404, "not_found");
        await using var scope = factory.Services.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var directForeign = await mediator.Send(new ApproveStoryCommand(owner.EpisodeId, "", null), Ct);
        directForeign.IsError.ShouldBeTrue();
        directForeign.FirstError.Code.ShouldBe("not_found");
        var directAbsent = await mediator.Send(new ApproveStoryCommand(Guid.CreateVersion7(), "", null), Ct);
        directAbsent.IsError.ShouldBeTrue();
        directAbsent.FirstError.Code.ShouldBe("not_found");
    }

    [Fact]
    public async Task ApproveStory_Fiction_SkipsAttestationAndTrueCrimeAudit()
    {
        var world = await SeedAsync();
        var gate = new RecordingGate(acceptedTextVersion: "ta.v1");
        using var factory = Factory(gate: gate);
        using var client = factory.ClientFor(world.UserId);
        using var response = await ApproveAsync(client, world, """{"attestation":{"accepted":true,"textVersion":"ta.v1"}}""");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        gate.Attestation.ShouldBeNull("fiction input is not forwarded to a true-crime gate");
        AttestationCount(world).ShouldBe(0);
    }

    [Fact]
    public async Task ApproveStory_TrueCrimeAcceptedTextResult_WritesAttestationInSameTransactionOnce()
    {
        var world = await SeedAsync(topic: Topic.TrueCrime);
        var gate = new RecordingGate(acceptedTextVersion: "ta.v1");
        using var factory = Factory(gate: gate);
        using var client = factory.ClientFor(world.UserId);
        using var first = await ApproveAsync(client, world, """{"attestation":{"accepted":true,"textVersion":"ta.v1"}}""");
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        gate.Attestation.ShouldBe(new AttestationInput(true, "ta.v1"));
        AttestationCount(world).ShouldBe(1);
        SqlProbe.ScalarText(Database,
            $"SELECT data->>'story_version_id' || '|' || (data->>'text_version') FROM audit_logs WHERE action='episode.true_crime_attested' AND subject_user_id='{world.UserId}'")
            .ShouldBe($"{world.VersionId}|ta.v1");
        using var repeat = await ApproveAsync(client, world);
        repeat.StatusCode.ShouldBe(HttpStatusCode.OK);
        AttestationCount(world).ShouldBe(1);
    }

    [Theory]
    [InlineData("sexual/minors", 409)]
    [InlineData("self-harm/instructions", 409)]
    [InlineData("illicit/violent", 409)]
    [InlineData("hate/threatening", 409)]
    [InlineData("violence", 200)]
    public async Task ApproveStory_FreshBlockingOrAdvisoryModeration_DecidesWithoutProviderCall(string category, int status)
    {
        var blocking = status == 409 ? $"[\"{category}\"]" : "[]";
        var world = await SeedAsync(audit: Audit($$"""{"flagged":true,"categories":["{{category}}"],"blocking":{{blocking}},"stale":false,"unavailable":false}"""));
        var moderator = new FakeContentModerator();
        using var factory = Factory(moderator);
        using var client = factory.ClientFor(world.UserId);
        using var response = await ApproveAsync(client, world);
        response.StatusCode.ShouldBe((HttpStatusCode)status);
        moderator.Calls.ShouldBe(0);
        if (status == 409)
        {
            (await ProblemAsync(response)).Code.ShouldBe("story_moderated");
            AssertNothingApproved(world);
        }
        else
        {
            AssertApproved(world);
        }
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("unavailable")]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("contradictory")]
    public async Task ApproveStory_StaleUnavailableMissingOrMalformedModeration_RerunsAndPreservesOtherKeys(string kind)
    {
        var report = JsonNode.Parse(Audit(Clean))!.AsObject();
        switch (kind)
        {
            case "stale": report["moderation"]!["stale"] = true; break;
            case "unavailable": report["moderation"]!["unavailable"] = true; break;
            case "missing": report.Remove("moderation"); break;
            case "malformed": report["moderation"] = "not a verdict"; break;
            case "contradictory": report["moderation"]!["categories"] = new JsonArray("sexual/minors"); break;
        }
        var before = report.DeepClone().AsObject();
        before.Remove("moderation");
        var world = await SeedAsync(audit: report.ToJsonString());
        var moderator = new FakeContentModerator().EnqueueClean(ModerationStage.Draft);
        using var factory = Factory(moderator);
        using var client = factory.ClientFor(world.UserId);
        using var response = await ApproveAsync(client, world);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        moderator.Calls.ShouldBe(1);
        moderator.Checks.Single().Stage.ShouldBe(ModerationStage.Draft);
        moderator.Checks.Single().Texts.ShouldContain(Draft().Title);
        moderator.Checks.Single().Texts.ShouldContain(Draft().Description!);
        var persisted = StoredAudit(world);
        JsonNode.DeepEquals(persisted["moderation"], JsonNode.Parse(Clean)).ShouldBeTrue();
        persisted.Remove("moderation");
        JsonNode.DeepEquals(persisted, before).ShouldBeTrue("only moderation is replaced");
        AssertApproved(world);
    }

    [Fact]
    public async Task ApproveStory_StaleBlockingVerdict_FreshCleanWinsOverOldBlocker()
    {
        var world = await SeedAsync(audit: Audit("""{"flagged":true,"categories":["sexual/minors"],"blocking":["sexual/minors"],"stale":true,"unavailable":false}"""));
        var moderator = new FakeContentModerator().EnqueueClean(ModerationStage.Draft);
        using var factory = Factory(moderator);
        using var client = factory.ClientFor(world.UserId);
        using var response = await ApproveAsync(client, world);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        moderator.Calls.ShouldBe(1);
        AssertApproved(world);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApproveStory_RefreshBlocksOrFails_Returns409AndRollsBackVerdictWriteback(bool unavailable)
    {
        var report = Audit(Clean, stale: true);
        var world = await SeedAsync(audit: report);
        var moderator = new FakeContentModerator();
        if (unavailable) moderator.EnqueueUnavailable(ModerationStage.Draft);
        else moderator.EnqueueFlagged(ModerationStage.Draft, "sexual/minors");
        using var factory = Factory(moderator);
        using var client = factory.ClientFor(world.UserId);
        using var response = await ApproveAsync(client, world);
        await AssertProblemAsync(response, 409, "story_moderated");
        moderator.Calls.ShouldBe(1);
        SqlProbe.Count(Database,
                $"SELECT count(*) FROM moderation_events WHERE user_id = '{world.UserId}' AND episode_id = '{world.EpisodeId}'")
            .ShouldBe(unavailable ? 0 : 1, "blocking evidence commits independently of the rejected approval");
        JsonNode.DeepEquals(StoredAudit(world), JsonNode.Parse(report)).ShouldBeTrue("the existing UoW rolls back an error result including a fresh blocking verdict");
        AssertNothingApproved(world);
    }

    [Theory]
    [InlineData("story_fact_sheet_outdated", 409)]
    [InlineData("story_sources_required", 409)]
    [InlineData("story_too_similar_to_source", 409)]
    [InlineData("story_attestation_required", 400)]
    public async Task ApproveStory_InjectedGateFailure_PrecedesModerationAndPropagatesExactError(string code, int status)
    {
        var world = await SeedAsync(topic: Topic.TrueCrime, audit: Audit(Clean, stale: true));
        var error = status == 409
            ? Error.Conflict(code, "injected stage 3–6 failure")
            : Error.Validation(code, "injected stage 3–6 failure");
        var gate = new RecordingGate(error);
        var moderator = new FakeContentModerator();
        using var factory = Factory(moderator, gate);
        using var client = factory.ClientFor(world.UserId);
        using var response = await ApproveAsync(client, world);
        await AssertProblemAsync(response, status, code);
        gate.Calls.ShouldBe(1);
        gate.VersionId.ShouldBe(world.VersionId);
        moderator.Calls.ShouldBe(0);
        AssertNothingApproved(world);
    }

    [Theory]
    [InlineData(Topic.Ghost)]
    [InlineData(Topic.TrueCrime)]
    public async Task ApproveStory_AuditFailure_RollsBackStatusTimestampRefreshAndOutbox(Topic topic)
    {
        var report = Audit(Clean, stale: true);
        var world = await SeedAsync(topic: topic, audit: report);
        using var factory = Factory(gate: new RecordingGate(acceptedTextVersion: "ta.v1")).WithServices(services =>
        {
            services.RemoveAll<IAuditWriter>();
            services.AddSingleton<IAuditWriter>(new ThrowingAuditWriter());
        });
        using var client = factory.ClientFor(world.UserId);
        using var response = await ApproveAsync(client, world);
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        AssertNothingApproved(world);
        JsonNode.DeepEquals(StoredAudit(world), JsonNode.Parse(report)).ShouldBeTrue();
    }

    [Fact]
    public async Task ApproveStory_CallerCancellation_PropagatesAndLeavesNoApproval()
    {
        var world = await SeedAsync(audit: Audit(Clean, stale: true));
        var moderator = new CancellingModerator();
        using var factory = Factory(moderator);
        using var client = factory.ClientFor(world.UserId);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pending = PostAsync(client, world.EpisodeId, StoryEtag.For(world.VersionId), ct: cancellation.Token);
        await moderator.Started.Task.WaitAsync(Ct);
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => { using var response = await pending; });
        await moderator.Cancelled.Task.WaitAsync(Ct);
        // A second request takes the same row lock: once it returns the cancelled transaction has released it.
        using var invalid = await PostAsync(client, world.EpisodeId, null, ct: Ct);
        await AssertProblemAsync(invalid, 428, "if_match_required");
        AssertNothingApproved(world);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApproveStory_AbsentFullReport_RechecksOnceAndFailsClosedOnOutage(bool unavailable)
    {
        var world = await SeedAsync(absentReport: true);
        var moderator = new FakeContentModerator();
        if (unavailable) moderator.EnqueueUnavailable(ModerationStage.Draft);
        else moderator.EnqueueClean(ModerationStage.Draft);
        using var factory = Factory(moderator);
        using var client = factory.ClientFor(world.UserId);
        using var response = await ApproveAsync(client, world);
        moderator.Calls.ShouldBe(1);
        if (unavailable)
        {
            await AssertProblemAsync(response, 409, "story_moderated");
            AssertNothingApproved(world);
            SqlProbe.Count(Database, $"SELECT count(*) FROM story_versions WHERE id='{world.VersionId}' AND audit_report IS NULL").ShouldBe(1);
        }
        else
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            var report = StoredAudit(world);
            report["schema_version"]!.GetValue<string>().ShouldBe("audit.v1");
            JsonNode.DeepEquals(report["moderation"], JsonNode.Parse(Clean)).ShouldBeTrue();
            AssertApproved(world);
        }
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"not a report\"")]
    [InlineData("null")]
    public async Task ApproveStory_MalformedWholeReport_RechecksAndBuildsSafeAuditV1(string audit)
    {
        var world = await SeedAsync(audit: audit);
        var moderator = new FakeContentModerator().EnqueueClean(ModerationStage.Draft);
        using var factory = Factory(moderator);
        using var client = factory.ClientFor(world.UserId);
        using var response = await ApproveAsync(client, world);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        moderator.Calls.ShouldBe(1);
        StoredAudit(world)["schema_version"]!.GetValue<string>().ShouldBe("audit.v1");
        JsonNode.DeepEquals(StoredAudit(world)["moderation"], JsonNode.Parse(Clean)).ShouldBeTrue();
    }

    [Fact]
    public async Task ApproveStory_RefreshFlagWithoutCategories_FailsClosed()
    {
        var world = await SeedAsync(audit: Audit(Clean, stale: true));
        var moderator = new FakeContentModerator().EnqueueFlagged(ModerationStage.Draft);
        using var factory = Factory(moderator);
        using var client = factory.ClientFor(world.UserId);
        using var response = await ApproveAsync(client, world);
        await AssertProblemAsync(response, 409, "story_moderated");
        moderator.Calls.ShouldBe(1);
        AssertNothingApproved(world);
    }

    [Fact]
    public async Task ApproveStory_ConcurrentTabs_CreateOneTransitionAuditAndEvent()
    {
        var world = await SeedAsync();
        var gate = new RecordingGate();
        using var factory = Factory(gate: gate);
        using var firstClient = factory.ClientFor(world.UserId);
        using var secondClient = factory.ClientFor(world.UserId);
        var responses = await Task.WhenAll(ApproveAsync(firstClient, world), ApproveAsync(secondClient, world));
        using var first = responses[0];
        using var second = responses[1];
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK, "the serialized second request is the same-version idempotent repeat");
        AuditCount(world).ShouldBe(1);
        OutboxCount(world).ShouldBe(1);
        gate.Calls.ShouldBe(1);
        AssertApproved(world);
    }

    [Fact]
    public async Task ApproveStory_AutopilotSource_RaisesSameEventWithAutopilot()
    {
        var world = await SeedAsync();
        using var factory = Factory().WithServices(services =>
        {
            services.RemoveAll<ICurrentUser>();
            services.AddSingleton<ICurrentUser>(new TestCurrentUser { Id = world.UserId });
        });
        using var client = factory.ClientFor(world.UserId);
        await using var scope = factory.Services.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var response = await mediator.Send(new ApproveStoryCommand(world.EpisodeId, StoryEtag.For(world.VersionId), null, ApprovalSource.Autopilot), Ct);
        response.IsError.ShouldBeFalse();
        AssertApproved(world);
        AuditCount(world).ShouldBe(1);
        OutboxCount(world).ShouldBe(1);
        SqlProbe.ScalarText(Database, $"SELECT data->>'approval_source' FROM audit_logs WHERE action='{Action}' AND subject_user_id='{world.UserId}'")
            .ShouldBe("autopilot");
        using var payload = JsonDocument.Parse(SqlProbe.ScalarText(Database,
            $"SELECT payload::text FROM outbox_messages WHERE type='story_approved.v1' AND user_id='{world.UserId}'"));
        payload.RootElement.GetProperty("approvalSource").GetString().ShouldBe("autopilot");
    }

    private ApiFactory Factory(IContentModerator? moderator = null, IStoryApprovalGate? gate = null, Guid? actorId = null) =>
        new ApiFactory(Database, new FakeClock(Start)).WithServices(services =>
        {
            services.RemoveAll<IContentModerator>();
            services.AddSingleton<IContentModerator>(moderator ?? new FakeContentModerator());
            services.RemoveAll<IStoryApprovalGate>();
            services.AddSingleton<IStoryApprovalGate>(gate ?? new RecordingGate());
            if (actorId is { } userId)
            {
                services.RemoveAll<ICurrentUser>();
                services.AddSingleton<ICurrentUser>(new TestCurrentUser { Id = userId });
            }
        });

    private async Task<World> SeedAsync(EpisodeStatus status = EpisodeStatus.StoryReview, Topic topic = Topic.Ghost, string? audit = null, bool absentReport = false)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, topic: topic, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, lengthTier: LengthTier.Short, status: status, ct: Ct);
        var draft = Draft();
        var version = StoryVersion.Create(user.Id, episode.Id, 1, StoryVersionSource.Generated, draft,
            StoryContentJson.Write(draft, new JsonArray()), "model-x", "rel=1", null, null, null, absentReport ? null : audit ?? Audit(Clean), null, Start);
        await using (var db = Database.CreateAppDbContext(new TestCurrentUser { Id = user.Id }))
        {
            db.StoryVersions.Add(version);
            await db.SaveChangesAsync(Ct);
        }
        SqlProbe.Execute(Database, $"UPDATE episodes SET current_story_version_id='{version.Id}' WHERE id='{episode.Id}'");
        return new World(user.Id, episode.Id, version.Id, series.Id);
    }

    private void AssertApproved(World world)
    {
        SqlProbe.ScalarText(Database, $"SELECT status || '|' || approved_story_version_id::text FROM episodes WHERE id='{world.EpisodeId}'")
            .ShouldBe($"story_approved|{world.VersionId}");
        SqlProbe.Count(Database, $"SELECT count(*) FROM episodes WHERE id='{world.EpisodeId}' AND approved_at IS NOT NULL").ShouldBe(1);
    }

    private void AssertNothingApproved(World world)
    {
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id='{world.EpisodeId}'").ShouldBe("story_review");
        SqlProbe.Count(Database, $"SELECT count(*) FROM episodes WHERE id='{world.EpisodeId}' AND (approved_story_version_id IS NOT NULL OR approved_at IS NOT NULL)").ShouldBe(0);
        AuditCount(world).ShouldBe(0);
        OutboxCount(world).ShouldBe(0);
        AttestationCount(world).ShouldBe(0);
    }

    private long AuditCount(World world) => SqlProbe.Count(Database, $"SELECT count(*) FROM audit_logs WHERE action='{Action}' AND subject_user_id='{world.UserId}'");
    private long AttestationCount(World world) => SqlProbe.Count(Database, $"SELECT count(*) FROM audit_logs WHERE action='episode.true_crime_attested' AND subject_user_id='{world.UserId}'");
    private long OutboxCount(World world) => SqlProbe.Count(Database, $"SELECT count(*) FROM outbox_messages WHERE type='story_approved.v1' AND user_id='{world.UserId}'");
    private JsonObject StoredAudit(World world) => JsonNode.Parse(SqlProbe.ScalarText(Database, $"SELECT audit_report::text FROM story_versions WHERE id='{world.VersionId}'"))!.AsObject();

    private static string Audit(string moderation, bool stale = false)
    {
        var report = JsonNode.Parse($$$"""{"schema_version":"audit.v1","local":{"issues":[]},"moderation":{{{moderation}}},"model_review":{"scores":{"overall":7.5},"stale":true},"revision":{"rounds":2},"similarity":{"score":null,"n":0},"unknown_future_key":{"keep":true}}""")!.AsObject();
        if (stale) report["moderation"]!["stale"] = true;
        return report.ToJsonString();
    }

    private static StoryContentV1 Draft() => new(
        "The house by the river",
        new StorySegmentV1("The night was quiet.", ["house by the river", "quiet night"], []),
        [new StorySegmentV1("A knock woke the narrator.", ["wooden door"], [], "setup")],
        new StorySegmentV1("Nobody stood outside.", ["empty path"], []),
        "A quiet ghost story.", ["ghost", "night"], ["supernatural"],
        new StoryStructureV1("ghost", "old house", "a knock", "the narrator", "first_person"));

    private static Task<HttpResponseMessage> ApproveAsync(HttpClient client, World world, string? body = null) =>
        PostAsync(client, world.EpisodeId, StoryEtag.For(world.VersionId), body);

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, Guid episodeId, string? tag, string? body = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/episodes/{episodeId}/story/approve");
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (tag is not null) request.Headers.TryAddWithoutValidation("If-Match", tag);
        return await client.SendAsync(request, ct == default ? Ct : ct);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.Clone();
    }

    private static async Task<ProblemResponse> ProblemAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<ProblemResponse>(await response.Content.ReadAsStringAsync(Ct), ProblemResponse.Options)!;

    private static async Task AssertProblemAsync(HttpResponseMessage response, int status, string code)
    {
        response.StatusCode.ShouldBe((HttpStatusCode)status, await response.Content.ReadAsStringAsync(Ct));
        (await ProblemAsync(response)).Code.ShouldBe(code);
    }

    private sealed record World(Guid UserId, Guid EpisodeId, Guid VersionId, Guid SeriesId);

    private sealed class RecordingGate(Error? error = null, string? acceptedTextVersion = null) : IStoryApprovalGate
    {
        public int Calls { get; private set; }
        public Guid? VersionId { get; private set; }
        public AttestationInput? Attestation { get; private set; }
        public ValueTask<ErrorOr<StoryApprovalGateResult>> ValidateAsync(Episode episode, StorySeries series, StoryVersion version, AttestationInput? attestation, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            VersionId = version.Id;
            Attestation = attestation;
            return ValueTask.FromResult<ErrorOr<StoryApprovalGateResult>>(error is { } failure ? failure : new StoryApprovalGateResult(acceptedTextVersion));
        }
    }

    private sealed class ThrowingAuditWriter : IAuditWriter
    {
        public Task WriteAsync(AuditEntry entry, CancellationToken ct) => throw new InvalidOperationException("Injected approval audit failure.");
    }

    private sealed class CancellingModerator : IContentModerator
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<ModerationResult> CheckAsync(IReadOnlyList<string> texts, ModerationStage stage, CancellationToken ct)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("Cancellation was not propagated.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
    }
}
