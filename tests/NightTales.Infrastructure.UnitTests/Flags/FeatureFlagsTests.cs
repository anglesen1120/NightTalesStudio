using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Flags;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Domain.Common;
using NightTales.Domain.Platform.Events;
using NightTales.Infrastructure.Persistence.Flags;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Flags;

/// <summary>
/// The read service of 09.9.2-T2 (09 README BD13): the 30 s window is measured against the injected
/// <see cref="IClock"/> — never against wall time — so every window assertion here moves a
/// <see cref="FakeClock"/> and the whole class runs in microseconds.
/// </summary>
/// <remarks>
/// The reader is a hand-written stub, which is what makes "one read per window" observable as a call
/// count; the cache is a real <see cref="MemoryCache"/> behind a recording wrapper, so the eviction
/// assertions see the production <c>Remove</c>/<c>Set</c> path while the test keeps its own entry
/// lifetime independent of the real clock (the window check is the <c>LoadedAt</c> comparison, and the
/// cache's own absolute expiry only bounds memory).
/// </remarks>
public sealed class FeatureFlagsTests
{
    private const string Key = "youtube_publish";

    /// <summary>The flag of the seed catalog reads as enabled.</summary>
    [Fact]
    public async Task IsEnabled_returns_seeded_value()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new StubReader(() => true);
        using var cache = new RecordingCache();
        var flags = Build(reader, cache);

