using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.MptAdapter.Tests.Fakes;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using Shouldly;
using Xunit;

namespace NightTales.MptAdapter.Tests;

/// <summary>
/// The durable store of plan §3.1 (11 README D6): the DDL and its <c>schema_meta</c> version, WAL, a row that
/// survives a reopen, two writers sharing one file, and the 7-day purge of cleaned rows. Real SQLite only —
/// nothing here needs MPT or a GPU (D18).
/// </summary>
public sealed class SqliteTaskStoreTests
{
    private static readonly string[] Warnings = ["warn-1", "warn-2"];

    [Fact]
    public async Task Migrate_CreatesSchemaAndVersion()
    {
        var path = NewDatabasePath();
        try
        {
            await using (var store = NewStore(path))
            {
                await store.MigrateAsync(CancellationToken.None);
                await store.MigrateAsync(CancellationToken.None);
            }

            await using var connection = await OpenAsync(path);
            (await ScalarAsync(connection, "SELECT value FROM schema_meta WHERE key = 'version';"))
                .ShouldBe(SqliteTaskStore.SchemaVersion);

            var tables = await NamesAsync(connection, "SELECT name FROM sqlite_master WHERE type = 'table';");
            tables.ShouldContain("tasks");
            tables.ShouldContain("schema_meta");

            var indexes = await NamesAsync(connection, "SELECT name FROM sqlite_master WHERE type = 'index';");
            indexes.ShouldContain("ux_tasks_job_body");
            indexes.ShouldContain("ix_tasks_state");
            indexes.ShouldContain("ix_tasks_cleaned");
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task WalEnabled()
    {
        var path = NewDatabasePath();
        try
        {
            await using (var store = NewStore(path))
            {
                await store.MigrateAsync(CancellationToken.None);
            }

            // journal_mode is persistent, so a second connection reads it back.
            await using var connection = await OpenAsync(path);
            (await ScalarAsync(connection, "PRAGMA journal_mode;")).ShouldBe("wal");
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task SurvivesReopen()
    {
        var path = NewDatabasePath();
        var check = JsonDocument.Parse("""{"schema":"render-check.v1","tool_version":"1.0.0"}""").RootElement.Clone();
        var images = JsonDocument.Parse("""{"schema":"images.v1","planned":4}""").RootElement.Clone();
        var record = AdapterHarness.NewRecord(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            backendTaskId: "67b5ecfe-c283-47d1-a265-382224c1c207",
            backendStartedAt: DateTimeOffset.UtcNow.AddMinutes(-30),
            state: RenderStates.Failed,
            stage: RenderStages.Images,
            percent: 40) with
        {
            Outcome = new RenderOutcomeV1(
                RenderOutcomeCodes.RenderLost,
                OutcomeMapper.MptTaskMissing,
                OutcomeMessages.For(RenderOutcomeCodes.RenderLost)),
            Check = check,
            Images = images,
            FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            MptLastSnapshot = new MptStateV1(null, 40, null, Warnings),
        };

        try
        {
            await using (var first = NewStore(path))
            {
                await first.MigrateAsync(CancellationToken.None);
                (await first.InsertAsync(record, CancellationToken.None)).ShouldBeTrue();
            }

            await using var second = NewStore(path);
            var reopened = await second.TryGetAsync(record.TaskId, CancellationToken.None);

            reopened.ShouldNotBeNull();
            reopened.BackendTaskId.ShouldBe(record.BackendTaskId);
            reopened.State.ShouldBe(RenderStates.Failed);
            reopened.Stage.ShouldBe(RenderStages.Images);
            reopened.Percent.ShouldBe(40);
            reopened.SubmittedAt.ShouldBe(record.SubmittedAt);
            reopened.BackendStartedAt.ShouldBe(record.BackendStartedAt);
            reopened.FinishedAt.ShouldBe(record.FinishedAt);
            reopened.Outcome.ShouldBe(record.Outcome);
            reopened.Check!.Value.GetRawText().ShouldBe(check.GetRawText());
            reopened.Images!.Value.GetRawText().ShouldBe(images.GetRawText());
            reopened.MptLastSnapshot!.State.ShouldBeNull();
            reopened.MptLastSnapshot.Progress.ShouldBe(40);
            reopened.MptLastSnapshot.Warnings.ShouldBe(Warnings);

            // The merge contract holds for this store too: percent never decreases and a terminal row is final.
            await second.UpdateAsync(reopened with { State = RenderStates.Running, Percent = 10 }, CancellationToken.None);
            var merged = await second.TryGetAsync(record.TaskId, CancellationToken.None);
            merged!.State.ShouldBe(RenderStates.Failed);
            merged.Percent.ShouldBe(40);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task ConcurrentWrites_Serialised()
    {
        var path = NewDatabasePath();
        try
        {
            await using var first = NewStore(path);
            await first.MigrateAsync(CancellationToken.None);
            await using var second = NewStore(path);

            var writers = new List<Task<bool>>();
            for (var index = 0; index < 40; index++)
            {
                writers.Add(first.InsertAsync(NewRow(), CancellationToken.None).AsTask());
                writers.Add(second.InsertAsync(NewRow(), CancellationToken.None).AsTask());
            }

            var results = await Task.WhenAll(writers);

            results.ShouldAllBe(inserted => inserted);
            (await first.CountNonTerminalAsync(CancellationToken.None)).ShouldBe(80);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task PurgeCleaned_AfterSevenDays()
    {
        var path = NewDatabasePath();
        var now = DateTimeOffset.UtcNow;
        try
        {
            await using var store = NewStore(path);
            await store.MigrateAsync(CancellationToken.None);

            var stale = NewRow() with
            {
                State = RenderStates.Succeeded,
                Stage = RenderStages.Done,
                Percent = 100,
                CleanedAt = now.AddDays(-8),
            };
            var recent = NewRow() with
            {
                State = RenderStates.Succeeded,
                Stage = RenderStages.Done,
                Percent = 100,
                CleanedAt = now.AddDays(-6),
            };
            (await store.InsertAsync(stale, CancellationToken.None)).ShouldBeTrue();
            (await store.InsertAsync(recent, CancellationToken.None)).ShouldBeTrue();

            (await store.PurgeCleanedAsync(now.AddDays(-7), CancellationToken.None)).ShouldBe(1);

            (await store.TryGetAsync(stale.TaskId, CancellationToken.None)).ShouldBeNull();
            (await store.TryGetAsync(recent.TaskId, CancellationToken.None)).ShouldNotBeNull();
        }
        finally
        {
            Cleanup(path);
        }
    }

    private static RenderTaskRecord NewRow() =>
        AdapterHarness.NewRecord(Guid.CreateVersion7(), Guid.CreateVersion7());

    private static SqliteTaskStore NewStore(string path) =>
        new(new SqliteStoreOptions(path), NullLogger<SqliteTaskStore>.Instance);

    private static string NewDatabasePath() =>
        Path.Combine(Path.GetTempPath(), "nts-adapter-store-" + Guid.NewGuid().ToString("N"), "tasks.db");

    private static async Task<SqliteConnection> OpenAsync(string path)
    {
        // Read-write-create on purpose: WAL needs to open (or create) the shared-memory file, which a
        // ReadOnly connection cannot do once the writer closed cleanly.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<string> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value?.ToString() ?? string.Empty;
    }

    private static async Task<List<string>> NamesAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static void Cleanup(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
