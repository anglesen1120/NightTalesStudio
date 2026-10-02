using System.Globalization;
using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.MptAdapter.Tests.Fakes;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using Shouldly;
using Xunit;

namespace NightTales.MptAdapter.Tests;

/// <summary>
/// QA edge cases for 11.3.5-T2 (added by the independent QA pass, not by the implementer): MPT unreachable
/// while polling (attempts, log back-off, ops alert), durability across a store reopen, the deleted-row 404,
/// the queued-state-4 boundary, a cancel racing a submit acceptance, the cleaner's root confinement, the
/// purge boundary and <c>OpenOutputAsync</c>.
/// </summary>
public sealed class QaEdgeCaseTests
{
    private const string MptTaskId = "67b5ecfe-c283-47d1-a265-382224c1c207";

    private static readonly string[] Timestamps = ["submitted_at", "started_at", "finished_at"];

    [Fact]
    public async Task MptQueued_ZeroProgress_StaysQueued()
    {
        await using var harness = await AdapterHarness.StartAsync(withHost: true);
        var record = await SeedSubmittedAsync(harness);

        // S11/C10: MPT accepted the task and queued it behind another — state 4, progress 0.
        harness.Mpt.Script(MptTaskId, 4, 0);
        await harness.PollOnceAsync();

        var row = await harness.RowAsync(record.TaskId);
        row.State.ShouldBe(RenderStates.Queued);
        row.Stage.ShouldBe(RenderStages.Queued);
        row.Percent.ShouldBe(0);
        row.MptLastSnapshot!.State.ShouldBe(4);
        row.MptLastSnapshot.Warnings.ShouldBeEmpty("an absent warnings member is stored as []");

        StatusComparison.ShouldMatchFixture(
            "render-status.v1.queued.json",
            await harness.StatusJsonAsync(record.TaskId),
            Timestamps);
    }

    [Fact]
    public async Task Poll_WhileMptUnreachable_StayNonTerminal_BackoffAndOpsAlert()
    {
        await using var harness = await AdapterHarness.StartAsync();
        var record = await SeedSubmittedAsync(harness);

        // MPT is down: the connection error must re-read the process start (D6) and count an attempt.
        await harness.Mpt.DisposeAsync();

        var before = await harness.Store.TryGetAsync(record.TaskId, CancellationToken.None);

        await harness.PollOnceAsync();

        var row = await harness.RowAsync(record.TaskId);
        row.ShouldBe(before, "an unreachable MPT must not change the row (05 D5: render_timeout is the Worker's)");
        row.IsTerminal.ShouldBeFalse();
        harness.FakeStartReader.RefreshCount.ShouldBeGreaterThanOrEqualTo(1,
            "the process start is re-read after every MPT connection error");

        // Log back-off: the second consecutive failure inside the back-off window is not logged.
        await harness.PollOnceAsync();
        var noAnswer = harness.Logs.Records.Count(record_ => record_.Contains("no MPT answer", StringComparison.Ordinal));
        noAnswer.ShouldBe(1, "exponential log back-off (2 s → 30 s cap)");

        // attempts > 900 (30 min at 2 s) logs the ops alert line, once.
        await SetAttemptsAsync(harness.Config.TasksDbPath, record.TaskId, MptTaskPoller.OpsAlertAttempts);
        await harness.PollOnceAsync();

        harness.Logs.Contains("ops alert").ShouldBeTrue();
        harness.Logs.Records.Count(record_ => record_.Contains("ops alert", StringComparison.Ordinal)).ShouldBe(1);

        await harness.PollOnceAsync();
        harness.Logs.Records.Count(record_ => record_.Contains("ops alert", StringComparison.Ordinal))
            .ShouldBe(1, "the alert is logged once per task");

        (await AttemptsAsync(harness.Config.TasksDbPath, record.TaskId)).ShouldBeGreaterThanOrEqualTo(
            MptTaskPoller.OpsAlertAttempts + 2);

        var after = await harness.RowAsync(record.TaskId);
        after.IsTerminal.ShouldBeFalse("the poller never marks an unreachable task failed");
    }

