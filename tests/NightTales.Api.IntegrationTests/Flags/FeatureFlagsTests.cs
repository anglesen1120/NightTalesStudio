using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Flags;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Flags;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Flags;

/// <summary>
/// The feature-flag read service against a real PostgreSQL 17 (09 README BD13, 09.9.2-T2 acceptance):
/// the seeded value, the one-statement-per-window read rate, and the fail-closed answers a typo or a
/// stopped database produce.
/// </summary>
/// <remarks>
/// The reader runs over a real <see cref="AppDbContext"/> built for this class's database with
/// <c>CountingDbCommandInterceptor</c> attached, so "one SQL per key per 30 s" is a count of executed
/// commands rather than an inference, and the window is driven by a <see cref="FakeClock"/> instead of a
/// wait.
/// </remarks>
public sealed class FeatureFlagsTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Key = "youtube_publish";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The row is visible to an anonymous actor, because <c>feature_flags</c> is platform data and not
    /// <c>IUserOwned</c>: no tenant filter applies, so no <c>IgnoreQueryFilters</c> is needed anywhere.
    /// </summary>
    [Fact]
    public async Task IsEnabled_returns_seeded_value_without_escaping_the_tenant_filter()
    {
        await InsertFlagAsync(Key, enabled: true);

        var harness = CreateHarness(anonymous: true);

        (await harness.Flags.IsEnabledAsync(Key, Ct)).ShouldBeTrue();

        // A key no row has answers false, and a seeded-but-disabled flag answers false too.
        await InsertFlagAsync("tiktok_publish", enabled: false);
        (await harness.Flags.IsEnabledAsync("tiktok_publish", Ct)).ShouldBeFalse();
        (await harness.Flags.IsEnabledAsync("source_finder", Ct)).ShouldBeFalse();
    }

    /// <summary>The window is the 30 s of BD13: two reads inside it cost exactly one statement.</summary>
    [Fact]
    public async Task One_statement_per_key_per_30s()
    {
        await InsertFlagAsync(Key, enabled: true);
        var harness = CreateHarness();

        (await harness.Flags.IsEnabledAsync(Key, Ct)).ShouldBeTrue();
        harness.FeatureFlagStatements.ShouldBe(1);

        harness.Clock.Advance(TimeSpan.FromSeconds(29));
        (await harness.Flags.IsEnabledAsync(Key, Ct)).ShouldBeTrue();
        harness.FeatureFlagStatements.ShouldBe(1, "29 s is inside the window");

        harness.Clock.Advance(TimeSpan.FromSeconds(2));
        (await harness.Flags.IsEnabledAsync(Key, Ct)).ShouldBeTrue();
        harness.FeatureFlagStatements.ShouldBe(2, "31 s is outside it");
    }

    /// <summary>An operator flip is believed as soon as the notice evicts, without waiting the window out.</summary>
    [Fact]
    public async Task Evicted_key_is_read_again()
    {
        await InsertFlagAsync(Key, enabled: false);
        var harness = CreateHarness();

        (await harness.Flags.IsEnabledAsync(Key, Ct)).ShouldBeFalse();

        await Database.ExecuteAsync(
            "UPDATE feature_flags SET enabled = true WHERE key = 'youtube_publish'",
            Ct);

        // The window has not run out, so the cache would still answer false.
        (await harness.Flags.IsEnabledAsync(Key, Ct)).ShouldBeFalse();

        harness.Cache.Remove(FeatureFlagsCache.Key(Key));

        (await harness.Flags.IsEnabledAsync(Key, Ct)).ShouldBeTrue("the evicted key is read again");
        harness.FeatureFlagStatements.ShouldBe(2);
    }

    /// <summary>A stopped database fails closed: false, no exception, and the guarded route stays a 404.</summary>
    [Fact]
    public async Task Database_unreachable_answers_false_without_throwing()
    {
        const string unreachable =
            "Host=127.0.0.1;Port=1;Database=nts_flags_unreachable;Username=nts;Password=nts;Timeout=1";

        await using var context = TestDbContextFactory.CreateAppContext(unreachable);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var flags = new CachedFeatureFlags(
            new FeatureFlagReader(context),
            cache,
            new FakeClock(),
            Options.Create(new FeatureFlagsOptions()),
            NullLogger<CachedFeatureFlags>.Instance);

        (await flags.IsEnabledAsync(Key, Ct)).ShouldBeFalse();
    }

    /// <summary>
    /// A seeded <c>rules</c> document — well-formed or merely valid JSON of the wrong shape — never
    /// changes the P answer: the reader projects <c>enabled</c> only (RULED — README §B10 WT13), and the
    /// column never even reaches the wire.
    /// </summary>
    [Fact]
    public async Task Seeded_rules_never_alter_the_P_answer_and_are_not_selected()
    {
        // A rules document that would say "off" if anything read it, and one that would say "on": the
        // row's enabled column alone decides. Both are malformed *as rules* for P's purposes — arbitrary
        // keys, no schema — and legal jsonb.
        await InsertFlagAsync(Key, enabled: true, rules: """{"enabled": false, "threshold": -1}""");
        await InsertFlagAsync("source_finder", enabled: false, rules: """{"enabled": true, "weights": [9, "x"]}""");
        var harness = CreateHarness();

        (await harness.Flags.IsEnabledAsync(Key, Ct)).ShouldBeTrue();
        (await harness.Flags.IsEnabledAsync("source_finder", Ct)).ShouldBeFalse();

        harness.Counting.Texts
            .Where(text => text.Contains("feature_flags", StringComparison.Ordinal))
            .ShouldAllBe(
                text => !text.Contains("rules", StringComparison.Ordinal),
                "the P reader never selects the rules column");
    }

    /// <summary>
    /// Inserts one flag row the way <c>09.2.5-T1</c> does (a key, its state, the empty rules document), so
    /// the read service is exercised against the real column contract.
    private Task InsertFlagAsync(string key, bool enabled, string rules = "{}") =>
        Database.ExecuteAsync(
            "INSERT INTO feature_flags (key, enabled, rules, created_at, updated_at) "
            + $"VALUES ('{key}', {(enabled ? "true" : "false")}, '{rules}'::jsonb, now(), now()) "
            + "ON CONFLICT (key) DO UPDATE SET enabled = EXCLUDED.enabled, "
            + $"rules = '{rules}'::jsonb, updated_at = now()",
            Ct);

    /// <summary>The read service over this class's database, with its new statement count observable.</summary>
    private ReadHarness CreateHarness(bool anonymous = false)
    {
        var counting = new CountingDbCommandInterceptor();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Database.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(counting)
            .Options;

        var currentUser = new TestCurrentUser();
        if (!anonymous)
        {
            currentUser.Id = Guid.CreateVersion7();
        }

        var context = new AppDbContext(options, currentUser);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var clock = new FakeClock();

        var flags = new CachedFeatureFlags(
            new FeatureFlagReader(context),
            cache,
            clock,
            Options.Create(new FeatureFlagsOptions()),
            NullLogger<CachedFeatureFlags>.Instance);

        return new ReadHarness(context, cache, clock, counting, flags);
    }

    /// <summary>One read service and the interceptor that counts what it asked the database.</summary>
    private sealed record ReadHarness(
        AppDbContext Context,
        IMemoryCache Cache,
        FakeClock Clock,
        CountingDbCommandInterceptor Counting,
        IFeatureFlags Flags) : IAsyncDisposable
    {
        /// <summary>How many statements named <c>feature_flags</c> this harness executed.</summary>
        public int FeatureFlagStatements =>
            Counting.Texts.Count(text => text.Contains("feature_flags", StringComparison.Ordinal));

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }
}
