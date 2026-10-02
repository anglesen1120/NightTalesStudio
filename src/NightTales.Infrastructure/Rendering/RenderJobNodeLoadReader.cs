using System.Data;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Rendering;

namespace NightTales.Infrastructure.Rendering;

/// <summary>
/// The derived render load per node (11 README D13, 00 overview §6 L311): one grouped count over
/// <c>render_jobs</c> for the statuses that consume a slot — <c>submitted</c> and <c>running</c>
/// (00 §7 L332). The poller's drift check and 11.3.3-T2's free-slot computation read the same number here,
/// so "in flight" is defined once.
/// </summary>
/// <remarks>
/// <para>
/// The statement is hand-written SQL rather than an EF projection because the <c>render_jobs</c> entity is
/// 09.6.1-T1's (the table arrives with that task) and this task must not pre-empt its model — 11 README F11
/// forbids the reverse dependency. It selects three columns, groups in the database, and returns one row
/// per node with in-flight work, so the read is bounded by the node count.
/// </para>
/// <para>
/// While <c>render_jobs</c> does not exist the reader answers "nothing in flight", which is the truth for a
/// schema without the table: an operator's <c>nodes list</c> and the poller keep working before 09.6.1
/// lands, and the drift check compares the reported count with zero.
/// </para>
/// </remarks>
/// <param name="db">The scope's write model, used only for its connection (the query is read-only).</param>
public sealed class RenderJobNodeLoadReader(IAppDbContext db) : INodeLoadReader
{
    private const string InFlightSql =
        "SELECT worker_node_id, count(*)::int AS in_flight "
        + "FROM render_jobs WHERE status IN ('submitted', 'running') AND worker_node_id IS NOT NULL "
        + "GROUP BY worker_node_id";

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, int>> InFlightByNodeAsync(CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken);

        if (!await TableExistsAsync(connection, cancellationToken))
        {
            return new Dictionary<Guid, int>();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = InFlightSql;
        command.CommandType = CommandType.Text;

        var counts = new Dictionary<Guid, int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            counts[reader.GetGuid(0)] = reader.GetInt32(1);
        }

        return counts;
    }

    /// <summary>
    /// Whether <c>public.render_jobs</c> exists yet. One <c>to_regclass</c> probe (a catalog lookup, no
    /// scan) keeps the reader honest on a schema that predates 09.6.1-T1 instead of failing every tick.
    /// </summary>
    private static async Task<bool> TableExistsAsync(
        System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT to_regclass('public.render_jobs') IS NOT NULL";
        probe.CommandType = CommandType.Text;

        return await probe.ExecuteScalarAsync(cancellationToken) is true;
    }
}
