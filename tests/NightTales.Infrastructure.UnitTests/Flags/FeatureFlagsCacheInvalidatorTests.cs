using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Flags;
using NightTales.Infrastructure.Persistence.Flags;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Flags;

/// <summary>
/// The <c>LISTEN feature_flags_changed</c> invalidator of 09.9.2-T2 (09 README BD13): a notification
/// evicts the key on the instance that receives it, and a session that drops reconnects on the BD11
/// schedule — all proven with hand-written connections and a hand-written delay, so nothing waits and no
/// server is needed.
/// </summary>
/// <remarks>
/// The real Npgsql session is <c>PgFeatureFlagsListener</c>'s, exercised against PostgreSQL 17 by
/// <c>FeatureFlagsListenerTests</c> in the Api integration project; these tests are about the loop's
/// policy.
/// </remarks>
public sealed class FeatureFlagsCacheInvalidatorTests
{
    private const string Key = "youtube_publish";

    /// <summary>A delivered notification drops the key at once, well inside the 30 s window.</summary>
    [Fact]
    public async Task Notification_evicts_the_key_immediately()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new StubReader(() => true, () => true);
        using var cache = new RecordingCache();
        var evicted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.OnRemove = _ => evicted.TrySetResult();

        using var invalidator = Build(
            new ScriptedFactory(() => new ScriptedConnection([() => Key])),
            cache);

        await invalidator.StartAsync(ct);
        await invalidator.ListeningAsync(ct);
        await evicted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        cache.Removed.ShouldBe([FeatureFlagsCache.Key(Key)]);

        // And the eviction is real, not a bookkeeping line: the next read goes back to the database.
        var flags = new CachedFeatureFlags(
            reader,
            cache,
            new FakeClock(),
            Options.Create(new FeatureFlagsOptions()),
            NullLogger<CachedFeatureFlags>.Instance);

        (await flags.IsEnabledAsync(Key, ct)).ShouldBeTrue();
        reader.Calls.ShouldBe(1);