    [Fact]
    public async Task StoreReopen_PollingResumes_AndDeletedRowAnswers404()
    {
        await using var before = await AdapterHarness.StartAsync();
        var record = await SeedSubmittedAsync(before);

        // The pre-restart half: the task was submitted and polled once.
        before.Mpt.Script(MptTaskId, 4, 40);
        await before.PollOnceAsync();
        (await before.RowAsync(record.TaskId)).State.ShouldBe(RenderStates.Running);
        var dataDir = before.Config.DataDir;

        // "Restart": a fresh adapter over the same DataDir — new store, client, poller, fake MPT.
        await using var after = await AdapterHarness.StartAsync(
            options => options.DataDir = dataDir,
            withHost: true);

        var restored = await after.Store.TryGetAsync(record.TaskId, CancellationToken.None);
        restored.ShouldNotBeNull();
        restored.BackendTaskId.ShouldBe(MptTaskId, "polling resumes from backend_task_id");
        restored.State.ShouldBe(RenderStates.Running);

        // The restarted MPT still knows the task: the poller keeps driving it to a terminal state.
        after.Mpt.Script(MptTaskId, -1, 40, failedStage: "audio");
        await after.PollOnceAsync();
        var terminal = await after.RowAsync(record.TaskId);
        terminal.State.ShouldBe(RenderStates.Failed, "the task reaches a terminal state after the restart");
        terminal.Outcome!.Code.ShouldBe(RenderOutcomeCodes.TtsFailed);

        // A row that is gone (purged or never submitted) answers the Worker's poll 404 → render_lost (ruling T2).
        (await after.Store.DeleteAsync(record.TaskId, CancellationToken.None)).ShouldBeTrue();
        var response = await after.SendAsync(HttpMethod.Get, $"/v1/renders/{record.TaskId}");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AdapterHarness.ReadErrorCodeAsync(response)).ShouldBe(AdapterErrorCodes.TaskUnknown);
    }
    /// <summary>
    /// The poller works on a snapshot: TryGet → HTTP GET → UpdateAsync. A cancel landing between the read and
    /// the write must survive — <c>cancel_requested</c> is a one-way flag (a cancelled task never becomes
    /// succeeded, 05 D4). The store's merge must not let the stale snapshot revert it.
    /// </summary>
    [Fact]
    public async Task StalePollObservation_MustNotRevertCancelRequested()
    {
        await using var harness = await AdapterHarness.StartAsync();
        var record = AdapterHarness.NewRecord(
            Guid.CreateVersion7(), Guid.CreateVersion7(), backendTaskId: MptTaskId);
        await harness.SeedAsync(record);

        // The poller's stale snapshot (read before the cancel).
        var snapshot = await harness.Store.TryGetAsync(record.TaskId, CancellationToken.None);

        // The cancel lands while the poller is asking MPT.
        (await harness.Store.UpdateAsync(
                record with { CancelRequested = true }, CancellationToken.None))
            .ShouldBeTrue();

        // The poller writes its stale observation (state running / percent up, flag still false).
        (await harness.Store.UpdateAsync(
                snapshot! with { State = RenderStates.Running, Percent = 40 }, CancellationToken.None))
            .ShouldBeTrue();

        var row = await harness.RowAsync(record.TaskId);
        row.State.ShouldBe(RenderStates.Running);
        row.CancelRequested.ShouldBeTrue(
            "a stale write must not revert a cancel that already landed (05 D4: cancelled never succeeds)");
    }


    [Fact]
    public async Task StatusRead_MakesNoMptCalls()
    {
        await using var harness = await AdapterHarness.StartAsync(withHost: true);
        var record = await SeedSubmittedAsync(harness);
        harness.Mpt.Script(MptTaskId, 4, 40);

        // F19: a Worker poll answers from the store; with the poller stopped nothing reaches MPT.
        var response = await harness.SendAsync(HttpMethod.Get, $"/v1/renders/{record.TaskId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        harness.Mpt.RequestCount.ShouldBe(0);

        // The output route is store-only too: a running task has no published output yet → 409, still no MPT call.
        var output = await harness.SendAsync(HttpMethod.Get, $"/v1/renders/{record.TaskId}/video");
        output.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AdapterHarness.ReadErrorCodeAsync(output)).ShouldBe(AdapterErrorCodes.OutputNotReady);
        harness.Mpt.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task CancelRacingAcceptance_SurvivesRecordBackendAcceptance()
    {
        await using var harness = await AdapterHarness.StartAsync();
        var record = AdapterHarness.NewRecord(Guid.CreateVersion7(), Guid.CreateVersion7());
        await harness.SeedAsync(record);

        // The store half of the 11.3.5-T1 race: cancel lands while backend.SubmitAsync is still in flight.
        (await harness.Store.UpdateAsync(record with { CancelRequested = true }, CancellationToken.None))
            .ShouldBeTrue();
        (await harness.Store.RecordBackendAcceptanceAsync(
                record.TaskId, MptTaskId, DateTimeOffset.UtcNow, CancellationToken.None))
            .ShouldBeTrue();

        var row = await harness.RowAsync(record.TaskId);
        row.CancelRequested.ShouldBeTrue("the racing cancel must survive the submit result");
        row.BackendTaskId.ShouldBe(MptTaskId);
        row.BackendStartedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Cleaner_NeverEscapesItsRoots()
    {
        await using var harness = await AdapterHarness.StartAsync();
        var record = AdapterHarness.NewRecord(Guid.CreateVersion7(), Guid.CreateVersion7());
        await harness.SeedAsync(record);
        harness.WriteTaskFiles(record.TaskId, MptTaskId);

        // A sibling task's files and a file outside every root must survive another task's cleanup.
        var siblingMptDir = Path.Combine(harness.Config.MptTasksDir, "2db4a2f6-1111-4c55-9abc-000000000001");
        Directory.CreateDirectory(siblingMptDir);
        var siblingFile = Path.Combine(siblingMptDir, "final.mp4");
        File.WriteAllText(siblingFile, "video");
        var siblingRelay = Path.Combine(harness.Config.RelayDir, "3cc5b307-2222-4d66-8bcd-000000000002.jsonl");
        File.WriteAllText(siblingRelay, "{}");
        var outside = Path.Combine(harness.Root, "outside-root.txt");
        File.WriteAllText(outside, "keep");

        await harness.Cleaner.CleanAsync(record.TaskId, MptTaskId, CancellationToken.None);

        harness.TaskFilesExist(record.TaskId, MptTaskId).ShouldBeFalse();
        Directory.Exists(siblingMptDir).ShouldBeTrue("a sibling MPT task dir is another task's property");
        File.Exists(siblingRelay).ShouldBeTrue("a sibling relay file is another task's property");
        File.Exists(outside).ShouldBeTrue("cleanup must never escape the configured roots");

        // Idempotent: cleaning a second time does not throw and still removes nothing else.
        await harness.Cleaner.CleanAsync(record.TaskId, MptTaskId, CancellationToken.None);
        Directory.Exists(siblingMptDir).ShouldBeTrue();
        File.Exists(outside).ShouldBeTrue();
    }

    [Fact]
    public async Task PurgeCleaned_ExactBoundaryIsKept()
    {
        var path = Path.Combine(Path.GetTempPath(), "nts-qa-store-" + Guid.NewGuid().ToString("N"), "tasks.db");
        try
        {
            await using var store = new SqliteTaskStore(
                new SqliteStoreOptions(path), Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteTaskStore>.Instance);
            await store.MigrateAsync(CancellationToken.None);

            var boundary = DateTimeOffset.UtcNow;
            var stale = AdapterHarness.NewRecord(Guid.CreateVersion7(), Guid.CreateVersion7()) with
            {
                State = RenderStates.Succeeded,
                Stage = RenderStages.Done,
                Percent = 100,
                CleanedAt = boundary.AddDays(-7).AddSeconds(-1),
            };
            var kept = AdapterHarness.NewRecord(Guid.CreateVersion7(), Guid.CreateVersion7()) with
            {
                State = RenderStates.Succeeded,
                Stage = RenderStages.Done,
                Percent = 100,
                CleanedAt = boundary.AddDays(-7).AddSeconds(1),
            };
            (await store.InsertAsync(stale, CancellationToken.None)).ShouldBeTrue();
            (await store.InsertAsync(kept, CancellationToken.None)).ShouldBeTrue();

            (await store.PurgeCleanedAsync(boundary.AddDays(-7), CancellationToken.None)).ShouldBe(1);
            (await store.TryGetAsync(stale.TaskId, CancellationToken.None)).ShouldBeNull();
            (await store.TryGetAsync(kept.TaskId, CancellationToken.None)).ShouldNotBeNull(
                "only rows cleaned BEFORE the window go");
        }
        finally
        {
            var directory = Path.GetDirectoryName(path);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task OpenOutput_NotPublishedUntilT3()
    {
        await using var harness = await AdapterHarness.StartAsync();
        var output = await harness.Client.OpenOutputAsync(Guid.CreateVersion7(), OutputKind.Video, CancellationToken.None);

        output.IsError.ShouldBeTrue();
        output.FirstError.Code.ShouldBe(AdapterErrorCodes.OutputAbsent);
    }

    /// <summary>
    /// The MPT-side corner of the completion hand-off: MPT may report progress 100 while state is still 4
    /// (the task dict's fields are not updated atomically). The adapter must not treat that as `done`, and the
    /// checking hand-off must fire exactly once when state 1 arrives.
    /// </summary>
    [Fact]
    public async Task State4AtProgress100_DoesNotStuckOrDoublePublish()
    {
        await using var harness = await AdapterHarness.StartAsync();
        var record = await SeedSubmittedAsync(harness);

        harness.Mpt.Script(MptTaskId, 4, 80);
        await harness.PollOnceAsync();

        // Progress hits 100 while state is still 4: the task is still running, not done.
        harness.Mpt.Script(MptTaskId, 4, 100);
        await harness.PollOnceAsync();
        var running = await harness.RowAsync(record.TaskId);
        running.State.ShouldBe(RenderStates.Running);
        running.Stage.ShouldNotBe(RenderStages.Done,
            "a task MPT still reports as running (state 4) is not done");
        running.Percent.ShouldBeLessThanOrEqualTo(99, "100 is written only with a terminal state or checking");

        harness.Mpt.Script(MptTaskId, 1, 100);
        await harness.PollOnceAsync();
        var row = await harness.RowAsync(record.TaskId);
        row.Stage.ShouldBe(RenderStages.Checking);
        row.Percent.ShouldBe(100);

        await harness.PollOnceAsync();
        var published = await harness.ReadPublishedAsync(3, TimeSpan.FromMilliseconds(500));
        published.Count.ShouldBe(1, "the post-processor hand-off must fire exactly once per task");
    }

    /// <summary>
    /// The Worker's contract vocabulary is closed (<c>adapter_*</c>): an MPT-side failure on a terminal delete
    /// must surface as an adapter error, never as an <c>mpt_*</c> code.
    /// </summary>
    [Fact]
    public async Task Delete_WhenMptUnreachable_AnswersAdapterErrorNotMptCode()
    {
        await using var harness = await AdapterHarness.StartAsync(withHost: true);
        var record = AdapterHarness.NewRecord(
            AdapterHarness.FixtureTaskId,
            AdapterHarness.FixtureRenderJobId,
            backendTaskId: MptTaskId,
            state: RenderStates.Succeeded,
            stage: RenderStages.Done,
            percent: 100,
            output: new RenderOutputV1(new RenderFileV1(98_100_000, "sha-video", null), null, 301.37));
        await harness.SeedAsync(record);

        await harness.Mpt.DisposeAsync();

        var response = await harness.SendAsync(HttpMethod.Delete, $"/v1/renders/{record.TaskId}");

        // The code must come from the closed adapter_* set; a backend-side failure is node-unhealthy.
        var code = await AdapterHarness.ReadErrorCodeAsync(response);
        code.ShouldBe(AdapterErrorCodes.NodeUnhealthy,
            "the mpt_* codes never reach a status body (05.4.1-T2 §1): expected adapter_node_unhealthy, "
            + $"got '{code}'");
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    private static async Task<RenderTaskRecord> SeedSubmittedAsync(AdapterHarness harness)
    {
        var record = AdapterHarness.NewRecord(
            AdapterHarness.FixtureTaskId,
            AdapterHarness.FixtureRenderJobId,
            backendTaskId: MptTaskId);
        await harness.SeedAsync(record);
        return record;
    }

    private static async Task<int> AttemptsAsync(string databasePath, Guid taskId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Default Timeout=5;Pooling=false");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT attempts FROM tasks WHERE task_id = $task_id;";
        command.Parameters.AddWithValue("$task_id", taskId.ToString("D"));
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task SetAttemptsAsync(string databasePath, Guid taskId, int attempts)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Default Timeout=5;Pooling=false");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE tasks SET attempts = $attempts WHERE task_id = $task_id;";
        command.Parameters.AddWithValue("$attempts", attempts);
        command.Parameters.AddWithValue("$task_id", taskId.ToString("D"));
        await command.ExecuteNonQueryAsync();
    }
}