        (await flags.IsEnabledAsync(Key, ct)).ShouldBeTrue();
        reader.Calls.ShouldBe(1);
    }

    /// <summary>A disabled row is a value, not an absence: it is answered and cached like any other.</summary>
    [Fact]
    public async Task IsEnabled_returns_false_for_a_disabled_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new StubReader(() => false);
        using var cache = new RecordingCache();
        var flags = Build(reader, cache);

        (await flags.IsEnabledAsync(Key, ct)).ShouldBeFalse();
        (await flags.IsEnabledAsync(Key, ct)).ShouldBeFalse();
        reader.Calls.ShouldBe(1, "a disabled row is cached for the window too");
    }

    /// <summary>A key no row has answers false — the fail-closed answer a typo in a route must get.</summary>
    [Fact]
    public async Task Unknown_key_returns_false()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new StubReader(() => null);
        using var cache = new RecordingCache();
        var flags = Build(reader, cache);

        (await flags.IsEnabledAsync("youtube_publish_typo", ct)).ShouldBeFalse();
        reader.Calls.ShouldBe(1);
    }

    /// <summary>
    /// An invalid key is refused before any lookup, so a route guard naming a malformed key costs nothing
    /// and never probes the table.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("Youtube")]
    [InlineData("1youtube")]
    [InlineData("youtube-publish")]
    [InlineData("youtube publish")]
    [InlineData("a")]
    public async Task Invalid_key_returns_false_without_a_lookup(string invalid)
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new StubReader(() => true);
        using var cache = new RecordingCache();
        var flags = Build(reader, cache);

        (await flags.IsEnabledAsync(invalid, ct)).ShouldBeFalse();
        reader.Calls.ShouldBe(0);
    }

    /// <summary>A 64-character key is the longest accepted one.</summary>
    [Fact]
    public async Task Longest_valid_key_is_accepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new StubReader(() => true);
        using var cache = new RecordingCache();
        var flags = Build(reader, cache);

        (await flags.IsEnabledAsync(new string('k', FeatureFlagKey.MaxLength), ct)).ShouldBeTrue();
        reader.Calls.ShouldBe(1);
    }

    /// <summary>Two reads inside the window cost one database read.</summary>
    [Fact]
    public async Task Second_call_within_30s_uses_cache()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new StubReader(() => true);
        using var cache = new RecordingCache();
        var clock = new FakeClock();
        var flags = Build(reader, cache, clock);

        await flags.IsEnabledAsync(Key, ct);
        clock.Advance(TimeSpan.FromSeconds(29));

        (await flags.IsEnabledAsync(Key, ct)).ShouldBeTrue();
        reader.Calls.ShouldBe(1);
    }

    /// <summary>Once the window is over the row is read again — the hard bound on staleness.</summary>
    [Fact]
    public async Task After_31s_rereads_from_database()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new StubReader(() => true, () => true);
        using var cache = new RecordingCache();
        var clock = new FakeClock();
        var flags = Build(reader, cache, clock);

        await flags.IsEnabledAsync(Key, ct);
        clock.Advance(TimeSpan.FromSeconds(31));

        (await flags.IsEnabledAsync(Key, ct)).ShouldBeTrue();
        reader.Calls.ShouldBe(2);
    }

    /// <summary>The window is the configured one, not a constant compiled into the cache.</summary>
    [Fact]
    public async Task Window_follows_the_configured_seconds()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new StubReader(() => true, () => true);
        using var cache = new RecordingCache();
        var clock = new FakeClock();
        var flags = Build(reader, cache, clock, cacheSeconds: 5);

        await flags.IsEnabledAsync(Key, ct);

        clock.Advance(TimeSpan.FromSeconds(4));
        await flags.IsEnabledAsync(Key, ct);
        reader.Calls.ShouldBe(1, "four seconds is inside a five-second window");

        clock.Advance(TimeSpan.FromSeconds(2));
        await flags.IsEnabledAsync(Key, ct);
        reader.Calls.ShouldBe(2, "six seconds is outside it");
    }

    /// <summary>
    /// The <c>feature_flags_changed.v1</c> handler evicts the key immediately, so the window never has to
    /// run out for a change to be believed.
    /// </summary>
    [Fact]
    public async Task FeatureFlagsChanged_evicts_cache()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new StubReader(() => false, () => true);
        using var cache = new RecordingCache();
        var flags = Build(reader, cache);
        var handler = new FeatureFlagsChangedHandler(cache);

        (await flags.IsEnabledAsync(Key, ct)).ShouldBeFalse();

        await handler.HandleAsync(
            new FeatureFlagsChanged(Key, true, FakeClock.DefaultStart),
            new OutboxContext(Guid.CreateVersion7(), UserId: null, Attempt: 1, FakeClock.DefaultStart),
            ct);

        cache.Removed.ShouldContain(FeatureFlagsCache.Key(Key));
        (await flags.IsEnabledAsync(Key, ct)).ShouldBeTrue("the evicted key is read again, not served cached");
        reader.Calls.ShouldBe(2);
    }

    /// <summary>A delivered event whose key is not a key evicts nothing (a payload guard, not a lookup).</summary>
    [Fact]
    public async Task FeatureFlagsChanged_with_an_invalid_key_evicts_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cache = new RecordingCache();
        var handler = new FeatureFlagsChangedHandler(cache);

        await handler.HandleAsync(
            new FeatureFlagsChanged("Not A Key", true, FakeClock.DefaultStart),
            new OutboxContext(Guid.CreateVersion7(), UserId: null, Attempt: 1, FakeClock.DefaultStart),
            ct);

        cache.Removed.ShouldBeEmpty();
    }

    /// <summary>A stopped database never reaches the caller as an exception.</summary>
    [Fact]
    public async Task Database_failure_returns_false_without_throwing()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new StubReader(() => throw new InvalidOperationException("the server is down"));
        using var cache = new RecordingCache();
        var flags = Build(reader, cache);

        (await flags.IsEnabledAsync(Key, ct)).ShouldBeFalse();
        reader.Calls.ShouldBe(1);
    }

    /// <summary>
    /// A failed read is not cached, so a database that recovers is believed on the very next call instead
    /// of serving a fabricated <c>false</c> for the rest of the window.
    /// </summary>
    [Fact]
    public async Task Database_failure_is_not_cached()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new StubReader(() => throw new InvalidOperationException("the server is down"), () => true);
        using var cache = new RecordingCache();
        var flags = Build(reader, cache);

        (await flags.IsEnabledAsync(Key, ct)).ShouldBeFalse();
        (await flags.IsEnabledAsync(Key, ct)).ShouldBeTrue();
        reader.Calls.ShouldBe(2);
    }

    /// <summary>A cancelled caller is not an error to swallow: the cancellation reaches the caller.</summary>
    [Fact]
    public async Task Cancellation_propagates()
    {
        var reader = new StubReader(() => throw new OperationCanceledException());
        using var cache = new RecordingCache();
        var flags = Build(reader, cache);

        await Should.ThrowAsync<OperationCanceledException>(
            () => flags.IsEnabledAsync(Key, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A cancelled read is neither a failure nor a value: it caches nothing, so the next caller gets a
    /// fresh database answer instead of a fabricated <see langword="false"/> for the rest of the window.
    /// </summary>
    [Fact]
    public async Task A_cancelled_read_caches_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new StubReader(
            () => throw new OperationCanceledException(),
            () => true);
        using var cache = new RecordingCache();
        var flags = Build(reader, cache);

        await Should.ThrowAsync<OperationCanceledException>(() => flags.IsEnabledAsync(Key, ct));

        (await flags.IsEnabledAsync(Key, ct)).ShouldBeTrue("the cancelled read left no entry behind");
        reader.Calls.ShouldBe(2);
    }

    /// <summary>
    /// A cached value that outlives its window is never resurrected: a database that fails after the
    /// expiry still answers <see langword="false"/>, not the stale <see langword="true"/> it can no
    /// longer vouch for (fail-closed holds at the boundary).
    /// </summary>
    [Fact]
    public async Task An_expired_entry_is_not_served_when_the_database_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new StubReader(
            () => true,
            () => throw new InvalidOperationException("the server is down"));
        using var cache = new RecordingCache();
        var clock = new FakeClock();
        var flags = Build(reader, cache, clock);

        (await flags.IsEnabledAsync(Key, ct)).ShouldBeTrue();

        clock.Advance(TimeSpan.FromSeconds(31));

        (await flags.IsEnabledAsync(Key, ct)).ShouldBeFalse("outside the window the row must be re-read");
        reader.Calls.ShouldBe(2);
    }

    /// <summary>
    /// A notification that lands while a read is still in flight evicts an entry that does not exist
    /// yet, so the completing read may write a stale value — the failure-table race the design accepts
    /// (last committed write wins, staleness bounded by the window). This asserts the bound holds: the
    /// stale answer lives at most one window, then the next read converges.
    /// </summary>
    [Fact]
    public async Task Eviction_during_an_in_flight_read_converges_within_the_window()
    {
        var ct = TestContext.Current.CancellationToken;
        var released = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new GatedReader(released.Task, after: true);
        using var cache = new RecordingCache();
        var clock = new FakeClock();
        var flags = Build(reader, cache, clock);

        // The read is in flight when the writer's notice arrives: the eviction removes nothing, and the
        // read completes by caching the value it observed — stale from that instant on.
        var inFlight = flags.IsEnabledAsync(Key, ct);
        cache.Remove(FeatureFlagsCache.Key(Key));

        released.SetResult(false);
        (await inFlight).ShouldBeFalse("the read answers with the row it read before the notice");
        (await flags.IsEnabledAsync(Key, ct)).ShouldBeFalse("inside the window the stale entry is served");

        clock.Advance(TimeSpan.FromSeconds(31));

        (await flags.IsEnabledAsync(Key, ct)).ShouldBeTrue("the window is the hard bound on the stale answer");
        reader.Calls.ShouldBe(2);
    }

    /// <summary>One warning per key per minute, so a loop over a typo cannot flood the log.</summary>
    [Fact]
    public async Task Repeated_warnings_for_one_key_are_rate_limited()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new StubReader(() => null, () => null);
        using var cache = new RecordingCache();
        var logger = new RecordingLogger();
        var clock = new FakeClock();
        var flags = Build(reader, cache, clock, logger: logger);

        await flags.IsEnabledAsync(Key, ct);
        clock.Advance(TimeSpan.FromSeconds(31));
        await flags.IsEnabledAsync(Key, ct);

        // The marker's own window is one real minute of wall time, so a second read 31 fake-seconds later
        // is still inside it: the rate limit is not tied to the flag window.
        logger.Lines.Count.ShouldBe(1, "the second read inside the warning window must not log again");
        logger.Lines[0].ShouldContain(Key);
    }

    private static CachedFeatureFlags Build(
        IFeatureFlagReader reader,
        IMemoryCache cache,
        FakeClock? clock = null,
        int cacheSeconds = 30,
        RecordingLogger? logger = null) =>
        new(
            reader,
            cache,
            clock ?? new FakeClock(),
            Options.Create(new FeatureFlagsOptions { CacheSeconds = cacheSeconds }),
            logger ?? new RecordingLogger());
}

