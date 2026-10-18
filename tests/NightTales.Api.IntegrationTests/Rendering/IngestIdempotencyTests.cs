using NightTales.Application.Features.Rendering.Jobs;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Rendering;

/// <summary>
/// At-least-once delivery: the ingest job can be handed the same <c>render_jobs</c> id twice (a Hangfire retry
/// that in fact committed, a duplicated enqueue). The second delivery must observe the same world, not a richer
/// one: no duplicate asset, no second blob variant, no second event, no second audit row, no second settlement.
/// </summary>
/// <remarks>
/// Every blob key of an attempt is derived from the <c>render_jobs</c> id (BD7), so a replay writes the same two
/// keys instead of a <c>video.mp4</c>/<c>video-1.mp4</c> pair; the assertions below pin that.
/// </remarks>
public sealed class IngestIdempotencyTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The token xunit cancels when this test's deadline passes.</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The outbox type of a successful ingest.</summary>
    private const string RenderSucceeded = "render_succeeded.v1";

    /// <summary>A second delivery of a finished row changes nothing at all.</summary>
    [Fact]
    public async Task SecondRun_IsNoOp()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideo(4096);
        var subtitles = harness.Downloads.ServeSubtitles();
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, subtitles), IngestBodies.Check()));

        await harness.RunAsync(world.RenderJobId, Ct);

        // The delivered world, captured before the replay.
        var rowAfterFirstRun = IngestDb.Row(Database, world);
        var episodeAfterFirstRun = IngestDb.Episode(Database, world);
        var putKeysAfterFirstRun = harness.Blobs.Puts.Select(put => put.Key).ToList();
        var orderAfterFirstRun = harness.Order.Entries.Count;
        var downloadCallsAfterFirstRun = harness.Downloads.Calls.Count;

        rowAfterFirstRun.ShouldBe("succeeded|-|true");
        putKeysAfterFirstRun.Count.ShouldBe(2);

        // Replay: the same job id, delivered again.
        await harness.RunAsync(world.RenderJobId, Ct);

        IngestDb.Row(Database, world).ShouldBe(rowAfterFirstRun);
        IngestDb.Episode(Database, world).ShouldBe(episodeAfterFirstRun);
        IngestDb.AssetCount(Database, world).ShouldBe(2, "no duplicate asset row");
        IngestDb.JobCount(Database, world).ShouldBe(1, "no retry row for a row that already succeeded");
        IngestDb.Outbox(Database, world, RenderSucceeded).ShouldBe(1, "the event is raised once");
        IngestDb.AuditCount(Database, world).ShouldBe(1, "the audit row is written once");

        harness.Blobs.Keys.Count.ShouldBe(2, "a replay reuses the attempt's keys, it never variants them");
        harness.Blobs.Puts.Count.ShouldBe(putKeysAfterFirstRun.Count, "no second PUT");
        harness.Blobs.Deletes.ShouldBeEmpty();

        harness.Money.Settles.Count.ShouldBe(1);
        harness.Money.Settles[0].Value.ShouldBe(world.ReservationId);
        harness.Money.Refunds.ShouldBeEmpty();

        harness.Downloads.Calls.Count.ShouldBe(downloadCallsAfterFirstRun, "nothing is downloaded twice");
        harness.Adapter.DeleteCalls.ShouldBe(1, "the cleanup is asked for once");
        harness.Order.Entries.Count.ShouldBe(orderAfterFirstRun, "the replay touches no port");
        harness.TempFiles().ShouldBeEmpty();
    }

    /// <summary>A replay of a row whose status was already terminal is a no-op even before the asset check.</summary>
    [Fact]
    public async Task SecondRun_AfterTerminalFailure_IsNoOp()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideoBytes(
            System.Text.Encoding.UTF8.GetBytes("nts-video-canary-aaaa"),
            tamper: true);
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));

        await harness.RunLastAttemptAsync(world.RenderJobId);
        IngestDb.Row(Database, world).ShouldBe("failed|render_output_invalid|-");
        var refundsAfterFirstRun = harness.Money.Refunds.Count;
        refundsAfterFirstRun.ShouldBe(1);

        await harness.RunAsync(world.RenderJobId, Ct);

        IngestDb.Row(Database, world).ShouldBe("failed|render_output_invalid|-");
        IngestDb.Outbox(Database, world, RenderSucceeded).ShouldBe(0);
        harness.Money.Refunds.Count.ShouldBe(refundsAfterFirstRun, "a failed row is never refunded twice");
        harness.Blobs.Puts.ShouldBeEmpty();
    }
}
