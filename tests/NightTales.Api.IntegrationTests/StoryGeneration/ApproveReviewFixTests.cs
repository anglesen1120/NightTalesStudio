using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories;
using NightTales.Application.Features.Stories.Approval;
using NightTales.Application.Features.Stories.Commands.ApproveStory;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Quality;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Moderation;
using Shouldly;
using Xunit;
using StorySeries = NightTales.Domain.Series.Series;

namespace NightTales.Api.IntegrationTests.StoryGeneration;

public sealed partial class ApproveStoryEndpointTests
{
    [Theory]
    [InlineData(Topic.Ghost)]
    [InlineData(Topic.TrueCrime)]
    public async Task NullStoryApprovalGate_FictionSucceedsButTrueCrimeFailsClosed(Topic topic)
    {
        // Exercise the gate directly, without a host, persistence or the HTTP pipeline.
        var userId = Guid.CreateVersion7();
        var series = StorySeries.Create(userId,
            new SeriesDraft("Approval gate", topic, default, default, "voice", Guid.CreateVersion7(), null), Start);
        var episode = Episode.Create(userId, series.Id, "Story", "A story for review", LengthTier.Short, Start);
        var draft = Draft();
        var version = StoryVersion.Create(userId, episode.Id, 1, StoryVersionSource.Generated, draft,
            StoryContentJson.Write(draft, new JsonArray()), "model-x", "rel=1", null, null, null, Audit(Clean), null, Start);

        var result = await new NullStoryApprovalGate().ValidateAsync(episode, series, version,
            new AttestationInput(true, "ta.v1"), Ct);

        if (topic.IsFiction())
        {
            result.IsError.ShouldBeFalse();
            result.Value.AcceptedTextVersion.ShouldBeNull();
        }
        else
        {
            result.IsError.ShouldBeTrue();
            result.FirstError.Type.ShouldBe(ErrorType.Conflict);
            result.FirstError.Code.ShouldBe(ErrorCodes.StoryFactSheetOutdated);
        }
    }

