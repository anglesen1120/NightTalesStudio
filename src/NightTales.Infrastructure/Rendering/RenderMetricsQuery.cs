using System.Data;
using Npgsql;

namespace NightTales.Infrastructure.Rendering;

/// <summary>
/// The platform-wide count behind the <c>render_jobs_active</c> gauge (09 README BD12, 09.9.3-T1, WT14): one
/// number — the <c>render_jobs</c> rows in <c>queued|submitted|running|ingesting</c> — never a row. The count
/// is deliberately cross-tenant (a platform gauge has no tenant), so it runs as one aggregate statement on its
/// own connection rather than through the tenant-filtered <c>AppDbContext</c>.
/// </summary>
/// <remarks>
/// <c>pg_try_advisory_lock(hashtext('render_metrics'))</c> keeps a second Worker from issuing the same count:
/// when the lock is held elsewhere the call returns <see langword="null"/> and the caller skips the tick. The
/// lock is session-scoped and released on the same connection before it is closed. While <c>render_jobs</c>
/// does not exist yet (09.6.1-T1) the answer is zero, like <see cref="RenderJobNodeLoadReader"/>.
/// </remarks>
/// <param name="connectionString">The platform database connection string.</param>
public sealed class RenderMetricsQuery(string connectionString)
{
    private const string TryLockSql = "SELECT pg_try_advisory_lock(hashtext('render_metrics'))";

    private const string UnlockSql = "SELECT pg_advisory_unlock(hashtext('render_metrics'))";

    private const string TableExistsSql = "SELECT to_regclass('public.render_jobs') IS NOT NULL";

    private const string CountSql =
        "SELECT count(*) FROM render_jobs WHERE status IN ('queued', 'submitted', 'running', 'ingesting')";

    /// <summary>Counts the in-flight render jobs, or reports that another Worker holds the lock.</summary>
    /// <param name="cancellationToken">The shutdown token.</param>
    /// <returns>The count, or <see langword="null"/> when the advisory lock was not acquired.</returns>
    public async Task<long?> CountActiveAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (await ScalarAsync(connection, TryLockSql, cancellationToken) is not true)
        {
            return null;
        }

        try
        {
            if (await ScalarAsync(connection, TableExistsSql, cancellationToken) is not true)
            {
                return 0;
            }

            return await ScalarAsync(connection, CountSql, cancellationToken) is long count ? count : 0;
        }
        finally
        {
            await ScalarAsync(connection, UnlockSql, CancellationToken.None);
        }
    }

    private static async Task<object?> ScalarAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandType = CommandType.Text;

        return await command.ExecuteScalarAsync(cancellationToken);
    }
}
