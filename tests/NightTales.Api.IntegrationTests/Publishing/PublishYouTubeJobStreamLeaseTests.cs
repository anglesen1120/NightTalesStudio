using NightTales.Api.IntegrationTests.Harness;
using NightTales.Application.Features.Publishing.Upload;
using NightTales.Domain.Publishing;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Google;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// The streaming lease of 06.2.3-T2 (plan §Performance, PERF-3/SEC-4): the run that streams a publication's session
/// stamps <c>platform_payload.uploadStreamLeaseUntil</c>/<c>uploadStreamLeaseOwner</c> in the short claim transaction
/// before the first byte, renews it while the stream runs, and releases it in the transaction that records the
/// outcome — so a second run of the same row can neither reuse the live session nor send overlapping ranges into it.
/// </summary>
/// <remarks>
/// The parked-chunk gate of <see cref="FakeGoogleServer"/> (<see cref="FakeGoogleServer.HoldChunks"/>) puts a real run
/// mid-stream while the test drives the second one, which is the interleaving a duplicate delivery produces in a
/// deployment. Every assertion reads committed state back through the harness's raw SQL.
/// </remarks>
/// <param name="database">The class database, a migrated copy of the run's template.</param>
public sealed class PublishYouTubeJobStreamLeaseTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task StreamLease_SecondRunDuringStreaming_WritesNothingAndStreamsNothing()
    {
        // PERF-3/SEC-4: two runs of one publication must not stream into the same resumable session at once. The
        // second run finds a live lease and returns without writing a row, a session or a byte; the first still
        // finishes on its own.
        await using var harness = await StartAsync();
        var world = await SeedReadyAsync(harness);
        harness.Google.HoldChunks();

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        wait.CancelAfter(TimeSpan.FromSeconds(90));

        var runToken = Ct;
        var first = Task.Run(() => harness.SendAsync(world.PublicationId, runToken), runToken);
        await harness.Google.WaitForHeldChunksAsync(1, wait.Token);

        var payload = await harness.PlatformPayloadAsync(world.PublicationId);
        PublicationPayload.ReadInstant(payload, PublicationPayload.UploadStreamLeaseUntilKey)
            .ShouldNotBeNull("the streaming run stamps its lease before it opens the stream");
        PublicationPayload.ReadString(payload, PublicationPayload.UploadStreamLeaseOwnerKey)
            .ShouldNotBeNullOrWhiteSpace("the lease names the run that owns it");

        var second = await harness.SendAsync(world.PublicationId, Ct);
        second.IsError.ShouldBeFalse();
        second.Value.ShouldBe(UploadOutcome.Rescheduled, "a live lease makes the second run a no-op, not a retry");

        harness.Google.SessionCreateCalls.ShouldBe(1, "the second run must not create a second session");
        (await harness.StatusAsync(world.PublicationId)).ShouldBe("uploading", "the second run leaves the row alone");

        harness.Google.ReleaseChunks();
        var outcome = await first;
        outcome.IsError.ShouldBeFalse();
        outcome.Value.ShouldBe(UploadOutcome.Processing);

        (await harness.StatusAsync(world.PublicationId)).ShouldBe("processing");

        var chunks = harness.Google.Chunks.Where(chunk => !chunk.IsProbe).ToList();
        chunks.ShouldNotBeEmpty();
        chunks.Select(chunk => chunk.Start).Distinct().Count().ShouldBe(
            chunks.Count,
            "no range is sent twice, which is what a second writer into the same session would do");

        PublicationPayload.ReadInstant(
                await harness.PlatformPayloadAsync(world.PublicationId),
                PublicationPayload.UploadStreamLeaseUntilKey)
            .ShouldBeNull("the transaction that records the outcome releases the lease");
    }

    [Fact]
    public async Task StreamLease_StaleLeaseIsTakenOver()
    {
        // A run that dies leaves its lease behind. Once `uploadStreamLeaseUntil` has passed the lease is stale, so
        // the next attempt takes it over instead of blocking the publication forever.
        await using var harness = await StartAsync();
        var staleOwner = Guid.CreateVersion7().ToString();
        var payload = PublicationPayload.WithInstant(
            PublicationPayload.WithKey(DefaultPayload(), PublicationPayload.UploadStreamLeaseOwnerKey, staleOwner),
            PublicationPayload.UploadStreamLeaseUntilKey,
            harness.Clock.UtcNow.AddMinutes(-1));

        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, ct: Ct);
        var episode = await harness.SeedEpisodeAsync(userId, harness.Blobs.DefaultLength, ct: Ct);
        var publicationId = await harness.SeedPublicationAsync(
            userId,
            episode.EpisodeId,
            channel.Id,
            platformPayloadJson: payload,
            ct: Ct);

        await harness.RunAsync(publicationId, Ct);

        (await harness.StatusAsync(publicationId)).ShouldBe("processing", "the stale lease does not block the run");
        harness.Google.SessionCreateCalls.ShouldBe(1);
        PublicationPayload.ReadString(
                await harness.PlatformPayloadAsync(publicationId),
                PublicationPayload.UploadStreamLeaseOwnerKey)
            .ShouldBeNull("the outcome clears the lease the stale run left");
    }

    [Fact]
    public async Task StreamLease_IsRenewedWhileTheStreamRuns()
    {
        // A long upload outlives the first lease. Every throttled progress report pushes `uploadStreamLeaseUntil`
        // forward, so a slow but healthy stream is never taken over by a second run.
        await using var harness = await StartAsync();
        var world = await SeedReadyAsync(harness);
        harness.Google.HoldChunks();

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        wait.CancelAfter(TimeSpan.FromSeconds(90));

        var runToken = Ct;
        var first = Task.Run(() => harness.SendAsync(world.PublicationId, runToken), runToken);
        await harness.Google.WaitForHeldChunksAsync(1, wait.Token);
        var stamped = PublicationPayload.ReadInstant(
            await harness.PlatformPayloadAsync(world.PublicationId),
            PublicationPayload.UploadStreamLeaseUntilKey);
        stamped.ShouldNotBeNull();

        // A minute of wall clock passes mid-stream (fake clock), then the chunk is answered and the next report
        // renews the lease.
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        harness.Google.ReleaseChunk();
        await harness.Google.WaitForHeldChunksAsync(2, wait.Token);

        var renewed = PublicationPayload.ReadInstant(
            await harness.PlatformPayloadAsync(world.PublicationId),
            PublicationPayload.UploadStreamLeaseUntilKey);
        renewed.ShouldNotBeNull();
        renewed!.Value.ShouldBeGreaterThan(stamped!.Value, "the progress reports push the lease forward");

        harness.Google.ReleaseChunks();
        (await first).Value.ShouldBe(UploadOutcome.Processing);
        (await harness.StatusAsync(world.PublicationId)).ShouldBe("processing");
    }

    private Task<PublishYouTubeJobHarness> StartAsync() => PublishYouTubeJobHarness.StartAsync(Database, Ct);

    private static async Task<World> SeedReadyAsync(PublishYouTubeJobHarness harness)
    {
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, ct: Ct);
        var episode = await harness.SeedEpisodeAsync(userId, harness.Blobs.DefaultLength, ct: Ct);
        var publicationId = await harness.SeedPublicationAsync(userId, episode.EpisodeId, channel.Id, ct: Ct);

        return new World(userId, channel, episode, publicationId);
    }

    /// <summary>One seeded world: the owner, its connection, the ready episode and the publication under test.</summary>
    /// <param name="UserId">The owner.</param>
    /// <param name="Channel">The seeded connection.</param>
    /// <param name="Episode">The seeded episode with its final video.</param>
    /// <param name="PublicationId">The row the test drives.</param>
    private sealed record World(
        Guid UserId,
        PublishYouTubeJobHarness.SeededChannel Channel,
        PublishYouTubeJobHarness.SeededEpisode Episode,
        Guid PublicationId);

    /// <summary>The plan the upload path derives for a scheduled, post-audit row with no instant.</summary>
    /// <returns>The <c>platform_payload</c> document with an <c>upload</c> block and no lease.</returns>
    private static string DefaultPayload() =>
        """{"upload":{"privacyStatus":"private","preAudit":false},"preAudit":false,"requestedPrivacy":"private"}""";
}
