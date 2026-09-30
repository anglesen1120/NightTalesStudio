using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace NightTales.Infrastructure.Persistence.Outbox;

/// <summary>
/// The outbox instruments of the platform (09 README D11, 09.2.4 plan §7): how far behind the dispatcher
/// is, how many messages it delivered, and how many became dead letters.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="Meter"/> instance per host, owned by DI, so a test can attach a
/// <see cref="MeterListener"/> to exactly this host's instruments instead of every meter of the process
/// that happens to be named <see cref="MeterName"/>.
/// </para>
/// <para>
/// <c>outbox_lag_seconds</c> is the age of the oldest row the last claim found due — the number 09 §8
/// alerts on above 30 s. <c>outbox_handled_total</c> is tagged with the event type and the outcome
/// (<c>processed</c>, <c>retried</c>, <c>dead_lettered</c>); <c>outbox_dead_letters_total</c> is untagged
/// so the alert on it is one number.
/// </para>
/// </remarks>
public sealed class OutboxMetrics : IDisposable
{
    /// <summary>The meter name every instrument of the outbox lives under.</summary>
    public const string MeterName = "NightTales.Outbox";

    /// <summary>The gauge of the oldest due row's age, in seconds.</summary>
    public const string LagInstrumentName = "outbox_lag_seconds";

    /// <summary>The counter of messages that reached a terminal failure.</summary>
    public const string DeadLettersInstrumentName = "outbox_dead_letters_total";

    /// <summary>The counter of deliveries, tagged by event type and outcome.</summary>
    public const string HandledInstrumentName = "outbox_handled_total";

    private readonly Counter<long> _deadLetters;
    private readonly Counter<long> _handled;
    private readonly Meter _meter = new(MeterName);
    private double _lagSeconds;

    /// <summary>The host's meter; a test attaches a listener to this exact instance.</summary>
    public Meter Meter => _meter;

    public OutboxMetrics()
    {
        Meter.CreateObservableGauge(
            LagInstrumentName,
            () => Volatile.Read(ref _lagSeconds),
            unit: "s",
            description: "How long the oldest row the dispatcher found due has been waiting.");

        _deadLetters = Meter.CreateCounter<long>(
            DeadLettersInstrumentName,
            unit: "{message}",
            description: "Messages that exhausted their attempts and will not be delivered again.");

        _handled = Meter.CreateCounter<long>(
            HandledInstrumentName,
            unit: "{message}",
            description: "Delivery outcomes, by event type and outcome (processed, retried, dead_lettered).");
    }

    /// <summary>Reports the lag of a claim: the oldest claimed row's age, or zero when nothing was due.</summary>
    /// <param name="oldestOccurredAt">The oldest row of the batch, or <c>null</c> when the batch was empty.</param>
    /// <param name="now">The instant the claim ran.</param>
    public void RecordClaim(DateTimeOffset? oldestOccurredAt, DateTimeOffset now) =>
        Volatile.Write(ref _lagSeconds, oldestOccurredAt is { } occurredAt ? (now - occurredAt).TotalSeconds : 0d);

    /// <summary>Counts one delivery outcome for <paramref name="type"/>.</summary>
    /// <param name="type">The stored event type.</param>
    /// <param name="outcome"><c>processed</c>, <c>retried</c> or <c>dead_lettered</c>.</param>
    public void RecordHandled(string type, string outcome) =>
        _handled.Add(1, new TagList { { "type", type }, { "outcome", outcome } });

    /// <summary>Counts one message that reached a terminal failure.</summary>
    public void RecordDeadLetter() => _deadLetters.Add(1);

    /// <inheritdoc />
    public void Dispose() => Meter.Dispose();
}