    [Fact]
    public async Task ApproveStory_FictionPatchedToTrueCrimeAfterReview_FailsClosedWithoutWrites()
    {
        var world = await SeedAsync();
        var before = StoredAudit(world).ToJsonString();
        using var factory = Factory(gate: new NullStoryApprovalGate());
        using var client = factory.ClientFor(world.UserId);
        using var series = await client.GetAsync($"/api/v1/series/{world.SeriesId}", Ct);
        series.StatusCode.ShouldBe(HttpStatusCode.OK, await series.Content.ReadAsStringAsync(Ct));
        var trueCrimeStyle = await new SeriesBuilder(Database, new TestCurrentUser { Id = world.UserId })
            .EnsureStyleTemplateAsync(name: $"crime-{world.SeriesId:N}", topicHint: Topic.TrueCrime, ct: Ct);
        using var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/series/{world.SeriesId}")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { topic = "true_crime", styleTemplateId = trueCrimeStyle.Id }),
                Encoding.UTF8, "application/json"),
        };
        patch.Headers.TryAddWithoutValidation("If-Match", series.Headers.ETag!.Tag);
        using var patched = await client.SendAsync(patch, Ct);
        patched.StatusCode.ShouldBe(HttpStatusCode.OK, await patched.Content.ReadAsStringAsync(Ct));
        SqlProbe.ScalarText(Database, $"SELECT topic FROM series WHERE id='{world.SeriesId}'").ShouldBe("true_crime");
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id='{world.EpisodeId}'").ShouldBe("story_review");
        var auditsBeforeApproval = SqlProbe.Count(Database, $"SELECT count(*) FROM audit_logs WHERE subject_user_id='{world.UserId}'");

        using var response = await ApproveAsync(client, world);

        await AssertProblemAsync(response, 409, "story_fact_sheet_outdated");
        AssertNothingApproved(world);
        SqlProbe.Count(Database, $"SELECT count(*) FROM audit_logs WHERE subject_user_id='{world.UserId}'")
            .ShouldBe(auditsBeforeApproval, "approval rejection must not write any audit row");
        StoredAudit(world).ToJsonString().ShouldBe(before);
    }

    [Fact]
    public async Task ApproveStory_HoldsSeriesShareLockUntilApprovalCommits()
    {
        var world = await SeedAsync();
        var gate = new PausingApprovalGate();
        using var factory = Factory(gate: gate);
        using var client = factory.ClientFor(world.UserId);
        var approval = ApproveAsync(client, world);
        try
        {
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            await using var contender = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId });
            await using var transaction = await contender.Database.BeginTransactionAsync(Ct);
            await contender.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '200ms'", Ct);
            var blocked = await Should.ThrowAsync<Npgsql.PostgresException>(() =>
                contender.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM series WHERE id={world.SeriesId} FOR UPDATE", Ct));
            blocked.SqlState.ShouldBe("55P03", "series topic changes must wait while approve holds its share lock");
            await transaction.RollbackAsync(Ct);
        }
        finally
        {
            gate.Release.TrySetResult();
        }

        using var response = await approval;
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        AssertApproved(world);
        await using var afterCommit = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId });
        await using var afterTransaction = await afterCommit.Database.BeginTransactionAsync(Ct);
        await afterCommit.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '200ms'", Ct);
        await afterCommit.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM series WHERE id={world.SeriesId} FOR UPDATE", Ct);
        await afterTransaction.RollbackAsync(Ct);
    }

    [Fact]
    public async Task ApproveStory_EditDuringModerationOutage_RefreshClearsOnlyModerationLocalIssues()
    {
        // The outage retains the prior blocking verdict, so the edit persists a genuine Q-MOD-01 issue.
        var world = await SeedAsync(audit: Audit("""{"flagged":true,"categories":["sexual/minors"],"blocking":["sexual/minors"],"stale":false,"unavailable":false}"""));
        var moderator = new FakeContentModerator().EnqueueUnavailable(ModerationStage.Draft).EnqueueClean(ModerationStage.Draft);
        using var factory = Factory(moderator, new NullStoryApprovalGate());
        using var client = factory.ClientFor(world.UserId);
        using var edit = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/episodes/{world.EpisodeId}/story")
        {
            Content = new StringContent(JsonSerializer.Serialize(ReviewFixEdit(), StoryContentJson.SerializerOptions),
                Encoding.UTF8, "application/json"),
        };
        edit.Headers.TryAddWithoutValidation("If-Match", StoryEtag.For(world.VersionId));
        using var edited = await client.SendAsync(edit, Ct);
        edited.StatusCode.ShouldBe(HttpStatusCode.OK, await edited.Content.ReadAsStringAsync(Ct));
        var editedId = (await BodyAsync(edited)).GetProperty("versionId").GetGuid();
        var current = world with { VersionId = editedId };
        var before = StoredAudit(current);
        var beforeIssues = before["local"]!["issues"]!.AsArray();
        beforeIssues.ShouldContain(node => node!["rule"]!.GetValue<string>() == StoryQualityChecker.ModerationBlocked
            && node["severity"]!.GetValue<string>() == "blocking");
        var retained = new JsonArray(beforeIssues.Where(node => node!["rule"]!.GetValue<string>() != StoryQualityChecker.ModerationBlocked)
            .Select(node => node!.DeepClone()).ToArray());
        retained.Count.ShouldBeGreaterThan(0, "the outage edit also creates non-moderation quality issues");
        var previousVersionAudit = StoredAudit(world).ToJsonString();

        using var approved = await ApproveAsync(client, current);

        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(Ct));
        moderator.Calls.ShouldBe(2, "one unavailable edit check and one clean approval check");
        AssertApproved(current);
        var after = StoredAudit(current);
        JsonNode.DeepEquals(after["moderation"], JsonNode.Parse(Clean)).ShouldBeTrue();
        JsonNode.DeepEquals(after["local"]!["issues"], retained).ShouldBeTrue("only moderation-derived issues are replaced");
        foreach (var property in before.Where(property => property.Key is not "local" and not "moderation"))
        {
            JsonNode.DeepEquals(after[property.Key], property.Value).ShouldBeTrue($"preserve {property.Key}");
        }

        // PostgreSQL jsonb persisted and decoded the report: invalid Unicode and NUL cannot be stored there.
        using var persisted = JsonDocument.Parse(after.ToJsonString());
        persisted.RootElement.ValueKind.ShouldBe(JsonValueKind.Object);
        persisted.RootElement.GetRawText().ShouldNotContain("\\u0000");
        StoredAudit(world).ToJsonString().ShouldBe(previousVersionAudit, "historical versions remain immutable");
        AuditCount(current).ShouldBe(1);
        OutboxCount(current).ShouldBe(1);
    }

    // Same D3-valid short/vi shape as EditStoryEndpointTests; 220 words stay inside the 195–260 band.
    private static StoryContentV1 ReviewFixEdit()
    {
        StorySegmentV1[] paragraphs =
        [
            new("Căn nhà thuê rẻ bất thường, chủ nhà chỉ dặn đừng mở cánh cửa cuối hành lang.", ["empty chair in the hallway"], [], "setup"),
            new("Tiếng gõ vang lên từ trong đó, và khi anh mở ra thì thấy chiếc ghế cũ vẫn còn ấm.", ["rain on the window"], [], "twist"),
            new("Anh kể lại chuyện ấy cho người hàng xóm, và người ấy chỉ im lặng nhìn ra sân.", ["figure at the door"], [], "reveal"),
        ];
        var hook = new StorySegmentV1("Đêm ấy mưa rất to, người kể ngồi một mình trong căn nhà cũ ven sông.",
            ["old house by the river", "stormy night over the river"], []);
        var ending = new StorySegmentV1("Anh trả nhà ngay hôm sau, không dám kể cho ai nghe chuyện mình đã thấy.", ["letter on the table"], []);
        var words = WordCount(hook.Text) + WordCount(ending.Text) + paragraphs.Sum(segment => WordCount(segment.Text));
        paragraphs[^1] = paragraphs[^1] with { Text = paragraphs[^1].Text + " " + string.Join(' ', Enumerable.Repeat("rồi", 220 - words)) };
        return new StoryContentV1("Căn nhà ven sông", hook, paragraphs, ending,
            "Một chuyện ma nhẹ nhàng về căn nhà bên sông.", ["truyện ma", "tâm linh"], ["supernatural"],
            new StoryStructureV1("truyện ma tâm linh", "ngôi nhà cũ", "một cuộc điện thoại lúc nửa đêm", "người kể là nguyên nhân", "first_person"));
    }

    private static int WordCount(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private sealed class PausingApprovalGate : IStoryApprovalGate
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ErrorOr<StoryApprovalGateResult>> ValidateAsync(Episode episode, StorySeries series,
            StoryVersion version, AttestationInput? attestation, CancellationToken ct)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            return new StoryApprovalGateResult(null);
        }
    }
}
