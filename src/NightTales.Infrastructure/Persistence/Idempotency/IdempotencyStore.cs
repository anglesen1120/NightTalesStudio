using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using NightTales.Application.Abstractions.Idempotency;

namespace NightTales.Infrastructure.Persistence.Idempotency;

/// <summary>
/// The <c>idempotency_keys</c> protocol in raw SQL (09 README D6, 09.1.3 plan §3): the insert that
/// claims a key, the <c>SELECT … FOR UPDATE</c> a twin waits on, and the update that stores the
/// response.
/// </summary>
/// <remarks>
/// <para>
/// Every statement runs on the scope's <see cref="AppDbContext"/> connection <em>and</em> its
/// transaction, so a claim commits with the effects it guards and disappears with them: a rolled-back
/// command leaves no key. Calling it without a transaction would let a crash commit a key whose command
/// never ran, so that is refused.
/// </para>
/// <para>
/// The claim rests on the PostgreSQL behaviour of <c>INSERT … ON CONFLICT DO NOTHING RETURNING</c>: an
/// insert that meets an uncommitted twin's row waits for that transaction — committing makes the insert
/// take the conflict path, rolling back lets it insert its own row — and the follow-up
/// <c>SELECT … FOR UPDATE</c> reads the stored response under a lock, so two twins can never both
/// execute.
/// </para>
/// <para>
/// The expiry sweep races this protocol in both directions, and neither side may lose a row the other
/// owns: <c>ReuseAsync</c> reports whether its row survived the sweep's <c>DELETE</c> (a vanished row
/// makes <c>ClaimAsync</c> claim the key afresh), and the sweep repeats its expiry test on the row
/// itself so a renewal that commits while the sweep waits on that row's lock is not deleted
/// (<see cref="IdempotencyCleanupStore"/>).
/// </para>
/// </remarks>
public sealed class IdempotencyStore(AppDbContext database, IOptions<PersistenceOptions> options)
    : IIdempotencyStore
{
    private const string InsertSql =
        """
        INSERT INTO idempotency_keys (id, user_id, key, request_hash, created_at, expires_at)
        VALUES (@id, @user_id, @key, @request_hash, @created_at, @created_at + interval '24 hours')
        ON CONFLICT (user_id, key) DO NOTHING
        RETURNING id
        """;

    private const string SelectSql =
        """
        SELECT id, request_hash, response_type, response, expires_at
        FROM idempotency_keys
        WHERE user_id = @user_id AND key = @key
        FOR UPDATE
        """;

    private const string ReuseSql =
        """
        UPDATE idempotency_keys
        SET request_hash = @request_hash,
            response_type = NULL,
            response = NULL,
            created_at = @created_at,
            expires_at = @created_at + interval '24 hours'
        WHERE id = @id
        """;

    private const string CompleteSql =
        """
        UPDATE idempotency_keys
        SET response_type = @response_type, response = @response
        WHERE id = @id
        """;

    /// <inheritdoc />
    public async Task<IdempotencyClaim> ClaimAsync(
        Guid userId,
        string key,
        string requestHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // One retry is all the race needs: the conflicting row can be released by the expiry sweep
        // between the insert and the lock, or between the lock and the reuse — a second miss means the
        // protocol is broken rather than raced.
        for (var attempt = 0; ; attempt++)
        {
            if (await TryInsertAsync(userId, key, requestHash, now, cancellationToken).ConfigureAwait(false) is { } claimId)
            {
                return new IdempotencyClaim.Fresh(claimId);
            }

            var stored = await SelectForUpdateAsync(userId, key, cancellationToken).ConfigureAwait(false);
            var decision = stored is { } row
                ? await DecideAsync(row, requestHash, now, cancellationToken).ConfigureAwait(false)
                : null;

            if (decision is { } claim)
            {
                return claim;
            }

            if (attempt > 0)
            {
                throw new InvalidOperationException(
                    "The claimed idempotency key row disappeared before it could be locked or reused; the "
                    + "key protocol cannot continue (09 README D6).");
            }
        }
    }

    /// <inheritdoc />
    public async Task CompleteAsync(
        Guid claimId,
        string responseType,
        string responseJson,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(CompleteSql);
        command.Parameters.AddWithValue("id", claimId);
        command.Parameters.AddWithValue("response_type", responseType);
        command.Parameters.Add(new NpgsqlParameter("response", NpgsqlDbType.Jsonb) { Value = responseJson });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The decision for a request that met an existing row: an expired key is reused for this request, a
    /// different body is a mismatch, and a row without a stored response cannot follow a committed twin,
    /// so it is this request's own claim.
    /// </summary>
    /// <returns>
    /// The decision, or <c>null</c> when the locked row was gone under the reuse — the expiry sweep
    /// deleted it between the <c>SELECT … FOR UPDATE</c> and the update — so the caller must claim the
    /// key again instead of carrying an id no row has.
    /// </returns>
    private async Task<IdempotencyClaim?> DecideAsync(
        StoredKey stored,
        string requestHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (stored.ExpiresAt < now)
        {
            return await ReuseAsync(stored.Id, requestHash, now, cancellationToken).ConfigureAwait(false)
                ? new IdempotencyClaim.Fresh(stored.Id)
                : null;
        }

        if (!string.Equals(stored.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return new IdempotencyClaim.Mismatch();
        }

        if (stored is { ResponseType: { } responseType, Response: { } response })
        {
            return new IdempotencyClaim.Replay(responseType, response);
        }

        return new IdempotencyClaim.Fresh(stored.Id);
    }

    /// <summary>Claims a fresh key row; <c>null</c> when the key is already claimed.</summary>
    private async Task<Guid?> TryInsertAsync(
        Guid userId,
        string key,
        string requestHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var claimId = Guid.CreateVersion7();

        await using var command = CreateCommand(InsertSql);
        command.Parameters.AddWithValue("id", claimId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("request_hash", requestHash);
        command.Parameters.AddWithValue("created_at", now);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid inserted
            ? inserted
            : null;
    }

    /// <summary>Reads the existing row under its lock; <c>null</c> when the key row is gone.</summary>
    private async Task<StoredKey?> SelectForUpdateAsync(Guid userId, string key, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(SelectSql);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("key", key);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var responseType = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
            ? null
            : reader.GetString(2);
        var response = await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false)
            ? null
            : reader.GetString(3);

        return new StoredKey(
            reader.GetFieldValue<Guid>(0),
            reader.GetFieldValue<string>(1),
            responseType,
            response,
            reader.GetFieldValue<DateTimeOffset>(4));
    }

    /// <summary>
    /// Resets an expired key onto the current request inside its transaction: the row keeps its id and
    /// loses the response the previous, expired request stored, so the handler runs again.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the row was updated; <c>false</c> when it is gone — the expiry sweep deleted it
    /// after the <c>SELECT … FOR UPDATE</c> took the row's lock — so the caller claims the key afresh
    /// instead of proceeding with a claim id that no longer exists.
    /// </returns>
    private async Task<bool> ReuseAsync(
        Guid claimId,
        string requestHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(ReuseSql);
        command.Parameters.AddWithValue("id", claimId);
        command.Parameters.AddWithValue("request_hash", requestHash);
        command.Parameters.AddWithValue("created_at", now);

        var updated = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return updated == 1;
    }

    /// <summary>
    /// A command over the scope's connection and its <em>open</em> transaction — the transaction is what
    /// makes the claim and the command's effects one unit — carrying the configured
    /// <c>Persistence:CommandTimeoutSeconds</c>.
    /// </summary>
    private NpgsqlCommand CreateCommand(string sql)
    {
        var transaction = database.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "The idempotency protocol runs inside the command's transaction (09 README D6); the store "
                + "was called outside one.");

        var connection = (NpgsqlConnection)database.Database.GetDbConnection();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = (NpgsqlTransaction)transaction.GetDbTransaction();

        // NpgsqlConnection.CreateCommand() does not inherit the EF provider's configured timeout, so the
        // raw statement would silently keep Npgsql's own default (09 README D8).
        command.CommandTimeout = options.Value.CommandTimeoutSeconds;

        return command;
    }

    /// <summary>One claimed or completed <c>idempotency_keys</c> row, locked for update.</summary>
    private readonly record struct StoredKey(
        Guid Id,
        string RequestHash,
        string? ResponseType,
        string? Response,
        DateTimeOffset ExpiresAt);
}
