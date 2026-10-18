using System.Text;
using ErrorOr;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Features.Rendering.Jobs;
using NightTales.Application.Rendering;
using NightTales.RenderNode.Contract;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Rendering;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Rendering;

/// <summary>
/// QA probes for <see cref="IngestRenderOutputJob"/> on the edges the sibling suites do not reach: two runs racing on
/// one row, a last attempt that ends in an unexpected exception or a deferred status, a subtitle over its cap, blob
/// cleanup on a refused verdict, and log hygiene.
/// </summary>
public sealed class IngestQaTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The token xunit cancels when this test's deadline passes.</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The job's own blob writes, without the storage health probe's start-up marker.</summary>
    private static IReadOnlyList<BlobPut> JobPuts(IngestHarness harness) =>
        [.. harness.Blobs.Puts.Where(put => !put.Key.StartsWith("_health/", StringComparison.Ordinal))];

    /// <summary>The job's own blob keys, without the storage health probe's start-up marker.</summary>
    private static IReadOnlyList<string> JobKeys(IngestHarness harness) =>
        [.. harness.Blobs.Keys.Where(key => !key.StartsWith("_health/", StringComparison.Ordinal))];

    /// <summary>The outbox type of a successful ingest.</summary>
    private const string RenderSucceeded = "render_succeeded.v1";

    /// <summary>The outbox type of a failed ingest.</summary>
    private const string RenderFailed = "render_failed.v1";

    /// <summary>
    /// Acceptance check 8: two runs of one job that both pass the entry guards must still commit one asset pair,
    /// one outbox event and one settlement. Both runs get identical bytes, so the storage-key reuse path is the one
    /// under test.
    /// </summary>
    /// <remarks>
    /// The blob store's barrier holds both deliveries at their first PUT until the other arrives, so both are past
    /// the entry guard and inside the final-transaction section every time — the interleaving AC8 describes, not
    /// the one a lucky scheduler produces.
    /// </remarks>
    [Fact]
    public async Task ConcurrentRuns_OfOneJob_CommitOnce_SettleOnce()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var bytes = Encoding.UTF8.GetBytes("nts-video-canary-aaaa" + new string('v', 4096));
        var video = harness.Downloads.ServeVideoBytes(bytes, contentType: IngestRenderOutputJob.VideoContentType);
        _ = harness.Downloads.ServeVideoBytes(bytes, contentType: IngestRenderOutputJob.VideoContentType);
        var subtitles = harness.Downloads.ServeSubtitles();
        _ = harness.Downloads.ServeSubtitles();
        var body = IngestBodies.Succeeded(world, IngestBodies.Output(video, subtitles), IngestBodies.Check());
        harness.Render.EnqueueStatus(body);
        harness.Render.EnqueueStatus(body);
        harness.Blobs.PutBarrier = new Barrier(2);

        var first = harness.RunAsync(world.RenderJobId);
        var second = harness.RunAsync(world.RenderJobId);
        var firstError = await Record.ExceptionAsync(() => first);
        var secondError = await Record.ExceptionAsync(() => second);
        firstError.ShouldBeNull();
        secondError.ShouldBeNull();

        IngestDb.Row(Database, world).ShouldStartWith("succeeded|");
        IngestDb.AssetCount(Database, world).ShouldBe(2);
        IngestDb.Outbox(Database, world, RenderSucceeded).ShouldBe(1);
        IngestDb.Outbox(Database, world, RenderFailed).ShouldBe(0);
        harness.Money.Settles.Count.ShouldBe(1);
        harness.Money.Refunds.ShouldBeEmpty();
        harness.IngestLines().Count(entry => entry.EventId == 6301).ShouldBe(
            1,
            "the delivery that lost the row lock re-reads the committed row and is the logged no-op of AC8");
    }

    /// <summary>
    /// Failure table, download row: an unexpected exception from the download on the last attempt must not strand the
    /// row in <c>ingesting</c>. The job should end it as <c>failed</c>, refund once, and leave the spool empty.
    /// </summary>
    [Fact]
    public async Task Download_OnLastAttempt_Throws_RowIsNotStranded()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideoThrow(
            Encoding.UTF8.GetBytes("nts-video-canary-aaaa"),
            new IOException("scripted: the node link dropped mid-body"));
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));

        _ = await Record.ExceptionAsync(() => harness.RunLastAttemptAsync(world.RenderJobId));

        IngestDb.Row(Database, world).ShouldStartWith("failed|");
        harness.Money.Refunds.ShouldHaveSingleItem();
        IngestDb.Outbox(Database, world, RenderFailed).ShouldBe(1);
        harness.TempFiles().ShouldBeEmpty();
    }

    /// <summary>
    /// Failure table, status row: a node that keeps answering a deferred status on the last attempt must end the row
    /// as <c>failed</c>, not leave it in <c>ingesting</c> for the watchdog to find.
    /// </summary>
    [Fact]
    public async Task StatusDeferred_OnLastAttempt_EndsTheRow()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        harness.Render.EnqueueFailure(
            RenderClientMethod.GetStatus,
            new RenderClientFailure(
                RenderClientFailureKind.Deferred,
                "adapter_capacity_full",
                "scripted",
                Alert: false,
                RetryAfter: TimeSpan.FromMinutes(1)));

        _ = await Record.ExceptionAsync(() => harness.RunLastAttemptAsync(world.RenderJobId));

        IngestDb.Row(Database, world).ShouldStartWith("failed|");
        harness.Money.Refunds.ShouldHaveSingleItem();
        JobPuts(harness).ShouldBeEmpty();
    }

    /// <summary>
    /// Acceptance check 7 and the blob-cleanup contract: a verdict the policy refuses must not leave any bytes in
    /// <c>nt-media</c>, whichever order the blob writes and the verdict happened in.
    /// </summary>
    [Fact]
    public async Task QualityFail_LeavesNoBlobsBehind()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideo(4096);
        var subtitles = harness.Downloads.ServeSubtitles();
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(
                world,
                IngestBodies.Output(video, subtitles),
                IngestBodies.Check(errors: ["ffmpeg: torn frame at 00:01:02"])));

        await harness.RunAsync(world.RenderJobId);

        IngestDb.Row(Database, world).ShouldBe("failed|render_quality_failed|false");
        IngestDb.AssetCount(Database, world).ShouldBe(0);
        JobKeys(harness).ShouldBeEmpty();
    }

    /// <summary>
    /// Failure table, subtitles row: a subtitle body over the 64 KiB cap on the last attempt fails the render as
    /// <c>render_output_invalid</c> before any blob is written, and the spool is empty afterwards.
    /// </summary>
    [Fact]
    public async Task SubtitlesOverCap_OnLastAttempt_FailsRenderOutputInvalid_NoBlobs()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideo(4096);
        var subtitles = harness.Downloads.ServeSubtitles(new string('x', 200 * 1024));
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, subtitles), IngestBodies.Check()));

        await harness.RunLastAttemptAsync(world.RenderJobId);

        IngestDb.Row(Database, world).ShouldBe("failed|render_output_invalid|-");
        JobPuts(harness).ShouldBeEmpty();
        JobKeys(harness).ShouldBeEmpty();
        harness.TempFiles().ShouldBeEmpty();
        harness.Money.Refunds.ShouldHaveSingleItem();
    }

    /// <summary>
    /// Acceptance check 11: no ingest log line carries the subtitle body, the story text, the story title or a URL.
    /// Log entries are checked for the whole test, so the assertion covers every logger the job touched.
    /// </summary>
    [Fact]
    public async Task IngestLogs_CarryNoBodyNoUrlNoTitle()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideo(4096);
        var subtitles = harness.Downloads.ServeSubtitles();
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, subtitles), IngestBodies.Check()));

        await harness.RunAsync(world.RenderJobId);

        var messages = harness.Logs.Entries.Select(entry => entry.Message).ToList();
        messages.ShouldAllBe(message => !message.Contains("https://", StringComparison.OrdinalIgnoreCase));
        messages.ShouldAllBe(message => !message.Contains(IngestBodies.DefaultSubtitles, StringComparison.Ordinal));
        messages.ShouldAllBe(message => !message.Contains(IngestBodies.StoryTextMarker, StringComparison.OrdinalIgnoreCase));
        messages.ShouldAllBe(message => !message.Contains(IngestBodies.StoryTitle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// D3: an unexpected exception — not the destination's own I/O and not a shutdown — escaping the final
    /// transaction on the last attempt must not strand the row in <c>ingesting</c>. It ends <c>failed</c> through
    /// the recorder, which refunds once, and this attempt's objects are deleted.
    /// </summary>
    /// <remarks>
    /// The exception is raised by the terminal seam, i.e. after the verdict is recorded, after both asset rows are
    /// staged and after the row is marked <c>succeeded</c> in memory: the hardest shape for the ending, because
    /// the attempt's own writes have to be dropped before the row can be ended at all.
    /// </remarks>
    [Fact]
    public async Task UnexpectedException_OnLastAttempt_EndsRowFailed_RefundsOnce_NoBlobs()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideo(4096);
        var subtitles = harness.Downloads.ServeSubtitles();
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, subtitles), IngestBodies.Check()));
        harness.Hooks.KeptVideoFailure = new InvalidOperationException("scripted: the kept-video seam exploded");

        await harness.RunLastAttemptAsync(world.RenderJobId);

        IngestDb.Row(Database, world).ShouldStartWith("failed|");
        IngestDb.Row(Database, world).ShouldNotBe("succeeded|-|true");
        IngestDb.AssetCount(Database, world).ShouldBe(0, "the staged asset rows are dropped, never inserted");
        IngestDb.Outbox(Database, world, RenderFailed).ShouldBe(1);
        IngestDb.Outbox(Database, world, RenderSucceeded).ShouldBe(0);
        harness.Money.Refunds.ShouldHaveSingleItem().ReservationId.ShouldBe(world.ReservationId);
        harness.Money.Settles.Count.ShouldBe(
            1,
            "the settlement runs before the terminal write and outside its transaction, so a later failure rolls "
            + "the row back over an already-settled reservation (recorded in the task's Delivery notes)");
        JobKeys(harness).ShouldBeEmpty();
        harness.TempFiles().ShouldBeEmpty();
        harness.Logs.Entries.ShouldContain(
            entry => entry.EventId == 6333 && entry.Message.Contains("unexpected", StringComparison.Ordinal),
            "the ending names the operation it happened in");
    }

    /// <summary>
    /// D4: the video is stored and the subtitle PUT is then refused, so the attempt ends without keeping anything
    /// — and the video it already stored is deleted with it instead of being left in the media bucket.
    /// </summary>
    [Fact]
    public async Task VideoPutOk_SubtitlePutFails_DeletesVideoBlob()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideo(4096);
        var subtitles = harness.Downloads.ServeSubtitles();
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, subtitles), IngestBodies.Check()));
        harness.Blobs.PutFailures[world.SubtitlesKey] =
            Error.Failure("storage_unavailable", "scripted: the store refused the subtitle");

        await harness.RunLastAttemptAsync(world.RenderJobId);

        IngestDb.Row(Database, world).ShouldBe("failed|render_output_invalid|-");
        IngestDb.AssetCount(Database, world).ShouldBe(0);
        JobPuts(harness).Select(put => put.Key).ShouldBe([world.VideoKey], "only the video was accepted");
        harness.Blobs.Deletes.Select(entry => entry.Key).ShouldBe(
            [world.VideoKey, world.SubtitlesKey],
            "the stored video goes with the attempt that did not keep it, and the refused key is cleared too");
        JobKeys(harness).ShouldBeEmpty();
        harness.Money.Refunds.ShouldHaveSingleItem();
        harness.TempFiles().ShouldBeEmpty();
    }

    /// <summary>
    /// D5: a settlement that fails must not end in a committed <c>succeeded</c> row whose reservation is unsettled.
    /// The attempt's transaction rolls back and the attempt is handed back to the scheduler; on the last attempt
    /// the row ends <c>failed</c> instead. Both shapes are covered: the ledger answering a refusal, and the ledger
    /// throwing.
    /// </summary>
    [Fact]
    public async Task SettleFails_DoesNotCommitSucceeded()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideo(4096);
        _ = harness.Downloads.ServeVideo(4096);
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));
        harness.Money.Settle = Error.Failure("service_unavailable", "scripted: the ledger refused the settlement");

        var thrown = await harness.ThrowsAsync(world.RenderJobId, 1, IngestHarness.MaxAttempts);

        // The ledger's refusal is never swallowed: it is logged, the transaction rolls back and the attempt is
        // handed back to the scheduler.
        thrown.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain("Settling the reservation");
        IngestDb.Row(Database, world).ShouldNotBe("succeeded|-|true");
        IngestDb.AssetCount(Database, world).ShouldBe(0, "the whole final transaction rolled back");
        harness.Money.Refunds.ShouldBeEmpty("a retryable attempt refunds nothing");

        // A ledger that throws instead of answering travels the same way, and the attempt that has none left ends
        // the row in-job rather than leaving it `ingesting`.
        harness.Money.SettleThrows = new InvalidOperationException("scripted: the ledger is down");

        await harness.RunLastAttemptAsync(world.RenderJobId);

        IngestDb.Row(Database, world).ShouldStartWith("failed|");
        IngestDb.Row(Database, world).ShouldNotBe("succeeded|-|true");
        harness.Money.Settles.Count.ShouldBe(2, "one settlement attempt per delivery");
        harness.Money.Refunds.ShouldHaveSingleItem().ReservationId.ShouldBe(world.ReservationId);
        IngestDb.Outbox(Database, world, RenderSucceeded).ShouldBe(0);
        JobKeys(harness).ShouldBeEmpty();
        harness.TempFiles().ShouldBeEmpty();
    }

    /// <summary>
    /// The download path's retried kinds carry no D5 code of their own, so a node that keeps answering
    /// transiently on the last attempt ends the row as the node error instead of leaving it <c>ingesting</c>.
    /// </summary>
    [Fact]
    public async Task TransientDownload_OnLastAttempt_EndsTheRow()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideoFailure(
            new RenderClientFailure(
                RenderClientFailureKind.TransientFailure,
                "adapter_node_overloaded",
                "scripted: the node dropped the connection",
                Alert: false,
                RetryAfter: TimeSpan.FromSeconds(5)));
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));

        await harness.RunLastAttemptAsync(world.RenderJobId);

        IngestDb.Row(Database, world).ShouldBe("failed|render_node_error|-");
        IngestDb.Outbox(Database, world, RenderFailed).ShouldBe(1);
        harness.Money.Refunds.ShouldHaveSingleItem();
        JobPuts(harness).ShouldBeEmpty();
        harness.TempFiles().ShouldBeEmpty();
    }

    /// <summary>
    /// A ledger that throws on the refund must not stop the row from ending: the recorder logs the refund failure
    /// and the terminal transition still commits.
    /// </summary>
    [Fact]
    public async Task RefundThrows_LastAttempt_StillEndsTheRowFailed()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideoBytes(
            Encoding.UTF8.GetBytes("nts-video-canary-aaaa"),
            tamper: true);
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));
        harness.Money.RefundThrows = new InvalidOperationException("scripted: the ledger is down");

        await harness.RunLastAttemptAsync(world.RenderJobId);

        IngestDb.Row(Database, world).ShouldBe("failed|render_output_invalid|-");
        IngestDb.Outbox(Database, world, RenderFailed).ShouldBe(1);
        harness.Money.Refunds.ShouldHaveSingleItem();
        harness.TempFiles().ShouldBeEmpty();
    }
}
