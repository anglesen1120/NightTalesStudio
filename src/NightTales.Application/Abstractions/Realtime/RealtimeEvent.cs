namespace NightTales.Application.Abstractions.Realtime;

/// <summary>
/// One real-time event a feature has just committed ([RULED — README §B10 WT54] freezes the shape):
/// the event <paramref name="Name"/>, the <paramref name="Ids"/> it is about, and — for
/// <see cref="RealtimeEventName.RenderProgress"/> only — the ruled <paramref name="Data"/>.
/// </summary>
/// <remarks>
/// <para>
/// The record is the whole producer contract of 09 README BD11: 02, 06 and 09.6.3 build it inside
/// their command or outbox handler and hand it to <see cref="IRealtimeNotifier"/> with the user id
/// of their own <c>ICurrentUser</c>/<c>ISystemContext</c> (09 README D10) — never with an id a client
/// sent. It carries ids and enums only; the NOTIFY body it becomes must stay shorter than 8 KB
/// (<see cref="RealtimePayload.MaxBytes"/>).
/// </para>
/// <para>
/// The position of <paramref name="Data"/> is not enforced here: a producer of a later wave may pass
/// data by mistake, and the notifier — the boundary that writes the wire body — is what refuses to
/// forward it for any event other than <see cref="RealtimeEventName.RenderProgress"/>
/// (<see cref="RealtimePayload.Build"/>). Failing there costs the event, not the caller's command.
/// </para>
/// </remarks>
/// <param name="Name">The event; its wire and method names come from <see cref="RealtimeEventNames"/>.</param>
/// <param name="Ids">The ids the event is about.</param>
/// <param name="Data">The ruled <see cref="RealtimeEventName.RenderProgress"/> data, otherwise <c>null</c>.</param>
public sealed record RealtimeEvent(
    RealtimeEventName Name,
    RealtimeIds Ids,
    RealtimeData? Data = null);
