using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;

namespace NightTales.MptAdapter;

/// <summary>Where the durable store lives (11 README D6: <c>Adapter:DataDir/tasks.db</c>).</summary>
public sealed record SqliteStoreOptions(string DatabasePath);

/// <summary>
/// The adapter's durable <see cref="ITaskStore"/> (11 README D6, 05 T33): one SQLite file, WAL, one connection
/// and one <see cref="SemaphoreSlim"/> so the poller, the endpoints and a cancel can never interleave a write.
/// The DDL is <c>Migrations/0001_tasks.sql</c> (plan §3.1, authoritative).
/// </summary>
/// <remarks>
/// <see cref="UpdateAsync"/> reproduces <see cref="InMemoryTaskStore"/>'s contract exactly (it stores
/// <see cref="RenderTaskRecord.Merge"/>'s result inside the critical section), so the 05 D4 status invariants
/// hold for both stores and the core's endpoints behave the same against each.
/// <para>
/// The DDL's <c>started_at</c> column stays NULL: the record has no separate task-start field — the status's
/// <c>started_at</c> is projected from <c>BackendStartedAt</c> (11.3.5-T1) and the lost rule pins that column.
/// </para>
/// </remarks>
public sealed class SqliteTaskStore : ITaskStore, IPollAttemptStore, IAsyncDisposable
{
    /// <summary>The single row of <c>schema_meta</c> this migration writes.</summary>
    public const string SchemaVersion = "1";

    /// <summary>The resource name of the embedded DDL (plan §3.1).</summary>
    public const string MigrationResourceName = "NightTales.MptAdapter.Migrations.0001_tasks.sql";

    private const string VersionKey = "version";

    private static readonly string[] Pragmas =
    [
        "PRAGMA journal_mode = WAL;",
        "PRAGMA synchronous = FULL;",
        "PRAGMA busy_timeout = 5000;",
        "PRAGMA foreign_keys = ON;",
    ];

    /// <summary>Plan §3.1's column set, in value order; <c>started_at</c> and <c>attempts</c> are not read here.</summary>
    private const string SelectColumns =
        "task_id, render_job_id, body_sha256, backend_task_id, state, stage, percent, cancel_requested, "
        + "submitted_at, backend_started_at, finished_at, cleaned_at, outcome_json, output_json, check_json, "
        + "images_json, mpt_json";

    private static readonly string NonTerminalPredicate =
        $"state NOT IN ('{RenderStates.Succeeded}', '{RenderStates.Failed}', '{RenderStates.Cancelled}')";

    private readonly SqliteStoreOptions _options;
    private readonly ILogger<SqliteTaskStore> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private bool _disposed;

    public SqliteTaskStore(SqliteStoreOptions options, ILogger<SqliteTaskStore> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        _options = options;
        _log = log;
    }

    /// <summary>
    /// Creates the schema when it is absent and records <c>schema_meta['version']</c>. Idempotent: every
    /// statement is <c>IF NOT EXISTS</c> and the version row is upserted.
    /// </summary>
    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

