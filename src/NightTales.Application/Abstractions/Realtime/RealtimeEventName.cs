namespace NightTales.Application.Abstractions.Realtime;

/// <summary>
/// The four real-time events of 00 §8 (09-backend-api L215): the closed set a producer may raise and a
/// client may subscribe to. The member name is the SignalR <em>method</em> name (PascalCase, plan §5);
/// the wire <c>event</c> member of the NOTIFY payload is the snake_case name of
/// <see cref="RealtimeEventNames.ToWireName"/> (09 README D14).
/// </summary>
/// <remarks>
/// 09.8.1-T1 plan §10 item 5 leaves a fifth event (<c>AutopilotRunUpdated</c>, asked for by 02.8.2
/// L227-228) out on purpose: it is not one of the four of 00 §8.
/// </remarks>
public enum RealtimeEventName
{
    /// <summary>An episode changed (status, generation stage, story versions, …), ids only.</summary>
    EpisodeUpdated,

    /// <summary>Render progress: the only event that may carry <see cref="RealtimeData"/> (WT54).</summary>
    RenderProgress,

    /// <summary>A publication changed (upload or processing status), ids only.</summary>
    PublicationUpdated,

    /// <summary>The credit balance changed, ids only.</summary>
    CreditBalanceChanged,
}

/// <summary>
/// The wire mapping of <see cref="RealtimeEventName"/>: the snake_case NOTIFY name, the PascalCase
/// SignalR method name, and the parse that makes the listener ignore an event it does not know
/// (09.8.1-T1 plan §5, [DECISION] of the listener).
/// </summary>
public static class RealtimeEventNames
{
    /// <summary>The name carried by the NOTIFY <c>event</c> member (<c>episode_updated</c>, …).</summary>
    /// <param name="name">The event; undefined values are a programming error.</param>
    public static string ToWireName(this RealtimeEventName name) => name switch
    {
        RealtimeEventName.EpisodeUpdated => "episode_updated",
        RealtimeEventName.RenderProgress => "render_progress",
        RealtimeEventName.PublicationUpdated => "publication_updated",
        RealtimeEventName.CreditBalanceChanged => "credit_balance_changed",
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown real-time event name."),
    };

    /// <summary>The SignalR method name of <paramref name="name"/>: the PascalCase member name.</summary>
    /// <param name="name">The event; undefined values are a programming error.</param>
    public static string ToMethodName(this RealtimeEventName name) =>
        Enum.IsDefined(name)
            ? name.ToString()
            : throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown real-time event name.");

    /// <summary>
    /// Parses a NOTIFY <c>event</c> value against the closed set. An unknown value is not an error at
    /// the transport boundary: the listener logs it and forwards nothing (09.8.1-T1 plan §5), so a
    /// producer of a later wave cannot make an Api instance throw.
    /// </summary>
    /// <param name="wireName">The value read from the payload, or <c>null</c> when it is absent.</param>
    /// <param name="name">The parsed event when the value is known.</param>
    public static bool TryParseWireName(string? wireName, out RealtimeEventName name)
    {
        foreach (var candidate in Enum.GetValues<RealtimeEventName>())
        {
            if (string.Equals(candidate.ToWireName(), wireName, StringComparison.Ordinal))
            {
                name = candidate;
                return true;
            }
        }

        name = default;
        return false;
    }
}
