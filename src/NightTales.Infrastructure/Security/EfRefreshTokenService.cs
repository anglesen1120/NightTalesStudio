using System.Data;
using System.Net;
using System.Net.Sockets;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Entities;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The <c>refresh_tokens</c> store of 09.3.2-T1 (09 README BD1/BD3): the family's first row, the locking
/// rotation, the set-based revocation and the durable re-authentication counters.
/// </summary>
/// <remarks>
/// <para>
/// <b>Raw SQL on purpose.</b> The rotation is a lock-then-decide algorithm — one <c>SELECT … FOR UPDATE</c>
/// on the presented row plus two writes — and the revocation is a set-based <c>UPDATE</c>. Nothing here
/// is a tracked-entity update, in the same way <c>IdempotencyStore</c> and <c>OutboxDispatcher</c> mutate
/// their infrastructure-only tables: that is what makes "the whole rotation is one transaction, never a
/// partial family row" (09.3.2-T1 Failure handling) true, and what keeps a concurrent double-refresh
/// serialised by the row lock instead of by a lost-update check.
/// </para>
/// <para>
/// The spec's rotation query joins a <c>LATERAL</c> subquery for the family's root <c>created_at</c>. Here
/// that is a second statement instead: the root's <c>created_at</c> is immutable (nothing ever updates it,
/// and it needs no second row lock), so reading it separately is the same read with fewer moving parts.
/// The presented row is locked first, so both statements see one consistent family.
/// </para>
/// <para>
/// Every write requires the caller's open UnitOfWork transaction (09 README D5): a rollback must be able
/// to take the rotation with it — which is exactly what a 403 <c>email_not_verified</c> refresh relies on.
/// </para>
/// </remarks>
/// <param name="database">The scope's write model; its connection and transaction carry every statement.</param>
/// <param name="issuer">The token factory: the random refresh token and its stored hash.</param>
/// <param name="sessionOptions">The configured session limits (<c>Auth:Session:*</c>).</param>
/// <param name="clock">The scope's clock; every stored instant comes from it.</param>
public sealed class EfRefreshTokenService(
    AppDbContext database,
    ITokenIssuer issuer,
    IOptions<SessionOptions> sessionOptions,
    IClock clock) : IRefreshTokenService, IRefreshTokenLookup
{
    /// <summary>The <c>pg_notify</c> channel every family revocation publishes on (WT37).</summary>
    public const string SecurityNotificationChannel = "security";

    /// <inheritdoc />
    public async Task<ErrorOr<RefreshTokenOwner>> FindAsync(string presentedToken, CancellationToken ct)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(presentedToken);

            await EnsureConnectionOpenAsync(ct).ConfigureAwait(false);

            await using var command = CreateCommand(SelectOwnerSql, requireTransaction: false);
            command.Parameters.AddWithValue("token_hash", issuer.HashToken(presentedToken));

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            return await reader.ReadAsync(ct).ConfigureAwait(false)
                ? new RefreshTokenOwner(reader.GetFieldValue<Guid>(0), reader.GetFieldValue<Guid>(1))
                : Error.Unauthorized(ErrorCodes.SessionRevoked, "The session is no longer valid; sign in again.");
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            // 09 README D5: a transient database failure is 503 service_unavailable, exactly as the unit
            // of work maps a refused commit — never an unhandled 500, and the transaction rolls back with it.
            return Unavailable<RefreshTokenOwner>();
        }
    }

    /// <inheritdoc />
    public async Task<ErrorOr<IssuedRefreshSession>> StartSessionAsync(
        AppUser user,
        RequestInfo info,
        CancellationToken ct)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(user);

            var now = clock.UtcNow;
            var token = issuer.CreateRefreshToken();
            var limits = sessionOptions.Value.LimitsFor(user.Role);

            var row = RefreshToken.Start(
                user.Id,
                issuer.HashToken(token),
                now,
                now + limits.Absolute,
                ParseIp(info.Ip),
                Truncate(info.UserAgent));

            database.RefreshTokens.Add(row);

            return new IssuedRefreshSession(token, row.FamilyId, row.ExpiresAt);
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            // 09 README D5: a transient database failure is 503 service_unavailable, exactly as the unit
            // of work maps a refused commit — never an unhandled 500, and the transaction rolls back with it.
            return Unavailable<IssuedRefreshSession>();
        }
    }

    /// <inheritdoc />
    public async Task<ErrorOr<RotatedSession>> RotateAsync(
        string presentedToken,
        RequestInfo info,
        CancellationToken ct)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(presentedToken);

            var now = clock.UtcNow;
            var hash = issuer.HashToken(presentedToken);

            // The family has to be known before its lock can be taken, and the row that names it is immutable
            // in that respect, so it is read once without a lock to resolve the family and then re-read under
            // the family lock below.
            var familyId = await FindFamilyIdAsync(hash, ct).ConfigureAwait(false);
            if (familyId is null)
            {
                // 1. Unknown cookie: there is nothing to revoke, and the answer is the same as for a stale one,
                //    so a stolen-and-already-swept cookie learns nothing (WT21).
                return SessionRevoked();
            }

            await LockFamilyAsync(familyId.Value, ct).ConfigureAwait(false);

            var presented = await SelectForUpdateAsync(hash, ct).ConfigureAwait(false);

            if (presented is null)
            {
                // The row disappeared between the two reads (a purge); nothing to revoke.
                return SessionRevoked();
            }

            if (presented.Value.RevokedAt is not null)
            {
                // 2. Only a row this task itself consumed by rotating is theft. Every other reason — logout,
                //    an expired or idle family, a password reset, an administrative revocation — is a stale
                //    cookie: 401 session_revoked, no alert, no further revocation (WT36).
                if (presented.Value.RevokedReason == RevocationReason.Rotated)
                {
                    await RevokeFamilyAsync(
                        presented.Value.FamilyId,
                        RevocationReason.ReuseDetected.ToWire(),
                        ct).ConfigureAwait(false);

                    return Error.Unauthorized(
                        ErrorCodes.RefreshTokenReused,
                        "The refresh token was already used; the session family was revoked.");
                }

                // 2b. The stale-cookie path changes nothing; see WT36.
                return SessionRevoked();
            }

            // The role decides the session limits, so the rotation reads the user once; the refresh flow's own
            // account re-check then reads the same tracked entity for free.
            var user = await database.Users
                .FirstOrDefaultAsync(candidate => candidate.Id == presented.Value.UserId, ct)
                .ConfigureAwait(false);

            if (user is null)
            {
                await RevokeFamilyAsync(
                    presented.Value.FamilyId,
                    RevocationReason.AccountNotActive.ToWire(),
                    ct).ConfigureAwait(false);

                return SessionRevoked();
            }

            var limits = sessionOptions.Value.LimitsFor(user.Role);
            var familyStartedAt = await FamilyStartedAtAsync(presented.Value.FamilyId, ct).ConfigureAwait(false);

            // 3. The absolute ceiling is measured from the family's first row, so a rotation can never extend a
            //    session; the idle window only exists for the owner/admin policy (BD3).
            if (presented.Value.ExpiresAt <= now)
            {
                await RevokeFamilyAsync(
                    presented.Value.FamilyId,
                    RevocationReason.SessionExpired.ToWire(),
                    ct).ConfigureAwait(false);

                return SessionRevoked();
            }

            var lastUsedAt = presented.Value.LastUsedAt ?? presented.Value.CreatedAt;
            if (limits.Idle is { } idle && now - lastUsedAt > idle)
            {
                await RevokeFamilyAsync(
                    presented.Value.FamilyId,
                    RevocationReason.SessionIdle.ToWire(),
                    ct).ConfigureAwait(false);

                return SessionRevoked();
            }

            // 4. Otherwise the successor inherits the family, the ceiling and the re-authentication state
            //    (WT32), and the presented row is consumed as `rotated`.
            var successorToken = issuer.CreateRefreshToken();
            var expiresAt = Min(familyStartedAt + limits.Absolute, now + (limits.Idle ?? limits.Absolute));

            var successorId = await InsertSuccessorAsync(
                presented.Value,
                issuer.HashToken(successorToken),
                now,
                expiresAt,
                ParseIp(info.Ip),
                Truncate(info.UserAgent),
                ct).ConfigureAwait(false);

            await MarkRotatedAsync(presented.Value.Id, successorId, now, ct).ConfigureAwait(false);

            return new RotatedSession(successorToken, presented.Value.FamilyId, expiresAt);
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            // 09 README D5: a transient database failure is 503 service_unavailable, exactly as the unit
            // of work maps a refused commit — never an unhandled 500, and the transaction rolls back with it.
            return Unavailable<RotatedSession>();
        }
    }

    /// <inheritdoc />
    public async Task<ErrorOr<Success>> RevokeFamilyAsync(Guid familyId, string reason, CancellationToken ct)
    {
        try
        {
            await LockFamilyAsync(familyId, ct).ConfigureAwait(false);

            var revoked = await RevokeAsync(
                "family_id = @family_id",
                reason,
                command => command.Parameters.AddWithValue("family_id", familyId),
                ct).ConfigureAwait(false);

            await NotifySecurityAsync(revoked, ct).ConfigureAwait(false);

            return Result.Success;
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            // 09 README D5: a transient database failure is 503 service_unavailable, exactly as the unit
            // of work maps a refused commit — never an unhandled 500, and the transaction rolls back with it.
            return Unavailable<Success>();
        }
    }

    /// <inheritdoc />
    public async Task<ErrorOr<int>> RevokeAllFamiliesAsync(
        Guid userId,
        string reason,
        Guid? exceptFamilyId,
        CancellationToken ct)
    {
        try
        {
            // One lock per live family, taken in a stable order so two overlapping revocations cannot deadlock;
            // each keeps its rotation from inserting a successor the revocation would not see.
            foreach (var familyId in await LiveFamilyIdsAsync(userId, exceptFamilyId, ct).ConfigureAwait(false))
            {
                await LockFamilyAsync(familyId, ct).ConfigureAwait(false);
            }

            var revoked = await RevokeAsync(
                "user_id = @user_id AND (@except_family_id IS NULL OR family_id <> @except_family_id)",
                reason,
                command =>
                {
                    command.Parameters.AddWithValue("user_id", userId);
                    command.Parameters.Add(new NpgsqlParameter("except_family_id", NpgsqlDbType.Uuid)
                    {
                        Value = exceptFamilyId is { } except ? except : DBNull.Value,
                    });
                },
                ct).ConfigureAwait(false);

            await NotifySecurityAsync(revoked, ct).ConfigureAwait(false);

            return revoked.Count;
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            // 09 README D5: a transient database failure is 503 service_unavailable, exactly as the unit
            // of work maps a refused commit — never an unhandled 500, and the transaction rolls back with it.
            return Unavailable<int>();
        }
    }

    /// <inheritdoc />
    public async Task<ErrorOr<Success>> MarkReauthenticatedAsync(
        Guid familyId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        try
        {
            await using var command = CreateCommand(MarkReauthenticatedSql, requireTransaction: true);
            command.Parameters.AddWithValue("family_id", familyId);
            command.Parameters.AddWithValue("reauthenticated_at", now);

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            return Result.Success;
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            // 09 README D5: a transient database failure is 503 service_unavailable, exactly as the unit
            // of work maps a refused commit — never an unhandled 500, and the transaction rolls back with it.
            return Unavailable<Success>();
        }
    }

    /// <inheritdoc />
    public async Task<ErrorOr<int>> RegisterReauthFailureAsync(
        Guid familyId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        try
        {
            await using var command = CreateCommand(RegisterReauthFailureSql, requireTransaction: true);
            command.Parameters.AddWithValue("family_id", familyId);
            command.Parameters.AddWithValue("now", now);

            var updated = 0;
            await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    // The counter is per family; more than one live row is possible only for an instant, so the
                    // largest value is the honest answer.
                    updated = Math.Max(updated, reader.GetInt32(0));
                }
            }

            return updated;
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            // 09 README D5: a transient database failure is 503 service_unavailable, exactly as the unit
            // of work maps a refused commit — never an unhandled 500, and the transaction rolls back with it.
            return Unavailable<int>();
        }
    }

    /// <inheritdoc />
    public async Task<ErrorOr<ReauthState>> GetReauthStateAsync(Guid familyId, CancellationToken ct)
    {
        try
        {
            await EnsureConnectionOpenAsync(ct).ConfigureAwait(false);

            await using var command = CreateCommand(SelectReauthStateSql, requireTransaction: false);
            command.Parameters.AddWithValue("family_id", familyId);

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                // A family with no row at all has no live session: for the re-auth guard that is a revoked
                // family, not an unknown state.
                return new ReauthState(null, 0, Revoked: true);
            }

            var reauthenticatedAt = await reader.IsDBNullAsync(1, ct).ConfigureAwait(false)
                ? (DateTimeOffset?)null
                : reader.GetFieldValue<DateTimeOffset>(1);

            return new ReauthState(
                reauthenticatedAt,
                reader.GetInt32(0),
                Revoked: !reader.GetBoolean(2));
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            // 09 README D5: a transient database failure is 503 service_unavailable, exactly as the unit
            // of work maps a refused commit — never an unhandled 500, and the transaction rolls back with it.
            return Unavailable<ReauthState>();
        }
    }

    /// <summary>
    /// Takes the family's transaction-scoped advisory lock, so a rotation and a revocation of the same
    /// family cannot interleave.
    /// </summary>
    /// <remarks>
    /// A rotation inserts a successor inside its transaction; a revocation running concurrently would not
    /// see that uncommitted row and would leave the family with a live token after "revoking" it. Both
    /// paths therefore take this lock <em>first</em>, in the same order — the revocation waits for the
    /// rotation's commit, and its own statement then sees the successor. The lock is
    /// <c>pg_advisory_xact_lock</c>, so the commit (or the rollback) releases it with no cleanup.
    /// </remarks>
    private async Task LockFamilyAsync(Guid familyId, CancellationToken ct)
    {
        await using var command = CreateCommand("SELECT pg_advisory_xact_lock(hashtextextended(@family_id::text, 0))", requireTransaction: true);
        command.Parameters.AddWithValue("family_id", familyId);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Whether the failure is a transient database problem rather than a bug or bad input.</summary>
    private static bool IsTransient(Exception exception) =>
        exception is NpgsqlException { IsTransient: true }
        || exception.InnerException is NpgsqlException { IsTransient: true }
        || exception is NpgsqlException { InnerException: SocketException }
        || exception is SocketException;

    /// <summary>The 503 of a transient failure, mapped exactly as the unit of work maps a commit failure.</summary>
    private static ErrorOr<T> Unavailable<T>() =>
        Error.Failure(
            code: ErrorCodes.ServiceUnavailable,
            description: "The database is temporarily unavailable; retry.");

    /// <summary>
    /// Revokes every live row the <paramref name="predicate"/> selects and returns the family ids and user
    /// ids it touched, so the notification carries the user whose live sockets must be aborted (WT37).
    /// </summary>
    private async Task<RevokedRows> RevokeAsync(
        string predicate,
        string reason,
        Action<NpgsqlCommand> bind,
        CancellationToken ct)
    {
        var revokedReason = RevocationReasons.Parse(reason);

        await using var command = CreateCommand(
            $"UPDATE refresh_tokens SET revoked_at = @revoked_at, revoked_reason = @revoked_reason "
            + $"WHERE {predicate} AND revoked_at IS NULL RETURNING family_id, user_id",
            requireTransaction: true);

        command.Parameters.AddWithValue("revoked_at", clock.UtcNow);
        command.Parameters.AddWithValue("revoked_reason", revokedReason.ToWire());
        bind(command);

        var rows = new List<(Guid FamilyId, Guid UserId)>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add((reader.GetFieldValue<Guid>(0), reader.GetFieldValue<Guid>(1)));
            }
        }

        return new RevokedRows(rows);
    }

    /// <summary>
    /// Publishes the <c>security</c> notification once per revoked user, inside the same transaction: the
    /// notification is delivered at commit, so a rolled-back revocation aborts no socket (WT37).
    /// </summary>
    private async Task NotifySecurityAsync(RevokedRows revoked, CancellationToken ct)
    {
        foreach (var userId in revoked.UserIds)
        {
            await using var command = CreateCommand(
                "SELECT pg_notify(@channel, @payload)",
                requireTransaction: true);

            command.Parameters.AddWithValue("channel", SecurityNotificationChannel);
            command.Parameters.AddWithValue("payload", userId.ToString());

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>The family a presented token belongs to, or null when no row has that hash.</summary>
    private async Task<Guid?> FindFamilyIdAsync(string tokenHash, CancellationToken ct)
    {
        await EnsureConnectionOpenAsync(ct).ConfigureAwait(false);

        await using var command = CreateCommand(SelectFamilyIdSql, requireTransaction: false);
        command.Parameters.AddWithValue("token_hash", tokenHash);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        return await reader.ReadAsync(ct).ConfigureAwait(false) ? reader.GetFieldValue<Guid>(0) : null;
    }

    /// <summary>The live families of one user, in a stable order (the lock order of a mass revocation).</summary>
    private async Task<IReadOnlyList<Guid>> LiveFamilyIdsAsync(Guid userId, Guid? exceptFamilyId, CancellationToken ct)
    {
        await using var command = CreateCommand(SelectLiveFamilyIdsSql, requireTransaction: true);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.Add(new NpgsqlParameter("except_family_id", NpgsqlDbType.Uuid)
        {
            Value = exceptFamilyId is { } except ? except : DBNull.Value,
        });

        var families = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                families.Add(reader.GetFieldValue<Guid>(0));
            }
        }

        return families;
    }

    private async Task<PresentedRow?> SelectForUpdateAsync(string tokenHash, CancellationToken ct)
    {
        await using var command = CreateCommand(SelectPresentedSql, requireTransaction: true);
        command.Parameters.AddWithValue("token_hash", tokenHash);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new PresentedRow(
            reader.GetFieldValue<Guid>(0),
            reader.GetFieldValue<Guid>(1),
            reader.GetFieldValue<Guid>(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            await reader.IsDBNullAsync(5, ct).ConfigureAwait(false) ? null : reader.GetFieldValue<DateTimeOffset>(5),
            await reader.IsDBNullAsync(6, ct).ConfigureAwait(false) ? null : reader.GetFieldValue<DateTimeOffset>(6),
            await reader.IsDBNullAsync(7, ct).ConfigureAwait(false) ? null : RevocationReasons.Parse(reader.GetString(7)),
            await reader.IsDBNullAsync(8, ct).ConfigureAwait(false) ? null : reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetInt16(9),
            await reader.IsDBNullAsync(10, ct).ConfigureAwait(false) ? null : reader.GetFieldValue<DateTimeOffset>(10));
    }

    /// <summary>The instant the family's first row was written — its absolute ceiling's base (BD3).</summary>
    private async Task<DateTimeOffset> FamilyStartedAtAsync(Guid familyId, CancellationToken ct)
    {
        await using var command = CreateCommand(SelectFamilyStartedAtSql, requireTransaction: true);
        command.Parameters.AddWithValue("family_id", familyId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "A refresh family without its first row cannot be rotated; the row was deleted mid-transaction.");
        }

        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    /// <summary>Inserts the successor row and returns its id, the value the consumed row points at.</summary>
    private async Task<Guid> InsertSuccessorAsync(
        PresentedRow presented,
        string successorHash,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        IPAddress? ip,
        string? userAgent,
        CancellationToken ct)
    {
        await using var command = CreateCommand(InsertSuccessorSql, requireTransaction: true);

        var successorId = Guid.CreateVersion7();
        command.Parameters.AddWithValue("id", successorId);
        command.Parameters.AddWithValue("user_id", presented.UserId);
        command.Parameters.AddWithValue("family_id", presented.FamilyId);
        command.Parameters.AddWithValue("token_hash", successorHash);
        command.Parameters.AddWithValue("created_at", now);
        command.Parameters.AddWithValue("expires_at", expiresAt);
        command.Parameters.Add(new NpgsqlParameter("reauthenticated_at", NpgsqlDbType.TimestampTz)
        {
            Value = presented.ReauthenticatedAt is { } reauthenticatedAt ? reauthenticatedAt : DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("ip", NpgsqlDbType.Inet)
        {
            Value = ip is null ? DBNull.Value : ip,
        });
        command.Parameters.Add(new NpgsqlParameter("user_agent", NpgsqlDbType.Text)
        {
            Value = userAgent is null ? DBNull.Value : userAgent,
        });
        command.Parameters.AddWithValue("reauth_attempts", presented.ReauthAttempts);
        command.Parameters.Add(new NpgsqlParameter("reauth_last_attempt_at", NpgsqlDbType.TimestampTz)
        {
            Value = presented.ReauthLastAttemptAt is { } lastAttemptAt ? lastAttemptAt : DBNull.Value,
        });

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return successorId;
    }

    /// <summary>
    /// Consumes the presented row and links it to the successor. The successor is inserted first so the
    /// foreign key of <c>replaced_by_id</c> always finds its target; both statements run in the caller's
    /// transaction, which is what makes the pair atomic.
    /// </summary>
    private async Task MarkRotatedAsync(
        Guid presentedId,
        Guid successorId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var command = CreateCommand(MarkRotatedSql, requireTransaction: true);
        command.Parameters.AddWithValue("id", presentedId);
        command.Parameters.AddWithValue("replaced_by_id", successorId);
        command.Parameters.AddWithValue("revoked_at", now);
        command.Parameters.AddWithValue("last_used_at", now);
        command.Parameters.AddWithValue("revoked_reason", RevocationReason.Rotated.ToWire());

        var updated = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        if (updated != 1)
        {
            // The row is locked by this transaction and was live a moment ago, so zero rows is a bug, not a
            // race: failing loudly keeps two usable tokens from ever existing.
            throw new InvalidOperationException(
                "The rotated refresh row was not consumed; the session family would have two live tokens.");
        }
    }

    /// <summary>
    /// Opens the scope's connection when a statement runs outside a transaction (a plain read such as the
    /// token → owner lookup). Inside a transaction the connection is already open, and the write paths
    /// require one anyway.
    /// </summary>
    private async Task EnsureConnectionOpenAsync(CancellationToken ct)
    {
        var connection = (NpgsqlConnection)database.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A command over the scope's connection and, for every write, its open transaction, carrying the
    /// configured <c>Persistence:CommandTimeoutSeconds</c> (the raw provider default would be silently
    /// shorter, 09 README D8).
    /// </summary>
    private NpgsqlCommand CreateCommand(string sql, bool requireTransaction)
    {
        var transaction = database.Database.CurrentTransaction;
        if (requireTransaction && transaction is null)
        {
            throw new InvalidOperationException(
                "The refresh-token store runs inside the command's transaction (09 README D5); it was called "
                + "outside one, where a rotation's row lock would not survive the statement.");
        }

        var connection = (NpgsqlConnection)database.Database.GetDbConnection();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction?.GetDbTransaction() as NpgsqlTransaction;
        command.CommandTimeout = database.Database.GetCommandTimeout() ?? PersistenceOptions.DefaultCommandTimeoutSeconds;

        return command;
    }

    /// <summary>The caller's address, or null when it is absent or unparsable.</summary>
    private static IPAddress? ParseIp(string? ip) =>
        !string.IsNullOrWhiteSpace(ip) && IPAddress.TryParse(ip, out var parsed) ? parsed : null;

    /// <summary>The caller's <c>User-Agent</c>, bounded before it reaches a <c>text</c> column.</summary>
    private static string? Truncate(string? userAgent) =>
        string.IsNullOrEmpty(userAgent) ? null
        : userAgent.Length <= RefreshToken.UserAgentMaxLength ? userAgent
        : userAgent[..RefreshToken.UserAgentMaxLength];

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;

    private static Error SessionRevoked() =>
        Error.Unauthorized(ErrorCodes.SessionRevoked, "The session is no longer valid; sign in again.");

    /// <summary>The presented row, read under <c>FOR UPDATE</c> together with what the algorithm classifies.</summary>
    private readonly record struct PresentedRow(
        Guid Id,
        Guid UserId,
        Guid FamilyId,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? LastUsedAt,
        DateTimeOffset? RevokedAt,
        RevocationReason? RevokedReason,
        DateTimeOffset? ReauthenticatedAt,
        short ReauthAttempts,
        DateTimeOffset? ReauthLastAttemptAt);

    /// <summary>The rows one revocation statement touched, grouped by the user the notification names.</summary>
    private readonly record struct RevokedRows(IReadOnlyList<(Guid FamilyId, Guid UserId)> Rows)
    {
        /// <summary>The number of rows revoked.</summary>
        internal int Count => Rows.Count;

        /// <summary>The distinct users whose live sockets must be aborted (WT37).</summary>
        internal IEnumerable<Guid> UserIds => Rows.Select(row => row.UserId).Distinct();
    }

    // The presented row, locked for the rest of the transaction: the decision (reuse, expired, idle) and the
    // successor write must see one and the same row even if a second request arrives with the same cookie.
    private const string SelectPresentedSql =
        """
        SELECT id, user_id, family_id, created_at, expires_at, last_used_at, revoked_at, revoked_reason,
               reauthenticated_at, reauth_attempts, reauth_last_attempt_at
        FROM refresh_tokens
        WHERE token_hash = @token_hash
        FOR UPDATE
        """;

    // The family's ceiling is its first row's creation instant (BD3): a rotation can never extend it. The
    // value is immutable, so it needs no lock of its own.
    private const string SelectFamilyStartedAtSql =
        """
        SELECT created_at
        FROM refresh_tokens
        WHERE family_id = @family_id
        ORDER BY created_at
        LIMIT 1
        """;

    // The successor: same family, same re-authentication state, a new token, and `last_used_at` NULL (only a
    // family's first row starts its idle clock at creation — WT33).
    private const string InsertSuccessorSql =
        """
        INSERT INTO refresh_tokens
            (id, user_id, family_id, token_hash, created_at, last_used_at, expires_at, revoked_at,
             revoked_reason, replaced_by_id, reauthenticated_at, ip, user_agent, reauth_attempts,
             reauth_last_attempt_at)
        VALUES
            (@id, @user_id, @family_id, @token_hash, @created_at, NULL, @expires_at, NULL,
             NULL, NULL, @reauthenticated_at, @ip, @user_agent, @reauth_attempts,
             @reauth_last_attempt_at)
        """;

    private const string MarkRotatedSql =
        """
        UPDATE refresh_tokens
        SET revoked_at = @revoked_at,
            revoked_reason = @revoked_reason,
            replaced_by_id = @replaced_by_id,
            last_used_at = @last_used_at
        WHERE id = @id AND revoked_at IS NULL
        """;

    private const string MarkReauthenticatedSql =
        """
        UPDATE refresh_tokens
        SET reauthenticated_at = @reauthenticated_at
        WHERE family_id = @family_id AND revoked_at IS NULL
        """;

    // WT32: the increment clamps, so two concurrent wrong attempts right after the fifth cannot violate the
    // column's CHECK and surface as a 500 instead of a 401.
    private const string RegisterReauthFailureSql =
        """
        UPDATE refresh_tokens
        SET reauth_attempts = LEAST(reauth_attempts + 1, 5)::smallint,
            reauth_last_attempt_at = @now
        WHERE family_id = @family_id AND revoked_at IS NULL
        RETURNING reauth_attempts
        """;

    // The owner of a presented token, resolved by the unique hash — the Design's "one indexed read". It
    // deliberately ignores `revoked_at`: a rotation consumes the row before the account re-check runs.
    private const string SelectOwnerSql =
        """
        SELECT user_id, family_id
        FROM refresh_tokens
        WHERE token_hash = @token_hash
        """;

    private const string SelectFamilyIdSql =
        """
        SELECT family_id
        FROM refresh_tokens
        WHERE token_hash = @token_hash
        """;

    private const string SelectLiveFamilyIdsSql =
        """
        SELECT DISTINCT family_id
        FROM refresh_tokens
        WHERE user_id = @user_id AND revoked_at IS NULL
              AND (@except_family_id IS NULL OR family_id <> @except_family_id)
        ORDER BY family_id
        """;

    private const string SelectReauthStateSql =
        """
        SELECT coalesce(max(reauth_attempts), 0)::integer,
               max(reauthenticated_at),
               coalesce(bool_or(revoked_at IS NULL), false)
        FROM refresh_tokens
        WHERE family_id = @family_id
        """;
}
