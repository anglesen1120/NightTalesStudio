using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using NightTales.Domain.Publishing;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Outbox;

namespace NightTales.Infrastructure.Publishing;

/// <summary>
/// The <see cref="IPlatformIncidentStore"/> over <c>platform_incidents</c> (06.2.3-T2, 06 README D14/YR25): the
/// conflict-tolerant raise, the open-incident probe and the operator's clear, each joining the caller's transaction.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why raw SQL for the writes.</b> The raise is idempotent through the partial unique index
/// <c>ux_platform_incidents_open</c>: <c>INSERT … ON CONFLICT (platform, oauth_client_id) WHERE cleared_at IS NULL DO
/// NOTHING RETURNING id</c> is one statement that both arbitrates "first raise" and answers the id, and EF cannot
/// express it (<c>IdempotencyStore</c>'s claim has the same shape). A first raise then writes its
/// <c>platform_incident_raised.v1</c> outbox row directly, built to <c>OutboxSaveChangesInterceptor.ToRow</c>'s own
/// contract, because no tracked aggregate raised the event — the same posture as
/// <c>PgFeatureFlagChangeNotifier</c>, whose event also has no user root.
/// </para>
/// <para>
/// <b>No transaction of its own.</b> Every write enlists in the caller's ambient transaction
/// (<c>PublishYouTubeHandler</c>'s on the upload path, 06.4.2-T2's on the token-health path), so the incident row, the
/// outbox row, the audit row and — on the upload path — <c>Publication.HoldForIncident</c> commit or roll back
/// together. A call outside a transaction is refused exactly as <c>IdempotencyStore</c> and <c>EfAuditWriter</c> refuse
/// it, because the pair of rows would otherwise be committed by a scope that may still roll the change back (09 README
/// D5).
/// </para>
/// <para>
/// <b>The count is platform-wide.</b> <c>AffectedConnections</c> counts every <c>active</c> connection pinned to the
/// refused client, so it runs as raw SQL too: <c>channel_connections</c> is <c>IUserOwned</c>, and the tenant filter of
/// 09 README D10 would otherwise narrow the count to the scope's own user — the bypass is the statement's own predicate,
/// never <c>IgnoreQueryFilters</c> (whose allow-list this type therefore does not join). The <c>'active'</c> literal is
/// the column's stored wire value (<c>ChannelConnectionStatus.Active</c> through the snake-case enum converter, which
/// <c>ck_channel_connections_status</c> also lists).
/// </para>
/// <para>
/// <b>Nothing secret reaches a sink.</b> The payloads and log lines carry the incident id, the platform, the client id,
/// Google's error code, the operator's label and counts; never a token, a session URI, a response body or a title
/// (06 README D17). The operator's name and note exist only in the row and in the audit row.
/// </para>
/// </remarks>
/// <param name="database">The scope's write context: its connection, its transaction and its change tracker.</param>
/// <param name="audit">The one writer of <c>audit_logs</c>; it refuses a scope without a transaction.</param>
/// <param name="events">Resolves the stable outbox <c>type</c> name of the two incident events.</param>
/// <param name="options">The configured statement timeout, which a raw command does not inherit.</param>
/// <param name="logger">Carries the raise/clear lines; ids and codes only.</param>
public sealed partial class PlatformIncidentStore(
    AppDbContext database,
    IAuditWriter audit,
    OutboxEventRegistry events,
    IOptions<PersistenceOptions> options,
    ILogger<PlatformIncidentStore> logger) : IPlatformIncidentStore
{
    /// <summary>The audit action a first raise records (plan §UC3, category <c>support</c>, visibility <c>internal</c>).</summary>
    public const string RaisedAuditAction = "publishing.incident_raised";

    /// <summary>
    /// The audit action of a clear (plan §UC2, category <c>support</c>, visibility <c>internal</c>). 06.2.3-T3's clear
    /// command writes that row — it is the only caller and the only place that knows <c>data.rescheduled</c> — so this
    /// constant is the reserved spelling it must reuse, and this store writes none.
    /// </summary>
    public const string ClearedAuditAction = "publishing.incident_cleared";

    /// <summary>The <c>audit_logs.entity_type</c> value of both actions.</summary>
    public const string AuditEntityType = "platform_incident";

    /// <summary>The stored <c>channel_connections.status</c> value the affected count matches.</summary>
    private const string ActiveConnectionStatus = "active";

    /// <summary>
    /// The raise (plan §UC1 step 7, UC3): one statement decides "first raise or already open", and <c>RETURNING id</c>
    /// answers the row this call inserted.
    /// </summary>
    private const string InsertSql =
        """
        INSERT INTO platform_incidents (id, platform, oauth_client_id, error_code, raised_at)
        VALUES (@id, @platform, @oauth_client_id, @error_code, @raised_at)
        ON CONFLICT (platform, oauth_client_id) WHERE cleared_at IS NULL DO NOTHING
        RETURNING id
        """;

    /// <summary>The client's live connections (D13's <c>affectedConnections</c>), counted platform-wide.</summary>
    private const string CountActiveConnectionsSql =
        $"""
        SELECT count(*) FROM channel_connections
        WHERE platform = @platform AND oauth_client_id = @oauth_client_id AND status = '{ActiveConnectionStatus}'
        """;

    /// <summary>
    /// The clear (plan §UC2): only an open row matches, so a repeat clears nothing and the <c>RETURNING</c> answers
    /// the client the released publications belong to. The sweep itself is 06.2.3-T3's.
    /// </summary>
    private const string ClearSql =
        """
        UPDATE platform_incidents
        SET cleared_at = @cleared_at, cleared_by = @cleared_by, note = @note
        WHERE id = @id AND cleared_at IS NULL
        RETURNING platform, oauth_client_id
        """;

    /// <inheritdoc />
    public async Task<ErrorOr<PlatformIncidentRaiseResult>> RaiseAsync(
        string platform,
        string oauthClientId,
        string errorCode,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(oauthClientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);

        now = now.ToUniversalTime();
        var incident = PlatformIncident.Raise(Guid.CreateVersion7(), platform, oauthClientId, errorCode, now);

        var inserted = await TryInsertAsync(incident, ct).ConfigureAwait(false);
        if (inserted is not { } incidentId)
        {
            // Another publication of the same client raised it first: answer that row and write nothing, so a storm of
            // refusals still produces one row, one outbox event and one audit row (YR25).
            var open = await database.PlatformIncidents
                .AsNoTracking()
                .Where(row => row.Platform == platform && row.OAuthClientId == oauthClientId && row.ClearedAt == null)
                .Select(row => (Guid?)row.Id)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"platform_incidents has no open row for ({platform}, {oauthClientId}) after a conflicting insert; "
                    + "ux_platform_incidents_open must hold exactly that row (06 README D14).");

            var alreadyAffected = await CountActiveConnectionsAsync(platform, oauthClientId, ct).ConfigureAwait(false);
            LogAlreadyOpen(logger, open, platform, oauthClientId, alreadyAffected);

            return new PlatformIncidentRaiseResult(open, IsNew: false, alreadyAffected);
        }

        var affectedConnections = await CountActiveConnectionsAsync(platform, oauthClientId, ct).ConfigureAwait(false);

        QueueOutbox(
            typeof(PlatformIncidentRaised),
            new PlatformIncidentRaised(incidentId, platform, oauthClientId, affectedConnections, now));

        await audit.WriteAsync(
            new AuditEntry(
                ActorUserId: null,
                ActorType: ActorType.System,
                SubjectUserId: null,
                Action: RaisedAuditAction,
                Category: Category.Support,
                Visibility: Visibility.Internal,
                EntityType: AuditEntityType,
                EntityId: incidentId,
                DataJson: RaisePayload(incidentId, platform, oauthClientId, errorCode, affectedConnections),
                Ip: null,
                UserAgent: null,
                ActorLabel: null,
                OccurredAt: now),
            ct).ConfigureAwait(false);

        LogRaised(logger, incidentId, platform, oauthClientId, errorCode, affectedConnections);

        return new PlatformIncidentRaiseResult(incidentId, IsNew: true, affectedConnections);
    }

    /// <inheritdoc />
    public async Task<ErrorOr<Guid?>> HasOpenIncidentAsync(
        string platform,
        string oauthClientId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(oauthClientId);

        // Both predicates are the partial index's own, so this is one index probe (plan §3.4). The table is not
        // IUserOwned, so no tenant filter narrows it.
        var open = await database.PlatformIncidents
            .AsNoTracking()
            .Where(row => row.Platform == platform && row.OAuthClientId == oauthClientId && row.ClearedAt == null)
            .Select(row => (Guid?)row.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return open;
    }

    /// <inheritdoc />
    public async Task<ErrorOr<PlatformIncidentClearResult>> ClearAsync(
        Guid incidentId,
        string operatorName,
        string note,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorName);
        ArgumentException.ThrowIfNullOrWhiteSpace(note);

        now = now.ToUniversalTime();

        var cleared = await TryClearAsync(incidentId, operatorName, note, now, ct).ConfigureAwait(false);
        if (cleared is not { } row)
        {
            var exists = await database.PlatformIncidents
                .AsNoTracking()
                .AnyAsync(incident => incident.Id == incidentId, ct)
                .ConfigureAwait(false);

            if (!exists)
            {
                LogNotFound(logger, incidentId);

                return Error.NotFound(
                    code: ErrorCodes.NotFound,
                    description: "No platform incident has this id.");
            }

            // Idempotent repeat: the row is already clear, so nothing is written and no second event is queued.
            LogAlreadyCleared(logger, incidentId);

            return new PlatformIncidentClearResult(incidentId, AlreadyCleared: true, PublicationsRescheduled: 0);
        }

        QueueOutbox(typeof(PlatformIncidentCleared), new PlatformIncidentCleared(incidentId, row.Platform, row.OAuthClientId, now));

        // The clear's audit row (`publishing.incident_cleared`) belongs to 06.2.3-T3's clear command, which is the only
        // caller: its commit is what knows how many held publications the reschedule sweep woke, and its row shape
        // (`data.rescheduled`, `data.actor` = the operator) is asserted as exactly one row per clear. This member
        // writes the columns, the outbox row and the log line only, so T3 adds that one row instead of a second
        // competing one.
        LogCleared(logger, incidentId, row.Platform, row.OAuthClientId);

        // The per-incident sweep is 06.2.3-T3's: this task writes the incident's own columns and the pair of rows only.
        return new PlatformIncidentClearResult(incidentId, AlreadyCleared: false, PublicationsRescheduled: 0);
    }

    /// <summary>Inserts the incident unless one is open for the client; <c>null</c> when it already existed.</summary>
    private async Task<Guid?> TryInsertAsync(PlatformIncident incident, CancellationToken ct)
    {
        await using var command = CreateCommand(InsertSql);
        command.Parameters.AddWithValue("id", incident.Id);
        command.Parameters.AddWithValue("platform", incident.Platform);
        command.Parameters.AddWithValue("oauth_client_id", incident.OAuthClientId);
        command.Parameters.AddWithValue("error_code", incident.ErrorCode);
        command.Parameters.AddWithValue("raised_at", incident.RaisedAt);

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is Guid inserted ? inserted : null;
    }

    /// <summary>Clears the incident unless it is already clear; the closed row's platform and client, or <c>null</c>.</summary>
    private async Task<(string Platform, string OAuthClientId)?> TryClearAsync(
        Guid incidentId,
        string operatorName,
        string note,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var command = CreateCommand(ClearSql);
        command.Parameters.AddWithValue("id", incidentId);
        command.Parameters.AddWithValue("cleared_at", now);
        command.Parameters.AddWithValue("cleared_by", operatorName);
        command.Parameters.AddWithValue("note", note);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return (reader.GetString(0), reader.GetString(1));
    }

    /// <summary>
    /// The client's <c>active</c> connections, counted platform-wide: the predicate is the statement's own, so the
    /// <c>IUserOwned</c> tenant filter of `channel_connections` never narrows it (plan §UC3, D13's payload field).
    /// </summary>
    private async Task<int> CountActiveConnectionsAsync(string platform, string oauthClientId, CancellationToken ct)
    {
        await using var command = CreateCommand(CountActiveConnectionsSql);
        command.Parameters.AddWithValue("platform", platform);
        command.Parameters.AddWithValue("oauth_client_id", oauthClientId);

        var counted = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

        return counted is long count ? (int)count : 0;
    }

    /// <summary>
    /// Queues the event's <c>outbox_messages</c> row in the caller's unit of work, built to
    /// <c>OutboxSaveChangesInterceptor.ToRow</c>'s contract: the registry's stable name, the payload serialized for the
    /// same concrete type, no <c>user_id</c> (both events are <c>[PlatformEvent]</c>), the event's instant as
    /// <c>occurred_at</c> and <c>next_attempt_at</c>, the current activity as <c>trace_parent</c>.
    /// </summary>
    private void QueueOutbox(Type eventType, IDomainEvent domainEvent)
    {
        using var document = JsonSerializer.SerializeToDocument(domainEvent, eventType, OutboxJson.Options);

        database.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            Type = events.NameOf(eventType),
            Payload = document.RootElement.GetRawText(),
            UserId = null,
            OccurredAt = domainEvent.OccurredAt,
            NextAttemptAt = domainEvent.OccurredAt,
            TraceParent = Activity.Current?.Id,
        });
    }

    /// <summary>The raise's audit payload (09 README BD16: safe text only — ids, codes and a count).</summary>
    private static string RaisePayload(
        Guid incidentId,
        string platform,
        string oauthClientId,
        string errorCode,
        int affectedConnections) => new JsonObject
        {
            ["incidentId"] = incidentId,
            ["platform"] = platform,
            ["oauthClientId"] = oauthClientId,
            ["errorCode"] = errorCode,
            ["affectedConnections"] = affectedConnections,
        }.ToJsonString();

    /// <summary>
    /// A command over the scope's connection and its <em>open</em> transaction: the transaction is what makes the
    /// incident, its outbox row and its audit row one unit with the caller's change (09 README D5), so a call outside
    /// one is refused rather than committed on its own.
    /// </summary>
    private NpgsqlCommand CreateCommand(string sql)
    {
        var transaction = database.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "IPlatformIncidentStore writes inside the caller's transaction (06 README D14/YR25): open an "
                + "IUnitOfWork first, so the incident, its outbox row and its audit row commit with the change.");

        var connection = (NpgsqlConnection)database.Database.GetDbConnection();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = (NpgsqlTransaction)transaction.GetDbTransaction();

        // NpgsqlConnection.CreateCommand() does not inherit the EF provider's configured timeout (09 README D8).
        command.CommandTimeout = options.Value.CommandTimeoutSeconds;

        return command;
    }

    [LoggerMessage(EventId = 2383, Level = LogLevel.Warning, Message = "publishing.incident.raised incident={IncidentId} platform={Platform} oauth_client_id={OAuthClientId} error_code={ErrorCode} affected_connections={AffectedConnections}")]
    private static partial void LogRaised(
        ILogger logger,
        Guid incidentId,
        string platform,
        string oauthClientId,
        string errorCode,
        int affectedConnections);

    [LoggerMessage(EventId = 2384, Level = LogLevel.Information, Message = "publishing.incident.already-open incident={IncidentId} platform={Platform} oauth_client_id={OAuthClientId} affected_connections={AffectedConnections}")]
    private static partial void LogAlreadyOpen(
        ILogger logger,
        Guid incidentId,
        string platform,
        string oauthClientId,
        int affectedConnections);

    [LoggerMessage(EventId = 2385, Level = LogLevel.Information, Message = "publishing.incident.cleared incident={IncidentId} platform={Platform} oauth_client_id={OAuthClientId}")]
    private static partial void LogCleared(ILogger logger, Guid incidentId, string platform, string oauthClientId);

    [LoggerMessage(EventId = 2386, Level = LogLevel.Debug, Message = "publishing.incident.already-cleared incident={IncidentId}")]
    private static partial void LogAlreadyCleared(ILogger logger, Guid incidentId);

    [LoggerMessage(EventId = 2387, Level = LogLevel.Warning, Message = "publishing.incident.not-found incident={IncidentId}")]
    private static partial void LogNotFound(ILogger logger, Guid incidentId);
}
