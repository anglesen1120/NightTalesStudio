using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Domain.Common;

namespace NightTales.Infrastructure.Persistence.Outbox;

/// <summary>
/// The Worker's outbox loop (09 README D11, 09.2.4 plan §3): it claims due <c>outbox_messages</c> rows with
/// <c>FOR UPDATE SKIP LOCKED</c> under a lease, delivers each one in its own scope as the event's user, and
/// records the outcome — processed, backed off, or dead-lettered.
/// </summary>
/// <remarks>
/// <para>
/// The claim commits before any handler runs, so no handler holds the claim's row locks, and the lease it
/// stamps (<c>Outbox:LeaseMinutes</c>) is what makes a crash mid-message recoverable: the row becomes
/// visible again when the lease is over and is delivered a second time. Delivery is therefore
/// <b>at least once</b> and handlers are idempotent by contract (D11, O4).
/// </para>
/// <para>
/// Several dispatchers (several Workers, or the harness) are safe: a claim only takes rows no other claim
/// has bumped the lease of. Ordering between the events of one aggregate is best-effort, not guaranteed —
/// a failed message backs off while its siblings move on, so a handler re-reads the state it needs by id.
/// </para>
/// <para>
/// The loop polls; it does not <c>LISTEN</c> (09 plan §7 [DECISION]). A claim takes every due row up to
/// <c>Outbox:BatchSize</c>, so a claim that did not fill the batch means nothing is left and the loop
/// sleeps <c>Outbox:PollIntervalSeconds</c>, while a full batch loops immediately and a backlog drains
/// without a pause. A stop (<c>stoppingToken</c>) lets the message in flight finish: the rows claimed but
/// not yet delivered are re-delivered once the lease is over.
/// </para>
/// </remarks>
/// <param name="scopeFactory">Creates one scope per batch and one per delivered message.</param>
/// <param name="registry">Resolves the stored <c>type</c> back to the event's CLR type.</param>
/// <param name="options">The poll, batch, lease and attempt limits.</param>
/// <param name="metrics">The outbox instruments.</param>
/// <param name="clock">The dispatcher's notion of "now" — a test moves it instead of sleeping.</param>
/// <param name="logger">The failure and dead-letter log.</param>
public sealed partial class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    OutboxEventRegistry registry,
    IOptions<OutboxDispatcherOptions> options,
    OutboxMetrics metrics,
    IClock clock,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    /// <summary>The activity name of one delivery; its parent is the stored <c>trace_parent</c> (NFR-09-4).</summary>
    public const string ActivityName = "outbox.handle";

    /// <summary>
    /// The exponential backoff of a failed message, in seconds: <c>2^attempts</c> capped at 5 minutes (D11).
    /// </summary>
    public const int MaxBackoffSeconds = 300;

    // 2^9 = 512 already exceeds the cap, so the shift never overflows for any stored attempt count.
    private const int MaxBackoffExponent = 9;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int claimed;

            try
            {
                claimed = await ProcessBatchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A claim that failed (a database that went away) must not spin: log it and take the poll
                // delay, exactly like an empty batch.
                LogBatchFailed(exception);
                claimed = 0;
            }

            if (claimed < options.Value.BatchSize)
            {
                try
                {
                    await Task.Delay(options.Value.PollInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Claims and delivers one batch (09.2.4 plan §9): the loop calls it, and so does the harness's
    /// <c>WorkerHost.RunOutboxOnceAsync</c>, which is how the tests drive delivery without sleeping.
    /// </summary>
    /// <param name="cancellationToken">
    /// The shutdown token. A cancelled token stops after the message in flight; the rows already claimed
    /// but not delivered are re-delivered once their lease is over.
    /// </param>
    /// <returns>How many rows this call claimed.</returns>
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // One connection for the whole batch. It is opened here because the claim's own transaction closes
        // the connection it opened as soon as it commits, while the marks that follow are raw statements on
        // this context's connection; disposing the scope returns it to the pool.
        await database.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var claimed = await ClaimAsync(database, now, cancellationToken).ConfigureAwait(false);

        // The oldest row of the batch, not the first one the claim happened to return: UPDATE … RETURNING
        // promises no order, and the lag the alert reads is the age of the oldest due row (D11).
        metrics.RecordClaim(claimed.Count > 0 ? claimed.Min(message => message.OccurredAt) : null, now);

        foreach (var message in claimed)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await DeliverAsync(database, message, cancellationToken).ConfigureAwait(false);
        }

        return claimed.Count;
    }

    /// <summary>
    /// The claim of D11, in raw SQL: the due rows are taken under their locks and the same statement pushes
    /// their <c>next_attempt_at</c> past the lease, so the migration of the transaction commits both the
    /// selection and the lease.
    /// </summary>
    private async Task<List<ClaimedMessage>> ClaimAsync(
        AppDbContext database,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var batch = options.Value.BatchSize;
        var claimed = new List<ClaimedMessage>(batch);

        await using var transaction = await database.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using (var command = CreateCommand(database, ClaimSql))
        {
            command.Parameters.AddWithValue("now", now);
            command.Parameters.AddWithValue("batch", batch);
            command.Parameters.AddWithValue("lease_minutes", options.Value.LeaseMinutes);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                claimed.Add(Read(reader));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return claimed;
    }

    /// <summary>
    /// Delivers one claimed message and records its outcome: processed, backed off, or dead-lettered.
    /// </summary>
    private async Task DeliverAsync(
        AppDbContext database,
        ClaimedMessage message,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var attempts = message.Attempts;
        int? exhaustedAttempts = null;
        string? deadLetter;

        try
        {
            deadLetter = await HandleAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A real stop: leave the row claimed. Its lease expires and the next Worker delivers it again.
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            attempts++;

            if (attempts < options.Value.MaxAttempts)
            {
                var nextAttemptAt = now + TimeSpan.FromSeconds(BackoffSeconds(attempts));
                await WriteRetryAsync(database, message.Id, attempts, nextAttemptAt, Describe(exception), cancellationToken)
                    .ConfigureAwait(false);
                metrics.RecordHandled(message.Type, "retried");
                LogHandlerFailed(message.Type, message.Id, attempts, exception);

                return;
            }

            exhaustedAttempts = attempts;
            deadLetter = Describe(exception);
        }

        if (deadLetter is null)
        {
            await MarkProcessedAsync(database, message.Id, now, cancellationToken).ConfigureAwait(false);
            metrics.RecordHandled(message.Type, "processed");

            return;
        }

        // A message that exhausted its attempts keeps the count that made it a dead letter; a terminal one
        // (an unknown type, a missing user, an unreadable payload) never spent an attempt.
        if (exhaustedAttempts is { } failed)
        {
            await ExhaustAsync(database, message.Id, failed, now + TimeSpan.FromSeconds(BackoffSeconds(failed)), deadLetter, now, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await DeadLetterAsync(database, message.Id, deadLetter, now, cancellationToken).ConfigureAwait(false);
        }

        metrics.RecordHandled(message.Type, "dead_lettered");
        metrics.RecordDeadLetter();
        LogDeadLettered(message.Type, message.Id, exhaustedAttempts ?? attempts, deadLetter);
    }

    /// <summary>
    /// Runs every handler of the message's event type in registration order, in a fresh scope pinned to the
    /// message's user, and returns the reason it must be dead-lettered — or <c>null</c> when it succeeded.
    /// </summary>
    /// <remarks>
    /// An unknown type, a missing user on a non-platform event and an unreadable payload are terminal: no
    /// handler could ever succeed, so the row becomes a dead letter instead of blocking the queue (09 plan
    /// §7 UC4/UC6).
    /// </remarks>
    private async Task<string?> HandleAsync(ClaimedMessage message, CancellationToken cancellationToken)
    {
        var eventType = registry.TypeOf(message.Type);
        if (eventType is null)
        {
            return $"the stored type '{message.Type}' is not a known outbox event";
        }

        if (message.UserId is null && !OutboxEventRegistry.IsPlatformEvent(eventType))
        {
            return $"{eventType.Name} is not a [PlatformEvent], so its message needs a user_id";
        }

        object domainEvent;
        try
        {
            domainEvent = JsonSerializer.Deserialize(message.Payload, eventType, OutboxJson.Options)
                ?? throw new JsonException("the payload is JSON null");
        }
        catch (JsonException exception)
        {
            return $"the payload of '{message.Type}' does not deserialize: {exception.Message}";
        }

        using var activity = StartActivity(message.TraceParent);
        await using var scope = scopeFactory.CreateAsyncScope();
        var scopeServices = scope.ServiceProvider;

        // 09 README D10: the user must be pinned before any DbContext of this scope is created, so a
        // handler can only ever read the rows of the event's user.
        if (message.UserId is { } userId)
        {
            scopeServices.GetRequiredService<ISystemContext>().For(userId);
        }

        var context = new OutboxContext(message.Id, message.UserId, message.Attempts + 1, message.OccurredAt);

        foreach (var registration in scopeServices.GetServices<OutboxHandlerRegistration>())
        {
            if (registration.EventType != eventType)
            {
                continue;
            }

            await registration
                .Create(scopeServices)
                .HandleAsync(domainEvent, context, cancellationToken)
                .ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>The delivery activity, parented on the command's trace so the two spans join (NFR-09-4).</summary>
    private static Activity? StartActivity(string? traceParent)
    {
        var activity = new Activity(ActivityName);
        if (traceParent is { Length: > 0 })
        {
            activity.SetParentId(traceParent);
        }

        return activity.Start();
    }

    /// <summary>The exponential backoff of D11: <c>2^attempts</c> seconds, capped at five minutes.</summary>
    private static int BackoffSeconds(int attempts) =>
        Math.Min(1 << Math.Min(attempts, MaxBackoffExponent), MaxBackoffSeconds);

    /// <summary>The failure text stored in <c>error</c>: the exception's type and message, never payload content.</summary>
    private static string Describe(Exception exception) => $"{exception.GetType().Name}: {exception.Message}";

    private static Task MarkProcessedAsync(
        AppDbContext database,
        Guid id,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ExecuteAsync(database, ProcessedSql, command =>
        {
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("now", now);
        }, cancellationToken);

    private static Task WriteRetryAsync(
        AppDbContext database,
        Guid id,
        int attempts,
        DateTimeOffset nextAttemptAt,
        string error,
        CancellationToken cancellationToken) =>
        ExecuteAsync(database, RetrySql, command =>
        {
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("attempts", attempts);
            command.Parameters.AddWithValue("next_attempt_at", nextAttemptAt);
            command.Parameters.AddWithValue("error", error);
        }, cancellationToken);

    /// <summary>Terminal failure: the message can never succeed, so no attempt was spent on it.</summary>
    private static Task DeadLetterAsync(
        AppDbContext database,
        Guid id,
        string error,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ExecuteAsync(database, DeadLetterSql, command =>
        {
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("error", error);
            command.Parameters.AddWithValue("failed_at", now);
        }, cancellationToken);

    /// <summary>The message failed its last allowed attempt: the count and the final backoff are stored with it.</summary>
    private static Task ExhaustAsync(
        AppDbContext database,
        Guid id,
        int attempts,
        DateTimeOffset nextAttemptAt,
        string error,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ExecuteAsync(database, ExhaustedSql, command =>
        {
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("attempts", attempts);
            command.Parameters.AddWithValue("next_attempt_at", nextAttemptAt);
            command.Parameters.AddWithValue("error", error);
            command.Parameters.AddWithValue("failed_at", now);
        }, cancellationToken);

    private static async Task ExecuteAsync(
        AppDbContext database,
        string sql,
        Action<NpgsqlCommand> bind,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(database, sql);
        bind(command);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A command over the scope's connection and, when the batch opened one, its transaction, carrying the
    /// configured <c>Persistence:CommandTimeoutSeconds</c> (the raw provider default would be silently
    /// shorter, 09 README D8).
    /// </summary>
    private static NpgsqlCommand CreateCommand(AppDbContext database, string sql)
    {
        var connection = (NpgsqlConnection)database.Database.GetDbConnection();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        command.CommandTimeout = database.Database.GetCommandTimeout() ?? PersistenceOptions.DefaultCommandTimeoutSeconds;

        return command;
    }

    /// <summary>One claimed <c>outbox_messages</c> row, read before its lease commits.</summary>
    private static ClaimedMessage Read(DbDataReader reader) =>
        new(
            reader.GetFieldValue<Guid>(0),
            reader.GetFieldValue<string>(1),
            reader.GetFieldValue<string>(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<Guid>(3),
            reader.GetInt32(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));

    // The claim of 09 README D11: due rows under lock, then the lease in the same statement, so the commit
    // of this transaction ends both the row locks and the window in which another dispatcher could see them.
    // D11's partial index — ix_outbox_messages_next_attempt_at on (next_attempt_at) WHERE processed_at IS
    // NULL AND failed_at IS NULL, pinned by Indexes_MatchPlanSection3 — is the access path for the due
    // predicate; the sort by occurred_at then orders only the rows the range scan found (measured on a
    // drained queue, 20 due of a 20k row table: Index Scan, 9 buffers, 0.07 ms), so this claim needs no
    // further index of its own.
    private const string ClaimSql =
        """
        WITH due AS (
            SELECT id
            FROM outbox_messages
            WHERE processed_at IS NULL
              AND failed_at IS NULL
              AND next_attempt_at <= @now
            ORDER BY occurred_at
            LIMIT @batch
            FOR UPDATE SKIP LOCKED
        )
        UPDATE outbox_messages claimed
        SET next_attempt_at = @now + make_interval(mins => @lease_minutes)
        FROM due
        WHERE claimed.id = due.id
        RETURNING claimed.id, claimed.type, claimed.payload, claimed.user_id, claimed.attempts, claimed.occurred_at, claimed.trace_parent
        """;

    private const string ProcessedSql =
        """
        UPDATE outbox_messages
        SET processed_at = @now, error = NULL
        WHERE id = @id
        """;

    // The error is truncated by the database: `left` counts characters, so a multi-byte message cannot be
    // cut in the middle of a character (the column is varchar(2000)).
    private const string RetrySql =
        """
        UPDATE outbox_messages
        SET attempts = @attempts, next_attempt_at = @next_attempt_at, error = left(@error, 2000)
        WHERE id = @id
        """;

    private const string DeadLetterSql =
        """
        UPDATE outbox_messages
        SET error = left(@error, 2000), failed_at = @failed_at
        WHERE id = @id
        """;

    private const string ExhaustedSql =
        """
        UPDATE outbox_messages
        SET attempts = @attempts,
            next_attempt_at = @next_attempt_at,
            error = left(@error, 2000),
            failed_at = @failed_at
        WHERE id = @id
        """;

    [LoggerMessage(EventId = 2300, Level = LogLevel.Warning, Message = "outbox.handler.failed: type={Type} message={MessageId} attempts={Attempts}")]
    private partial void LogHandlerFailed(string type, Guid messageId, int attempts, Exception exception);

    [LoggerMessage(EventId = 2301, Level = LogLevel.Error, Message = "outbox.dead-lettered: type={Type} message={MessageId} attempts={Attempts} reason={Reason}")]
    private partial void LogDeadLettered(string type, Guid messageId, int attempts, string reason);

    [LoggerMessage(EventId = 2302, Level = LogLevel.Error, Message = "outbox.claim.failed")]
    private partial void LogBatchFailed(Exception exception);

    /// <summary>One due row, as the claim returned it.</summary>
    private readonly record struct ClaimedMessage(
        Guid Id,
        string Type,
        string Payload,
        Guid? UserId,
        int Attempts,
        DateTimeOffset OccurredAt,
        string? TraceParent);
}
