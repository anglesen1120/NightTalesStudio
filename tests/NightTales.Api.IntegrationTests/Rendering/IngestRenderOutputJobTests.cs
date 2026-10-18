using System.Text;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Features.Rendering;
using NightTales.Application.Features.Rendering.Jobs;
using NightTales.Application.Rendering;
using NightTales.Application.Rendering.Quality;
using NightTales.Domain.Common;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Rendering;

/// <summary>
/// The happy path of <see cref="IngestRenderOutputJob"/> and every way it can refuse an adapter's output: the
/// download caps, the integrity verdict, the quality verdict, the storage failures, the terminal states and the
/// attempt bookkeeping. Every assertion reads an observable: the <c>render_jobs</c>/<c>episodes</c>/
/// <c>assets</c>/<c>outbox_messages</c>/<c>audit_logs</c> rows, the recorded blob keys, the ledger calls, the
/// spool directory and the job's own log lines.
/// </summary>
/// <remarks>
/// <para>
/// <b>The Worker graph is the graph under test.</b> <see cref="IngestHarness"/> composes the production Worker
/// host with the recorded fakes in place of the node, the blob store and the money ports, so the registrations
/// (<c>AddRenderJobs</c>, <c>AddRenderIngest</c>, the <c>Render:Ingest</c>/<c>Assets</c> options and their
/// validator) are exercised rather than mocked away.
/// </para>
/// <para>
/// <b>Attempts.</b> The job reads the scheduler's attempt through <c>IJobAttempt</c>; a test publishes it with
/// <c>JobAttemptScope.Set</c> exactly as the Worker's job filter does.
/// </para>
/// <para>
/// <b>Cost isolation.</b> The seeded reservation is <c>0.01</c> USD, so a leaked retry row or undelivered outbox
/// row from an earlier test cannot cross this class's <c>UsageBudgetGuard</c> cap.
/// </para>
/// </remarks>
public sealed class IngestRenderOutputJobTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The token xunit cancels when this test's deadline passes.</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The outbox type of a successful ingest.</summary>
    private const string RenderSucceeded = "render_succeeded.v1";

    /// <summary>The outbox type of a failed ingest.</summary>
    private const string RenderFailed = "render_failed.v1";

    /// <summary>The stored schema of the verdict this job writes.</summary>
    private const string QualitySchema = "render-quality.v1";

    /// <summary>Every port the ingest job resolves must be registered in the Worker's own graph.</summary>
    private static readonly Type[] IngestDependencies =
    [
        typeof(IAppDbContext),
        typeof(IUnitOfWork),
        typeof(IClock),
        typeof(RenderOptions),
        typeof(RenderIngestOptions),
        typeof(RenderQualityPolicy),
        typeof(RenderMetrics),
        typeof(IRenderClient),
        typeof(IRenderDownloadClient),
        typeof(IRenderFailureRecorder),
        typeof(IRenderTerminalHooks),
        typeof(IBlobStore),
        typeof(StorageSettings),
        typeof(IJobAttempt),
        typeof(ICreditLedger),
        typeof(IAuditWriter),
        typeof(ISystemContext),
        typeof(IngestRenderOutputJob),
    ];

    /// <summary>A passing render becomes reviewed video: blobs, assets, verdict, episode state, event, audit.</summary>
    [Fact]
    public async Task HappyPath_WritesBlobsAssetsQualityReport_AndVideoReview()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideo(4096);
        var subtitles = harness.Downloads.ServeSubtitles();
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, subtitles), IngestBodies.Check()));

        await harness.RunAsync(world.RenderJobId, Ct);

        // The row and its stored verdict.
        IngestDb.Row(Database, world).ShouldBe("succeeded|-|true");
        IngestDb.Report(Database, world, "->>'schema'").ShouldBe(QualitySchema);
        IngestDb.ReportChecks(Database, world).ShouldBeGreaterThanOrEqualTo(3);

        // The episode moved to review and points at the video asset.
        var episode = IngestDb.Episode(Database, world);
        episode.ShouldStartWith("video_review|");
        var videoAssetId = SqlProbe.ScalarText(
            Database,
            $"SELECT id::text FROM assets WHERE episode_id = '{world.EpisodeId}' AND kind = 'video'");
        episode.ShouldBe($"video_review|{videoAssetId}");

        // One asset row per output, stamped with the unapproved-render retention window (WT56).
        var assets = IngestDb.Assets(Database, world, IngestHarness.UnapprovedRetention);
        assets.Count.ShouldBe(2);
        assets.ShouldContain(
            $"video|{world.VideoKey}|{IngestRenderOutputJob.VideoContentType}|4096|{video.DeclaredSha256}|301.37|true");
        assets.ShouldContain(
            $"subtitle|{world.SubtitlesKey}|{IngestRenderOutputJob.SubtitlesContentType}|{subtitles.DeclaredBytes}"
            + $"|{subtitles.DeclaredSha256}|-|true");

        // Both blobs were written to the media bucket under the BD7 keys.
        harness.Blobs.Keys.Count.ShouldBe(2);
        harness.Blobs.Keys.ShouldContain(world.VideoKey);
        harness.Blobs.Keys.ShouldContain(world.SubtitlesKey);

        // The event carries ids only, and the audit row carries the hash but never the content (AC1, AC2).
        IngestDb.Outbox(Database, world, RenderSucceeded).ShouldBe(1);
        IngestDb.SucceededPayload(Database, world).ShouldBe(
            $"{world.RenderJobId}|{world.EpisodeId}|false|false|episodeId,occurredAt,renderJobId",
            "the payload carries the event's ids and its instant only: no blob key, no URL, no content");
        IngestDb.Outbox(Database, world, RenderFailed).ShouldBe(0);
        IngestDb.AuditCount(Database, world).ShouldBe(1);
        var audit = IngestDb.Audit(Database, world).ShouldHaveSingleItem();
        audit.ShouldStartWith("render.succeeded|system|");
        audit.ShouldContain(video.DeclaredSha256);
        audit.ShouldEndWith("|false");

        // The attempt settled with the adapter cleaned up last, and the spool is empty.
        harness.Money.Settles.ShouldHaveSingleItem().Value.ShouldBe(world.ReservationId);
        harness.Money.Refunds.ShouldBeEmpty();
        harness.Adapter.DeleteCalls.ShouldBe(1);
        harness.TempFiles().ShouldBeEmpty();
    }

    /// <summary>An absent SRT writes no subtitle asset, and the verdict is the node's own check.</summary>
    /// <param name="subtitlesEnabled">Whether the params expected subtitles at all.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SubtitlesAbsent_NoSubtitleAsset_VerdictComesFromNodeCheck(bool subtitlesEnabled)
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(
            Database,
            Ct,
            new IngestSeed { SubtitlesEnabled = subtitlesEnabled });
        var video = harness.Downloads.ServeVideo(4096);
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));

        await harness.RunAsync(world.RenderJobId, Ct);

        IngestDb.Row(Database, world).ShouldBe("succeeded|-|true");
        IngestDb.AssetCount(Database, world).ShouldBe(1);
        IngestDb.Assets(Database, world, IngestHarness.UnapprovedRetention).ShouldAllBe(row => row.StartsWith("video|", StringComparison.Ordinal));
        harness.Blobs.Keys.ShouldHaveSingleItem().ShouldBe(world.VideoKey);
        harness.Downloads.SubtitleCalls.ShouldBe(0);
        IngestDb.Outbox(Database, world, RenderSucceeded).ShouldBe(1);
    }

    /// <summary>Bytes that disagree with the declared hash fail the render and leave nothing written.</summary>
    [Fact]
    public async Task Sha256Mismatch_FailsRenderOutputInvalid_NoPartialWrite()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideoBytes(Encoding.UTF8.GetBytes("nts-video-canary-aaaa"), tamper: true);
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));

        // The verdict is written on the scheduler's last attempt (WT52). One attempt earlier the job throws so the
        // scheduler re-downloads instead of recording, which NonLastAttempt_InvalidOutput_RethrowsAndDoesNotRefund
        // pins.
        await harness.RunLastAttemptAsync(world.RenderJobId);

        IngestDb.Row(Database, world).ShouldBe("failed|render_output_invalid|-");
        harness.Blobs.Puts.ShouldBeEmpty();
        harness.Blobs.Keys.ShouldBeEmpty();
        IngestDb.AssetCount(Database, world).ShouldBe(0);
        harness.TempFiles().ShouldBeEmpty();
        IngestDb.Outbox(Database, world, RenderFailed).ShouldBe(1);
        IngestDb.FailedPayload(Database, world).ShouldBe($"{world.RenderJobId}|render_output_invalid|false");
        IngestDb.JobRows(Database, world).ShouldContain(
            row => row.EndsWith("|2|queued", StringComparison.Ordinal),
            "render_output_invalid is transient (05 D5), so the policy queues the render's attempt 2 — which is "
            + "why the event carries Final = false");
        IngestDb.Outbox(Database, world, RenderSucceeded).ShouldBe(0);
    }

    /// <summary>A body larger than the cap is stopped while it streams: never a whole-second copy in memory.</summary>
    [Fact]
    public async Task SizeCap_AbortsWithoutBuffering()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var declared = 2 * IngestHarness.VideoCapBytes;
        var video = harness.Downloads.ServeVideo(declared);
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));

        // The cap trip is recorded on the scheduler's last attempt (WT52); the caps of the call itself are
        // asserted below.
        await harness.RunLastAttemptAsync(world.RenderJobId);

        IngestDb.Row(Database, world).ShouldBe("failed|render_output_invalid|-");
        harness.Blobs.Puts.ShouldBeEmpty();
        harness.TempFiles().ShouldBeEmpty();

        // The cap is enforced on the stream: the client read the body in bounded chunks and stopped at the cap
        // instead of holding the whole two megabytes.
        var call = harness.Downloads.Calls.ShouldHaveSingleItem();
        call.MaxBytes.ShouldBe(IngestHarness.VideoCapBytes);
        call.BytesWritten.ShouldBeLessThanOrEqualTo(IngestHarness.VideoCapBytes + FakeRenderDownloadClient.ChunkBytes);
        harness.Downloads.LargestWriteBytes.ShouldBeLessThanOrEqualTo(FakeRenderDownloadClient.ChunkBytes);
        harness.Downloads.LargestWriteBytes.ShouldBeLessThan(declared);
    }

    /// <summary>A content type the contract does not name is refused before anything is stored.</summary>
    [Fact]
    public async Task ContentTypeUnexpected_FailsRenderOutputInvalid()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideoBytes(
            Encoding.UTF8.GetBytes("nts-video-canary-aaaa"),
            contentType: "application/octet-stream");
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));

        // The refusal is recorded on the scheduler's last attempt (WT52).
        await harness.RunLastAttemptAsync(world.RenderJobId);

        IngestDb.Row(Database, world).ShouldBe("failed|render_output_invalid|-");
        harness.Blobs.Puts.ShouldBeEmpty();
        harness.TempFiles().ShouldBeEmpty();
        IngestDb.AssetCount(Database, world).ShouldBe(0);
    }

    /// <summary>A verdict the policy refuses stores the report, refunds, and never retries the render.</summary>
    [Fact]
    public async Task QualityFail_WritesReport_FailsJob_NoRetry_RefundsReservation()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideo(4096);
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(
                world,
                IngestBodies.Output(video, null),
                IngestBodies.Check(errors: ["ffmpeg: torn frame at 00:01:02"])));

        await harness.RunAsync(world.RenderJobId, Ct);

        IngestDb.Row(Database, world).ShouldBe("failed|render_quality_failed|false");
        IngestDb.Report(Database, world, "->>'schema'").ShouldBe(QualitySchema);
        IngestDb.ReportChecks(Database, world).ShouldBeGreaterThan(0);
        IngestDb.FailedPayload(Database, world).ShouldBe($"{world.RenderJobId}|render_quality_failed|true");
        IngestDb.Outbox(Database, world, RenderFailed).ShouldBe(1);
        IngestDb.Outbox(Database, world, RenderSucceeded).ShouldBe(0);
        IngestDb.JobCount(Database, world).ShouldBe(1, "a refused verdict is terminal, so no retry row exists");

        // Step 6 stores the objects before step 8 decides, and a refused attempt's objects are deleted best-effort
        // once the verdict commits: nothing is left in nt-media and no asset row exists. The delete is by the
        // row's two deterministic keys (BD7), not by the keys this attempt happened to PUT, so the subtitle key is
        // cleared even though this attempt never wrote it (C5/S3).
        harness.Blobs.Puts.Select(put => put.Key).ShouldBe([world.VideoKey]);
        harness.Blobs.Deletes.Select(entry => entry.Key).ShouldBe([world.VideoKey, world.SubtitlesKey]);
        harness.Blobs.Keys.ShouldBeEmpty();
        IngestDb.AssetCount(Database, world).ShouldBe(0);
        IngestDb.Episode(Database, world).ShouldBe("story_approved|-");

        var refund = harness.Money.Refunds.ShouldHaveSingleItem();
        refund.ReservationId.ShouldBe(world.ReservationId);
        refund.Reason.ShouldNotBeNullOrWhiteSpace();
        harness.Money.Settles.ShouldBeEmpty();
        harness.TempFiles().ShouldBeEmpty();
    }

    /// <summary>The reservation is settled by its own id, exactly once.</summary>
    [Fact]
    public async Task Ingest_SettlesReservation_OnSuccess()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideo(4096);
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));

        await harness.RunAsync(world.RenderJobId, Ct);

        IngestDb.Row(Database, world).ShouldBe("succeeded|-|true");
        harness.Money.Settles.Count.ShouldBe(1);
        harness.Money.Settles[0].Value.ShouldBe(world.ReservationId);
        harness.Money.Refunds.ShouldBeEmpty();
    }

    /// <summary>The <c>MarkIngesting</c> step and the poller release commit before the first byte is read (WT45).</summary>
    /// <remarks>
    /// The row is seeded <c>running</c> with a live poll instant: that is the only shape where the ingest's own
    /// step-2 write is observable, because <c>MarkIngesting</c> clears <c>next_poll_at</c> (a row that is already
    /// <c>ingesting</c> and still claimable cannot be produced by the poller, and seeding one would make the
    /// assertion below hold whether or not the ingest wrote anything).
    /// </remarks>
    [Fact]
    public async Task Ingest_CommitsMarkIngestingBeforeDownload()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct, new IngestSeed { Status = "running" });
        var video = harness.Downloads.ServeVideo(4096);
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));

        string? rowDuringDownload = null;
        string? pollDuringDownload = null;
        harness.Downloads.OnBeforeWrite = _ =>
        {
            rowDuringDownload = IngestDb.Row(Database, world);
            pollDuringDownload = SqlProbe.ScalarText(
                Database,
                $"SELECT (next_poll_at IS NULL)::text FROM render_jobs WHERE id = '{world.RenderJobId}'");
            return ValueTask.CompletedTask;
        };

        await harness.RunAsync(world.RenderJobId, Ct);

        rowDuringDownload.ShouldBe("ingesting|-|-", "the state change is committed in its own transaction");
        pollDuringDownload.ShouldBe("true", "the poller was released before the download started");
        IngestDb.Row(Database, world).ShouldBe("succeeded|-|true");
    }

    /// <summary>Every asset the ingest writes is stamped into the abandoned-render window (WT56).</summary>
    [Fact]
    public async Task Ingest_StampsUnapprovedRetentionUntil_OnEveryLiveAsset()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideo(4096);
        var subtitles = harness.Downloads.ServeSubtitles();
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, subtitles), IngestBodies.Check()));

        await harness.RunAsync(world.RenderJobId, Ct);

        var assets = IngestDb.Assets(Database, world, IngestHarness.UnapprovedRetention);
        assets.Count.ShouldBe(2);
        assets.ShouldAllBe(row => row.EndsWith("|true", StringComparison.Ordinal));
        IngestHarness.UnapprovedRetention.ShouldBe(IngestHarness.Start.AddDays(IngestHarness.UnapprovedRetentionDays));
    }

    /// <summary>
    /// The last scheduler attempt cannot hand the row back: it refunds once, raises the event with the final flag
    /// and takes the episode out of rendering.
    /// </summary>
    /// <remarks>
    /// The row is the render's second and last attempt (WT50), so <c>RenderRetryPolicy</c> refuses a third one and
    /// the verdict is final — the last allowed scheduler attempt ends the row itself instead of leaving it
    /// <c>ingesting</c> with no claim on it (WT52, <c>IJobAttempt</c>).
    /// </remarks>
    [Fact]
    public async Task LastAttempt_InvalidOutput_FailsRenderOutputInvalid_RaisesRenderFailed_WithFinalFlag_RefundsOnce()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct, new IngestSeed { Attempt = IngestHarness.MaxAttempts });
        var video = harness.Downloads.ServeVideoBytes(Encoding.UTF8.GetBytes("nts-video-canary-aaaa"), tamper: true);
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));

        await harness.RunLastAttemptAsync(world.RenderJobId);

        IngestDb.Row(Database, world).ShouldBe("failed|render_output_invalid|-");
        IngestDb.FailedPayload(Database, world).ShouldBe($"{world.RenderJobId}|render_output_invalid|true");
        IngestDb.Outbox(Database, world, RenderFailed).ShouldBe(1);
        IngestDb.JobCount(Database, world).ShouldBe(
            1, "the row is the policy's last attempt, so the transient code queues no third render attempt");
        IngestDb.Episode(Database, world).ShouldBe("story_approved|-");

        var refund = harness.Money.Refunds.ShouldHaveSingleItem();
        refund.ReservationId.ShouldBe(world.ReservationId);
        harness.TempFiles().ShouldBeEmpty();
    }

    /// <summary>The same failure one attempt earlier is a rethrow the scheduler can retry, and never a refund.</summary>
    [Fact]
    public async Task NonLastAttempt_InvalidOutput_RethrowsAndDoesNotRefund()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideoBytes(Encoding.UTF8.GetBytes("nts-video-canary-aaaa"), tamper: true);
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));

        var thrown = await harness.ThrowsAsync(world.RenderJobId, 1, IngestHarness.MaxAttempts);

        // The job's own typed failure (a type deriving from InvalidOperationException) is the one exception a
        // retried attempt rethrows unchanged (N5).
        thrown.ShouldBeAssignableTo<InvalidOperationException>().Message.ShouldContain("render_output_invalid");
        harness.Money.Refunds.ShouldBeEmpty("the reservation is carried by the row that will be retried");
        harness.Money.Settles.ShouldBeEmpty();
        harness.Blobs.Puts.ShouldBeEmpty();
        harness.TempFiles().ShouldBeEmpty();
        IngestDb.Outbox(Database, world, RenderSucceeded).ShouldBe(0);
    }

    /// <summary>The stored keys and hashes are the BD7/BD10 shape, bucket included.</summary>
    [Fact]
    public async Task BlobKey_MatchesBd7Shape()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideo(4096);
        var subtitles = harness.Downloads.ServeSubtitles();
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, subtitles), IngestBodies.Check()));

        await harness.RunAsync(world.RenderJobId, Ct);

        world.VideoKey.ShouldBe($"media/{world.UserId:D}/{world.EpisodeId:D}/{world.RenderJobId:D}/video.mp4");
        world.SubtitlesKey.ShouldBe($"media/{world.UserId:D}/{world.EpisodeId:D}/{world.RenderJobId:D}/subtitles.srt");
        IngestHarness.MediaBucket.ShouldStartWith("nt-media-");
        harness.Blobs.Puts.Select(put => put.Bucket).ShouldAllBe(bucket => bucket == IngestHarness.MediaBucket);
        harness.Blobs.Puts.Select(put => put.Key).ShouldContain(world.VideoKey);
        harness.Blobs.Puts.Select(put => put.Key).ShouldContain(world.SubtitlesKey);

        // The stored hash is the hash of the row it belongs to (BlobInfo.Sha256 == assets.sha256).
        var assets = IngestDb.Assets(Database, world, IngestHarness.UnapprovedRetention);
        assets.ShouldContain(row => row.Contains(video.DeclaredSha256, StringComparison.Ordinal));
        assets.ShouldContain(row => row.Contains(subtitles.DeclaredSha256, StringComparison.Ordinal));
    }

    /// <summary>The adapter is only asked to clean up after the ingest's own transaction committed (BD7).</summary>
    [Fact]
    public async Task AdapterDelete_HappensAfterCommit()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideo(4096);
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));

        string? rowAtDelete = null;
        harness.Adapter.OnDelete = () =>
        {
            rowAtDelete = IngestDb.Row(Database, world);
            return ValueTask.CompletedTask;
        };

        await harness.RunAsync(world.RenderJobId, Ct);

        rowAtDelete.ShouldBe("succeeded|-|true", "the state is durable before the cleanup is requested");
        harness.Adapter.DeleteCalls.ShouldBe(1);
        var entries = harness.Order.Entries.ToList();
        entries.ShouldContain($"blob.put:{world.VideoKey}");
        entries[entries.Count - 1].ShouldBe($"adapter.delete:{world.TaskId}");
        entries.IndexOf($"blob.put:{world.VideoKey}").ShouldBeLessThan(entries.IndexOf($"adapter.delete:{world.TaskId}"));
    }

    /// <summary>A cleanup the adapter refuses is logged and never fails an ingest that already committed.</summary>
    [Fact]
    public async Task AdapterDeleteFailure_DoesNotFailIngest()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideo(4096);
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));
        harness.Render.EnqueueDelete(Error.Failure("adapter_delete_failed", "scripted cleanup failure"));

        await harness.RunAsync(world.RenderJobId, Ct);

        IngestDb.Row(Database, world).ShouldBe("succeeded|-|true");
        IngestDb.Episode(Database, world).ShouldStartWith("video_review|");
        harness.Adapter.DeleteCalls.ShouldBe(1);
        harness.Blobs.Keys.ShouldContain(world.VideoKey);
        harness.Blobs.Deletes.ShouldBeEmpty();
        harness.IngestLines().ShouldContain(
            entry => entry.EventId == 6318 || entry.EventId == 6319 || entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    /// <summary>A busy adapter (409) is asked once more, and a second refusal is still not the ingest's problem.</summary>
    [Fact]
    public async Task AdapterDeleteBusy_IsRetriedOnce_AndStillDoesNotFailIngest()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideo(4096);
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));
        harness.Render.EnqueueDelete(Error.Conflict("adapter_task_busy", "scripted: the adapter is still busy"));
        harness.Render.EnqueueDelete(Error.Conflict("adapter_task_busy", "scripted: the adapter is still busy"));

        await harness.RunAsync(world.RenderJobId, Ct);

        IngestDb.Row(Database, world).ShouldBe("succeeded|-|true");
        harness.Adapter.DeleteCalls.ShouldBe(2, "a busy adapter is retried exactly once");
        harness.Blobs.Keys.Count.ShouldBe(1);
        harness.Blobs.Deletes.ShouldBeEmpty();
    }

    /// <summary>Neither a cap trip nor a refused verdict leaves a spool file behind.</summary>
    [Fact]
    public async Task TempDirectory_EmptyAfterFailure()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);

        var mismatch = await IngestWorlds.SeedAsync(Database, Ct);
        var tampered = harness.Downloads.ServeVideoBytes(Encoding.UTF8.GetBytes("nts-video-canary-aaaa"), tamper: true);
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(mismatch, IngestBodies.Output(tampered, null), IngestBodies.Check()));
        await harness.RunLastAttemptAsync(mismatch.RenderJobId);
        IngestDb.Row(Database, mismatch).ShouldStartWith("failed|");
        harness.TempFiles().ShouldBeEmpty();

        var quality = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideo(4096);
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(
                quality,
                IngestBodies.Output(video, null),
                IngestBodies.Check(errors: ["ffmpeg: torn frame at 00:01:02"])));
        await harness.RunLastAttemptAsync(quality.RenderJobId);
        IngestDb.Row(Database, quality).ShouldStartWith("failed|");
        harness.TempFiles().ShouldBeEmpty();
    }

    /// <summary>A store that answers an error on a retryable attempt propagates with nothing written.</summary>
    [Fact]
    public async Task BlobStoreUnavailable_NonLastAttempt_RethrowsWithNothingWritten()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideo(4096);
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));
        harness.Blobs.PutFailure = Error.Failure("storage_unavailable", "scripted: the media store is down");

        var thrown = await harness.ThrowsAsync(world.RenderJobId, 1, IngestHarness.MaxAttempts);

        // The thrown code is the D5 outcome of the failure (a store fault has no D5 code of its own and maps to
        // render_output_invalid — task §Failure handling); the store's own code is what the job logs.
        thrown.Message.ShouldContain("render_output_invalid");
        harness.IngestLines().ShouldContain(
            entry => entry.Message.Contains("storage_unavailable", StringComparison.Ordinal),
            "the store's own code is what the attempt's log names");
        harness.Blobs.Keys.ShouldBeEmpty();
        harness.Money.Refunds.ShouldBeEmpty();
        harness.Money.Settles.ShouldBeEmpty();
        harness.TempFiles().ShouldBeEmpty();
        IngestDb.AssetCount(Database, world).ShouldBe(0);
        IngestDb.Row(Database, world).ShouldNotBe("succeeded|-|true");
    }

    /// <summary>On the last attempt the same store failure refunds once and ends the render with the final flag.</summary>
    /// <remarks>
    /// The row is the render's second and last attempt (WT50), so the code the store failure maps to is final: no
    /// third attempt is queued and the attempt is settled in-job (WT52).
    /// </remarks>
    [Fact]
    public async Task BlobStoreUnavailable_LastAttempt_RefundsOnceAndRaisesRenderFailedFinal()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct, new IngestSeed { Attempt = IngestHarness.MaxAttempts });
        var video = harness.Downloads.ServeVideo(4096);
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));
        harness.Blobs.PutFailure = Error.Failure("storage_unavailable", "scripted: the media store is down");

        await harness.RunLastAttemptAsync(world.RenderJobId);

        IngestDb.Row(Database, world).ShouldBe("failed|render_output_invalid|-");
        IngestDb.Outbox(Database, world, RenderFailed).ShouldBe(1);
        IngestDb.FailedPayload(Database, world).ShouldBe($"{world.RenderJobId}|render_output_invalid|true");
        IngestDb.JobCount(Database, world).ShouldBe(
            1, "the row is the policy's last attempt, so no third render attempt is queued");
        IngestDb.Episode(Database, world).ShouldBe("story_approved|-");
        harness.Blobs.Keys.ShouldBeEmpty();
        harness.Money.Refunds.ShouldHaveSingleItem().ReservationId.ShouldBe(world.ReservationId);
        harness.TempFiles().ShouldBeEmpty();
    }

    /// <summary>A cancelled render is never handed back for a retry, whatever the attempt number says.</summary>
    [Fact]
    public async Task CancelRequested_InvalidOutput_IsFinal_NoRetryRow()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct, new IngestSeed { CancelRequested = true });
        var video = harness.Downloads.ServeVideoBytes(Encoding.UTF8.GetBytes("nts-video-canary-aaaa"), tamper: true);
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));

        // Deliberately a non-last attempt: the cancel flag, not the attempt counter, is what makes this final.
        await harness.RunAsync(world.RenderJobId, 1, IngestHarness.MaxAttempts);

        IngestDb.Row(Database, world).ShouldBe("failed|render_output_invalid|-");
        IngestDb.FailedPayload(Database, world).ShouldBe($"{world.RenderJobId}|render_output_invalid|true");
        IngestDb.JobCount(Database, world).ShouldBe(1, "a cancelled render is never retried");
        IngestDb.Episode(Database, world).ShouldBe("story_approved|-");
        harness.Money.Refunds.ShouldHaveSingleItem().ReservationId.ShouldBe(world.ReservationId);
        harness.TempFiles().ShouldBeEmpty();
    }

    /// <summary>An id no row carries is a logged no-op: no node call, no write.</summary>
    [Fact]
    public async Task UnknownRenderJobId_IsALoggedNoOp()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var missing = Guid.CreateVersion7();

        await harness.RunAsync(missing, Ct);

        var line = harness.IngestLines().ShouldHaveSingleItem();
        line.EventId.ShouldBe(6300);
        line.Message.ShouldBe($"render_ingest_job_missing renderJobId={missing}");
        harness.Render.StatusCalls.ShouldBe(0);
        harness.Downloads.Calls.ShouldBeEmpty();
        harness.Blobs.Puts.ShouldBeEmpty();
        harness.Adapter.DeleteCalls.ShouldBe(0);
    }

    /// <summary>A row the poller already finished, or never handed over, is a logged no-op as well.</summary>
    [Fact]
    public async Task TerminalJob_IsALoggedNoOp()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var terminal = await IngestWorlds.SeedAsync(Database, Ct, new IngestSeed { Status = "succeeded" });

        await harness.RunAsync(terminal.RenderJobId, Ct);

        harness.IngestLines().ShouldHaveSingleItem().EventId.ShouldBe(6301);
        harness.Downloads.Calls.ShouldBeEmpty();
        harness.Blobs.Puts.ShouldBeEmpty();
        harness.Adapter.DeleteCalls.ShouldBe(0);
        IngestDb.Row(Database, terminal).ShouldBe("succeeded|-|-");
    }

    /// <summary>A row the poller has not handed over yet is skipped with its own line.</summary>
    [Fact]
    public async Task NotHandedOverJob_IsALoggedNoOp()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var queued = await IngestWorlds.SeedAsync(Database, Ct, new IngestSeed { Status = "queued" });

        await harness.RunAsync(queued.RenderJobId, Ct);

        harness.IngestLines().ShouldHaveSingleItem().EventId.ShouldBe(6302);
        harness.Downloads.Calls.ShouldBeEmpty();
        harness.Blobs.Puts.ShouldBeEmpty();
    }

    /// <summary>
    /// An I/O failure while spooling still hands the attempt back to the scheduler and leaves neither a file nor a
    /// row behind — but the raw exception does not travel with it. Hangfire persists the message and the stack of
    /// what it catches and the Api's dashboard shows them, so an IO or storage exception would put the spool path,
    /// the bucket or the endpoint in front of whoever opens the job: only the job id, the stage and the exception's
    /// type name travel (S5).
    /// </summary>
    [Fact]
    public async Task DownloadDestinationIOException_LeavesNothingBehind()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideoThrow(
            Encoding.UTF8.GetBytes("nts-video-canary-aaaa"),
            new IOException("scripted: the spool disk refused a write"));
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));

        var thrown = await harness.ThrowsAsync(world.RenderJobId, 1, IngestHarness.MaxAttempts);

        var wrapped = thrown.ShouldBeOfType<InvalidOperationException>();
        wrapped.Message.ShouldContain(world.RenderJobId.ToString());
        wrapped.Message.ShouldContain("IOException", Case.Insensitive);
        wrapped.Message.ShouldNotContain("scripted: the spool disk refused a write");
        wrapped.InnerException.ShouldBeNull("neither does the raw exception object");
        IngestDb.Row(Database, world).ShouldStartWith("ingesting|");
        harness.TempFiles().ShouldBeEmpty();
        harness.Blobs.Puts.ShouldBeEmpty();
        harness.Money.Refunds.ShouldBeEmpty();
    }

    /// <summary>
    /// The wire copy is tampered with while the node's manifest still declares the honest hash: the same refusal
    /// as <see cref="Sha256Mismatch_FailsRenderOutputInvalid_NoPartialWrite"/>, from the other side. No mock-mpt
    /// byte-tampering fixture exists in the TestKit (its <c>SyntheticDownloadHandler</c> is a transport fake for
    /// the real client), so the scripted download client covers this path.
    /// </summary>
    [Fact]
    public async Task TamperedByte_FailsRenderOutputInvalid()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        // The ingest downloads the *first* scripted answer of a kind, so the tampered one must be scripted first:
        // the honest answer is only the declaration both bodies carry.
        var tampered = harness.Downloads.ServeVideo(4096, tamper: true);
        var honest = harness.Downloads.ServeVideo(4096);
        tampered.DeclaredSha256.ShouldBe(honest.DeclaredSha256, "the declaration is the untampered file's hash");
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(tampered, null), IngestBodies.Check()));

        // The wire copy disagrees with the manifest: the verdict is written on the scheduler's last attempt (WT52).
        await harness.RunLastAttemptAsync(world.RenderJobId);

        IngestDb.Row(Database, world).ShouldBe("failed|render_output_invalid|-");
        harness.Blobs.Puts.ShouldBeEmpty();
        harness.TempFiles().ShouldBeEmpty();
    }

    /// <summary>A status body without a usable check fails closed: the verdict never assumes the best.</summary>
    [Fact]
    public async Task CheckMissing_VerdictFailsClosed_RenderQualityFailed()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideo(4096);
        harness.Render.EnqueueStatus(
            IngestBodies.SucceededRaw(world, IngestBodies.Output(video, null), IngestBodies.EmptyCheck()));

        await harness.RunAsync(world.RenderJobId, Ct);

        IngestDb.Row(Database, world).ShouldBe("failed|render_quality_failed|false");
        IngestDb.FailedPayload(Database, world).ShouldBe($"{world.RenderJobId}|render_quality_failed|true");

        // The stored video is written before the verdict and removed with it (the same rule as the other refused
        // verdict): no bytes stay in nt-media and no asset row exists. Both deterministic keys go, not only the
        // key this attempt PUT (C5/S3).
        harness.Blobs.Puts.Select(put => put.Key).ShouldBe([world.VideoKey]);
        harness.Blobs.Deletes.Select(entry => entry.Key).ShouldBe([world.VideoKey, world.SubtitlesKey]);
        harness.Blobs.Keys.ShouldBeEmpty();
        IngestDb.AssetCount(Database, world).ShouldBe(0);
        harness.Money.Refunds.ShouldHaveSingleItem().ReservationId.ShouldBe(world.ReservationId);
    }

    /// <summary>The real job type carries the attempt count these tests publish, so the two sides agree.</summary>
    [Fact]
    public void JobPolicy_DeclaresFiveRetries()
    {
        // `IngestHarness.MaxAttempts` reads this attribute by reflection; this pins the number itself in the
        // folder that owns the decision, so the filter's unit test stays free of this feature's policy value.
        IngestHarness.MaxAttempts.ShouldBe(6, "one attempt plus the five retries the job declares");
    }

    /// <summary>The graph the job runs in resolves every dependency it takes from the scope.</summary>
    [Fact]
    public async Task WorkerGraph_ResolvesEveryIngestDependency()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        using var scope = harness.Host.CreateScope();

        var missing = IngestDependencies
            .Where(type => scope.ServiceProvider.GetService(type) is null)
            .Select(type => type.Name)
            .ToList();

        missing.ShouldBeEmpty(
            "the Worker graph must register everything the ingest job resolves, or the job cannot run in production");
    }
}
