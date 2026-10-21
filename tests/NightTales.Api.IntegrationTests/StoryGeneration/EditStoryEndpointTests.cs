using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Features.Stories;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Text;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.StoryGeneration;

/// <summary>
/// <c>PUT /api/v1/episodes/{id}/story</c> against the real host and a real PostgreSQL 17 (02.3.1-T1): the handler's
/// outcomes (the task's <c>EditStory_*</c> list needs the row lock, the tenant filter and the audit row, so they run
/// through the endpoint, as 02.2.2-T2's tests do), the problem bodies, and the rows a save does and does not write.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EditStoryEndpointTests(TestDatabase database) : IntegrationTest(database)
{
    private const string SavedAction = "episode.story_version_saved";
    private const int ValidBodyTokens = 220;

    private static readonly DateTimeOffset Start = new(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);

    private static readonly string Scores = """{"overall":7.5,"hook":8}""";

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task EditStory_ValidEditOnReview_CreatesEditedVersionAndAdvancesEtag()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PutAsync(client, world.EpisodeId, Edited(), StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await ReadBodyAsync(response);
        var newId = body.GetProperty("versionId").GetGuid();
        newId.ShouldNotBe(world.VersionId);
        body.GetProperty("version").GetInt32().ShouldBe(2);
        body.GetProperty("source").GetString().ShouldBe("edited");
        body.GetProperty("parentVersionId").GetGuid().ShouldBe(world.VersionId);
        body.GetProperty("title").GetString().ShouldBe(Edited().Title);
        body.GetProperty("etag").GetString().ShouldBe(StoryEtag.For(newId));
        response.Headers.ETag!.Tag.ShouldBe(StoryEtag.For(newId));

        SqlProbe.QueryStrings(
                Database,
                "SELECT version::text || '|' || source || '|' || coalesce(parent_version_id::text, 'null') "
                + $"FROM story_versions WHERE episode_id = '{world.EpisodeId}' ORDER BY version")
            .ShouldBe(["1|generated|null", $"2|edited|{world.VersionId}"]);
        SqlProbe.ScalarText(Database, $"SELECT current_story_version_id::text FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe(newId.ToString());
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("story_review");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = 'story_version_saved.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(1);
    }

    [Fact]
    public async Task EditStory_StaleEtag_Returns412VersionConflict()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var first = await PutAsync(client, world.EpisodeId, Edited(), StoryEtag.For(world.VersionId));
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var audits = AuditCount(world);

        using var replay = await PutAsync(client, world.EpisodeId, Edited("Một tựa khác"), StoryEtag.For(world.VersionId));

        replay.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await ReadProblemAsync(replay)).Code.ShouldBe("story_version_conflict");
        VersionCount(world).ShouldBe(2);
        AuditCount(world).ShouldBe(audits, "a refused edit writes no audit row");
    }

    [Fact]
    public async Task EditStory_MissingIfMatch_Returns428()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PutAsync(client, world.EpisodeId, Edited(), ifMatch: null);

        response.StatusCode.ShouldBe((HttpStatusCode)428, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("if_match_required");
        VersionCount(world).ShouldBe(1);
        AuditCount(world).ShouldBe(0);
    }

    [Theory]
    [InlineData("W/\"sv-{0}\"")]
    [InlineData("{0}")]
    [InlineData("\"sv-{0}")]
    [InlineData("\"ep-12345\"")]
    [InlineData("\"sv-not-a-guid\"")]
    public async Task EditStory_MalformedIfMatch_Returns412(string template)
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PutAsync(
            client,
            world.EpisodeId,
            Edited(),
            string.Format(System.Globalization.CultureInfo.InvariantCulture, template, world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("story_version_conflict");
        VersionCount(world).ShouldBe(1);
    }

    [Fact]
    public async Task EditStory_IdenticalContent_DoesNotCreateVersion_AndKeepsEtag()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PutAsync(client, world.EpisodeId, ValidDraft(), StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        response.Headers.ETag!.Tag.ShouldBe(StoryEtag.For(world.VersionId));
        (await ReadBodyAsync(response)).GetProperty("versionId").GetGuid().ShouldBe(world.VersionId);
        AssertNothingSaved(world);
        factory.Moderator.Calls.ShouldBe(0, "no-op edits never moderate");
    }

    [Fact]
    public async Task EditStory_WhitespaceOnlyDifference_IsTreatedAsNoOp()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var draft = ValidDraft();
        var padded = draft with
        {
            Title = draft.Title + "  ",
            Hook = draft.Hook with { Text = draft.Hook.Text + "\r\n" },
            Tags = [.. draft.Tags.Select(tag => " " + tag)],
        };

        using var response = await PutAsync(client, world.EpisodeId, padded, StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        response.Headers.ETag!.Tag.ShouldBe(StoryEtag.For(world.VersionId));
        AssertNothingSaved(world);
        factory.Moderator.Calls.ShouldBe(0, "canonical no-op edits never moderate");
    }

    [Fact]
    public async Task EditStory_ReorderedKeywords_IsNotANoOp()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var draft = ValidDraft();
        var reordered = draft with { Hook = draft.Hook with { Keywords = [.. draft.Hook.Keywords.Reverse()] } };

        using var response = await PutAsync(client, world.EpisodeId, reordered, StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        response.Headers.ETag!.Tag.ShouldNotBe(StoryEtag.For(world.VersionId));
        VersionCount(world).ShouldBe(2);
    }

    [Theory]
    [InlineData(EpisodeStatus.StoryApproved)]
    [InlineData(EpisodeStatus.StoryGenerating)]
    [InlineData(EpisodeStatus.Rendering)]
    public async Task EditStory_NotEditableEpisode_Returns409InvalidState(EpisodeStatus status)
    {
        var world = await SeedAsync(status);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PutAsync(client, world.EpisodeId, Edited(), StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("episode_invalid_state");
        VersionCount(world).ShouldBe(1);
        AuditCount(world).ShouldBe(0);
    }

    [Fact]
    public async Task EditStory_InvalidContent_Returns400WithFieldErrors()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var draft = ValidDraft();
        var paragraphs = draft.Paragraphs.ToArray();
        paragraphs[0] = paragraphs[0] with { Keywords = ["old house by the river", "rain on the window", "figure at the door"] };

        using var response = await PutAsync(
            client,
            world.EpisodeId,
            draft with { Paragraphs = paragraphs },
            StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        var problem = await ReadProblemAsync(response);
        problem.Code.ShouldBe("story_invalid_content");
        problem.Errors.ShouldNotBeNull();
        problem.Errors.ShouldNotBeEmpty();
        problem.Errors.ShouldContainKey("p:0");
        problem.Errors["p:0"].ShouldContain(message => message.StartsWith("keyword_count_out_of_range:", StringComparison.Ordinal));
        VersionCount(world).ShouldBe(1);
        AuditCount(world).ShouldBe(0);
    }

    [Fact]
    public async Task EditStory_BodyIsNotAStoryDocument_Returns400StoryInvalidContent()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PutRawAsync(client, world.EpisodeId, """{"title":"only a title"}""", StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        var problem = await ReadProblemAsync(response);
        problem.Code.ShouldBe("story_invalid_content");
        problem.Errors.ShouldNotBeNull();
        problem.Errors.ShouldContainKey("body");
        VersionCount(world).ShouldBe(1);
    }

    [Fact]
    public async Task EditStory_SetsModelReviewStale_AndKeepsPreviousScores()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PutAsync(client, world.EpisodeId, Edited(), StoryEtag.For(world.VersionId));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var newId = (await ReadBodyAsync(response)).GetProperty("versionId").GetGuid();

        SqlProbe.ScalarText(Database, $"SELECT audit_report->'model_review'->>'stale' FROM story_versions WHERE id = '{newId}'")
            .ShouldBe("true");
        JsonNode.DeepEquals(
                JsonNode.Parse(SqlProbe.ScalarText(Database, $"SELECT audit_report->'model_review'->'scores' FROM story_versions WHERE id = '{newId}'")),
                JsonNode.Parse(Scores))
            .ShouldBeTrue("the previous scores are kept (F25)");
        SqlProbe.ScalarText(Database, $"SELECT audit_report->'local'->'issues' IS NOT NULL FROM story_versions WHERE id = '{newId}'")
            .ShouldBe("True", "the local issues are recomputed for the new text");
        SqlProbe.ScalarText(Database, $"SELECT audit_report->'model_review'->>'stale' FROM story_versions WHERE id = '{world.VersionId}'")
            .ShouldBe("false", "the previous version is immutable");
    }

    [Fact]
    public async Task EditStory_ModerationUnavailable_KeepsPreviousVerdictMarkedStale()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        factory.Moderator.EnqueueUnavailable(ModerationStage.Draft);
        using var client = factory.ClientFor(world.UserId);

        using var response = await PutAsync(client, world.EpisodeId, Edited(), StoryEtag.For(world.VersionId));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var newId = (await ReadBodyAsync(response)).GetProperty("versionId").GetGuid();

        SqlProbe.ScalarText(Database, $"SELECT audit_report->'moderation'->>'stale' FROM story_versions WHERE id = '{newId}'")
            .ShouldBe("true");
        SqlProbe.ScalarText(Database, $"SELECT audit_report->'moderation'->>'flagged' FROM story_versions WHERE id = '{newId}'")
            .ShouldBe("false", "the kept verdict is not rewritten");
        SqlProbe.ScalarText(Database, $"SELECT audit_report->'moderation'->>'stale' FROM story_versions WHERE id = '{world.VersionId}'")
            .ShouldBe("false", "the previous version keeps its own verdict");
        factory.Moderator.Calls.ShouldBe(1);
    }

    /// <summary>
    /// NTS-EDIT-L1: a previous version with no readable <c>audit_report</c> (SQL NULL or a JSON value that is not an object)
    /// has no verdict to carry. The new version gets the fail-closed marker, never a missing key that would read as clean.
    /// </summary>
    [Theory]
    [InlineData("NULL")]
    [InlineData("'[]'::jsonb")]
    [InlineData("'\"unreadable\"'::jsonb")]
    public async Task EditStory_PreviousAuditReportMissingOrUnreadable_WritesFailClosedModerationMarker(string previousAudit)
    {
        var world = await SeedAsync();
        SqlProbe.Execute(Database, $"UPDATE story_versions SET audit_report = {previousAudit} WHERE id = '{world.VersionId}'");
        using var factory = Factory();
        factory.Moderator.EnqueueUnavailable(ModerationStage.Draft).EnqueueUnavailable(ModerationStage.Draft);
        using var client = factory.ClientFor(world.UserId);

        using var response = await PutAsync(client, world.EpisodeId, Edited(), StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await ReadBodyAsync(response);
        var newId = body.GetProperty("versionId").GetGuid();
        JsonNode.DeepEquals(
                JsonNode.Parse(SqlProbe.ScalarText(Database, $"SELECT audit_report->'moderation' FROM story_versions WHERE id = '{newId}'")),
                JsonNode.Parse("""{"stale":true,"unavailable":true}"""))
            .ShouldBeTrue("the marker is exactly { stale: true, unavailable: true }");
        var hint = body.GetProperty("qualityHints").EnumerateArray()
            .Single(found => found.GetProperty("ruleId").GetString() == "Q-MOD-01");
        hint.GetProperty("severity").GetString().ShouldBe("blocking", "an unavailable verdict counts as not moderated");

        // The marker survives the next edit: an unavailable verdict stays unavailable until a moderator has re-checked it.
        using var second = await PutAsync(client, world.EpisodeId, Edited("Một tựa khác"), response.Headers.ETag!.Tag);
        second.StatusCode.ShouldBe(HttpStatusCode.OK, await second.Content.ReadAsStringAsync(Ct));
        var secondBody = await ReadBodyAsync(second);
        var secondId = secondBody.GetProperty("versionId").GetGuid();
        SqlProbe.ScalarText(Database, $"SELECT audit_report->'moderation'->>'unavailable' FROM story_versions WHERE id = '{secondId}'")
            .ShouldBe("true");
        secondBody.GetProperty("qualityHints").EnumerateArray()
            .Select(found => found.GetProperty("ruleId").GetString())
            .ShouldContain("Q-MOD-01");
    }

    [Fact]
    public async Task EditStory_NoOpOnPreviousVersionWithoutAuditReport_StillReportsModerationAsBlocking()
    {
        var world = await SeedAsync();
        SqlProbe.Execute(Database, $"UPDATE story_versions SET audit_report = NULL WHERE id = '{world.VersionId}'");
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PutAsync(client, world.EpisodeId, ValidDraft(), StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await ReadBodyAsync(response)).GetProperty("qualityHints").EnumerateArray()
            .Select(found => found.GetProperty("ruleId").GetString())
            .ShouldContain("Q-MOD-01");
        AssertNothingSaved(world);
        factory.Moderator.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task EditStory_FreshCleanModerationVerdict_RaisesNoModerationHint()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PutAsync(client, world.EpisodeId, Edited(), StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await ReadBodyAsync(response);
        body.GetProperty("qualityHints").EnumerateArray()
            .Select(found => found.GetProperty("ruleId").GetString())
            .ShouldNotContain("Q-MOD-01", "a fresh clean verdict replaces the previous moderation");
        var newId = body.GetProperty("versionId").GetGuid();
        JsonNode.DeepEquals(
            JsonNode.Parse(SqlProbe.ScalarText(Database, $"SELECT audit_report->'moderation' FROM story_versions WHERE id = '{newId}'")),
            JsonNode.Parse("""{"flagged":false,"categories":[],"blocking":[],"stale":false,"unavailable":false}""")).ShouldBeTrue();
        factory.Moderator.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task EditStory_StaleCarriedModerationVerdict_StillBlocksUntilRechecked()
    {
        var world = await SeedAsync(audit: AuditJson(moderation: """{"flagged":true,"categories":["sexual/minors"],"stale":false}"""));
        using var factory = Factory();
        factory.Moderator.EnqueueUnavailable(ModerationStage.Draft);
        using var client = factory.ClientFor(world.UserId);

        using var response = await PutAsync(client, world.EpisodeId, Edited(), StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await ReadBodyAsync(response);
        body.GetProperty("qualityHints").EnumerateArray()
            .Select(hint => hint.GetProperty("ruleId").GetString())
            .ShouldContain("Q-MOD-01", "a stale verdict is not a pass: its blocking categories keep applying");
    }

    [Fact]
    public async Task EditStory_FreshBlockingModeration_WritesVerdictHintAndEvidence()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        factory.Moderator.EnqueueFlagged(ModerationStage.Draft, "sexual/minors");
        using var client = factory.ClientFor(world.UserId);

        using var response = await PutAsync(client, world.EpisodeId, Edited(), StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await ReadBodyAsync(response);
        var newId = body.GetProperty("versionId").GetGuid();
        body.GetProperty("qualityHints").EnumerateArray()
            .Single(hint => hint.GetProperty("ruleId").GetString() == "Q-MOD-01")
            .GetProperty("severity").GetString().ShouldBe("blocking");
        JsonNode.DeepEquals(
            JsonNode.Parse(SqlProbe.ScalarText(Database, $"SELECT audit_report->'moderation' FROM story_versions WHERE id = '{newId}'")),
            JsonNode.Parse("""{"flagged":true,"categories":["sexual/minors"],"blocking":["sexual/minors"],"stale":false,"unavailable":false}""")).ShouldBeTrue();
        SqlProbe.Count(Database, $"SELECT count(*) FROM moderation_events WHERE user_id = '{world.UserId}' AND episode_id = '{world.EpisodeId}'")
            .ShouldBe(1);
        factory.Moderator.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task EditStory_FreshCleanModeration_ReplacesPreviousBlockingVerdict()
    {
        var world = await SeedAsync(audit: AuditJson(moderation: """{"flagged":true,"categories":["sexual/minors"],"stale":true}"""));
        using var factory = Factory();
        factory.Moderator.EnqueueClean(ModerationStage.Draft);
        using var client = factory.ClientFor(world.UserId);

        using var response = await PutAsync(client, world.EpisodeId, Edited(), StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await ReadBodyAsync(response);
        body.GetProperty("qualityHints").EnumerateArray()
            .Select(hint => hint.GetProperty("ruleId").GetString()).ShouldNotContain("Q-MOD-01");
        var newId = body.GetProperty("versionId").GetGuid();
        SqlProbe.ScalarText(Database, $"SELECT audit_report->'moderation'->>'flagged' FROM story_versions WHERE id = '{newId}'")
            .ShouldBe("false");
        SqlProbe.ScalarText(Database, $"SELECT audit_report->'moderation'->>'stale' FROM story_versions WHERE id = '{newId}'")
            .ShouldBe("false");
        factory.Moderator.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task EditStory_FreshAdvisoryModeration_KeepsCategoryWithoutBlockingApproval()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        factory.Moderator.EnqueueFlagged(ModerationStage.Draft, "violence");
        using var client = factory.ClientFor(world.UserId);

        using var response = await PutAsync(client, world.EpisodeId, Edited(), StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await ReadBodyAsync(response);
        body.GetProperty("qualityHints").EnumerateArray()
            .Select(hint => hint.GetProperty("ruleId").GetString())
            .ShouldNotContain("Q-MOD-01", "D12 advisory categories are stored but do not create a blocking moderation hint");
        var newId = body.GetProperty("versionId").GetGuid();
        SqlProbe.ScalarText(Database, $"SELECT audit_report->'moderation'->'categories'->>0 FROM story_versions WHERE id = '{newId}'")
            .ShouldBe("violence");
        SqlProbe.Count(Database, $"SELECT jsonb_array_length(audit_report->'moderation'->'blocking') FROM story_versions WHERE id = '{newId}'")
            .ShouldBe(0);
        factory.Moderator.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task EditStory_MakesNoTextCall()
    {
        var world = await SeedAsync();
        var fake = new FakeTextClient();
        using var factory = Factory(fake);
        using var client = factory.ClientFor(world.UserId);

        using var response = await PutAsync(client, world.EpisodeId, Edited(), StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        fake.Requests.ShouldBeEmpty("an edit makes no paid call (D20)");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM usage_records WHERE user_id = '{world.UserId}' "
                + "AND operation IN ('story', 'story_review', 'story_revision')")
            .ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM usage_records WHERE user_id = '{world.UserId}'").ShouldBe(0);
    }

    [Fact]
    public async Task EditStory_RecomputesLocalIssuesAndSimilarity()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PutAsync(client, world.EpisodeId, Edited(), StoryEtag.For(world.VersionId));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var newId = (await ReadBodyAsync(response)).GetProperty("versionId").GetGuid();

        SqlProbe.ScalarText(Database, $"SELECT audit_report->'similarity'->>'n' FROM story_versions WHERE id = '{newId}'")
            .ShouldBe("0");
        SqlProbe.ScalarText(Database, $"SELECT coalesce(audit_report->'similarity'->>'score', 'null') FROM story_versions WHERE id = '{newId}'")
            .ShouldBe("null");
        SqlProbe.ScalarText(Database, $"SELECT coalesce(similarity_score::text, 'null') FROM story_versions WHERE id = '{newId}'")
            .ShouldBe("null");
        SqlProbe.ScalarText(Database, $"SELECT audit_report->>'schema_version' FROM story_versions WHERE id = '{newId}'")
            .ShouldBe("audit.v1");
        SqlProbe.ScalarText(Database, $"SELECT audit_report->'revision'->>'rounds' FROM story_versions WHERE id = '{newId}'")
            .ShouldBe("1", "the revision block is carried over");
    }

    [Fact]
    public async Task EditStory_TrueCrime_KeepsFactSheetIdFromPreviousVersion()
    {
        var world = await SeedAsync(withFactSheet: true);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PutAsync(client, world.EpisodeId, Edited(), StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await ReadBodyAsync(response);
        body.GetProperty("factSheetId").GetGuid().ShouldBe(world.FactSheetId!.Value);
        SqlProbe.ScalarText(
                Database,
                $"SELECT fact_sheet_id::text FROM story_versions WHERE id = '{body.GetProperty("versionId").GetGuid()}'")
            .ShouldBe(world.FactSheetId!.Value.ToString());
    }

    [Fact]
    public async Task EditStory_ClientSentFactSources_AreNeverTrusted_PreviousSourcesAreCarriedOver()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var node = ToNode(Edited());
        node["fact_sources"] = new JsonArray(new JsonObject
        {
            ["source_id"] = Guid.CreateVersion7().ToString(),
            ["title"] = "Injected",
            ["url"] = "https://evil.example/injected",
        });

        using var response = await PutAsync(client, world.EpisodeId, node, StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var newId = (await ReadBodyAsync(response)).GetProperty("versionId").GetGuid();
        var stored = SqlProbe.ScalarText(Database, $"SELECT content->'fact_sources' FROM story_versions WHERE id = '{newId}'");
        stored.ShouldNotContain("evil.example");
        stored.ShouldContain("https://example.test/source-a", Case.Sensitive, "the previous version's sources are carried over");
    }

    [Fact]
    public async Task EditStory_AuditDataContainsNoStoryText()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PutAsync(client, world.EpisodeId, Edited(), StoryEtag.For(world.VersionId));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var newId = (await ReadBodyAsync(response)).GetProperty("versionId").GetGuid();

        AuditCount(world).ShouldBe(1);
        SqlProbe.ScalarText(
                Database,
                "SELECT string_agg(key, ',' ORDER BY key) FROM audit_logs, jsonb_object_keys(data) AS key "
                + $"WHERE action = '{SavedAction}' AND subject_user_id = '{world.UserId}'")
            .ShouldBe("episodeId,version,versionId");
        SqlProbe.ScalarText(
                Database,
                $"SELECT data->>'versionId' || '|' || (data->>'version') || '|' || (data->>'episodeId') FROM audit_logs "
                + $"WHERE action = '{SavedAction}' AND subject_user_id = '{world.UserId}'")
            .ShouldBe($"{newId}|2|{world.EpisodeId}");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM audit_logs WHERE action = '{SavedAction}' AND subject_user_id = '{world.UserId}' "
                + $"AND (position('{Edited().Title}' in data::text) > 0 OR position('{ValidDraft().Hook.Text}' in data::text) > 0)")
            .ShouldBe(0);
    }

    [Fact]
    public async Task EditStory_ConcurrentSecondEditWithSameEtag_SecondReturns412()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var clientA = factory.ClientFor(world.UserId);
        using var clientB = factory.ClientFor(world.UserId);
        var etag = StoryEtag.For(world.VersionId);

        var calls = await Task.WhenAll(
            PutAsync(clientA, world.EpisodeId, Edited("Tựa của tab một"), etag),
            PutAsync(clientB, world.EpisodeId, Edited("Tựa của tab hai"), etag));
        using var _ = calls[0];
        using var __ = calls[1];

        calls.Count(call => call.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        var loser = calls.Single(call => call.StatusCode != HttpStatusCode.OK);
        loser.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await ReadProblemAsync(loser)).Code.ShouldBe("story_version_conflict");
        VersionCount(world).ShouldBe(2);
        AuditCount(world).ShouldBe(1);
    }

    [Fact]
    public async Task ForeignEpisode_EditReturns404()
    {
        var owner = await SeedAsync();
        var stranger = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(stranger.UserId);

        using var response = await PutAsync(client, owner.EpisodeId, Edited(), StoryEtag.For(owner.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync(Ct));
        VersionCount(owner).ShouldBe(1);
        AuditCount(owner).ShouldBe(0);
    }

    [Fact]
    public async Task EditStory_Anonymous_Returns401()
    {
        using var factory = Factory();
        using var client = factory.AnonymousClient();

        using var response = await PutAsync(client, Guid.CreateVersion7(), Edited(), StoryEtag.For(Guid.CreateVersion7()));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EditStory_ThenGetEpisodeDetail_CurrentStoryReflectsTheEdit()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var before = await client.GetAsync($"/api/v1/episodes/{world.EpisodeId}", Ct);
        before.StatusCode.ShouldBe(HttpStatusCode.OK, await before.Content.ReadAsStringAsync(Ct));

        using var put = await PutAsync(client, world.EpisodeId, Edited(), StoryEtag.For(world.VersionId));
        put.StatusCode.ShouldBe(HttpStatusCode.OK, await put.Content.ReadAsStringAsync(Ct));
        var newId = (await ReadBodyAsync(put)).GetProperty("versionId").GetGuid();

        using var after = await client.GetAsync($"/api/v1/episodes/{world.EpisodeId}", Ct);
        after.StatusCode.ShouldBe(HttpStatusCode.OK, await after.Content.ReadAsStringAsync(Ct));
        after.Headers.ETag!.Tag.ShouldNotBe(before.Headers.ETag!.Tag, "the edit moved the episode row");
        var detail = await ReadBodyAsync(after);
        detail.GetProperty("status").GetString().ShouldBe("story_review");
        detail.GetProperty("currentStory").GetProperty("versionId").GetGuid().ShouldBe(newId);
        detail.GetProperty("currentStory").GetProperty("etag").GetString().ShouldBe(StoryEtag.For(newId));
        SqlProbe.ScalarText(Database, $"SELECT current_story_version_id::text FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe(newId.ToString());
    }

    private ApiFactory Factory(FakeTextClient? text = null) =>
        new ApiFactory(Database, new FakeClock(Start))
            .WithServices(services =>
            {
                services.RemoveAll<ITextClient>();
                services.AddSingleton<ITextClient>(text ?? new FakeTextClient());
            });

    private void AssertNothingSaved(World world)
    {
        VersionCount(world).ShouldBe(1);
        AuditCount(world).ShouldBe(0, "a no-op writes no audit row");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = 'story_version_saved.v1' AND user_id = '{world.UserId}'")
            .ShouldBe(0);
        SqlProbe.ScalarText(Database, $"SELECT current_story_version_id::text FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe(world.VersionId.ToString());
    }

    private long VersionCount(World world) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM story_versions WHERE episode_id = '{world.EpisodeId}'");

    private long AuditCount(World world) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM audit_logs WHERE action = '{SavedAction}' AND subject_user_id = '{world.UserId}'");

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid episodeId, StoryContentV1 content, string? ifMatch) =>
        PutAsync(client, episodeId, ToNode(content), ifMatch);

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid episodeId, JsonNode body, string? ifMatch) =>
        PutRawAsync(client, episodeId, body.ToJsonString(), ifMatch);

    private static Task<HttpResponseMessage> PutRawAsync(HttpClient client, Guid episodeId, string json, string? ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/episodes/{episodeId}/story")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return client.SendAsync(request, Ct);
    }

    private static JsonObject ToNode(StoryContentV1 content) =>
        JsonSerializer.SerializeToNode(content, StoryContentJson.SerializerOptions)!.AsObject();

    private static async Task<JsonElement> ReadBodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private static async Task<ProblemResponse> ReadProblemAsync(HttpResponseMessage response)
    {
        var problem = JsonSerializer.Deserialize<ProblemResponse>(await response.Content.ReadAsStringAsync(Ct), ProblemResponse.Options);
        problem.ShouldNotBeNull();

        return problem;
    }

    private async Task<World> SeedAsync(EpisodeStatus status = EpisodeStatus.StoryReview, string? audit = null, bool withFactSheet = false)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor)
            .CreateAsync(user.Id, series.Id, lengthTier: LengthTier.Short, status: status, ct: Ct);

        // story_versions.fact_sheet_id is a same-owner FK to fact_sheets (02 CR-29), so a seeded version that
        // carries one names a real draft row of this episode.
        var sheet = withFactSheet
            ? await new FactSheetBuilder(Database, actor).CreateAsync(user.Id, episode.Id, Ct)
            : null;

        var draft = ValidDraft();
        var version = StoryVersion.Create(
            user.Id,
            episode.Id,
            1,
            StoryVersionSource.Generated,
            draft,
            StoryContentJson.Write(draft, FactSources()),
            "model-x",
            "rel=1",
            null,
            null,
            sheet?.Id,
            audit ?? AuditJson(),
            null,
            Start);

        await using (var db = Database.CreateAppDbContext(new TestCurrentUser { Id = user.Id }))
        {
            db.StoryVersions.Add(version);
            await db.SaveChangesAsync(Ct);
        }

        SqlProbe.Execute(Database, $"UPDATE episodes SET current_story_version_id = '{version.Id}' WHERE id = '{episode.Id}'");

        return new World(user.Id, series.Id, episode.Id, version.Id, sheet?.Id);
    }

    private static JsonArray FactSources() =>
        new(new JsonObject
        {
            ["source_id"] = "11111111-1111-7111-8111-111111111111",
            ["title"] = "Source A",
            ["url"] = "https://example.test/source-a",
        });

    private static string AuditJson(string moderation = """{"flagged":false,"categories":[],"stale":false}""") =>
        $$$"""
        {"schema_version":"audit.v1","local":{"issues":[]},"moderation":{{{moderation}}},
         "model_review":{"scores":{{{Scores}}},"stale":false},"revision":{"rounds":1},"similarity":{"score":0.31,"n":120}}
        """;

    /// <summary>The seeded draft with a new title; everything else is unchanged, so it still passes the D3 rules.</summary>
    private static StoryContentV1 Edited(string title = "Căn nhà ven sông") => ValidDraft() with { Title = title };

    /// <summary>
    /// A short/vi draft inside the D4 band of 195–260 tokens (the 02.2.1-T1 fixture, padded the same way), with two hook
    /// keywords so that reordering them is a visible change.
    /// </summary>
    private static StoryContentV1 ValidDraft()
    {
        StorySegmentV1[] paragraphs =
        [
            new("Căn nhà thuê rẻ bất thường, chủ nhà chỉ dặn đừng mở cánh cửa cuối hành lang.", ["empty chair in the hallway"], [], "setup"),
            new("Tiếng gõ vang lên từ trong đó, và khi anh mở ra thì thấy chiếc ghế cũ vẫn còn ấm.", ["rain on the window"], [], "twist"),
            new("Anh kể lại chuyện ấy cho người hàng xóm, và người ấy chỉ im lặng nhìn ra sân.", ["figure at the door"], [], "reveal"),
        ];
        var hook = new StorySegmentV1(
            "Đêm ấy mưa rất to, người kể ngồi một mình trong căn nhà cũ ven sông.",
            ["old house by the river", "stormy night over the river"],
            []);
        var ending = new StorySegmentV1("Anh trả nhà ngay hôm sau, không dám kể cho ai nghe chuyện mình đã thấy.", ["letter on the table"], []);

        var others = Words(hook.Text) + Words(ending.Text) + Words(paragraphs[0].Text) + Words(paragraphs[1].Text);
        var last = paragraphs[^1];
        var missing = ValidBodyTokens - others - Words(last.Text);
        paragraphs[^1] = last with { Text = last.Text + " " + string.Join(' ', Enumerable.Repeat("rồi", missing)) };

        return new StoryContentV1(
            "Căn nhà cuối hành lang",
            hook,
            paragraphs,
            ending,
            "Một chuyện ma nhẹ nhàng về căn nhà bên sông.",
            ["truyện ma", "tâm linh"],
            ["supernatural"],
            new StoryStructureV1(
                "truyện ma tâm linh",
                "ngôi nhà cũ",
                "một cuộc điện thoại lúc nửa đêm",
                "người kể là nguyên nhân",
                "first_person"));
    }

    private static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId, Guid VersionId, Guid? FactSheetId = null);
}
