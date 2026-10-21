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

        var first = harness.RunAsync(world.RenderJobId, Ct);
        var second = harness.RunAsync(world.RenderJobId, Ct);
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

        await harness.RunAsync(world.RenderJobId, Ct);

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

        await harness.RunAsync(world.RenderJobId, Ct);

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
        harness.Money.Settles.ShouldBeEmpty(
            "the settlement is the last statement before the commit (C3), and the kept-video hook that fails here "
            + "runs before it, so the reservation is never settled and then refunded by the same attempt");
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
        // handed back to the scheduler. The job's own typed failure is rethrown unchanged (its type derives from
        // InvalidOperationException; N5 keeps that exemption for this type alone).
        thrown.ShouldBeAssignableTo<InvalidOperationException>().Message.ShouldContain("Settling the reservation");
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

    /// <summary>
    /// C2: the episode leaves <c>rendering</c> behind the attempt (a cancel, an owner action), so the output is
    /// real but there is no episode left to attach it to. The attempt ends through the recorder, and the rows it
    /// had already staged — the two assets and the passing verdict — must not survive into that ending: the
    /// verdict would sit on a failed row and the asset rows would name objects the ending deletes.
    /// </summary>
    [Fact]
    public async Task AttachRefused_OnLastAttempt_EndsRowFailedWithoutTheStagedRows()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct, new IngestSeed { EpisodeStatus = "story_approved" });
        var video = harness.Downloads.ServeVideo(4096);
        var subtitles = harness.Downloads.ServeSubtitles();
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, subtitles), IngestBodies.Check()));

        await harness.RunLastAttemptAsync(world.RenderJobId);

        IngestDb.Row(Database, world).ShouldBe(
            "failed|render_node_error|-",
            "the verdict is dropped with the attempt's other staged rows, so the failed row carries no report");
        IngestDb.AssetCount(Database, world).ShouldBe(0, "the staged asset rows never reach the table");
        IngestDb.Episode(Database, world).ShouldBe("story_approved|-", "the episode keeps the state it moved to");
        IngestDb.Outbox(Database, world, RenderFailed).ShouldBe(1);
        IngestDb.Outbox(Database, world, RenderSucceeded).ShouldBe(0);
        harness.Money.Refunds.ShouldHaveSingleItem().ReservationId.ShouldBe(world.ReservationId);
        harness.Money.Settles.ShouldBeEmpty();
        harness.Blobs.Deletes.Select(entry => entry.Key).ShouldBe([world.VideoKey, world.SubtitlesKey]);
        JobKeys(harness).ShouldBeEmpty();
        harness.TempFiles().ShouldBeEmpty();
        harness.IngestLines().ShouldContain(
            entry => entry.EventId == 6359,
            "the refused attach names the episode state that refused it");
    }

    /// <summary>
    /// C2, the cancel-requested variant: a row the owner asked to cancel never waits for another attempt (WT51),
    /// so the refused attach ends it here even though the scheduler still had attempts left — and still without
    /// the rows it had staged.
    /// </summary>
    [Fact]
    public async Task AttachRefused_OnCancelRequestedRow_EndsTheRowWithoutWaitingForARetry()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(
            Database,
            Ct,
            new IngestSeed { EpisodeStatus = "story_approved", CancelRequested = true });
        var video = harness.Downloads.ServeVideo(4096);
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));

        await harness.RunAsync(world.RenderJobId, 1, IngestHarness.MaxAttempts);

        IngestDb.Row(Database, world).ShouldBe("failed|render_node_error|-");
        IngestDb.AssetCount(Database, world).ShouldBe(0);
        IngestDb.JobCount(Database, world).ShouldBe(1, "a cancelled render queues no retry row");
        harness.Money.Refunds.ShouldHaveSingleItem().ReservationId.ShouldBe(world.ReservationId);
        harness.Blobs.Deletes.Select(entry => entry.Key).ShouldBe([world.VideoKey, world.SubtitlesKey]);
        harness.TempFiles().ShouldBeEmpty();
    }

    /// <summary>
    /// C4: the ingest leg — a download plus up to two uploads — can take minutes, so the instants the success path
    /// stamps are read inside the final transaction instead of at the moment the attempt was picked up. The clock
    /// moves while the download streams, and every stamp (the row's <c>finished_at</c>, the video asset's
    /// <c>created_at</c> and retention, the audit row and the event) must carry the later reading.
    /// </summary>
    [Fact]
    public async Task IngestLegThatOutlivesTheEntryReading_StampsTheFinalReading()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideo(4096);
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));

        var finishedAt = IngestHarness.Start.AddMinutes(7);
        harness.Downloads.OnBeforeWrite = _ =>
        {
            harness.Clock.Set(finishedAt);

            return ValueTask.CompletedTask;
        };

        await harness.RunAsync(world.RenderJobId, Ct);

        IngestDb.Row(Database, world).ShouldBe("succeeded|-|true");
        IngestDb.Stamps(Database, world, finishedAt, finishedAt.AddDays(IngestHarness.UnapprovedRetentionDays))
            .ShouldBe(
                "true|true|true|true|true",
                "the row, the asset and its retention, the audit row and the event all carry the reading taken "
                + "inside the final transaction");
    }

    /// <summary>
    /// C5/S3: a delivery that reaches its final transaction behind another delivery that already ended the row as
    /// <c>failed</c> commits nothing — and clears both of the row's deterministic objects, because a failed row
    /// owns neither. A success would keep them (they are its own two keys).
    /// </summary>
    [Fact]
    public async Task RowEndedFailedByAnotherDelivery_IsANoOpThatClearsBothKeys()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideo(4096);
        var subtitles = harness.Downloads.ServeSubtitles();
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, subtitles), IngestBodies.Check()));
        harness.Downloads.OnBeforeWrite = async _ =>
        {
            await Database.ExecuteAsync(
                $"UPDATE render_jobs SET status = 'failed', error_code = 'render_node_error' "
                + $"WHERE id = '{world.RenderJobId}'",
                Ct);
        };

        await harness.RunAsync(world.RenderJobId, Ct);

        IngestDb.Row(Database, world).ShouldStartWith("failed|");
        IngestDb.AssetCount(Database, world).ShouldBe(0);
        IngestDb.Outbox(Database, world, RenderSucceeded).ShouldBe(0);
        harness.Money.Settles.ShouldBeEmpty();
        harness.Money.Refunds.ShouldBeEmpty();
        harness.Blobs.Deletes.Select(entry => entry.Key).ShouldBe(
            [world.VideoKey, world.SubtitlesKey],
            "a row another delivery ended as failed owns neither key, so this attempt clears both");
        JobKeys(harness).ShouldBeEmpty();
        harness.IngestLines().ShouldContain(entry => entry.EventId == 6301);
    }

    /// <summary>
    /// C6/S2: a delivery that decides to fail must not refund a reservation another delivery already settled, nor
    /// end a row it already ended. Hangfire redelivers a job whose download outlived the invisibility timeout, so
    /// the shape is real: the row turns terminal behind this attempt, and the failure path's row lock plus
    /// committed-status re-read is what keeps it the logged no-op of AC8 instead of a second ending.
    /// </summary>
    [Fact]
    public async Task FailingDelivery_OnARowAnotherDeliveryCommitted_RefundsNothing()
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
        harness.Downloads.OnBeforeWrite = async _ =>
        {
            await Database.ExecuteAsync(
                $"UPDATE render_jobs SET status = 'succeeded', finished_at = now() WHERE id = '{world.RenderJobId}'",
                Ct);
        };

        await harness.RunLastAttemptAsync(world.RenderJobId);

        IngestDb.Row(Database, world).ShouldStartWith("succeeded|");
        IngestDb.Outbox(Database, world, RenderFailed).ShouldBe(0);
        IngestDb.Outbox(Database, world, RenderSucceeded).ShouldBe(0, "this delivery raises no event of its own");
        harness.Money.Refunds.ShouldBeEmpty("the row another delivery settled is never refunded again");
        harness.Money.Settles.ShouldBeEmpty();
        harness.Blobs.Deletes.ShouldBeEmpty("a success owns both keys, so nothing is cleared");
        harness.Logs.Entries.ShouldContain(entry => entry.EventId == 6301);
    }

    /// <summary>
    /// S3/N2/N3: a shutdown cancels the attempt's token mid-download. The attempt's writes are dropped and the
    /// cancellation the host raised travels unchanged, so Hangfire requeues the job — and nothing else happens:
    /// the shutdown path neither reads the row nor deletes the two objects. A delete decided there could only
    /// come from an *unlocked* status read, and a duplicate delivery that has already PUT these exact BD7 keys
    /// but has not committed still reads <c>ingesting</c>, so the delete would leave its imminent success pointing
    /// at nothing. The keys are content-addressed, so the next attempt overwrites them, and a row that never runs
    /// again is the failed/cancelled sweep's to collect.
    /// </summary>
    [Fact]
    public async Task ShutdownMidDownload_PropagatesTheCancellation_WithoutReadingOrDeleting()
    {
        using var shutdown = new CancellationTokenSource();
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideo(4096);
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));
        harness.Downloads.OnBeforeWrite = _ =>
        {
            // Every command recorded from here on belongs to the shutdown path itself.
            harness.Commands.Reset();
            shutdown.Cancel();

            return ValueTask.CompletedTask;
        };

        // The type of what escapes is the assertion: an `OperationCanceledException` is what Hangfire uses to
        // requeue the job, so a cleanup failure that replaced it — the unbudgeted, unguarded status read of N3 —
        // would fail this line.
        await Should.ThrowAsync<OperationCanceledException>(
            () => harness.RunAsync(world.RenderJobId, shutdown.Token));

        IngestDb.Row(Database, world).ShouldStartWith("ingesting|");
        harness.Money.Refunds.ShouldBeEmpty();
        harness.Money.Settles.ShouldBeEmpty();
        harness.Blobs.Deletes.ShouldBeEmpty(
            "the shutdown path deletes nothing: the keys it would clear can belong to a delivery that still commits");
        harness.Commands.Texts.ShouldNotContain(
            text => text.Contains("render_jobs", StringComparison.OrdinalIgnoreCase),
            "the unlocked status read of the old shutdown path is gone (N2/N3)");
        harness.TempFiles().ShouldBeEmpty();
    }

    /// <summary>
    /// S6: an output the node declares as empty is refused as <c>render_output_invalid</c> before anything is
    /// stored. The empty body has a real hash and the declared byte count matches what was served, so without the
    /// zero-byte check the declaration and the served bytes agree on nothing at all and an empty video would be
    /// stored as the episode's final asset.
    /// </summary>
    [Fact]
    public async Task ZeroByteVideo_FailsRenderOutputInvalid()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideo(0);
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));

        await harness.RunLastAttemptAsync(world.RenderJobId);

        IngestDb.Row(Database, world).ShouldBe("failed|render_output_invalid|-");
        harness.IngestLines().ShouldContain(
            entry => entry.EventId == 6308 && entry.Message.Contains("bytes_zero", StringComparison.Ordinal),
            "the reason the output was refused is the zero-byte one");
        JobPuts(harness).ShouldBeEmpty();
        JobKeys(harness).ShouldBeEmpty();
        harness.Money.Refunds.ShouldHaveSingleItem();
        harness.TempFiles().ShouldBeEmpty();
    }

    /// <summary>
    /// S3/N7: a shutdown can land in the one window an ending still has work to do — after its verdict committed,
    /// before its objects are cleared. The cleanup must still run, and it does because its token is the helper's
    /// own and not a link to the job's (which is cancelled by then): a linked token would have the store refuse
    /// both deletes and leave a paid render in the bucket for a row that will never run again. The fake store
    /// throws on a cancelled token now, so a regression to a linked token fails here rather than only against S3.
    /// </summary>
    [Fact]
    public async Task QualityFailure_WithTheJobTokenCancelledBeforeTheCleanup_StillClearsBothKeys()
    {
        using var shutdown = new CancellationTokenSource();
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideo(4096);
        var subtitles = harness.Downloads.ServeSubtitles();
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(
                world,
                IngestBodies.Output(video, subtitles),
                IngestBodies.Check(errors: ["ffmpeg: torn frame at 00:01:02"])));

        // 6314 is the line the quality ending writes between its own commit and its two deletes: cancelling the
        // *job* token there is the shutdown landing in the cleanup window, with everything already durable.
        harness.Logs.OnLine = entry =>
        {
            if (entry.EventId == 6314)
            {
                shutdown.Cancel();
            }
        };

        await harness.RunAsync(world.RenderJobId, shutdown.Token);

        shutdown.IsCancellationRequested.ShouldBeTrue("the shutdown has to land before the cleanup for this to test it");
        IngestDb.Row(Database, world).ShouldBe("failed|render_quality_failed|false");
        harness.Blobs.Deletes.Select(entry => entry.Key).ShouldBe(
            [world.VideoKey, world.SubtitlesKey],
            "the cleanup's token is its own, not a link to the cancelled job token");
        JobKeys(harness).ShouldBeEmpty();
        harness.TempFiles().ShouldBeEmpty();
    }

    /// <summary>
    /// N1: the settlement is issued immediately before the commit, so a commit that fails after it leaves a
    /// settled reservation with a rolled-back row. A retried attempt then settles the <em>same</em> reservation a
    /// second time. That is correct only because 12.1.2-T1's ledger is idempotent per reservation id: the test
    /// pins the sequence so the double settle is deliberate and visible, not an accident.
    /// </summary>
    [Fact]
    public async Task CommitFailsAfterTheSettle_RetrySettlesTheSameReservationAgain()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideo(4096);
        _ = harness.Downloads.ServeVideo(4096);
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));

        // A competing writer on the render_jobs row: EF sees zero affected rows and the unit of work answers
        // concurrency_conflict — the one failure left in the success path once the settlement is out.
        harness.Refuses.Armed = true;

        var thrown = await harness.ThrowsAsync(world.RenderJobId, 1, IngestHarness.MaxAttempts);

        harness.Refuses.Refusals.ShouldBe(1, "the refusal is one-shot so the ending below can commit");
        thrown.ShouldBeAssignableTo<InvalidOperationException>().Message.ShouldContain("concurrency_conflict");
        IngestDb.Row(Database, world).ShouldStartWith(
            "ingesting|",
            customMessage: "the row is not committed, and the settlement outside its transaction is not rolled back with it");
        harness.Money.Calls.ShouldBe(
            [$"settle:{world.ReservationId:D}"],
            customMessage: "the failed commit leaves the reservation settled");

        // The retry: everything runs again, including the settlement of the reservation that is already settled.
        await harness.RunAsync(world.RenderJobId, 2, IngestHarness.MaxAttempts);

        IngestDb.Row(Database, world).ShouldBe("succeeded|-|true");
        harness.Money.Calls.ShouldBe(
            [$"settle:{world.ReservationId:D}", $"settle:{world.ReservationId:D}"],
            customMessage: "the retry settles the same reservation again: 12.1.2-T1's ledger must treat that as one settlement");
        harness.Money.Refunds.ShouldBeEmpty();
    }

    /// <summary>
    /// N1, the last attempt: the commit fails after the settlement and there is no retry left, so the ending ends
    /// the row and refunds a reservation this job already settled. Deliberate, and safe only because 12.1.2-T1's
    /// ledger must answer a refund-after-settle as a typed no-op or a refusal — the sequence is what this test
    /// pins.
    /// </summary>
    [Fact]
    public async Task CommitFailsAfterTheSettle_LastAttemptRefundsTheSettledReservation()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideo(4096);
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));
        harness.Refuses.Armed = true;

        await harness.RunLastAttemptAsync(world.RenderJobId);

        harness.Refuses.Refusals.ShouldBe(1);
        IngestDb.Row(Database, world).ShouldBe("failed|render_output_invalid|-");
        harness.Money.Calls.ShouldBe(
            [$"settle:{world.ReservationId:D}", $"refund:{world.ReservationId:D}"],
            customMessage: "the settlement was already issued when the commit failed, so the ending refunds a settled reservation");
        harness.Money.Refunds.ShouldHaveSingleItem().ReservationId.ShouldBe(world.ReservationId);
        harness.Blobs.Deletes.Select(entry => entry.Key).ShouldBe([world.VideoKey, world.SubtitlesKey]);
        JobKeys(harness).ShouldBeEmpty();
        harness.TempFiles().ShouldBeEmpty();
    }
}
