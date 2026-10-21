using System.Text;
using System.Text.Json;

namespace NightTales.Application.Abstractions.Realtime;

/// <summary>
/// The wire body of the <c>realtime</c> NOTIFY channel (09 README BD11, plan §3): the frozen JSON
/// <c>{ "userId", "event", "ids", "data" }</c> and its exclusive 8 KB cap.
/// </summary>
/// <remarks>
/// <para>
/// The builder lives in Application because the body is a contract, not transport plumbing: the
/// producers of 02/06/09.6.3 are Application types, and the rules it enforces are the ones WT54
/// ruled — ids only, <c>data</c> only for <see cref="RealtimeEventName.RenderProgress"/>, and
/// <c>data</c> holding nothing but <c>stage</c> and <c>percent</c>.
/// </para>
/// <para>
/// <c>data</c> is always present, <c>null</c> for every event other than
/// <see cref="RealtimeEventName.RenderProgress"/> (plan §3: "every other event sends <c>data: null</c>"),
/// while the ids leave out the members the event does not carry. Even if a caller passes
/// <see cref="RealtimeEvent.Data"/> for another event, it is never written: the ruling wins over the
/// caller's mistake, and an oversized body is dropped by the notifier rather than truncated
/// (<see cref="MaxBytes"/>).
/// </para>
/// </remarks>
public static class RealtimePayload
{
    /// <summary>
    /// The exclusive cap of the NOTIFY body in UTF-8 bytes (BD11/BD16; PostgreSQL's own payload limit is
    /// 8,000 bytes, and it refuses a payload as long as that). A body of <see cref="MaxBytes"/> bytes or
    /// more is dropped by the notifier and counted, never truncated: the last deliverable body is 7,999
    /// bytes long.
    /// </summary>
    public const int MaxBytes = 8_000;

    /// <summary>
    /// Builds the NOTIFY body of <paramref name="event"/> addressed to <paramref name="userId"/>.
    /// </summary>
    /// <param name="userId">The id of the user whose page must be updated.</param>
    /// <param name="realtimeEvent">The committed event; its data is written only when the ruling allows it.</param>
    public static string Build(Guid userId, RealtimeEvent realtimeEvent)
    {
        ArgumentNullException.ThrowIfNull(realtimeEvent);

        return JsonSerializer.Serialize(new
        {
            userId,
            @event = realtimeEvent.Name.ToWireName(),
            ids = realtimeEvent.Ids,
            data = CarriesRuledData(realtimeEvent) ? realtimeEvent.Data : null,
        });
    }

    /// <summary>
    /// Whether <paramref name="payload"/> is short enough for the channel's cap: it must be
    /// <b>strictly shorter</b> than <paramref name="maxBytes"/>, because PostgreSQL refuses a payload as
    /// long as its limit.
    /// </summary>
    /// <param name="payload">A body returned by <see cref="Build"/>.</param>
    /// <param name="maxBytes">
    /// The exclusive cap in UTF-8 bytes (<c>Realtime:MaxPayloadBytes</c>); defaults to the contract's
    /// <see cref="MaxBytes"/>. A body of exactly this many bytes does <b>not</b> fit.
    /// </param>
    public static bool FitsLimit(string payload, int maxBytes = MaxBytes)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        return Encoding.UTF8.GetByteCount(payload) < maxBytes;
    }

    /// <summary>
    /// Whether the ruling lets this event carry data: only <see cref="RealtimeEventName.RenderProgress"/>
    /// may ([RULED — README §B10 WT54]).
    /// </summary>
    /// <param name="realtimeEvent">The event a producer built.</param>
    public static bool CarriesRuledData(RealtimeEvent realtimeEvent)
    {
        ArgumentNullException.ThrowIfNull(realtimeEvent);

        return realtimeEvent.Name == RealtimeEventName.RenderProgress;
    }
}
