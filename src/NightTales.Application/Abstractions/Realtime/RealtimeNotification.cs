using System.Text.Json;

namespace NightTales.Application.Abstractions.Realtime;

/// <summary>
/// One notification read from PostgreSQL's <c>LISTEN</c> side: either a <c>realtime</c> event for one
/// user, or the <c>security</c> signal that says "close that user's live connections"
/// (09-backend-api L218, 09 README §B10 WT37).
/// </summary>
/// <remarks>
/// <para>
/// The parse is deliberately strict about the identifiers and lenient about the event name: a body
/// whose <c>userId</c> cannot be read is dropped (it cannot be routed anywhere), while an unknown
/// <c>event</c> value is kept in <see cref="EventName"/> so the listener can log it and forward
/// nothing instead of throwing inside the loop (09.8.1-T1 plan §5 [DECISION]).
/// </para>
/// <para>
/// <see cref="Body"/> is the whole NOTIFY body as a JSON element and is what the listener forwards to
/// the group; it is <c>null</c> on the <c>security</c> channel, whose payload is the user id alone.
/// The listener targets the group of <see cref="UserId"/> — the only user id in play — so a message can
/// never name a group of its own.
/// </para>
/// </remarks>
/// <param name="Channel">The PostgreSQL channel the message arrived on (<c>realtime</c> or <c>security</c>).</param>
/// <param name="UserId">The user the message is about.</param>
/// <param name="EventName">The wire event name as it arrived; unknown or absent values are not a parse failure.</param>
/// <param name="Body">The body to forward on <c>realtime</c>; <c>null</c> on <c>security</c>.</param>
public sealed record RealtimeNotification(string Channel, Guid UserId, string? EventName, JsonElement? Body)
{
    /// <summary>The channel <see cref="IRealtimeNotifier"/> publishes on (<c>realtime</c>, BD11).</summary>
    public const string RealtimeChannel = "realtime";

    /// <summary>
    /// The channel a revocation signal uses (<c>security</c>, 09-backend-api L218): its payload is the
    /// user id only. 09.8.1 plan §10 item 3 records that BD11 does not name this channel yet.
    /// </summary>
    public const string SecurityChannel = "security";

    /// <summary>
    /// Parses one <c>LISTEN</c> message. Returns <c>false</c> — without throwing — for a channel this
    /// listener does not serve, a body that is not JSON, or a message without a usable user id, so the
    /// loop can log it and carry on.
    /// </summary>
    /// <param name="channel">The channel PostgreSQL delivered the message on.</param>
    /// <param name="payload">The message's payload, exactly as PostgreSQL delivered it.</param>
    /// <param name="notification">The parsed notification when the message is one this listener serves.</param>
    public static bool TryParse(string channel, string payload, out RealtimeNotification? notification)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(payload);

        notification = null;

        if (string.Equals(channel, SecurityChannel, StringComparison.Ordinal))
        {
            if (!Guid.TryParse(payload, out var userId) || userId == Guid.Empty)
            {
                return false;
            }

            notification = new RealtimeNotification(SecurityChannel, userId, EventName: null, Body: null);
            return true;
        }

        if (!string.Equals(channel, RealtimeChannel, StringComparison.Ordinal))
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("userId", out var userIdElement)
                || userIdElement.ValueKind != JsonValueKind.String
                || !Guid.TryParse(userIdElement.GetString(), out var userId)
                || userId == Guid.Empty)
            {
                return false;
            }

            var eventName = root.TryGetProperty("event", out var eventElement)
                && eventElement.ValueKind == JsonValueKind.String
                    ? eventElement.GetString()
                    : null;

            // Clone detaches the element from the document this method disposes.
            notification = new RealtimeNotification(RealtimeChannel, userId, eventName, root.Clone());
            return true;
        }
    }
}