/// <summary>One flag row read, with the call count the window assertions are about.</summary>
internal sealed class StubReader(params Func<bool?>[] answers) : IFeatureFlagReader
{
    private readonly Queue<Func<bool?>> _answers = new(answers);

    /// <summary>How many times the read service went to the database.</summary>
    public int Calls { get; private set; }

    /// <inheritdoc />
    public Task<bool?> GetAsync(string key, CancellationToken ct)
    {
        Calls++;

        // An exhausted script answers "no row", which keeps a test that reads more often than it planned
        // failing on the call count instead of on a null reference.
        return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue()() : null);
    }
}

/// <summary>A reader whose first call blocks on a gate and whose later calls answer one fixed value.</summary>
/// <param name="first">The task the first read awaits; the test releases it when it wants the read to complete.</param>
/// <param name="after">The answer of every later read.</param>
internal sealed class GatedReader(Task<bool?> first, bool? after) : IFeatureFlagReader
{
    /// <summary>How many times the read service went to the database.</summary>
    public int Calls { get; private set; }

    /// <inheritdoc />
    public Task<bool?> GetAsync(string key, CancellationToken ct)
    {
        Calls++;

        return Calls == 1 ? first : Task.FromResult(after);
    }
}

/// <summary>
/// A real <see cref="MemoryCache"/> that records every eviction, so an assertion can see the production
/// <c>Remove</c> instead of inferring it from a second read.
/// </summary>
internal sealed class RecordingCache : IMemoryCache
{
    private readonly MemoryCache _inner = new(new MemoryCacheOptions());

    /// <summary>The cache keys <see cref="Remove"/> was called with, in order.</summary>
    public List<object> Removed { get; } = [];

    /// <summary>Called with each key <see cref="Remove"/> evicts — the barrier a loop test awaits.</summary>
    public Action<object>? OnRemove { get; set; }

    /// <inheritdoc />
    public ICacheEntry CreateEntry(object key) => _inner.CreateEntry(key);

    /// <inheritdoc />
    public void Dispose() => _inner.Dispose();

    /// <inheritdoc />
    public void Remove(object key)
    {
        Removed.Add(key);
        OnRemove?.Invoke(key);
        _inner.Remove(key);
    }

    /// <inheritdoc />
    public bool TryGetValue(object key, out object? value) => _inner.TryGetValue(key, out value);
}

/// <summary>Keeps every rendered line of one logger, so a warning's rate limit is assertable.</summary>
internal sealed class RecordingLogger : ILogger<CachedFeatureFlags>
{
    /// <summary>Every line, in order.</summary>
    public List<string> Lines { get; } = [];

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
}
