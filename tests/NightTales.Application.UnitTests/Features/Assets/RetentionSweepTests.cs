using System.Diagnostics.Metrics;
using System.Linq.Expressions;
using System.Reflection;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Audit;
using NightTales.Application.Features.Assets.Jobs;
using NightTales.Application.Features.Platform.Retention;
using NightTales.Domain.Assets;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using NightTales.TestKit.Storage;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Assets;

/// <summary>
/// <c>RetentionSweepJob</c> (09.7.3-T1) against hand-written fakes: the asset phase's selection, idempotence,
/// failure memory and bounds, the per-row user scope, the advisory lock and the audit keyset loop.
/// </summary>
public sealed class RetentionSweepTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 3, 0, 0, TimeSpan.Zero);
    private static readonly StorageSettings Storage = new();

    private static string Bucket => Storage.Buckets.Media;

    [Fact]
    public async Task RetentionSweep_DeletesOnlyDueAndLiveAssets()
    {
        var due = NewAsset(Now.AddDays(-1));
        var future = NewAsset(Now.AddDays(1));
        var softDeleted = NewAsset(Now.AddDays(-1), deletedAt: Now.AddDays(-2));
        var unstamped = NewAsset(null);
        var world = new World(due, future, softDeleted, unstamped);

        var summary = await world.Job().SweepAsync(Ct);

        summary.Deleted.ShouldBe(1);
        due.DeletedAt.ShouldBe(Now);
        world.Blobs.Contains(Bucket, due.StorageKey).ShouldBeFalse();
        world.Blobs.Contains(Bucket, future.StorageKey).ShouldBeTrue();
        world.Blobs.Contains(Bucket, softDeleted.StorageKey).ShouldBeTrue();
        world.Blobs.Contains(Bucket, unstamped.StorageKey).ShouldBeTrue();
        future.DeletedAt.ShouldBeNull();
        unstamped.DeletedAt.ShouldBeNull();
    }

    [Fact]
    public async Task RetentionSweep_SetsDeletedAt_AndSecondRunIsNoOp()
    {
        var world = new World(NewAsset(Now.AddDays(-1)), NewAsset(Now.AddDays(-2)));
        var job = world.Job();

        (await job.SweepAsync(Ct)).Deleted.ShouldBe(2);
        var deletes = world.Blobs.CountOf(BlobOperation.Delete);

        var second = await job.SweepAsync(Ct);

        second.Deleted.ShouldBe(0);
        world.Blobs.CountOf(BlobOperation.Delete).ShouldBe(deletes, "the second run deletes no object");
        world.Query.Calls.Count.ShouldBe(2, "one query per run: the first batch is short");
    }

    [Fact]
    public async Task RetentionSweep_ToleratesMissingObject_AndStillMarksDeleted()
    {
        var asset = NewAsset(Now.AddDays(-1));
        var world = new World(asset);
        world.Blobs.Seed(Bucket, asset.StorageKey, [1]);
        await world.Blobs.DeleteAsync(Bucket, asset.StorageKey, Ct);

        var summary = await world.Job().SweepAsync(Ct);

        summary.Deleted.ShouldBe(1);
        asset.DeletedAt.ShouldBe(Now);
    }

    [Fact]
    public async Task RetentionSweep_ContinuesAfterOneObjectFails()
    {
        var poison = NewAsset(Now.AddDays(-1));
        var fine = NewAsset(Now.AddDays(-1));
        var world = new World(poison, fine) { FailKey = poison.StorageKey };
        using var counters = new CounterListener();

        var summary = await world.Job().SweepAsync(Ct);

        summary.Deleted.ShouldBe(1);
        summary.Failed.ShouldBe(1);
        poison.DeletedAt.ShouldBeNull("a failed row stays live");
        fine.DeletedAt.ShouldBe(Now);
        counters.Total(RetentionSweepMetrics.AssetFailuresCounterName).ShouldBe(1);
    }

    [Fact]
    public async Task RetentionSweep_ExcludesThisRunsFailuresFromTheNextQuery()
    {
        var poison = NewAsset(Now.AddDays(-1));
        var world = new World([poison, .. Enumerable.Range(0, 5).Select(_ => NewAsset(Now.AddDays(-1)))])
        {
            FailKey = poison.StorageKey,
            Options = { BatchSize = 3 },
        };

        await world.Job().SweepAsync(Ct);

        // The first batch contains the poison row; no later batch of the same run may return it.
        world.Query.Calls.Count.ShouldBeGreaterThan(1);
        world.Query.Calls.Skip(1).SelectMany(call => call.Returned).ShouldNotContain(poison.Id);
        world.Query.Calls.Skip(1).ShouldAllBe(call => call.ExcludedFailed(poison.Id));
    }

    [Fact]
    public async Task RetentionSweep_StopsAtMaxBatches_AndReportsCapped()
    {
        var assets = Enumerable.Range(0, 40).Select(_ => NewAsset(Now.AddDays(-1))).ToArray();
        var world = new World(assets) { Options = { BatchSize = 2, MaxBatches = 3 } };
        world.Blobs.AsyncFailure = Error.Failure("service_unavailable", "down");
        using var counters = new CounterListener();

        var summary = await world.Job().SweepAsync(Ct);

        summary.Capped.ShouldBeTrue();
        summary.Deleted.ShouldBe(0);
        world.Query.Calls.Count.ShouldBe(4, "the run ends after MaxBatches iterations plus one probe, not never");
        counters.Total(RetentionSweepMetrics.BatchesCappedCounterName).ShouldBe(1);
        world.Logs.Messages.ShouldContain(message => message.Contains("capped=True", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RetentionSweep_ExactlyMaxBatchesAndNoBacklog_IsNotCapped()
    {
        var assets = Enumerable.Range(0, 6).Select(_ => NewAsset(Now.AddDays(-1))).ToArray();
        var world = new World(assets) { Options = { BatchSize = 2, MaxBatches = 3 } };
        using var counters = new CounterListener();

        var summary = await world.Job().SweepAsync(Ct);

        summary.Deleted.ShouldBe(6);
        summary.Capped.ShouldBeFalse("nothing is left after the last full batch");
        world.Query.Calls.Count.ShouldBe(4, "three batches plus the one-row probe");
        counters.Total(RetentionSweepMetrics.BatchesCappedCounterName).ShouldBe(0);
    }

    [Fact]
    public async Task RetentionSweep_BatchesAt500()
    {
        var world = new World([.. Enumerable.Range(0, 1001).Select(_ => NewAsset(Now.AddDays(-1)))]);

        var summary = await world.Job().SweepAsync(Ct);

        summary.Deleted.ShouldBe(1001);
        world.Query.Calls.Select(call => call.Returned.Count).ShouldBe([500, 500, 1]);
    }

    [Fact]
    public async Task RetentionSweep_ProcessesEachRowUnderItsOwnUserScope()
    {
        var first = NewAsset(Now.AddDays(-1));
        var second = NewAsset(Now.AddDays(-1));
        var world = new World(first, second);

        await world.Job().SweepAsync(Ct);

        first.DeletedAt.ShouldNotBeNull();
        second.DeletedAt.ShouldNotBeNull();
        world.PinnedUsers.ShouldBe([first.UserId, second.UserId], ignoreOrder: true);
        world.UserSeenByStore.ShouldBe(world.PinnedUsers, ignoreOrder: true);
    }

    [Fact]
    public async Task RetentionSweep_ExitsWhenAdvisoryLockIsHeld()
    {
        var asset = NewAsset(Now.AddDays(-1));
        var world = new World(asset) { LockHeld = true };

        var summary = await world.Job().SweepAsync(Ct);

        summary.Deleted.ShouldBe(0);
        world.Query.Calls.ShouldBeEmpty();
        world.Audit.Loads.ShouldBeEmpty();
        asset.DeletedAt.ShouldBeNull();
        world.Logs.Messages.ShouldContain(message => message.Contains("reason=locked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RetentionSweep_LogsCountsOnly_NoKeys()
    {
        var asset = NewAsset(Now.AddDays(-1));
        var world = new World(asset) { FailKey = asset.StorageKey };

        await world.Job().SweepAsync(Ct);

        var summary = world.Logs.Messages.Single(message => message.StartsWith("retention_sweep_completed", StringComparison.Ordinal));
        summary.ShouldContain("deleted=");
        summary.ShouldContain("seconds=");
        world.Logs.Messages.ShouldAllBe(message => !message.Contains(asset.StorageKey, StringComparison.Ordinal));
        world.Logs.Messages.ShouldAllBe(message => !message.Contains(asset.UserId.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AuditRetention_ScansByKeyset_AndDeletesOnlyExpiredRows()
    {
        var old = Now.AddDays(-400);
        var rows = Enumerable.Range(0, 1001)
            .Select(index => Candidate(Category.Episodes, ActorType.User, Visibility.User, old.AddSeconds(index)))
            .ToList();
        rows.Add(Candidate(Category.Billing, ActorType.User, Visibility.User, Now.AddDays(-10)));
        var world = new World();
        world.Audit.Rows.AddRange(rows);

        var summary = await world.Job().SweepAsync(Ct);

        summary.AuditDeleted.ShouldBe(1001);
        world.Audit.Rows.Count.ShouldBe(1, "the recent row stays");
        world.Audit.Loads.Count.ShouldBe(3, "1,002 rows at 500 per page");
        world.Audit.Loads[0].AfterCreatedAt.ShouldBeNull();
        world.Audit.Loads[1].AfterCreatedAt.ShouldNotBeNull();
        world.Audit.Loads[2].AfterCreatedAt!.Value.ShouldBeGreaterThan(world.Audit.Loads[1].AfterCreatedAt!.Value);
    }

    [Fact]
    public async Task AuditRetention_DeletesAtMostBatchSizeRowsPerStatement()
    {
        var old = Now.AddDays(-400);
        var world = new World();
        world.Audit.Rows.AddRange(Enumerable.Range(0, 1200)
            .Select(index => Candidate(Category.Episodes, ActorType.User, Visibility.User, old.AddSeconds(index))));

        await world.Job().SweepAsync(Ct);

        world.Audit.DeleteSizes.ShouldAllBe(size => size <= 500);
    }

    [Fact]
    public async Task AuditRetention_FailureKeepsAssetWorkAndRethrows()
    {
        var asset = NewAsset(Now.AddDays(-1));
        var world = new World(asset);
        world.Audit.Throw = true;

        await Should.ThrowAsync<InvalidOperationException>(() => world.Job().SweepAsync(Ct));

        asset.DeletedAt.ShouldBe(Now, "asset deletions committed before the audit phase stay");
        world.Logs.Messages.ShouldContain(message => message.StartsWith("retention_sweep_completed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Qa_AssetDueExactlyNow_IsKept_AndOneTickEarlierIsDeleted()
    {
        var atNow = NewAsset(Now);
        var before = NewAsset(Now.AddTicks(-1));
        var world = new World(atNow, before);

        var summary = await world.Job().SweepAsync(Ct);

        summary.Deleted.ShouldBe(1);
        atNow.DeletedAt.ShouldBeNull("retention_until < now is strict");
        before.DeletedAt.ShouldBe(Now);
    }

    [Fact]
    public async Task RetentionSweep_RestampedBetweenIdQueryAndLoad_KeepsTheObjectAndTheRow()
    {
        var asset = NewAsset(Now.AddDays(-1));
        var other = NewAsset(Now.AddDays(-1));
        var world = new World(asset, other)
        {
            BeforeLoad = loaded =>
            {
                if (loaded == asset)
                {
                    // approve (09.7.1-T1) re-stamps retention_until into the future.
                    Set(loaded, typeof(Asset), nameof(Asset.RetentionUntil), Now.AddDays(30));
                }
            },
        };

        var summary = await world.Job().SweepAsync(Ct);

        summary.Deleted.ShouldBe(1);
        summary.Failed.ShouldBe(0, "a row that is no longer due is skipped, not failed");
        asset.DeletedAt.ShouldBeNull();
        world.Blobs.Contains(Bucket, asset.StorageKey).ShouldBeTrue("the object of a re-stamped asset is not deleted");
        other.DeletedAt.ShouldBe(Now);
    }

    [Fact]
    public async Task Qa_Disabled_SkipsLockQueryAndAudit()
    {
        var asset = NewAsset(Now.AddDays(-1));
        var world = new World(asset);
        world.Options.Enabled = false;

        var summary = await world.Job().SweepAsync(Ct);

        summary.Deleted.ShouldBe(0);
        world.Query.Calls.ShouldBeEmpty();
        world.Audit.Loads.ShouldBeEmpty();
        world.LockReleases.ShouldBe(0, "the lock is never taken when disabled");
        asset.DeletedAt.ShouldBeNull();
        world.Logs.Messages.ShouldContain(message => message.Contains("reason=disabled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Qa_ThrownBlobException_IsContained_AndNeverLogsTheKey()
    {
        var poison = NewAsset(Now.AddDays(-1));
        var fine = NewAsset(Now.AddDays(-1));
        var world = new World(poison, fine)
        {
            ThrowKey = poison.StorageKey,
            Thrower = key => new HttpRequestException("socket reset while deleting " + key),
        };

        var summary = await world.Job().SweepAsync(Ct);

        summary.Deleted.ShouldBe(1);
        summary.Failed.ShouldBe(1);
        poison.DeletedAt.ShouldBeNull("a row whose delete threw is never marked");
        fine.DeletedAt.ShouldBe(Now);
        world.Logs.Messages.ShouldAllBe(message => !message.Contains(poison.StorageKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Qa_Cancellation_Propagates_LeavesRowLive_AndReleasesTheLock()
    {
        var asset = NewAsset(Now.AddDays(-1));
        var world = new World(asset)
        {
            ThrowKey = asset.StorageKey,
            Thrower = _ => new OperationCanceledException(),
        };

        await Should.ThrowAsync<OperationCanceledException>(() => world.Job().SweepAsync(Ct));

        asset.DeletedAt.ShouldBeNull();
        world.LockReleases.ShouldBe(1, "the handle is disposed on the exceptional path");
    }

    [Fact]
    public async Task Qa_LockIsReleasedAfterNormalRun_AndAfterAuditFailure()
    {
        var normal = new World(NewAsset(Now.AddDays(-1)));
        await normal.Job().SweepAsync(Ct);
        normal.LockReleases.ShouldBe(1);

        var failing = new World();
        failing.Audit.Throw = true;
        await Should.ThrowAsync<InvalidOperationException>(() => failing.Job().SweepAsync(Ct));
        failing.LockReleases.ShouldBe(1);
    }

    [Fact]
    public async Task Qa_AuditRowAtExactlyTheWindow_IsKept_AndOneTickOlderIsDeleted()
    {
        var exactly = Candidate(Category.Episodes, ActorType.User, Visibility.User, Now.AddDays(-365));
        var older = Candidate(Category.Episodes, ActorType.User, Visibility.User, Now.AddDays(-365).AddTicks(-1));
        var world = new World();
        world.Audit.Rows.AddRange([exactly, older]);

        var summary = await world.Job().SweepAsync(Ct);

        summary.AuditDeleted.ShouldBe(1);
        world.Audit.Rows.ShouldHaveSingleItem().Id.ShouldBe(exactly.Id);
    }

    [Fact]
    public async Task Qa_AuditPhase_StopsAtMaxBatches_AndLeavesTheBacklog()
    {
        var old = Now.AddDays(-400);
        var world = new World { Options = { BatchSize = 2, MaxBatches = 2 } };
        world.Audit.Rows.AddRange(Enumerable.Range(0, 10)
            .Select(index => Candidate(Category.Episodes, ActorType.User, Visibility.User, old.AddSeconds(index))));
        using var counters = new CounterListener();

        var summary = await world.Job().SweepAsync(Ct);

        summary.Capped.ShouldBeTrue();
        summary.AuditDeleted.ShouldBe(4);
        world.Audit.Rows.Count.ShouldBe(6, "the backlog stays for tomorrow");
        world.Audit.Loads.Count.ShouldBe(3, "two batches plus the one-row probe");
        counters.Total(RetentionSweepMetrics.BatchesCappedCounterName).ShouldBe(1);
        counters.Total(RetentionSweepMetrics.AuditRowsDeletedCounterName).ShouldBe(4);
    }

    [Fact]
    public async Task Qa_AuditPhase_ExactlyMaxBatchesAndNoBacklog_IsNotCapped()
    {
        var old = Now.AddDays(-400);
        var world = new World { Options = { BatchSize = 2, MaxBatches = 2 } };
        world.Audit.Rows.AddRange(Enumerable.Range(0, 4)
            .Select(index => Candidate(Category.Episodes, ActorType.User, Visibility.User, old.AddSeconds(index))));
        using var counters = new CounterListener();

        var summary = await world.Job().SweepAsync(Ct);

        summary.Capped.ShouldBeFalse("nothing is left after the last full batch");
        summary.AuditDeleted.ShouldBe(4);
        world.Audit.Loads.Count.ShouldBe(3, "two batches plus the one-row probe");
        counters.Total(RetentionSweepMetrics.BatchesCappedCounterName).ShouldBe(0);
    }

    [Fact]
    public async Task Qa_RetainedRows_AreSkippedByTheCursor_NotReadTwice()
    {
        var old = Now.AddDays(-400);
        var world = new World { Options = { BatchSize = 2 } };
        // Four 400-day-old account_security rows (kept 730 d) in front of one expired default row.
        world.Audit.Rows.AddRange(Enumerable.Range(0, 4)
            .Select(index => Candidate(Category.AccountSecurity, ActorType.User, Visibility.User, old.AddSeconds(index))));
        world.Audit.Rows.Add(Candidate(Category.Episodes, ActorType.User, Visibility.User, old.AddSeconds(10)));

        var summary = await world.Job().SweepAsync(Ct);

        summary.AuditDeleted.ShouldBe(1);
        world.Audit.Rows.Count.ShouldBe(4);
        world.Audit.Loads.Count.ShouldBe(3, "2 + 2 + 1 rows, each read once");
        world.Audit.Loads.Select(load => load.AfterId).Where(id => id is not null).Distinct().Count().ShouldBe(2);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AuditRetentionCandidate Candidate(Category category, ActorType actor, Visibility visibility, DateTimeOffset createdAt) =>
        new(Guid.CreateVersion7(), new AuditRetentionRow(category, actor, visibility, false, createdAt), createdAt);

    private static Asset NewAsset(DateTimeOffset? retentionUntil, DateTimeOffset? deletedAt = null)
    {
        var asset = (Asset)Activator.CreateInstance(typeof(Asset), nonPublic: true)!;
        Set(asset, typeof(Entity), nameof(Entity.Id), Guid.CreateVersion7());
        Set(asset, typeof(Asset), nameof(Asset.UserId), Guid.CreateVersion7());
        Set(asset, typeof(Asset), nameof(Asset.StorageKey), $"media/{Guid.NewGuid():N}.mp4");
        Set(asset, typeof(Asset), nameof(Asset.RetentionUntil), retentionUntil);
        Set(asset, typeof(Asset), nameof(Asset.DeletedAt), deletedAt);

        return asset;
    }

    private static void Set(Asset asset, Type declaring, string name, object? value) =>
        declaring.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(asset, value);

    /// <summary>The fakes of one run, wired the way the Worker wires the real ports.</summary>
    private sealed class World
    {
        private readonly List<Asset> _assets;
        private readonly ServiceProvider _provider;

        public World(params Asset[] assets)
        {
            _assets = [.. assets];
            Query = new QueryFake(_assets);
            Audit = new AuditFake();
            Logs = new LogSink();
            Blobs = new InMemoryBlobStore();
            foreach (var asset in _assets)
            {
                Blobs.Seed(Bucket, asset.StorageKey, [1]);
            }

            var services = new ServiceCollection();
            services.AddScoped<PinFake>(_ => new PinFake(PinnedUsers));
            services.AddScoped<ISystemContext>(provider => provider.GetRequiredService<PinFake>());
            services.AddScoped<IAssetRetentionStore>(provider => new AssetStoreFake(_assets, provider.GetRequiredService<PinFake>(), UserSeenByStore, () => BeforeLoad));
            _provider = services.BuildServiceProvider();
        }

        public RetentionSweepOptions Options { get; } = new();

        public QueryFake Query { get; }

        public AuditFake Audit { get; }

        public LogSink Logs { get; }

        public InMemoryBlobStore Blobs { get; }

        public List<Guid> PinnedUsers { get; } = [];

        public List<Guid> UserSeenByStore { get; } = [];

        public bool LockHeld { get; set; }

        public Func<string, Exception>? Thrower { get; set; }

        public string? ThrowKey { get; set; }

        public int LockReleases { get; set; }

        public string? FailKey { get; set; }

        /// <summary>Runs after the id query and before the per-row load: the window a concurrent writer uses.</summary>
        public Action<Asset>? BeforeLoad { get; set; }

        public RetentionSweepJob Job() => new(
            new LockFake(this),
            Query,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            Audit,
            Store(),
            Storage,
            Options,
            new FakeClock(Now),
            new RetentionSweepMetrics(),
            Logs);

        private IBlobStore Store() =>
            ThrowKey is { } throwKey && Thrower is { } thrower
                ? new ThrowOnDeleteStore(Blobs, throwKey, thrower)
                : FailKey is null ? Blobs : new SelectiveFailureStore(Blobs, FailKey);
    }

    private sealed class LockFake(World world) : IRetentionSweepLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct) =>
            Task.FromResult<IAsyncDisposable?>(world.LockHeld ? null : new Handle(world));

        private sealed class Handle(World world) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                world.LockReleases++;

                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class PinFake(List<Guid> sink) : ISystemContext
    {
        public Guid? Current { get; private set; }

        public IDisposable For(Guid userId)
        {
            Current = userId;
            sink.Add(userId);

            return new Release();
        }

        private sealed class Release : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class AssetStoreFake(List<Asset> assets, PinFake pin, List<Guid> seen, Func<Action<Asset>?> beforeLoad) : IAssetRetentionStore
    {
        public Task<Asset?> FindDueAsync(Guid assetId, DateTimeOffset now, CancellationToken ct)
        {
            // The tenant filter: only the pinned user's rows are visible.
            seen.Add(pin.Current!.Value);
            var asset = assets.SingleOrDefault(a => a.Id == assetId && a.UserId == pin.Current);
            if (asset is not null)
            {
                beforeLoad()?.Invoke(asset);
            }

            return Task.FromResult(assets.SingleOrDefault(a =>
                a.Id == assetId && a.UserId == pin.Current && a.DeletedAt == null && a.RetentionUntil is { } until && until < now));
        }

        public Task MarkDeletedAsync(Asset asset, DateTimeOffset now, CancellationToken ct)
        {
            asset.MarkDeleted(now);

            return Task.CompletedTask;
        }
    }

    private sealed class QueryCall(IReadOnlyList<Guid> returned, Expression<Func<Asset, bool>> where)
    {
        public IReadOnlyList<Guid> Returned { get; } = returned;

        public Expression<Func<Asset, bool>> Where { get; } = where;

        /// <summary>Whether the predicate handed to the port rejects an otherwise-due row with this id.</summary>
        public bool ExcludedFailed(Guid id)
        {
            var probe = (Asset)Activator.CreateInstance(typeof(Asset), nonPublic: true)!;
            Set(probe, typeof(Entity), nameof(Entity.Id), id);
            Set(probe, typeof(Asset), nameof(Asset.RetentionUntil), Now.AddDays(-1));

            return !Where.Compile()(probe);
        }
    }

    private sealed class QueryFake(List<Asset> assets) : ISystemWorkQuery
    {
        public List<QueryCall> Calls { get; } = [];

        public async IAsyncEnumerable<(Guid Id, Guid UserId)> PendingAsync<T>(
            Expression<Func<T, bool>> where,
            int batch,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
            where T : Entity, IUserOwned
        {
            var typed = (Expression<Func<Asset, bool>>)(object)where;
            var matches = assets.Where(typed.Compile()).Take(batch).ToList();
            Calls.Add(new QueryCall([.. matches.Select(a => a.Id)], typed));

            foreach (var asset in matches)
            {
                await Task.Yield();
                yield return (asset.Id, asset.UserId);
            }
        }
    }

    private sealed class AuditFake : IAuditRetentionStore
    {
        public List<AuditRetentionCandidate> Rows { get; } = [];

        public List<(DateTimeOffset? AfterCreatedAt, Guid? AfterId)> Loads { get; } = [];

        public List<int> DeleteSizes { get; } = [];

        public bool Throw { get; set; }

        public Task<IReadOnlyList<AuditRetentionCandidate>> LoadAfterAsync(DateTimeOffset? afterCreatedAt, Guid? afterId, int take, CancellationToken ct)
        {
            Loads.Add((afterCreatedAt, afterId));
            if (Throw)
            {
                throw new InvalidOperationException("audit store down");
            }

            var page = Rows
                .OrderBy(row => row.CreatedAt).ThenBy(row => row.Id)
                .Where(row => afterCreatedAt is null || row.CreatedAt > afterCreatedAt || (row.CreatedAt == afterCreatedAt && row.Id.CompareTo(afterId!.Value) > 0))
                .Take(take)
                .ToList();

            return Task.FromResult<IReadOnlyList<AuditRetentionCandidate>>(page);
        }

        public Task<int> DeleteAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
        {
            DeleteSizes.Add(ids.Count);

            return Task.FromResult(Rows.RemoveAll(row => ids.Contains(row.Id)));
        }
    }

    /// <summary>Delegates to the in-memory store but fails the delete of one key.</summary>
    private sealed class SelectiveFailureStore(InMemoryBlobStore inner, string failKey) : IBlobStore
    {
        public Task<ErrorOr<Success>> DeleteAsync(string bucket, string key, CancellationToken cancellationToken) =>
            key == failKey
                ? Task.FromResult<ErrorOr<Success>>(Error.Failure("service_unavailable", "boom"))
                : inner.DeleteAsync(bucket, key, cancellationToken);

        public Task<ErrorOr<BlobInfo>> PutAsync(string bucket, string key, Stream content, string contentType, CancellationToken cancellationToken) =>
            inner.PutAsync(bucket, key, content, contentType, cancellationToken);

        public Task<ErrorOr<BlobObject>> OpenReadAsync(string bucket, string key, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(bucket, key, cancellationToken);

        public Task<ErrorOr<BlobObject>> OpenReadAsync(string bucket, string key, long fromByte, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(bucket, key, fromByte, cancellationToken);

        public Task<ErrorOr<bool>> ExistsAsync(string bucket, string key, CancellationToken cancellationToken) =>
            inner.ExistsAsync(bucket, key, cancellationToken);

        public IAsyncEnumerable<string> ListKeysAsync(string bucket, string prefix, CancellationToken cancellationToken) =>
            inner.ListKeysAsync(bucket, prefix, cancellationToken);

        public ErrorOr<Uri> CreateReadUrl(string bucket, string key, TimeSpan ttl, string? downloadFileName) =>
            inner.CreateReadUrl(bucket, key, ttl, downloadFileName);
    }

    /// <summary>Delegates to the in-memory store but throws (rather than returns an error) on one key's delete.</summary>
    private sealed class ThrowOnDeleteStore(InMemoryBlobStore inner, string throwKey, Func<string, Exception> thrower) : IBlobStore
    {
        public Task<ErrorOr<Success>> DeleteAsync(string bucket, string key, CancellationToken cancellationToken) =>
            key == throwKey
                ? throw thrower(key)
                : inner.DeleteAsync(bucket, key, cancellationToken);

        public Task<ErrorOr<BlobInfo>> PutAsync(string bucket, string key, Stream content, string contentType, CancellationToken cancellationToken) =>
            inner.PutAsync(bucket, key, content, contentType, cancellationToken);

        public Task<ErrorOr<BlobObject>> OpenReadAsync(string bucket, string key, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(bucket, key, cancellationToken);

        public Task<ErrorOr<BlobObject>> OpenReadAsync(string bucket, string key, long fromByte, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(bucket, key, fromByte, cancellationToken);

        public Task<ErrorOr<bool>> ExistsAsync(string bucket, string key, CancellationToken cancellationToken) =>
            inner.ExistsAsync(bucket, key, cancellationToken);

        public IAsyncEnumerable<string> ListKeysAsync(string bucket, string prefix, CancellationToken cancellationToken) =>
            inner.ListKeysAsync(bucket, prefix, cancellationToken);

        public ErrorOr<Uri> CreateReadUrl(string bucket, string key, TimeSpan ttl, string? downloadFileName) =>
            inner.CreateReadUrl(bucket, key, ttl, downloadFileName);
    }

    private sealed class LogSink : ILogger<RetentionSweepJob>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class CounterListener : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Dictionary<string, long> _totals = [];

        public CounterListener()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == RetentionSweepMetrics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
                _totals[instrument.Name] = _totals.GetValueOrDefault(instrument.Name) + value);
            _listener.Start();
        }

        public long Total(string name) => _totals.GetValueOrDefault(name);

        public void Dispose() => _listener.Dispose();
    }
}
