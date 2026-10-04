using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
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
/// QA pass over <c>PUT /api/v1/episodes/{id}/story</c> (02.3.1-T1), written by an agent that did not write the code:
/// idempotency-key replays, a same-ETag race, size and shape limits, control characters, foreign and stale ETags,
/// an edit racing a generation, tenancy, the outbox payload, and the fields a client must not be able to set.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EditStoryQaTests(TestDatabase database) : IntegrationTest(database)
{
    private const string SavedAction = "episode.story_version_saved";
    private const string SavedEvent = "story_version_saved.v1";
    private const int ValidBodyTokens = 220;

    private static readonly DateTimeOffset Start = new(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);

    private static readonly string Scores = """{"overall":7.5,"hook":8}""";

    private static CancellationToken Ct => TestContextToken.Value;

    // ---- QA-01: Idempotency-Key on a PUT that does not declare one -------------------------------------------------

    [Fact]
    public async Task QA01_IdempotencyKeyReplayedWithDifferentBody_StaleEtag_Is412_NotTheCachedResponse()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var key = Guid.CreateVersion7().ToString();

        using var first = await PutAsync(client, world.EpisodeId, Edited("Bản A"), StoryEtag.For(world.VersionId), key);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(Ct));
        var firstId = (await ReadBodyAsync(first)).GetProperty("versionId").GetGuid();

        using var replay = await PutAsync(client, world.EpisodeId, Edited("Bản B"), StoryEtag.For(world.VersionId), key);

        replay.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed, await replay.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(replay)).Code.ShouldBe("story_version_conflict");
        VersionCount(world).ShouldBe(2);
        AuditCount(world).ShouldBe(1);
        OutboxCount(world).ShouldBe(1);
        SqlProbe.ScalarText(Database, $"SELECT current_story_version_id::text FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe(firstId.ToString());
        SqlProbe.ScalarText(Database, $"SELECT title FROM story_versions WHERE id = '{firstId}'").ShouldBe("Bản A");
    }

    [Fact]
    public async Task QA01_IdempotencyKeyReusedWithDifferentBody_AndCurrentEtag_IsAnOrdinaryNewEdit()
    {
        // The route declares no Idempotency-Key (the edit spends nothing; If-Match is its replay guard), so the header
        // is ignored and must never serve the first body back for the second request.
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var key = Guid.CreateVersion7().ToString();

        using var first = await PutAsync(client, world.EpisodeId, Edited("Bản A"), StoryEtag.For(world.VersionId), key);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(Ct));
        var firstId = (await ReadBodyAsync(first)).GetProperty("versionId").GetGuid();

        using var second = await PutAsync(client, world.EpisodeId, Edited("Bản B"), StoryEtag.For(firstId), key);

        second.StatusCode.ShouldBe(HttpStatusCode.OK, await second.Content.ReadAsStringAsync(Ct));
        var secondBody = await ReadBodyAsync(second);
        secondBody.GetProperty("title").GetString().ShouldBe("Bản B");
        secondBody.GetProperty("versionId").GetGuid().ShouldNotBe(firstId);
        secondBody.GetProperty("parentVersionId").GetGuid().ShouldBe(firstId);
        VersionCount(world).ShouldBe(3);
        AuditCount(world).ShouldBe(2);
    }

    [Fact]
    public async Task QA01_IdempotencyKeyAndBodyReplayedAfterSuccess_Is412_BecauseTheEtagMoved()
    {
        // Observation pinned, not a defect: a client that lost the 200 and retries byte for byte gets 412, never a second
        // version and never a replayed 200. The If-Match guard is the only replay protection of this route.
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var key = Guid.CreateVersion7().ToString();

        using var first = await PutAsync(client, world.EpisodeId, Edited("Bản A"), StoryEtag.For(world.VersionId), key);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(Ct));

        using var retry = await PutAsync(client, world.EpisodeId, Edited("Bản A"), StoryEtag.For(world.VersionId), key);

        retry.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed, await retry.Content.ReadAsStringAsync(Ct));
        VersionCount(world).ShouldBe(2);
        AuditCount(world).ShouldBe(1);
    }

    // ---- QA-02: several PUTs with the same ETag ---------------------------------------------------------------------

    [Fact]
    public async Task QA02_EightConcurrentPutsWithTheSameEtag_ExactlyOneIs200_TheRestAre412()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        var etag = StoryEtag.For(world.VersionId);
        var clients = Enumerable.Range(0, 8).Select(_ => factory.ClientFor(world.UserId)).ToList();

        try
        {
            var calls = await Task.WhenAll(
                clients.Select((client, index) => PutAsync(client, world.EpisodeId, Edited($"Tựa của tab {index}"), etag)));
            try
            {
                calls.Count(call => call.StatusCode == HttpStatusCode.OK).ShouldBe(1);
                calls.Where(call => call.StatusCode != HttpStatusCode.OK)
                    .ShouldAllBe(call => call.StatusCode == HttpStatusCode.PreconditionFailed);

                var winner = await ReadBodyAsync(calls.Single(call => call.StatusCode == HttpStatusCode.OK));
                VersionCount(world).ShouldBe(2);
                AuditCount(world).ShouldBe(1);
                OutboxCount(world).ShouldBe(1);
                SqlProbe.ScalarText(Database, $"SELECT current_story_version_id::text FROM episodes WHERE id = '{world.EpisodeId}'")
                    .ShouldBe(winner.GetProperty("versionId").GetGuid().ToString());
                SqlProbe.QueryStrings(Database, $"SELECT version::text FROM story_versions WHERE episode_id = '{world.EpisodeId}' ORDER BY version")
                    .ShouldBe(["1", "2"], "no duplicate or skipped version number");
            }
            finally
            {
                foreach (var call in calls)
                {
                    call.Dispose();
                }
            }
        }
        finally
        {
            clients.ForEach(client => client.Dispose());
        }
    }

    // ---- QA-03: size and count limits ---------------------------------------------------------------------------------

    [Fact]
    public async Task QA03_BodyOverTheOneMiBCap_Is413_AndWritesNothing()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var node = ToNode(Edited());
        node["description"] = new string('a', (1024 * 1024) + 4096);

        using var response = await PutAsync(client, world.EpisodeId, node, StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        AssertNothingSaved(world);
    }

    [Fact]
    public async Task QA03_FiveThousandParagraphs_ExceedTheRouteBodyCap_Is413_AndNothingIsSaved()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var node = ToNode(Edited());
        var template = node["paragraphs"]![0]!.DeepClone();
        template["beat"] = "build_up";
        var paragraphs = new JsonArray();
        for (var index = 0; index < 5000; index++)
        {
            paragraphs.Add(template.DeepClone());
        }

        node["paragraphs"] = paragraphs;

        var watch = Stopwatch.StartNew();
        using var response = await PutAsync(client, world.EpisodeId, node, StoryEtag.For(world.VersionId));
        watch.Stop();

        // 5 000 segments are also over the 1 MiB request cap (BD12), so the body limit answers first.
        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge, await response.Content.ReadAsStringAsync(Ct));
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15), "a hostile segment count must not stall the request");
        AssertNothingSaved(world);
    }

    [Fact]
    public async Task QA03_EightyParagraphs_BeyondTheSegmentBand_UnderTheBodyCap_Is400_WithSegmentCountOutOfRange()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var node = ToNode(Edited());
        var template = node["paragraphs"]![0]!.DeepClone();
        template["beat"] = "build_up";
        var paragraphs = new JsonArray();
        for (var index = 0; index < 80; index++)
        {
            paragraphs.Add(template.DeepClone());
        }

        node["paragraphs"] = paragraphs;

        using var response = await PutAsync(client, world.EpisodeId, node, StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        var problem = await ReadProblemAsync(response);
        problem.Code.ShouldBe("story_invalid_content");
        Messages(problem).ShouldContain(message => message.StartsWith("segment_count_out_of_range:", StringComparison.Ordinal));
        AssertNothingSaved(world);
    }

    [Fact]
    public async Task QA03_NineHundredThousandCharSingleTokenParagraph_IsAcceptedToday_ObservationNoPerSegmentCharCap()
    {
        // D3/D4 bound segments by words, not characters: one 900k-char "word" counts as a single token, so the
        // edit saves. Pinned as an observation (QA-OBS-1): the 1 MiB body cap is the only size bound.
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var node = ToNode(Edited());
        node["paragraphs"]![1]!["text"] = new string('a', 900_000);

        using var response = await PutAsync(client, world.EpisodeId, node, StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await ShortBodyAsync(response));
        VersionCount(world).ShouldBe(2);
    }

    [Theory]
    [InlineData("hook_keywords")]
    [InlineData("tags")]
    [InlineData("title_500k")]
    [InlineData("description_900k")]
    public async Task QA03_PathologicalFieldSizes_Under1MiB_Are400_NotSlowAndNot5xx(string shape)
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var node = ToNode(Edited());
        switch (shape)
        {
            case "hook_keywords":
                node["hook"]!["keywords"] = new JsonArray([.. Enumerable.Repeat("old house by the river", 20_000).Select(text => (JsonNode)JsonValue.Create(text)!)]);
                break;
            case "tags":
                node["tags"] = new JsonArray([.. Enumerable.Range(0, 30_000).Select(index => (JsonNode)JsonValue.Create("tag" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))!)]);
                break;
            case "title_500k":
                node["title"] = new string('t', 500_000);
                break;
            default:
                node["description"] = new string('d', 900_000);
                break;
        }

        var watch = Stopwatch.StartNew();
        using var response = await PutAsync(client, world.EpisodeId, node, StoryEtag.For(world.VersionId));
        watch.Stop();

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await ShortBodyAsync(response));
        (await ReadProblemAsync(response)).Code.ShouldBe("story_invalid_content");
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15));
        AssertNothingSaved(world);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("{\"title\":null}")]
    public async Task QA03_NotAStoryDocument_Is400_AndWritesNothing(string raw)
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PutRawAsync(client, world.EpisodeId, raw, StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await ShortBodyAsync(response));
        AssertNothingSaved(world);
    }

    [Fact]
    public async Task QA03_TwoHundredLevelsOfNesting_Is400_NotA500()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var raw = "{\"title\":" + new string('[', 200) + new string(']', 200) + "}";

        using var response = await PutRawAsync(client, world.EpisodeId, raw, StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await ShortBodyAsync(response));
        AssertNothingSaved(world);
    }

    // ---- QA-04: NUL and control characters ---------------------------------------------------------------------------

    [Theory]
    [InlineData("title")]
    [InlineData("description")]
    [InlineData("hook.text")]
    [InlineData("paragraph.text")]
    [InlineData("ending.text")]
    [InlineData("keyword")]
    [InlineData("tag")]
    [InlineData("structure.setting")]
    public async Task QA04_NulCharacter_IsRefusedAs400_NeverA500(string where)
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var node = ToNode(Edited());
        Inject(node, where, "\u0000");

        using var response = await PutAsync(client, world.EpisodeId, node, StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await ShortBodyAsync(response));
        (await ReadProblemAsync(response)).Code.ShouldBe("story_invalid_content");
        AssertNothingSaved(world);
    }

    [Theory]
    [InlineData("\u0007")]
    [InlineData("\u001B")]
    [InlineData("\u0085")]
    [InlineData("\u2028")]
    [InlineData("\u200B")]
    [InlineData("\u202E")]
    [InlineData("\uFEFF")]
    [InlineData("\u007F")]
    public async Task QA04_OtherControlAndFormatCharacters_AreNeverA5xx_AndNeverHalfSaved(string character)
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var node = ToNode(Edited());
        Inject(node, "paragraph.text", character);

        using var response = await PutAsync(client, world.EpisodeId, node, StoryEtag.For(world.VersionId));

        ((int)response.StatusCode).ShouldBeLessThan(500, await ShortBodyAsync(response));
        if (response.StatusCode == HttpStatusCode.OK)
        {
            VersionCount(world).ShouldBe(2);
            OutboxCount(world).ShouldBe(1);
        }
        else
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            AssertNothingSaved(world);
        }
    }

    [Fact]
    public async Task QA04_LoneSurrogateEscape_IsRefusedAs400_NeverA500()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var node = ToNode(Edited());
        node["hook"]!["text"] = "@@SURROGATE@@";
        var raw = node.ToJsonString().Replace("@@SURROGATE@@", "Đêm \\ud800 mưa", StringComparison.Ordinal);

        using var response = await PutRawAsync(client, world.EpisodeId, raw, StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await ShortBodyAsync(response));
        AssertNothingSaved(world);
    }

    // ---- QA-05: foreign, stale and odd If-Match values ---------------------------------------------------------------

    [Fact]
    public async Task QA05_EtagOfAnotherEpisodesVersion_Is412_ForSameOwnerAndForAStranger()
    {
        var owner = await SeedAsync();
        var sameOwnerOtherEpisode = await SeedForUserAsync(owner.UserId);
        var stranger = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(owner.UserId);

        foreach (var foreign in new[] { sameOwnerOtherEpisode.VersionId, stranger.VersionId, Guid.CreateVersion7() })
        {
            using var response = await PutAsync(client, owner.EpisodeId, Edited(), StoryEtag.For(foreign));

            response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed, await response.Content.ReadAsStringAsync(Ct));
            (await ReadProblemAsync(response)).Code.ShouldBe("story_version_conflict");
        }

        AssertNothingSaved(owner);
        AssertNothingSaved(sameOwnerOtherEpisode);
        AssertNothingSaved(stranger);
    }

    [Fact]
    public async Task QA05_WildcardAndListIfMatch_Are412_AndWriteNothing()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var current = StoryEtag.For(world.VersionId);

        foreach (var header in new[] { "*", $"{current}, \"sv-{Guid.CreateVersion7()}\"", $"\"sv-{Guid.CreateVersion7()}\", {current}" })
        {
            using var response = await PutAsync(client, world.EpisodeId, Edited(), header);

            response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed, $"If-Match: {header} -> {await ShortBodyAsync(response)}");
        }

        AssertNothingSaved(world);
    }

    [Fact]
    public async Task QA05_EtagOfAnOlderVersionOfTheSameEpisode_Is412()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var first = await PutAsync(client, world.EpisodeId, Edited("Bản A"), StoryEtag.For(world.VersionId));
        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(Ct));
        var firstId = (await ReadBodyAsync(first)).GetProperty("versionId").GetGuid();
        using var second = await PutAsync(client, world.EpisodeId, Edited("Bản B"), StoryEtag.For(firstId));
        second.StatusCode.ShouldBe(HttpStatusCode.OK, await second.Content.ReadAsStringAsync(Ct));

        using var third = await PutAsync(client, world.EpisodeId, Edited("Bản C"), StoryEtag.For(firstId));

        third.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed, await third.Content.ReadAsStringAsync(Ct));
        VersionCount(world).ShouldBe(3);
    }

    // ---- QA-06: an edit while the episode is generating ----------------------------------------------------------------

    [Fact]
    public async Task QA06_EditOfAGeneratingEpisode_Is409_AndLeavesPointerStatusAndOutboxUntouched()
    {
        var world = await SeedAsync(EpisodeStatus.StoryGenerating);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PutAsync(client, world.EpisodeId, Edited(), StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(response)).Code.ShouldBe("episode_invalid_state");
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("story_generating");
        SqlProbe.ScalarText(Database, $"SELECT current_story_version_id::text FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe(world.VersionId.ToString());
        VersionCount(world).ShouldBe(1);
        AuditCount(world).ShouldBe(0);
        OutboxCount(world).ShouldBe(0);
    }

    [Fact]
    public async Task QA06_EditWaitingOnTheEpisodeRowLock_WhenGenerationStartsFirst_Is409_AndSavesNothing()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        await using var holder = new NpgsqlConnection(Database.ConnectionString);
        await holder.OpenAsync(Ct);
        await using var transaction = await holder.BeginTransactionAsync(Ct);
        await using (var lockCommand = new NpgsqlCommand(
            $"SELECT id FROM episodes WHERE id = '{world.EpisodeId}' FOR UPDATE",
            holder,
            transaction))
        {
            await lockCommand.ExecuteScalarAsync(Ct);
        }

        // The ETag is current when the edit is sent; generation then takes the row and moves the status before it commits.
        var put = PutAsync(client, world.EpisodeId, Edited(), StoryEtag.For(world.VersionId));
        await WaitForALockWaiterAsync();
        put.IsCompleted.ShouldBeFalse("the edit must wait for the row the generation holds");

        await using (var flip = new NpgsqlCommand(
            $"UPDATE episodes SET status = 'story_generating' WHERE id = '{world.EpisodeId}'",
            holder,
            transaction))
        {
            await flip.ExecuteNonQueryAsync(Ct);
        }

        await transaction.CommitAsync(Ct);
        using var response = await put;

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await ShortBodyAsync(response));
        (await ReadProblemAsync(response)).Code.ShouldBe("episode_invalid_state");
        VersionCount(world).ShouldBe(1);
        AuditCount(world).ShouldBe(0);
        OutboxCount(world).ShouldBe(0);
        SqlProbe.ScalarText(Database, $"SELECT current_story_version_id::text FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe(world.VersionId.ToString());
    }

    // ---- QA-07: other and suspended users ------------------------------------------------------------------------------

    [Fact]
    public async Task QA07_ForeignEpisode_AndMissingEpisode_AreIndistinguishable404s()
    {
        var owner = await SeedAsync();
        var stranger = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(stranger.UserId);

        using var foreign = await PutAsync(client, owner.EpisodeId, Edited(), StoryEtag.For(owner.VersionId));
        using var missing = await PutAsync(client, Guid.CreateVersion7(), Edited(), StoryEtag.For(owner.VersionId));

        foreign.StatusCode.ShouldBe(HttpStatusCode.NotFound, await foreign.Content.ReadAsStringAsync(Ct));
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound, await missing.Content.ReadAsStringAsync(Ct));
        (await ReadProblemAsync(foreign)).Code.ShouldBe((await ReadProblemAsync(missing)).Code);
        AssertNothingSaved(owner);
        AssertNothingSaved(stranger);
    }

    [Fact]
    public async Task QA07_ForeignEpisode_WithAWrongEtag_Is404_Not412()
    {
        var owner = await SeedAsync();
        var stranger = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(stranger.UserId);

        using var response = await PutAsync(client, owner.EpisodeId, Edited(), StoryEtag.For(Guid.CreateVersion7()));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a 412 would confirm the episode exists");
        AssertNothingSaved(owner);
    }

    [Fact]
    public async Task QA07_SuspendedOwnerWithALiveToken_FollowsPhaseP_RefreshOnlyEnforcement()
    {
        // Same contract MeEndpointsQaTests pins for PATCH /me (09.3.2-T1 task file: suspension is enforced at refresh in
        // phase P; the per-request status check is 09.3.2-T2). Flips with it. The write must still belong to the owner.
        var world = await SeedAsync();
        SqlProbe.Execute(Database, $"UPDATE users SET status = 'suspended' WHERE id = '{world.UserId}'");
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await PutAsync(client, world.EpisodeId, Edited(), StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await ShortBodyAsync(response));
        SqlProbe.ScalarText(
                Database,
                $"SELECT user_id::text FROM story_versions WHERE episode_id = '{world.EpisodeId}' AND version = 2")
            .ShouldBe(world.UserId.ToString());
    }

    // ---- QA-08: the outbox row ---------------------------------------------------------------------------------------

    [Fact]
    public async Task QA08_StoryVersionSavedOutboxRow_IsWrittenOnce_AndCarriesIdsOnly()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        using var saved = await PutAsync(client, world.EpisodeId, Edited("Một tựa rất riêng"), StoryEtag.For(world.VersionId));
        saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));
        var newId = (await ReadBodyAsync(saved)).GetProperty("versionId").GetGuid();

        // Everything that must not add a second row: a no-op, a stale tag, an invalid body.
        using var noOp = await PutAsync(client, world.EpisodeId, Edited("Một tựa rất riêng"), StoryEtag.For(newId));
        noOp.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var stale = await PutAsync(client, world.EpisodeId, Edited("Khác"), StoryEtag.For(world.VersionId));
        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        using var invalid = await PutRawAsync(client, world.EpisodeId, "{}", StoryEtag.For(newId));
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        OutboxCount(world).ShouldBe(1);
        var payload = JsonNode.Parse(SqlProbe.ScalarText(
            Database,
            $"SELECT payload::text FROM outbox_messages WHERE type = '{SavedEvent}' AND user_id = '{world.UserId}'"))!.AsObject();
        payload.Select(pair => pair.Key).Order(StringComparer.Ordinal)
            .ShouldBe(["episodeId", "occurredAt", "storyVersionId", "userId", "version"]);
        payload["episodeId"]!.GetValue<Guid>().ShouldBe(world.EpisodeId);
        payload["userId"]!.GetValue<Guid>().ShouldBe(world.UserId);
        payload["storyVersionId"]!.GetValue<Guid>().ShouldBe(newId);
        payload["version"]!.GetValue<int>().ShouldBe(2);
        payload["occurredAt"]!.GetValue<DateTimeOffset>().ShouldBe(Start);

        var text = payload.ToJsonString();
        text.ShouldNotContain("Một tựa rất riêng");
        text.ShouldNotContain(ValidDraft().Hook.Text);
        text.ShouldNotContain(ValidDraft().Hook.Keywords[0]);
    }

    // ---- QA-09: audit_report.local is the checker's, not the client's -----------------------------------------------------

    [Fact]
    public async Task QA09_ClientSentAuditReport_IsIgnored_LocalIssuesModerationReviewSimilarityAndRevisionAreServerSide()
    {
        var world = await SeedAsync(audit: AuditJson(moderation: """{"flagged":true,"categories":["sexual/minors"],"stale":false}"""));
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var node = ToNode(Edited());
        var forged = JsonNode.Parse("""
            {"schema_version":"audit.v1","local":{"issues":[]},
             "moderation":{"flagged":false,"categories":[],"stale":false},
             "model_review":{"scores":{"overall":10,"hook":10},"stale":false},
             "similarity":{"score":0.99,"n":500},"revision":{"rounds":99}}
            """)!;
        node["audit_report"] = forged.DeepClone();
        node["auditReport"] = forged.DeepClone();
        node["quality_hints"] = new JsonArray();
        node["qualityHints"] = new JsonArray();

        using var response = await PutAsync(client, world.EpisodeId, node, StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await ShortBodyAsync(response));
        var body = await ReadBodyAsync(response);
        var newId = body.GetProperty("versionId").GetGuid();
        body.GetProperty("qualityHints").EnumerateArray().Select(hint => hint.GetProperty("ruleId").GetString())
            .ShouldContain("Q-MOD-01", "the carried flagged verdict is recomputed into a hint despite the forged empty list");

        SqlProbe.ScalarText(Database, $"SELECT audit_report::text FROM story_versions WHERE id = '{newId}'").ShouldContain("Q-MOD-01");
        SqlProbe.Count(Database, $"SELECT jsonb_array_length(audit_report->'local'->'issues') FROM story_versions WHERE id = '{newId}'")
            .ShouldBeGreaterThanOrEqualTo(1);
        SqlProbe.ScalarText(Database, $"SELECT audit_report->'moderation'->>'flagged' FROM story_versions WHERE id = '{newId}'").ShouldBe("true");
        SqlProbe.ScalarText(Database, $"SELECT audit_report->'moderation'->>'stale' FROM story_versions WHERE id = '{newId}'").ShouldBe("true");
        SqlProbe.ScalarText(Database, $"SELECT audit_report->'model_review'->>'stale' FROM story_versions WHERE id = '{newId}'").ShouldBe("true");
        JsonNode.DeepEquals(
                JsonNode.Parse(SqlProbe.ScalarText(Database, $"SELECT audit_report->'model_review'->'scores' FROM story_versions WHERE id = '{newId}'")),
                JsonNode.Parse(Scores))
            .ShouldBeTrue("the stored scores are the previous version's, not the client's 10s");
        SqlProbe.ScalarText(Database, $"SELECT audit_report->'similarity'->>'n' FROM story_versions WHERE id = '{newId}'").ShouldBe("0");
        SqlProbe.ScalarText(Database, $"SELECT coalesce(audit_report->'similarity'->>'score', 'null') FROM story_versions WHERE id = '{newId}'").ShouldBe("null");
        SqlProbe.ScalarText(Database, $"SELECT audit_report->'revision'->>'rounds' FROM story_versions WHERE id = '{newId}'").ShouldBe("1");
        SqlProbe.ScalarText(Database, $"SELECT audit_report::text FROM story_versions WHERE id = '{newId}'").ShouldNotContain("0.99");
    }

    // ---- QA-10: fields a client sends that must be ignored ----------------------------------------------------------------

    [Fact]
    public async Task QA10_ClientSentIdsVersionSourceScoresModerationFactFieldsAndTimestamps_AreIgnored()
    {
        var factSheetId = Guid.CreateVersion7();
        var world = await SeedAsync(factSheetId: factSheetId);
        var stranger = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var injectedId = Guid.CreateVersion7();
        var node = ToNode(Edited());
        node["id"] = injectedId.ToString();
        node["version_id"] = injectedId.ToString();
        node["versionId"] = injectedId.ToString();
        node["version"] = 99;
        node["source"] = "generated";
        node["episode_id"] = stranger.EpisodeId.ToString();
        node["user_id"] = stranger.UserId.ToString();
        node["parent_version_id"] = stranger.VersionId.ToString();
        node["fact_sheet_id"] = Guid.CreateVersion7().ToString();
        node["schema_version"] = "evil.v9";
        node["model"] = "gpt-evil";
        node["generation_run_id"] = Guid.CreateVersion7().ToString();
        node["similarity_score"] = 0.99;
        node["word_count"] = 1;
        node["created_at"] = "2000-01-01T00:00:00Z";
        node["review"] = JsonNode.Parse("""{"overall":10}""");
        node["scores"] = JsonNode.Parse("""{"overall":10}""");
        node["model_review"] = JsonNode.Parse("""{"scores":{"overall":10},"stale":false}""");
        node["moderation"] = JsonNode.Parse("""{"flagged":false,"categories":[],"stale":false}""");
        node["fact_sources"] = JsonNode.Parse("""[{"source_id":"x","title":"Injected","url":"https://evil.example/x"}]""");
        var previousSchema = SqlProbe.ScalarText(Database, $"SELECT content->>'schema_version' FROM story_versions WHERE id = '{world.VersionId}'");

        using var response = await PutAsync(client, world.EpisodeId, node, StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await ShortBodyAsync(response));
        var body = await ReadBodyAsync(response);
        var newId = body.GetProperty("versionId").GetGuid();
        newId.ShouldNotBe(injectedId);
        body.GetProperty("version").GetInt32().ShouldBe(2);
        body.GetProperty("source").GetString().ShouldBe("edited");
        body.GetProperty("parentVersionId").GetGuid().ShouldBe(world.VersionId);
        body.GetProperty("factSheetId").GetGuid().ShouldBe(factSheetId);
        body.GetProperty("createdAt").GetDateTimeOffset().ShouldBe(Start);
        body.GetProperty("wordCount").GetInt32().ShouldBeGreaterThan(100, "word_count is derived from the text");

        SqlProbe.Count(Database, $"SELECT count(*) FROM story_versions WHERE id = '{injectedId}'").ShouldBe(0);
        SqlProbe.ScalarText(
                Database,
                $"SELECT episode_id::text || '|' || user_id::text || '|' || version::text || '|' || source || '|' "
                + $"|| parent_version_id::text || '|' || fact_sheet_id::text FROM story_versions WHERE id = '{newId}'")
            .ShouldBe($"{world.EpisodeId}|{world.UserId}|2|edited||{world.VersionId}|{factSheetId}".Replace("||", "|", StringComparison.Ordinal));
        SqlProbe.ScalarText(Database, $"SELECT coalesce(similarity_score::text, 'null') FROM story_versions WHERE id = '{newId}'").ShouldBe("null");
        SqlProbe.ScalarText(Database, $"SELECT content->>'schema_version' FROM story_versions WHERE id = '{newId}'").ShouldBe(previousSchema);
        var stored = SqlProbe.ScalarText(Database, $"SELECT content::text FROM story_versions WHERE id = '{newId}'");
        stored.ShouldNotContain("evil");
        stored.ShouldNotContain("gpt-evil");
        SqlProbe.ScalarText(Database, $"SELECT audit_report->'model_review'->>'stale' FROM story_versions WHERE id = '{newId}'").ShouldBe("true");
        SqlProbe.ScalarText(Database, $"SELECT coalesce(audit_report->'moderation'->>'stale', 'none') FROM story_versions WHERE id = '{newId}'").ShouldBe("true");
        SqlProbe.Count(Database, $"SELECT count(*) FROM story_versions WHERE episode_id = '{stranger.EpisodeId}'").ShouldBe(1);
        SqlProbe.ScalarText(Database, $"SELECT current_story_version_id::text FROM episodes WHERE id = '{stranger.EpisodeId}'")
            .ShouldBe(stranger.VersionId.ToString());
    }

    [Fact]
    public async Task QA10_ClientCitedFactRefThePreviousVersionNeverCited_Is400()
    {
        var world = await SeedAsync();
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var node = ToNode(Edited());
        node["paragraphs"]![0]!["fact_refs"] = new JsonArray("f-invented");

        using var response = await PutAsync(client, world.EpisodeId, node, StoryEtag.For(world.VersionId));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await ShortBodyAsync(response));
        var problem = await ReadProblemAsync(response);
        problem.Code.ShouldBe("story_invalid_content");
        Messages(problem).ShouldContain(message => message.StartsWith("fact_ref_unknown:", StringComparison.Ordinal));
        AssertNothingSaved(world);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private ApiFactory Factory(FakeTextClient? text = null) =>
        new ApiFactory(Database, new FakeClock(Start))
            .WithServices(services =>
            {
                services.RemoveAll<ITextClient>();
                services.AddSingleton<ITextClient>(text ?? new FakeTextClient());
            });

    private async Task WaitForALockWaiterAsync()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var waiting = SqlProbe.Count(
                Database,
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND datname = current_database()");
            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(100, Ct);
        }

        throw new TimeoutException("The edit never queued on the episode row lock.");
    }

    private static void Inject(JsonObject node, string where, string value)
    {
        switch (where)
        {
            case "title":
                node["title"] = "Tựa" + value + "x";
                break;
            case "description":
                node["description"] = "Mô tả" + value + "ngắn.";
                break;
            case "hook.text":
                node["hook"]!["text"] = "Đêm ấy" + value + "mưa rất to.";
                break;
            case "paragraph.text":
                node["paragraphs"]![1]!["text"] = "Tiếng gõ" + value + "vang lên từ trong đó, và khi anh mở ra thì thấy chiếc ghế cũ vẫn còn ấm.";
                break;
            case "ending.text":
                node["ending"]!["text"] = "Anh trả nhà" + value + "ngay hôm sau, không dám kể cho ai nghe chuyện mình đã thấy.";
                break;
            case "keyword":
                node["paragraphs"]![0]!["keywords"] = new JsonArray("empty" + value + "chair in the hallway");
                break;
            case "tag":
                node["tags"] = new JsonArray("truyện" + value + "ma", "tâm linh");
                break;
            case "structure.setting":
                node["structure"]!["setting"] = "ngôi nhà" + value + "cũ";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(where), where, null);
        }
    }

    private static IEnumerable<string> Messages(ProblemResponse problem) =>
        problem.Errors is null ? [] : problem.Errors.SelectMany(pair => pair.Value);

    private static async Task<string> ShortBodyAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);

        return text.Length > 600 ? text[..600] : text;
    }

    private void AssertNothingSaved(World world)
    {
        VersionCount(world).ShouldBe(1);
        AuditCount(world).ShouldBe(0, "nothing was saved, so no audit row");
        OutboxCount(world).ShouldBe(0, "nothing was saved, so no outbox row");
        SqlProbe.ScalarText(Database, $"SELECT current_story_version_id::text FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe(world.VersionId.ToString());
    }

    private long VersionCount(World world) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM story_versions WHERE episode_id = '{world.EpisodeId}'");

    private long AuditCount(World world) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM audit_logs WHERE action = '{SavedAction}' AND subject_user_id = '{world.UserId}' "
            + $"AND data->>'episodeId' = '{world.EpisodeId}'");

    private long OutboxCount(World world) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM outbox_messages WHERE type = '{SavedEvent}' AND user_id = '{world.UserId}' "
            + $"AND payload->>'episodeId' = '{world.EpisodeId}'");

    private static Task<HttpResponseMessage> PutAsync(
        HttpClient client,
        Guid episodeId,
        StoryContentV1 content,
        string? ifMatch,
        string? idempotencyKey = null) =>
        PutRawAsync(client, episodeId, ToNode(content).ToJsonString(), ifMatch, idempotencyKey);

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid episodeId, JsonNode body, string? ifMatch) =>
        PutRawAsync(client, episodeId, body.ToJsonString(), ifMatch);

    private static Task<HttpResponseMessage> PutRawAsync(
        HttpClient client,
        Guid episodeId,
        string json,
        string? ifMatch,
        string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/episodes/{episodeId}/story")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
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

    private async Task<World> SeedAsync(EpisodeStatus status = EpisodeStatus.StoryReview, string? audit = null, Guid? factSheetId = null)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);

        return await SeedForUserAsync(user.Id, status, audit, factSheetId);
    }

    private async Task<World> SeedForUserAsync(
        Guid userId,
        EpisodeStatus status = EpisodeStatus.StoryReview,
        string? audit = null,
        Guid? factSheetId = null)
    {
        var actor = new TestCurrentUser();
        var series = await new SeriesBuilder(Database, actor).CreateAsync(userId, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor)
            .CreateAsync(userId, series.Id, lengthTier: LengthTier.Short, status: status, ct: Ct);

        var draft = ValidDraft();
        var version = StoryVersion.Create(
            userId,
            episode.Id,
            1,
            StoryVersionSource.Generated,
            draft,
            StoryContentJson.Write(draft, FactSources()),
            "model-x",
            "rel=1",
            null,
            null,
            factSheetId,
            audit ?? AuditJson(),
            null,
            Start);

        await using (var db = Database.CreateAppDbContext(new TestCurrentUser { Id = userId }))
        {
            db.StoryVersions.Add(version);
            await db.SaveChangesAsync(Ct);
        }

        SqlProbe.Execute(Database, $"UPDATE episodes SET current_story_version_id = '{version.Id}' WHERE id = '{episode.Id}'");

        return new World(userId, series.Id, episode.Id, version.Id);
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

    private static StoryContentV1 Edited(string title = "Căn nhà ven sông") => ValidDraft() with { Title = title };

    /// <summary>The same short/vi draft the endpoint tests seed: inside the D4 band of 195-260 tokens.</summary>
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

    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId, Guid VersionId);
}
