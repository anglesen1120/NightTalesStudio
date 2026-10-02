using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NightTales.Infrastructure.Persistence.Flags;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Flags;

/// <summary>
/// The real <c>LISTEN feature_flags_changed</c> session (09 README BD13) against a real PostgreSQL 17:
/// it receives what <c>pg_notify</c> delivers on its one channel, its wait is the loop's pause, and a
/// session the server drops surfaces as an error — the one thing the invalidator's reconnect schedule
/// reacts to.
/// </summary>
/// <remarks>
/// The loop-level policy is unit-tested with hand-written connections
/// (<c>FeatureFlagsCacheInvalidatorTests</c>); these tests exercise the Npgsql path those fakes
/// deliberately hide.
/// </remarks>
public sealed class FeatureFlagsListenerTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Listener_reads_a_real_notify_and_pauses_on_the_heartbeat()
    {
        await using var listener = new PgFeatureFlagsListener(Database.ConnectionString);
        await listener.ConnectAsync(Ct);

        // Nothing was sent: the wait itself is the pause, and it returns with no payload.
        (await listener.WaitAsync(TimeSpan.FromMilliseconds(250), Ct)).ShouldBeNull();

        await Database.ExecuteAsync(
            $"SELECT pg_notify('{FeatureFlagsCache.ChangedChannel}', 'youtube_publish')",
            Ct);

        (await listener.WaitAsync(TimeSpan.FromSeconds(10), Ct)).ShouldBe("youtube_publish");
    }

    [Fact]
    public async Task Listener_does_not_receive_a_previous_sessions_unread_notice()
    {
        var connectionString = WithApplicationName("nts-qa-flags-reused-" + Guid.CreateVersion7().ToString("N"));
        await using (var previous = new NpgsqlConnection(connectionString))
        {
            await previous.OpenAsync(Ct);
            await using var command = previous.CreateCommand();
            command.CommandText = "LISTEN " + FeatureFlagsCache.ChangedChannel;
            await command.ExecuteNonQueryAsync(Ct);
            await Database.ExecuteAsync(
                $"SELECT pg_notify('{FeatureFlagsCache.ChangedChannel}', 'youtube_publish')", Ct);
        }

        await using var listener = new PgFeatureFlagsListener(connectionString);
        await listener.ConnectAsync(Ct);
        (await listener.WaitAsync(TimeSpan.FromMilliseconds(250), Ct)).ShouldBeNull(
            "a new listener must not inherit the previous session's unread notification");
    }

    /// <summary>A batch already in the read buffer is drained without waiting a heartbeat per payload.</summary>
    [Fact]
    public async Task Listener_drains_a_committed_burst()
    {
        await using var listener = new PgFeatureFlagsListener(Database.ConnectionString);
        await listener.ConnectAsync(Ct);

        await Database.ExecuteAsync(
            "SELECT pg_notify('feature_flags_changed', 'youtube_publish'), "
            + "pg_notify('feature_flags_changed', 'tiktok_publish'), "
            + "pg_notify('feature_flags_changed', 'source_finder')",
            Ct);

        var payloads = new List<string?>();
        for (var i = 0; i < 3; i++)
        {
            payloads.Add(await listener.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        }

        payloads.ShouldBe(["youtube_publish", "tiktok_publish", "source_finder"], ignoreOrder: true);
    }

    [Fact]
    public async Task Listener_surfaces_a_dropped_session_so_the_loop_reconnects()
    {
        var applicationName = "nts-qa-flags-" + Guid.CreateVersion7().ToString("N");

        await using var listener = new PgFeatureFlagsListener(WithApplicationName(applicationName));
        await listener.ConnectAsync(Ct);

        (await listener.WaitAsync(TimeSpan.FromMilliseconds(250), Ct)).ShouldBeNull();

        var terminated = await Database.QueryStringsAsync(
            "SELECT pg_terminate_backend(pid)::text FROM pg_stat_activity "
            + $"WHERE application_name = '{applicationName}' AND pid <> pg_backend_pid()",
            Ct);

        terminated.ShouldContain("true", "the test must actually have killed the LISTEN session");

        // The invalidator catches this and reconnects on the 1-2-4-8-16-30 s schedule; a wait that kept
        // returning null would leave the instance silently deaf instead.
        var thrown = await Record.ExceptionAsync(
            () => listener.WaitAsync(TimeSpan.FromSeconds(10), Ct).AsTask());

        thrown.ShouldNotBeNull("a dropped session must surface as an error");
        thrown.ShouldNotBeOfType<OperationCanceledException>();
    }

    /// <summary>
    /// One committed <c>NOTIFY feature_flags_changed</c> reaches every instance's own LISTEN session:
    /// two independent readers, each with its own cache and its own <see cref="FeatureFlagsCacheInvalidator"/>
    /// like two deployed hosts, both believe a flip at once rather than waiting out the 30 s window.
    /// </summary>
    [Fact]
    public async Task One_notify_evicts_every_instances_cache()
    {
        await Database.ExecuteAsync(
            "INSERT INTO feature_flags (key, enabled, rules, created_at, updated_at) "
            + "VALUES ('youtube_publish', false, '{}', now(), now()) "
            + "ON CONFLICT (key) DO UPDATE SET enabled = false, updated_at = now()",
            Ct);

        await using var instanceA = new FlaggedInstance(Database);
        await using var instanceB = new FlaggedInstance(Database);
        await instanceA.StartAsync(Ct);
        await instanceB.StartAsync(Ct);

        // Both instances have a cached answer the update must invalidate.
        (await instanceA.Flags.IsEnabledAsync("youtube_publish", Ct)).ShouldBeFalse();
        (await instanceB.Flags.IsEnabledAsync("youtube_publish", Ct)).ShouldBeFalse();

        // The writer's shape: the row and the notice commit together.
        await Database.ExecuteAsync(
            "UPDATE feature_flags SET enabled = true, updated_at = now() WHERE key = 'youtube_publish'; "
            + $"SELECT pg_notify('{FeatureFlagsCache.ChangedChannel}', 'youtube_publish')",
            Ct);

        await WaitUntilAsync(
            async () => await instanceA.Flags.IsEnabledAsync("youtube_publish", Ct),
            Ct);
        await WaitUntilAsync(
            async () => await instanceB.Flags.IsEnabledAsync("youtube_publish", Ct),
            Ct);
    }

    /// <summary>
    /// A LISTEN session the server drops is re-established on the invalidator's reconnect schedule, and
    /// the instance keeps evicting afterwards — the failure-table promise that a lost session is a pause,
    /// never a deaf instance.
    /// </summary>
    [Fact]
    public async Task A_dropped_listen_session_reconnects_and_still_evicts()
    {
        await Database.ExecuteAsync(
            "INSERT INTO feature_flags (key, enabled, rules, created_at, updated_at) "
            + "VALUES ('youtube_publish', false, '{}', now(), now()) "
            + "ON CONFLICT (key) DO UPDATE SET enabled = false, updated_at = now()",
            Ct);

        var applicationName = "nts-qa-flags-inv-" + Guid.CreateVersion7().ToString("N");
        var builder = new NpgsqlConnectionStringBuilder(Database.ConnectionString)
        {
            ApplicationName = applicationName,
        };

        await using var instance = new FlaggedInstance(Database, builder.ConnectionString);
        await instance.StartAsync(Ct);
        (await instance.Flags.IsEnabledAsync("youtube_publish", Ct)).ShouldBeFalse();

        // Kill the LISTEN backend; the invalidator's first reconnect lands a second later (1, 2, 4, …).
        var terminated = await Database.QueryStringsAsync(
            "SELECT pg_terminate_backend(pid)::text FROM pg_stat_activity "
            + $"WHERE application_name = '{applicationName}' AND pid <> pg_backend_pid()",
            Ct);
        terminated.ShouldContain("true", "the test must actually have killed the LISTEN session");

        // Wait for the replacement session before notifying: a notice sent to a deaf moment is legitimately
        // lost (the 30 s TTL covers it), so the assertion is about the reconnected session.
        await WaitUntilAsync(
            async () => (await Database.QueryStringsAsync(
                    "SELECT 1::text FROM pg_stat_activity "
                    + $"WHERE application_name = '{applicationName}' AND pid <> pg_backend_pid()",
                    Ct)).Count > 0,
            Ct);

        await Database.ExecuteAsync(
            $"SELECT pg_notify('{FeatureFlagsCache.ChangedChannel}', 'youtube_publish')",
            Ct);

        // The row itself is still false: the notice alone must evict, proving the reconnected session
        // delivered it — the test asserts on the Remove, not on a re-read.
        await WaitUntilAsync(() => instance.Cache.RemovedCount > 0, Ct);
    }

    /// <summary>Polls 10 ms apart until the condition holds or the run's token bounds the wait.</summary>
    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken ct)
    {
        while (!await condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
        }
    }

    /// <summary>Polls 10 ms apart until the condition holds or the run's token bounds the wait.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
        }
    }

    /// <summary>
    /// One test host: its own memory cache, its own reader over a real context, and its own real
    /// <c>LISTEN</c> invalidator — the shape of one deployed Api or Worker instance.
    /// </summary>
    /// <param name="database">The class database the reader context and the LISTEN session both reach.</param>
    private sealed class FlaggedInstance : IAsyncDisposable
    {
        private readonly NightTales.Infrastructure.Persistence.AppDbContext _context;
        private readonly MemoryCache _cache = new(new MemoryCacheOptions());
        private readonly ObservingCache _observing;
        private readonly FeatureFlagsCacheInvalidator _invalidator;

        public FlaggedInstance(TestDatabase database, string? connectionString = null)
        {
            _context = database.CreateAppDbContext();
            _observing = new ObservingCache(_cache);
            Flags = new CachedFeatureFlags(
                new FeatureFlagReader(_context),
                _observing,
                new FakeClock(),
                Options.Create(new FeatureFlagsOptions()),
                NullLogger<CachedFeatureFlags>.Instance);
            _invalidator = new FeatureFlagsCacheInvalidator(
                new Factory(connectionString ?? database.ConnectionString),
                _observing,
                Options.Create(new FeatureFlagsOptions { HeartbeatSeconds = 1 }),
                new SystemFeatureFlagsListenerDelay(),
                NullLogger<FeatureFlagsCacheInvalidator>.Instance);
        }

        /// <summary>The instance's cached read service.</summary>
        public CachedFeatureFlags Flags { get; }

        /// <summary>The cache the invalidator evicts from, with the removal count observable.</summary>
        public ObservingCache Cache => _observing;

        /// <summary>Starts the instance's invalidator and waits for its live LISTEN session.</summary>
        public async Task StartAsync(CancellationToken ct)
        {
            await _invalidator.StartAsync(ct);
            await _invalidator.ListeningAsync(ct);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await _invalidator.StopAsync(CancellationToken.None);
            _invalidator.Dispose();
            await _context.DisposeAsync();
            _cache.Dispose();
        }

        /// <summary>The session factory the invalidator creates its connection through.</summary>
        private sealed class Factory(string connectionString) : IFeatureFlagsListenerConnectionFactory
        {
            /// <inheritdoc />
            public IFeatureFlagsListenerConnection Create() => new PgFeatureFlagsListener(connectionString);
        }

        /// <summary>A real MemoryCache whose removals are countable.</summary>
        public sealed class ObservingCache(MemoryCache inner) : IMemoryCache
        {
            /// <summary>How many keys were evicted.</summary>
            public int RemovedCount { get; private set; }

            /// <inheritdoc />
            public ICacheEntry CreateEntry(object key) => inner.CreateEntry(key);

            /// <inheritdoc />
            public void Dispose()
            {
            }

            /// <inheritdoc />
            public void Remove(object key)
            {
                RemovedCount++;
                inner.Remove(key);
            }

            /// <inheritdoc />
            public bool TryGetValue(object key, out object? value) => inner.TryGetValue(key, out value);
        }
    }

    /// <summary>The same connection string carries an <c>ApplicationName</c>, for the drop test above.</summary>
    private string WithApplicationName(string applicationName)
    {
        var builder = new NpgsqlConnectionStringBuilder(Database.ConnectionString)
        {
            ApplicationName = applicationName,
        };

        return builder.ConnectionString;
    }

}