            using var transaction = connection.BeginTransaction();
            foreach (var statement in Statements(ReadMigrationScript()))
            {
                // The PRAGMAs were applied when the connection opened: journal_mode cannot change inside a
                // transaction, so re-running them here would silently do nothing.
                if (statement.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                await ExecuteAsync(connection, transaction, statement, cancellationToken).ConfigureAwait(false);
            }

            await SetVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();

            _log.TaskStoreReady(SchemaVersion);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<RenderTaskRecord?> TryGetAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            return await ReadAsync(connection, taskId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<RenderTaskRecord?> FindByRenderJobIdAsync(
        Guid renderJobId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT {SelectColumns} FROM tasks WHERE render_job_id = $render_job_id "
                + "ORDER BY submitted_at LIMIT 1;";
            command.Parameters.AddWithValue("$render_job_id", Key(renderJobId));

            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> InsertAsync(RenderTaskRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

            // The interface's rule is "the same task id or render job id exists"; plan §3.1's unique index only
            // covers (job, body hash), so the render-job-id half is checked here, inside the same critical
            // section the insert runs in.
            using (var probe = connection.CreateCommand())
            {
                probe.CommandText =
                    "SELECT 1 FROM tasks WHERE task_id = $task_id OR render_job_id = $render_job_id LIMIT 1;";
                probe.Parameters.AddWithValue("$task_id", Key(record.TaskId));
                probe.Parameters.AddWithValue("$render_job_id", Key(record.RenderJobId));
                if (await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                {
                    return false;
                }
            }

            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO tasks (task_id, render_job_id, body_sha256, backend_task_id, state, stage, percent, "
                + "cancel_requested, submitted_at, started_at, backend_started_at, finished_at, cleaned_at, "
                + "outcome_json, output_json, check_json, images_json, mpt_json, attempts) VALUES "
                + "($task_id, $render_job_id, $body_sha256, $backend_task_id, $state, $stage, $percent, "
                + "$cancel_requested, $submitted_at, NULL, $backend_started_at, $finished_at, $cleaned_at, "
                + "$outcome_json, $output_json, $check_json, $images_json, $mpt_json, 0);";
            BindRecord(command, record);

            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> UpdateAsync(RenderTaskRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var stored = await ReadAsync(connection, record.TaskId, cancellationToken).ConfigureAwait(false);
            if (stored is null)
            {
                return false;
            }

            using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE tasks SET render_job_id = $render_job_id, body_sha256 = $body_sha256, "
                + "backend_task_id = $backend_task_id, state = $state, stage = $stage, percent = $percent, "
                + "cancel_requested = $cancel_requested, submitted_at = $submitted_at, "
                + "backend_started_at = $backend_started_at, finished_at = $finished_at, cleaned_at = $cleaned_at, "
                + "outcome_json = $outcome_json, output_json = $output_json, check_json = $check_json, "
                + "images_json = $images_json, mpt_json = $mpt_json WHERE task_id = $task_id;";
            BindRecord(command, stored.Merge(record));

            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> RecordBackendAcceptanceAsync(
        Guid taskId,
        string backendTaskId,
        DateTimeOffset? backendStartedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(backendTaskId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();

            // Deliberately not Merge: only these two columns move, so a cancel accepted while the submit was in
            // flight survives the submit's result (11.3.5-T1 Failure handling).
            command.CommandText =
                "UPDATE tasks SET backend_task_id = $backend_task_id, "
                + "backend_started_at = COALESCE($backend_started_at, backend_started_at) WHERE task_id = $task_id;";
            command.Parameters.AddWithValue("$task_id", Key(taskId));
            command.Parameters.AddWithValue("$backend_task_id", backendTaskId);
            command.Parameters.AddWithValue("$backend_started_at", Timestamp(backendStartedAt));

            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<int> CountNonTerminalAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM tasks WHERE {NonTerminalPredicate};";
            var count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt32(count, CultureInfo.InvariantCulture);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<Guid>> ListNonTerminalAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT task_id FROM tasks WHERE {NonTerminalPredicate} ORDER BY submitted_at, task_id;";

            var ids = new List<Guid>();
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Guid.TryParse(reader.GetString(0), out var id))
                {
                    ids.Add(id);
                }
            }

            return ids;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> DeleteAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM tasks WHERE task_id = $task_id;";
            command.Parameters.AddWithValue("$task_id", Key(taskId));
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<int> PurgeCleanedAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText =
                "DELETE FROM tasks WHERE cleaned_at IS NOT NULL AND cleaned_at < $older_than;";
            command.Parameters.AddWithValue("$older_than", Timestamp(olderThan));
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// D6's poll bookkeeping: the row's <c>attempts</c> counts consecutive failed polls and resets on a good
    /// one. It is not on <see cref="ITaskStore"/> because it is the poller's own counter, and the plan's column
    /// is the only place it lives.
    /// </summary>
    public async ValueTask<int> RecordPollOutcomeAsync(
        Guid taskId,
        bool failed,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE tasks SET attempts = CASE WHEN $failed = 1 THEN attempts + 1 ELSE 0 END "
                + "WHERE task_id = $task_id;";
            command.Parameters.AddWithValue("$task_id", Key(taskId));
            command.Parameters.AddWithValue("$failed", failed ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            using var read = connection.CreateCommand();
            read.CommandText = "SELECT attempts FROM tasks WHERE task_id = $task_id;";
            read.Parameters.AddWithValue("$task_id", Key(taskId));
            var value = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }

        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            return _connection;
        }

        var directory = Path.GetDirectoryName(_options.DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var pragma in Pragmas)
        {
            await ExecuteAsync(connection, transaction: null, pragma, cancellationToken).ConfigureAwait(false);
        }

        _connection = connection;
        return connection;
    }

    private static async Task<RenderTaskRecord?> ReadAsync(
        SqliteConnection connection,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM tasks WHERE task_id = $task_id LIMIT 1;";
        command.Parameters.AddWithValue("$task_id", Key(taskId));

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    private static RenderTaskRecord Map(SqliteDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var submittedAt = ParseRequired(reader.GetString(reader.GetOrdinal("submitted_at")));

        return new RenderTaskRecord(
            TaskId: Guid.Parse(reader.GetString(reader.GetOrdinal("task_id"))),
            RenderJobId: Guid.Parse(reader.GetString(reader.GetOrdinal("render_job_id"))),
            BodySha256: reader.GetString(reader.GetOrdinal("body_sha256")),
            BackendTaskId: Text(reader, "backend_task_id"),
            SubmittedAt: submittedAt,
            BackendStartedAt: Optional(reader, "backend_started_at"),
            State: reader.GetString(reader.GetOrdinal("state")),
            Stage: reader.GetString(reader.GetOrdinal("stage")),
            Percent: reader.GetInt32(reader.GetOrdinal("percent")),
            CancelRequested: reader.GetInt32(reader.GetOrdinal("cancel_requested")) != 0,
            Outcome: ReadOutcome(Text(reader, "outcome_json")),
            Output: ReadOutput(Text(reader, "output_json")),
            Check: ReadJson(Text(reader, "check_json")),
            Images: ReadJson(Text(reader, "images_json")),
            FinishedAt: Optional(reader, "finished_at"),
            CleanedAt: Optional(reader, "cleaned_at"),
            MptLastSnapshot: ReadSnapshot(Text(reader, "mpt_json")));
    }

    private static void BindRecord(SqliteCommand command, RenderTaskRecord record)
    {
        command.Parameters.AddWithValue("$task_id", Key(record.TaskId));
        command.Parameters.AddWithValue("$render_job_id", Key(record.RenderJobId));
        command.Parameters.AddWithValue("$body_sha256", record.BodySha256);
        command.Parameters.AddWithValue("$backend_task_id", (object?)record.BackendTaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$state", record.State);
        command.Parameters.AddWithValue("$stage", record.Stage);
        command.Parameters.AddWithValue("$percent", record.Percent);
        command.Parameters.AddWithValue("$cancel_requested", record.CancelRequested ? 1 : 0);
        command.Parameters.AddWithValue("$submitted_at", Timestamp(record.SubmittedAt));
        command.Parameters.AddWithValue("$backend_started_at", Timestamp(record.BackendStartedAt));
        command.Parameters.AddWithValue("$finished_at", Timestamp(record.FinishedAt));
        command.Parameters.AddWithValue("$cleaned_at", Timestamp(record.CleanedAt));
        command.Parameters.AddWithValue("$outcome_json", (object?)Serialize(record.Outcome) ?? DBNull.Value);
        command.Parameters.AddWithValue("$output_json", (object?)Serialize(record.Output) ?? DBNull.Value);
        command.Parameters.AddWithValue("$check_json", (object?)record.Check?.GetRawText() ?? DBNull.Value);
        command.Parameters.AddWithValue("$images_json", (object?)record.Images?.GetRawText() ?? DBNull.Value);
        command.Parameters.AddWithValue("$mpt_json", (object?)Serialize(record.MptLastSnapshot) ?? DBNull.Value);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task SetVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO schema_meta (key, value) VALUES ($key, $value) "
            + "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$key", VersionKey);
        command.Parameters.AddWithValue("$value", SchemaVersion);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ReadMigrationScript()
    {
        using var stream = typeof(SqliteTaskStore).Assembly.GetManifestResourceStream(MigrationResourceName)
            ?? throw new InvalidOperationException($"The embedded migration '{MigrationResourceName}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Statements of the DDL. Comments are removed first — a <c>--</c> comment may itself contain a <c>;</c>
    /// (plan §3.1's <c>cleaned_at</c> column), and a <c>PRAGMA</c> must keep its first token to be recognised.
    /// The DDL holds no string literal with a semicolon; a future one would need a real parser.
    /// </summary>
    private static string[] Statements(string script) =>
        string
            .Join('\n', script.Split('\n').Select(WithoutComment))
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string WithoutComment(string line)
    {
        var marker = line.IndexOf("--", StringComparison.Ordinal);
        return marker < 0 ? line : line[..marker];
    }

    private static string Key(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static object Timestamp(DateTimeOffset? value) =>
        value is { } instant ? Timestamp(instant) : DBNull.Value;

    private static DateTimeOffset ParseRequired(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static string? Text(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset? Optional(SqliteDataReader reader, string column)
    {
        var text = Text(reader, column);
        return text is null ? null : ParseRequired(text);
    }

    private static string? Serialize<T>(T? value) where T : class =>
        value is null ? null : JsonSerializer.Serialize(value, RenderNodeJson.Tolerant);

    private static RenderOutcomeV1? ReadOutcome(string? json) =>
        Deserialize<RenderOutcomeV1>(json);

    private static RenderOutputV1? ReadOutput(string? json) =>
        Deserialize<RenderOutputV1>(json);

    private static MptStateV1? ReadSnapshot(string? json) =>
        Deserialize<MptStateV1>(json);

    private static T? Deserialize<T>(string? json) where T : class =>
        json is null ? null : JsonSerializer.Deserialize<T>(json, RenderNodeJson.Tolerant);

    private static JsonElement? ReadJson(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
