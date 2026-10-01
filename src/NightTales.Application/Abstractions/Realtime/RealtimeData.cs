using System.Text.Json.Serialization;

namespace NightTales.Application.Abstractions.Realtime;

/// <summary>
/// The ruled exception to the ids-only payload ([RULED — README §B10 WT54]): real-time progress may
/// carry <see cref="Stage"/> and <see cref="Percent"/> — and nothing else — and only for
/// <see cref="RealtimeEventName.RenderProgress"/>; every other event leaves
/// <see cref="RealtimeEvent.Data"/> <c>null</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Stage"/> is the wire value of the render stage (<c>queued</c>, <c>running</c>,
/// <c>ingesting</c>, and the finer <c>voice</c>, <c>subtitles</c>, <c>images</c>, <c>assembling</c>,
/// <c>checking</c> of 00 §8 / 05 CR-2). It is a string rather than the domain's <c>RenderStage</c>
/// enum because that enum belongs to the render aggregate of 09.6/03 — this folder adds no Domain
/// type (09.8.1 plan §3, "no Domain change") — and because the adapter may report a stage this wave
/// does not know, which must travel, not throw.
/// </para>
/// <para>
/// <see cref="Percent"/> is 0-100; <see cref="ForRenderProgress"/> is the only way a producer should
/// build the value, so a stage can never travel without its percentage and a percentage can never
/// leave the range.
/// </para>
/// </remarks>
public sealed record RealtimeData(
    [property: JsonPropertyName("stage")] string? Stage,
    [property: JsonPropertyName("percent")] short? Percent)
{
    /// <summary>The lowest percentage a <see cref="RealtimeEventName.RenderProgress"/> may report.</summary>
    public const short MinPercent = 0;

    /// <summary>The highest percentage a <see cref="RealtimeEventName.RenderProgress"/> may report.</summary>
    public const short MaxPercent = 100;

    /// <summary>
    /// The data of one progress event: <paramref name="stage"/> (the wire stage) at
    /// <paramref name="percent"/> (0-100).
    /// </summary>
    /// <param name="stage">The stage the render job is in; must not be blank.</param>
    /// <param name="percent">How far it is, 0-100.</param>
    public static RealtimeData ForRenderProgress(string stage, short percent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentOutOfRangeException.ThrowIfLessThan(percent, MinPercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, MaxPercent);

        return new RealtimeData(stage, percent);
    }
}
