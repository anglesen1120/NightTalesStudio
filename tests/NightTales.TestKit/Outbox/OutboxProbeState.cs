using System.Collections.Concurrent;
using System.Diagnostics;
using NightTales.Application.Abstractions.Outbox;

namespace NightTales.TestKit.Outbox;

/// <summary>
/// One recorded handler invocation (09.2.4-T1): which probe handler ran, for which message, as which
/// user, on which attempt, and inside which activity.
/// </summary>
/// <param name="Handler">The probe handler's own name (<c>first</c>, <c>crash</c>, …).</param>
/// <param name="MessageId">The delivered message.</param>
/// <param name="UserId">The user the delivery ran as; <c>null</c> for a platform event.</param>
/// <param name="Attempt">The 1-based delivery attempt.</param>
/// <param name="ActivityName">The name of the ambient activity, when the handler recorded one.</param>
/// <param name="ActivityParentId">The ambient activity's parent — the stored <c>trace_parent</c>.</param>
public sealed record OutboxProbeCall(
    string Handler,
    Guid MessageId,
    Guid? UserId,
    int Attempt,
    string? ActivityName,
    string? ActivityParentId);

/// <summary>
/// The shared recorder of the outbox probe handlers (09.2.4-T1): the tests assert on the calls it saw and
/// on the per-message delivery count, which is how "at least once, but the effect exactly once" becomes a
/// number.
/// </summary>
/// <remarks>
/// Registered as a singleton by the test (<c>WorkerHost.ConfigureServices</c>) and injected into the probe
/// handlers, so the counts survive the per-delivery scopes the dispatcher creates. Every member is safe
/// for the two-dispatcher test, where several deliveries run at once.
/// </remarks>
public sealed class OutboxProbeState
{
    private readonly ConcurrentQueue<OutboxProbeCall> _calls = new();
    private readonly ConcurrentDictionary<Guid, int> _deliveries = new();

    /// <summary>Every recorded call, in completion order.</summary>
    public IReadOnlyList<OutboxProbeCall> Calls => [.. _calls];

    /// <summary>The recorded calls of one probe handler, in completion order.</summary>
    /// <param name="handler">The handler name to filter by.</param>
    public IReadOnlyList<OutboxProbeCall> CallsOf(string handler) =>
        [.. _calls.Where(call => string.Equals(call.Handler, handler, StringComparison.Ordinal))];

    /// <summary>How many times <paramref name="messageId"/> was delivered to a recording handler.</summary>
    /// <param name="messageId">The message to count.</param>
    public int Deliveries(Guid messageId) => _deliveries.TryGetValue(messageId, out var count) ? count : 0;

    /// <summary>The distinct messages a recording handler saw.</summary>
    public IReadOnlyList<Guid> MessageIds => [.. _deliveries.Keys];

    /// <summary>A <c>series</c> row of another user, which the tenant probe handler looks up.</summary>
    public Guid ForeignSeriesId { get; set; }

    /// <summary>The tenant probe handler's result: the found id, or <c>null</c> when it saw no row.</summary>
    public Guid? ForeignSeriesResult { get; set; }

    /// <summary>Records one invocation, with the ambient activity when the handler reports it.</summary>
    /// <param name="handler">The probe handler's own name.</param>
    /// <param name="context">The delivery the dispatcher handed over.</param>
    /// <param name="activity">The ambient activity, when the handler is the trace probe.</param>
    public void Record(string handler, OutboxContext context, Activity? activity = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        _calls.Enqueue(new OutboxProbeCall(
            handler,
            context.MessageId,
            context.UserId,
            context.Attempt,
            activity?.DisplayName,
            activity?.ParentId));

        _deliveries.AddOrUpdate(context.MessageId, 1, (_, count) => count + 1);
    }
}