        await invalidator.StopAsync(ct);
    }

    /// <summary>A payload that is not a flag key is dropped; only the valid key in the same session evicts.</summary>
    [Fact]
    public async Task Payload_that_is_not_a_key_evicts_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cache = new RecordingCache();
        var evicted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.OnRemove = _ => evicted.TrySetResult();

        using var invalidator = Build(
            new ScriptedFactory(() => new ScriptedConnection([() => "not a key", () => Key])),
            cache);

        await invalidator.StartAsync(ct);
        await invalidator.ListeningAsync(ct);
        await evicted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        cache.Removed.ShouldBe([FeatureFlagsCache.Key(Key)]);

        await invalidator.StopAsync(ct);
    }

    /// <summary>
    /// A session that drops reconnects on the 1, 2, 4, 8, 16, 30 s schedule, and only a wait that has
    /// returned resets it — so a session that never becomes healthy ramps instead of retrying every second.
    /// </summary>
    [Fact]
    public async Task Dropped_session_reconnects_on_the_1_2_4_8_16_30_schedule()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cache = new RecordingCache();
        var delay = new RecordingDelay();
        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Six sessions that never connect, then one that does: the ramp is what the delay records.
        var factory = new ScriptedFactory(
        [
            FailingConnection,
            FailingConnection,
            FailingConnection,
            FailingConnection,
            FailingConnection,
            FailingConnection,
            () =>
            {
                listening.TrySetResult();

                return new ScriptedConnection([]);
            },
        ]);

        using var invalidator = Build(factory, cache, delay);

        await invalidator.StartAsync(ct);
        await listening.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        delay.Requested.ShouldBe(
        [
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(16),
            TimeSpan.FromSeconds(30),
        ]);

        await invalidator.StopAsync(ct);
    }

    /// <summary>
    /// A wait that returned proves the session is live, so the schedule restarts from its first entry: an
    /// outage after a healthy session retries after one second, not where the previous ramp had reached.
    /// </summary>
    [Fact]
    public async Task A_returned_wait_restarts_the_reconnect_schedule()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cache = new RecordingCache();
        var delay = new RecordingDelay();
        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var factory = new ScriptedFactory(
        [
            // 1: cannot connect — the ramp reaches 1 s.
            FailingConnection,
            // 2: one heartbeat (a returned wait, which resets the counter) and then the session drops.
            () => new ScriptedConnection([() => null, () => throw new IOException("the session dropped")]),
            // 3: cannot connect again — 1 s, because the heartbeat reset the ramp.
            FailingConnection,
            // 4 onwards: a healthy session to stop the ramp.
            () =>
            {
                listening.TrySetResult();

                return new ScriptedConnection([]);
            },
        ]);

        using var invalidator = Build(factory, cache, delay);

        await invalidator.StartAsync(ct);
        await listening.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // The heartbeat reset the ramp: the drop after it waits 1 s, where the ramp the first failure had
        // started would already be at 2 s. The third delay then continues from the first entry (2 s where
        // an unreset ramp would be at 4 s).
        delay.Requested.Count.ShouldBeGreaterThanOrEqualTo(3);
        delay.Requested[0].ShouldBe(TimeSpan.FromSeconds(1));
        delay.Requested[1].ShouldBe(TimeSpan.FromSeconds(1), "the returned wait restarted the schedule");
        delay.Requested[2].ShouldBe(TimeSpan.FromSeconds(2));

        await invalidator.StopAsync(ct);
    }

    /// <summary>The production factory names the missing key instead of failing at the first reconnect.</summary>
    [Fact]
    public void Listener_factory_refuses_to_build_without_a_connection_string()
    {
        var factory = new PgFeatureFlagsListenerFactory(new ConfigurationBuilder().Build());

        var thrown = Should.Throw<InvalidOperationException>(() => factory.Create());

        thrown.Message.ShouldContain("ConnectionStrings:Postgres");
    }

    private static FeatureFlagsCacheInvalidator Build(
        IFeatureFlagsListenerConnectionFactory connections,
        RecordingCache cache,
        RecordingDelay? delay = null) =>
        new(
            connections,
            cache,
            Options.Create(new FeatureFlagsOptions()),
            delay ?? new RecordingDelay(),
            NullLogger<FeatureFlagsCacheInvalidator>.Instance);

    /// <summary>A session that cannot be opened; its type is what the loop logs.</summary>
    private static FailingListenerConnection FailingConnection() =>
        new();

    /// <summary>Hands back one scripted wait result at a time, then blocks until the host stops.</summary>
    private sealed class ScriptedConnection(IReadOnlyList<Func<string?>> script) : IFeatureFlagsListenerConnection
    {
        private readonly Queue<Func<string?>> _script = new(script);

        /// <inheritdoc />
        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

        /// <inheritdoc />
        public async ValueTask<string?> WaitAsync(TimeSpan heartbeat, CancellationToken ct)
        {
            if (_script.Count > 0)
            {
                return _script.Dequeue()();
            }

            // The wait itself is the loop's pause: a session with nothing left to say stays in one wait
            // until the host stops, exactly as a real one would.
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);

            return null;
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A connection whose opening always fails, so the loop has to back off.</summary>
    private sealed class FailingListenerConnection : IFeatureFlagsListenerConnection
    {
        /// <inheritdoc />
        public Task ConnectAsync(CancellationToken ct) => throw new IOException("the server is unreachable");

        /// <inheritdoc />
        public ValueTask<string?> WaitAsync(TimeSpan heartbeat, CancellationToken ct) =>
            throw new InvalidOperationException("a session that never opened cannot be read.");

        /// <inheritdoc />
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Hands out the scripted sessions in order, then a healthy one that only waits.</summary>
    private sealed class ScriptedFactory : IFeatureFlagsListenerConnectionFactory
    {
        private readonly Queue<Func<IFeatureFlagsListenerConnection>> _connections;

        public ScriptedFactory(params Func<IFeatureFlagsListenerConnection>[] connections) =>
            _connections = new Queue<Func<IFeatureFlagsListenerConnection>>(connections);

        /// <inheritdoc />
        public IFeatureFlagsListenerConnection Create() =>
            _connections.Count > 0 ? _connections.Dequeue()() : new ScriptedConnection([]);
    }

    /// <summary>Records every requested pause and returns immediately, so the schedule is walked, not spent.</summary>
    private sealed class RecordingDelay : IFeatureFlagsListenerDelay
    {
        /// <summary>The pauses the loop asked for, in order.</summary>
        public List<TimeSpan> Requested { get; } = [];

        /// <inheritdoc />
        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            Requested.Add(delay);

            return Task.CompletedTask;
        }
    }
}
