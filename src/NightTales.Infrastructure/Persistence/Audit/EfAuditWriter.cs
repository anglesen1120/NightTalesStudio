using System.Text.Json.Nodes;
using NightTales.Application.Abstractions.Audit;
using NightTales.Domain.Audit;

namespace NightTales.Infrastructure.Persistence.Audit;

/// <summary>
/// The one writer of <c>audit_logs</c> (09 README BD15, D9): it adds the row to the scope's
/// <see cref="AppDbContext"/> and lets the caller's transaction commit it.
/// </summary>
/// <remarks>
/// <para>
/// The behavioural slot order is what makes the row honest (D4/G6): <c>AuditBehavior</c> sits inside
/// <c>UnitOfWorkBehavior</c>'s transaction, so the row commits with the change, an error response
/// commits neither, and an idempotent replay never reaches this port. A hand-written caller — an auth
/// handler, a Worker job, a CLI verb — opens the transaction itself, and this class refuses to run
/// without one: adding a row to a scope that has no transaction would persist an audit trail that the
/// change it describes may still roll back.
/// </para>
/// <para>
/// It never saves and never commits, so the caller keeps the one <c>SaveChangesAsync</c> of its unit of
/// work (D5). There is no update path: the table is append-only.
/// </para>
/// </remarks>
/// <param name="database">The tracked write model of the scope.</param>
public sealed class EfAuditWriter(AppDbContext database) : IAuditWriter
{
    /// <summary>The <c>data</c> key an operator label lands in (09 README BD13).</summary>
    private const string ActorLabelKey = "actor";

    /// <inheritdoc />
    public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        if (database.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "IAuditWriter.WriteAsync requires an ambient transaction: the AuditBehavior runs inside "
                + "UnitOfWorkBehavior (09 README D4 slot 6), and a hand-written caller must call "
                + "IUnitOfWork.BeginAsync itself.");
        }

        database.AuditLogs.Add(AuditLog.Record(
            entry.ActorUserId,
            entry.ActorType,
            entry.SubjectUserId,
            entry.Action,
            entry.Category,
            entry.Visibility,
            entry.EntityType,
            entry.EntityId,
            DataJsonWithActorLabel(entry.DataJson, entry.ActorLabel),
            entry.Ip,
            entry.UserAgent,
            entry.OccurredAt));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Merges <see cref="AuditEntry.ActorLabel"/> into the payload as <c>data.actor</c> — the operator
    /// verbs of 11 T5 and BD13 are the only callers that set a label, and their row must carry
    /// <c>"system:operator"</c> without a new column. Without a label the payload is passed through
    /// untouched, so a command's <c>data</c> holds exactly its projection.
    /// </summary>
    private static string DataJsonWithActorLabel(string dataJson, string? actorLabel)
    {
        if (string.IsNullOrEmpty(actorLabel))
        {
            return dataJson;
        }

        if (JsonNode.Parse(dataJson) is not JsonObject payload)
        {
            // A caller that set a label but projected no JSON object is a programming error; failing here
            // names it, where the provider would only report an invalid jsonb input.
            throw new InvalidOperationException(
                "An audit entry with an actor label must carry a JSON object as its data payload.");
        }

        payload[ActorLabelKey] = actorLabel;

        return payload.ToJsonString();
    }
}
