using System.Diagnostics.Metrics;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ErrorOr;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Common.Errors;
using NightTales.Application.Rendering;
using NightTales.Domain.Rendering;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Rendering;

/// <summary>
/// QA probes for 11.3.3-T2 that <see cref="NodeSelectorTests"/> does not cover: the once-per-interval
/// warning gate, the warning's hygiene (never a URL or a certificate), the failure's <see cref="Error"/>
/// shape, ordering boundaries and the cache-only contract of the gauges.
/// </summary>
public sealed class NodeSelectorQaTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NoCandidate_ErrorIsFailure_WithFixedCodeAndDescription()
    {
        var rig = new QaRig();

        var picked = await rig.PickAsync();

        picked.IsError.ShouldBeTrue();
        picked.FirstError.Type.ShouldBe(ErrorType.Failure);
        picked.FirstError.Code.ShouldBe(ErrorCodes.RenderWorkerUnavailable);
        picked.FirstError.Code.ShouldBe("render_worker_unavailable");
        picked.FirstError.Description.ShouldBe("no render node with a free slot");
    }

    [Fact]
    public async Task NoCandidate_WarnsOncePerPollInterval_NotPerPick()
    {
        var rig = new QaRig();
        rig.AddNode("busy", capacity: 1, inFlight: 1);

        await rig.PickAsync();
        await rig.PickAsync();
        await rig.PickAsync();
        rig.WarningsOf("render.dispatch_unavailable").ShouldBe(1);

        // One poll interval later the gate re-arms and the next failed pick warns again.
        rig.Clock.Advance(TimeSpan.FromSeconds(new RenderNodesOptions().HealthPollSeconds));
        await rig.PickAsync();
        rig.WarningsOf("render.dispatch_unavailable").ShouldBe(2);

        // Just under the interval stays silent.
        rig.Clock.Advance(TimeSpan.FromSeconds(new RenderNodesOptions().HealthPollSeconds - 1));
        await rig.PickAsync();
        rig.WarningsOf("render.dispatch_unavailable").ShouldBe(2);
    }

    [Fact]
    public async Task NoCandidate_WarningCarriesReasonHistogram_NoUrlNoCert()
    {
        var rig = new QaRig();
        rig.AddNode("stale", capacity: 2, inFlight: 0, pulledAt: Now - TimeSpan.FromSeconds(120));
        rig.AddNode("closed", capacity: 2, inFlight: 0, accepting: false);
        rig.AddNode("full", capacity: 1, inFlight: 1);

        await rig.PickAsync();

        var warning = rig.LastWarning("render.dispatch_unavailable");
        warning.ShouldContain("activeNodes=3");
        warning.ShouldContain("staleHealth=1");
        warning.ShouldContain("notAccepting=1");
        warning.ShouldContain("noFreeSlot=1");
        warning.ShouldNotContain("https://");
        warning.ShouldNotContain("example");
        warning.ShouldNotContain("AAAA");
    }

    [Fact]
    public void LogGate_SuppressesInsideInterval_ClaimsAtExactBoundary()
    {
        var gate = new NodeDispatchLogGate();
        var interval = TimeSpan.FromSeconds(30);

        gate.TryClaim(Now, interval).ShouldBeTrue();
        gate.TryClaim(Now + TimeSpan.FromSeconds(29), interval).ShouldBeFalse();
        gate.TryClaim(Now + TimeSpan.FromSeconds(30), interval).ShouldBeTrue();
    }

    [Fact]
    public void LogGate_BackwardsClock_ClaimsAndReArms()
    {
        var gate = new NodeDispatchLogGate();
        var interval = TimeSpan.FromSeconds(30);

        gate.TryClaim(Now, interval).ShouldBeTrue();
        // now < last: the gate claims rather than waiting out a stale instant (F12 clock-jump handling).
        gate.TryClaim(Now - TimeSpan.FromSeconds(10), interval).ShouldBeTrue();
        gate.TryClaim(Now - TimeSpan.FromSeconds(10) + TimeSpan.FromSeconds(5), interval).ShouldBeFalse();
    }

    [Fact]
    public async Task TieBreak_IsOrdinal_DigitsSortByFirstCharacter()
    {
        var rig = new QaRig();
        rig.AddNode("node-2", capacity: 2, inFlight: 0);
        rig.AddNode("node-10", capacity: 2, inFlight: 0);

        // Ordinal: '1' < '2', so node-10 precedes node-2 — deterministic, matching PostgreSQL-agnostic memory sort.
        (await rig.PickAsync()).Value.Name.ShouldBe("node-10");
    }

    [Fact]
    public async Task Ordering_UsesMinOfCapacityAndHealthCap()
    {
        var rig = new QaRig();
        // 'big' has the larger raw capacity but the node's adapter accepts only 1; 'small' has 2 free.
        rig.AddNode("big", capacity: 8, inFlight: 0, maxConcurrentTasks: 1);
        rig.AddNode("small", capacity: 2, inFlight: 0, maxConcurrentTasks: 2);

        (await rig.PickAsync()).Value.Name.ShouldBe("small");
    }

    [Fact]
    public async Task InFlightAboveCap_ExcludesNode_StillCountsTowardNoFreeSlot()
    {
        var rig = new QaRig();
        rig.AddNode("over", capacity: 1, inFlight: 5, maxConcurrentTasks: 1);

        var picked = await rig.PickAsync();

        picked.FirstError.Code.ShouldBe(ErrorCodes.RenderWorkerUnavailable);
        rig.LastWarning("render.dispatch_unavailable").ShouldContain("noFreeSlot=1");
    }

    [Fact]
    public async Task HttpOkFalse_AndNotAccepting_CountAsNotAccepting()
    {
        var rig = new QaRig();
        rig.AddNode("flapping", capacity: 2, inFlight: 0, httpOk: false);

        await rig.PickAsync();

        rig.LastWarning("render.dispatch_unavailable").ShouldContain("notAccepting=1");
    }

    [Fact]
    public async Task SnapshotFromFuture_StaysFresh_ForwardJumpOnlyAges()
    {
        // The spec's clock-jump rule is forward-only; this pins the observed backward behaviour so a
        // deliberate tightening would flip this test loudly instead of silently.
        var rig = new QaRig();
        rig.AddNode("future", capacity: 1, inFlight: 0, pulledAt: Now + TimeSpan.FromSeconds(60));

        (await rig.PickAsync()).Value.Name.ShouldBe("future");

        rig.Clock.Advance(TimeSpan.FromHours(1));
        (await rig.PickAsync()).FirstError.Code.ShouldBe(ErrorCodes.RenderWorkerUnavailable);
    }

    [Fact]
    public async Task FreeSlotsGauge_ZeroForNodeWithoutCachedHealth()
    {
        var rig = new QaRig();
        rig.AddNode("silent", capacity: 4, inFlight: 0, withHealth: false);
        await rig.PickAsync();

        rig.ReadGauge(NodeMetrics.FreeSlotsInstrumentName)["silent"].ShouldBe(0);
    }

    [Fact]
    public void MetricsWithoutCache_RegistersOnlyEventDrivenInstruments()
    {
        using var metrics = new NodeMetrics();
        metrics.RecordUp("a", up: true);

        var up = QaRig.ReadGauge(metrics, NodeMetrics.UpInstrumentName);
        up["a"].ShouldBe(1);
        QaRig.ReadGauge(metrics, NodeMetrics.FreeSlotsInstrumentName).ShouldBeEmpty();
        QaRig.ReadGauge(metrics, NodeMetrics.GpuUtilInstrumentName).ShouldBeEmpty();
        QaRig.ReadGauge(metrics, NodeMetrics.GpuMemUsedInstrumentName).ShouldBeEmpty();
        QaRig.ReadGauge(metrics, NodeMetrics.GpuTempInstrumentName).ShouldBeEmpty();
        QaRig.ReadGauge(metrics, NodeMetrics.DiskUsedInstrumentName).ShouldBeEmpty();
    }

    [Fact]
    public void CacheGauges_OmitMeasurement_WhenNodeCannotSample()
    {
        var cache = new QaHealthCache();
        cache.Set(new NodeHealthSnapshot(
            "thin", Now, true, "ok", [], 2, 0, true,
            GpuUtilPct: null, GpuMemUsedMb: null, DiskUsedPct: null, GpuTempC: null));
        using var metrics = new NodeMetrics(cache);

        QaRig.ReadGauge(metrics, NodeMetrics.GpuUtilInstrumentName).ShouldBeEmpty();
        QaRig.ReadGauge(metrics, NodeMetrics.GpuMemUsedInstrumentName).ShouldBeEmpty();
        QaRig.ReadGauge(metrics, NodeMetrics.GpuTempInstrumentName).ShouldBeEmpty();
        QaRig.ReadGauge(metrics, NodeMetrics.DiskUsedInstrumentName).ShouldBeEmpty();
    }

    [Fact]
    public async Task NonActiveNode_PolledBySelectorQuery_OnlyActiveQueried()
    {
        // draining/down nodes never reach the predicate (the WHERE is in the query itself);
        // here we pin that a non-active node contributes nothing to the warning histogram either.
        var rig = new QaRig();
        var node = rig.AddNode("leaving", capacity: 2, inFlight: 0);
        node.SetOperatorState(WorkerNodeState.Draining).IsError.ShouldBeFalse();

        await rig.PickAsync();

        rig.LastWarning("render.dispatch_unavailable").ShouldContain("activeNodes=0");
    }

    private sealed class QaRig
    {
        private readonly QaHealthCache _cache = new();
        private readonly List<WorkerNode> _nodes = [];
        private readonly Dictionary<Guid, int> _inFlight = [];
        private readonly List<string> _warnings = [];

        public QaRig()
        {
            Metrics = new NodeMetrics(_cache);
            var logger = new ListLogger(_warnings);
            _selector = new NodeSelector(
                new QaReadDb(_nodes),
                _cache,
                new QaLoadReader(_inFlight),
                Clock,
                Options.Create(new RenderNodesOptions()),
                Metrics,
                new NodeDispatchLogGate(),
                logger);
        }

        private readonly NodeSelector _selector;

        public FakeClock Clock { get; } = new(Now);

        public NodeMetrics Metrics { get; }

        public WorkerNode AddNode(
            string name,
            int capacity,
            int inFlight,
            int? maxConcurrentTasks = null,
            DateTimeOffset? pulledAt = null,
            bool accepting = true,
            bool httpOk = true,
            bool withHealth = true)
        {
            var node = WorkerNode.Create(name, new Uri("https://" + name + ".example"), capacity, new string('A', 64), null).Value;
            _nodes.Add(node);
            _inFlight[node.Id] = inFlight;

            if (withHealth)
            {
                _cache.Set(new NodeHealthSnapshot(
                    name,
                    pulledAt ?? Now,
                    httpOk,
                    httpOk ? "ok" : "error",
                    httpOk ? [] : ["proxy_down"],
                    maxConcurrentTasks ?? capacity,
                    inFlight,
                    accepting,
                    GpuUtilPct: 37,
                    GpuMemUsedMb: 2048,
                    DiskUsedPct: 41,
                    GpuTempC: 64));
            }

            return node;
        }

        public Task<ErrorOr<WorkerNode>> PickAsync() => _selector.PickAsync(CancellationToken.None);

        public int WarningsOf(string prefix) => _warnings.Count(m => m.StartsWith("Warning:" + prefix, StringComparison.Ordinal));

        public string LastWarning(string prefix) =>
            _warnings.Last(m => m.StartsWith("Warning:" + prefix, StringComparison.Ordinal));

        public Dictionary<string, double> ReadGauge(string instrument) => ReadGauge(Metrics, instrument);

        public static Dictionary<string, double> ReadGauge(NodeMetrics metrics, string instrument)
        {
            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            using var listener = new MeterListener();
            listener.InstrumentPublished = (candidate, l) =>
            {
                if (ReferenceEquals(candidate.Meter, metrics.Meter) && candidate.Name == instrument)
                {
                    l.EnableMeasurementEvents(candidate);
                }
            };
            listener.SetMeasurementEventCallback<int>((_, value, tags, _) => values[Node(tags)] = value);
            listener.SetMeasurementEventCallback<double>((_, value, tags, _) => values[Node(tags)] = value);
            listener.Start();
            listener.RecordObservableInstruments();
            return values;
        }

        private static string Node(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (var tag in tags)
            {
                if (tag.Key == NodeMetrics.NodeTagName)
                {
                    return (string)tag.Value!;
                }
            }

            throw new InvalidOperationException("node tag missing");
        }
    }

    private sealed class ListLogger(List<string> warnings) : ILogger<NodeSelector>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            warnings.Add($"{logLevel}:{formatter(state, exception)}");
        }
    }

    private sealed class QaHealthCache : INodeHealthCache
    {
        private readonly Dictionary<string, NodeHealthSnapshot> _entries = new(StringComparer.Ordinal);

        public void Set(NodeHealthSnapshot snapshot) => _entries[snapshot.NodeName] = snapshot;

        public bool TryGet(string nodeName, out NodeHealthSnapshot? snapshot)
        {
            var found = _entries.TryGetValue(nodeName, out var value);
            snapshot = value;
            return found;
        }

        public IReadOnlyCollection<NodeHealthSnapshot> All() => _entries.Values;
    }

    private sealed class QaLoadReader(Dictionary<Guid, int> counts) : INodeLoadReader
    {
        public Task<IReadOnlyDictionary<Guid, int>> InFlightByNodeAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, int>>(new Dictionary<Guid, int>(counts));
    }

    private sealed class QaReadDb(List<WorkerNode> nodes) : NightTales.Application.Abstractions.Persistence.IReadDb
    {
        public IQueryable<TEntity> Query<TEntity>()
            where TEntity : class
            => new QaAsyncQueryable<TEntity>((IEnumerable<TEntity>)(object)nodes);
    }

    private sealed class QaAsyncQueryable<T>(IEnumerable<T> source) : IOrderedQueryable<T>, IAsyncQueryProvider, IAsyncEnumerable<T>
    {
        private readonly IQueryable<T> _query = source.AsQueryable();

        public Type ElementType => _query.ElementType;

        public Expression Expression => _query.Expression;

        public IQueryProvider Provider => this;

        public IEnumerator<T> GetEnumerator() => _query.GetEnumerator();

        public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            foreach (var item in _query)
            {
                yield return item;
            }

            await Task.CompletedTask;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public IQueryable CreateQuery(Expression expression) => new QaAsyncQueryable<T>(_query.Provider.CreateQuery<T>(expression));

        public IQueryable<TElement> CreateQuery<TElement>(Expression expression) =>
            new QaAsyncQueryable<TElement>(_query.Provider.CreateQuery<TElement>(expression));

        public object? Execute(Expression expression) => _query.Provider.Execute(expression);

        public TResult Execute<TResult>(Expression expression) => _query.Provider.Execute<TResult>(expression);

        public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken)
        {
            var resultType = typeof(TResult).GetGenericArguments()[0];
            var result = typeof(IQueryProvider)
                .GetMethod(nameof(IQueryProvider.Execute), 1, [typeof(Expression)])!
                .MakeGenericMethod(resultType)
                .Invoke(_query.Provider, [expression]);

            return (TResult)typeof(Task)
                .GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(resultType)
                .Invoke(null, [result])!;
        }
    }
}
