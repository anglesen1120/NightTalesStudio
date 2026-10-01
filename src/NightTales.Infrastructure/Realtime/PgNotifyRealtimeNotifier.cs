using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Infrastructure.Persistence;

namespace NightTales.Infrastructure.Realtime;

/// <summary>
/// The one implementation of <see cref="IRealtimeNotifier"/> (09 README BD11): it writes
/// <c>SELECT pg_notify('realtime', &lt;body&gt;)</c> and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// [DECISION] The statement runs on the caller's <see cref="AppDbContext"/>, so it joins the
/// transaction <c>UnitOfWorkBehavior</c> opened (09 README D5): PostgreSQL delivers the notification
/// <b>at commit</b> and discards it on rollback, and a command that fails never announces a change
/// that did not happen. With no ambient transaction — an outbox handler, 09 README D11 — the statement
/// commits on its own and the notification goes out immediately.
/// </para>
/// <para>
/// The notifier never opens a transaction, never calls <c>SaveChangesAsync</c> and never writes a row:
/// one round trip per event, and nothing at all when the body would not be strictly shorter than the
/// cap. A database that is down when a producer notifies makes that producer's command fail and roll
/// back — a change is never committed without its notification (09.8.1-T1 failure table).
/// </para>
/// </remarks>
/// <param name="database">The caller's write context, whose transaction the statement joins.</param>
/// <param name="options">The payload cap (<c>Realtime:MaxPayloadBytes</c>).</param>
/// <param name="metrics">The notify and drop counters.</param>
/// <param name="logger">The single error line of a dropped payload; never the body or an id.</param>
public sealed partial class PgNotifyRealtimeNotifier(
    AppDbContext database,
    IOptions<RealtimeOptions> options,
    RealtimeMetrics metrics,
    ILogger<PgNotifyRealtimeNotifier> logger) : IRealtimeNotifier
{
    /// <summary>
    /// The one statement this notifier issues: a single <c>SELECT pg_notify</c> with the body as its
    /// only parameter (the channel name is a literal, never input).
    /// </summary>
    public const string NotifySql = "SELECT pg_notify('realtime', {0})";

    /// <inheritdoc />
    public async Task NotifyAsync(Guid userId, RealtimeEvent realtimeEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(realtimeEvent);

        var wireEvent = realtimeEvent.Name.ToWireName();
        var payload = RealtimePayload.Build(userId, realtimeEvent);

        if (!RealtimePayload.FitsLimit(payload, options.Value.MaxPayloadBytes))
        {
            // Drop, never truncate (BD11/BD16): a cut-off JSON is worse than a lost event, and the
            // client refetches. The cap is exclusive, so this also catches a body PostgreSQL would
            // refuse (22023). The event name is logged, the ids are not.
            metrics.NotifyDropped(wireEvent);
            LogPayloadDropped(wireEvent, Encoding.UTF8.GetByteCount(payload), options.Value.MaxPayloadBytes);

            return;
        }

        await database.Database
            .ExecuteSqlRawAsync(NotifySql, [payload], cancellationToken)
            .ConfigureAwait(false);

        metrics.Notify(wireEvent);
    }

    [LoggerMessage(
        EventId = 2400,
        Level = LogLevel.Error,
        Message = "realtime.notify.dropped: event={Event} bytes={Bytes} cap={Cap}")]
    private partial void LogPayloadDropped(string @event, int bytes, int cap);
}
