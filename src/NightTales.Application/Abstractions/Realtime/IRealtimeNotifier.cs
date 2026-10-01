namespace NightTales.Application.Abstractions.Realtime;

/// <summary>
/// The producer seam of 09 README BD11 (signature frozen there): a feature that has just committed a
/// change hands the event to the platform, which delivers it to that user's open page.
/// </summary>
/// <remarks>
/// <para>
/// The only implementation is 09.8.1-T1's <c>PgNotifyRealtimeNotifier</c>, which writes
/// <c>SELECT pg_notify('realtime', …)</c> on the <em>caller's</em> <c>AppDbContext</c>: inside a
/// command the notification is delivered at commit and discarded on rollback (09 README D5), and in
/// an outbox handler — where there is no ambient transaction (09 README D11) — it goes out at once.
/// The notifier never opens a transaction, never calls <c>SaveChanges</c> and never writes a row.
/// </para>
/// <para>
/// The user id always comes from the caller's own <c>ICurrentUser</c>/<c>ISystemContext</c>
/// (09 README D10), never from a request body: the hub only ever serves the group of the user the
/// producer named.
/// </para>
/// </remarks>
public interface IRealtimeNotifier
{
    /// <summary>
    /// Delivers <paramref name="realtimeEvent"/> to <paramref name="userId"/>'s open hub connections, or drops
    /// it — with a metric and one log line — when its body would not be strictly shorter than
    /// <see cref="RealtimePayload.MaxBytes"/> (BD11/BD16: drop, never truncate).
    /// </summary>
    /// <param name="userId">The owner of the page that must be updated.</param>
    /// <param name="realtimeEvent">The event, built from Application types only.</param>
    /// <param name="cancellationToken">The caller's token; a failure propagates and rolls the command back.</param>
    Task NotifyAsync(Guid userId, RealtimeEvent realtimeEvent, CancellationToken cancellationToken);
}
