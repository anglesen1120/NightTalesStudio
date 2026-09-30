using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit.Database;

namespace NightTales.TestKit.Outbox;

/// <summary>
/// The probe handlers of the outbox integration tests (09.2.4-T1). Each one records into
/// <see cref="OutboxProbeState"/> and does nothing else, except where a test needs a specific behaviour:
/// the crashing, failing and tenant handlers.
/// </summary>
/// <remarks>
/// They are registered per test with
/// <see cref="OutboxServiceCollectionExtensions.AddOutboxHandler{TEvent, THandler}"/>, so the dispatcher
/// builds them in the delivery's own scope exactly as it builds a production handler.
/// </remarks>
public sealed class OutboxFirstProbeHandler(OutboxProbeState state) : IOutboxHandler<OutboxProbeEvent>
{
    /// <inheritdoc />
    public ValueTask HandleAsync(OutboxProbeEvent domainEvent, OutboxContext context, CancellationToken cancellationToken)
    {
        state.Record("first", context);

        return ValueTask.CompletedTask;
    }
}

/// <summary>The second handler of one event type: registration order is what the test asserts.</summary>
public sealed class OutboxSecondProbeHandler(OutboxProbeState state) : IOutboxHandler<OutboxProbeEvent>
{
    /// <inheritdoc />
    public ValueTask HandleAsync(OutboxProbeEvent domainEvent, OutboxContext context, CancellationToken cancellationToken)
    {
        state.Record("second", context);

        return ValueTask.CompletedTask;
    }
}

/// <summary>Counts deliveries per message — the two-dispatcher test's whole assertion.</summary>
public sealed class OutboxCountingProbeHandler(OutboxProbeState state) : IOutboxHandler<OutboxProbeEvent>
{
    /// <inheritdoc />
    public ValueTask HandleAsync(OutboxProbeEvent domainEvent, OutboxContext context, CancellationToken cancellationToken)
    {
        state.Record("counting", context);

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Always throws: it drives the backoff and the dead-letter tests. The invocation is recorded first, so the
/// test can also assert how many times the message was delivered.
/// </summary>
public sealed class OutboxFailingProbeHandler(OutboxProbeState state) : IOutboxHandler<OutboxProbeEvent>
{
    /// <inheritdoc />
    public ValueTask HandleAsync(OutboxProbeEvent domainEvent, OutboxContext context, CancellationToken cancellationToken)
    {
        state.Record("failing", context);

        throw new OutboxProbeFailureException($"outbox probe handler failure on attempt {context.Attempt}");
    }
}

/// <summary>
/// The "crash after the effect, before the row was marked processed" probe (09.2.4-T1): on the first
/// delivery it commits an idempotent effect outside the dispatcher's own work and then throws; from the
/// second delivery on it records the same effect and returns.
/// </summary>
/// <remarks>
/// The effect is committed through <see cref="TestDatabase"/> on purpose — a real handler writes through
/// its own command, which commits before the dispatcher ever sees the row again. Repeating it is harmless
/// because the row is keyed by the message (the handler's own idempotency contract, 09 README D11/O4).
/// </remarks>
public sealed class OutboxCrashProbeHandler(OutboxProbeState state, TestDatabase database)
    : IOutboxHandler<OutboxProbeEvent>
{
    /// <summary>The probe table one delivered message may write exactly one row into.</summary>
    public const string EffectTable = "outbox_probe_effects";

    /// <inheritdoc />
    public async ValueTask HandleAsync(
        OutboxProbeEvent domainEvent,
        OutboxContext context,
        CancellationToken cancellationToken)
    {
        state.Record("crash", context);

        await database
            .ExecuteAsync(
                $"INSERT INTO {EffectTable} (message_id) VALUES ('{context.MessageId}') ON CONFLICT (message_id) DO NOTHING",
                cancellationToken)
            .ConfigureAwait(false);

        if (context.Attempt == 1)
        {
            throw new OutboxProbeFailureException("outbox probe crashed after its effect (09.2.4-T1)");
        }
    }
}

/// <summary>
/// Reads another user's <c>series</c> row through the delivery's own <see cref="AppDbContext"/>: the scope
/// was pinned to the message's user before this handler was built, so the tenant filter decides whether the
/// row is visible (09 README D10, 09.2.4 plan §8).
/// </summary>
public sealed class OutboxTenantProbeHandler(OutboxProbeState state, AppDbContext database)
    : IOutboxHandler<OutboxProbeEvent>
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(
        OutboxProbeEvent domainEvent,
        OutboxContext context,
        CancellationToken cancellationToken)
    {
        var foreign = await database.Series
            .SingleOrDefaultAsync(series => series.Id == state.ForeignSeriesId, cancellationToken)
            .ConfigureAwait(false);

        state.ForeignSeriesResult = foreign?.Id;
        state.Record("tenant", context);
    }
}

/// <summary>Records the ambient activity, so the test can prove the delivery continued the command's trace.</summary>
public sealed class OutboxTraceProbeHandler(OutboxProbeState state) : IOutboxHandler<OutboxProbeEvent>
{
    /// <inheritdoc />
    public ValueTask HandleAsync(OutboxProbeEvent domainEvent, OutboxContext context, CancellationToken cancellationToken)
    {
        state.Record("trace", context, Activity.Current);

        return ValueTask.CompletedTask;
    }
}

/// <summary>Handles a platform event: the delivery has no user, so it must not be scoped to one.</summary>
public sealed class OutboxPlatformProbeHandler(OutboxProbeState state) : IOutboxHandler<OutboxProbePlatformEvent>
{
    /// <inheritdoc />
    public ValueTask HandleAsync(
        OutboxProbePlatformEvent domainEvent,
        OutboxContext context,
        CancellationToken cancellationToken)
    {
        state.Record("platform", context);

        return ValueTask.CompletedTask;
    }
}
