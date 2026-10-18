using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Domain.Publishing;
using NightTales.Infrastructure.Persistence;

namespace NightTales.Infrastructure.Publishing;

/// <summary>
/// The raw-SQL claim of <c>oauth_states</c> (06.1.2-T1, 06 README D3): one <c>UPDATE … RETURNING</c> on the
/// scope's connection, inside the caller's transaction, so two Api instances presenting the same state can never
/// both claim it.
/// </summary>
/// <remarks>
/// <para>
/// The statement is raw for the two reasons the port records: the <c>WHERE</c> clause decides the winner
/// atomically, and the row is not <c>IUserOwned</c>, so a tracked read would run under the tenant filter of a
/// scope that has no principal (the callback authenticates with the state alone, YR10).
/// </para>
/// <para>
/// No failure is reported: an unknown, spent or expired state all match no row and answer <see langword="null"/>,
/// and the callback turns every one of them into the same outcome, so nothing here can be used as an oracle.
/// </para>
/// </remarks>
/// <param name="database">The scope's context: its connection and its ambient transaction carry the claim.</param>
public sealed class EfOAuthStateStore(AppDbContext database) : IOAuthStateStore
{
    /// <summary>
    /// The claim (plan.md §3.1): spending the row is the write, and only a live, unspent row of the presented
    /// hash can be spent, so the statement itself is the single authenticator of the anonymous callback.
    /// </summary>
    private const string ClaimSql = """
        UPDATE oauth_states
        SET used_at = @now
        WHERE state_hash = @state_hash AND used_at IS NULL AND expires_at > @now
        RETURNING id, user_id, platform, code_verifier_protected, oauth_client_id, refresh_family_id, browser_nonce_hash;
        """;

    /// <inheritdoc />
    public async Task<OAuthStateClaim?> ClaimAsync(string stateHash, DateTimeOffset now, CancellationToken ct)
    {
        await using var command = await CreateCommandAsync(ClaimSql, ct).ConfigureAwait(false);
        command.Parameters.Add(new NpgsqlParameter("state_hash", NpgsqlDbType.Text) { Value = stateHash });
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var platformWire = reader.GetString(2);
        if (!PlatformExtensions.TryParse(platformWire, out var platform))
        {
            // The column carries a CHECK over the domain's closed set, so this is a schema/domain mismatch and
            // not an input problem; the message names no value.
            throw new InvalidOperationException(
                "The claimed oauth_states row carries a platform outside the domain's closed set.");
        }

        return new OAuthStateClaim(
            reader.GetGuid(0),
            reader.GetGuid(1),
            platform,
            reader.GetString(3),
            reader.GetString(4),
            reader.GetGuid(5),
            reader.GetString(6));
    }

    /// <summary>
    /// A command over the scope's connection and its open transaction, carrying the configured
    /// <c>Persistence:CommandTimeoutSeconds</c>. The claim is committed by the caller's unit of work, and a claim
    /// outside a transaction could be lost with the row still spent, so the transaction is required.
    /// </summary>
    private async Task<NpgsqlCommand> CreateCommandAsync(string sql, CancellationToken ct)
    {
        var transaction = database.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "The oauth_states claim runs inside the caller's transaction (06 README D3): the claiming "
                + "statement is unit of work #1 and must be committed before any call to Google.");

        var connection = (NpgsqlConnection)database.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }

        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction.GetDbTransaction() as NpgsqlTransaction;
        command.CommandTimeout = database.Database.GetCommandTimeout() ?? PersistenceOptions.DefaultCommandTimeoutSeconds;

        return command;
    }
}
