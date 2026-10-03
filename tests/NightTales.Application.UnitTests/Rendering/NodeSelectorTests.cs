using System.Diagnostics.Metrics;
using System.Linq.Expressions;
using ErrorOr;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Common.Errors;
using NightTales.Application.Rendering;
using NightTales.Domain.Rendering;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Rendering;

/// <summary>
/// The dispatch rules of 11 README D13 (11.3.3-T2): the selector is driven with hand-written fakes (no mocking
/// library, 09 README D1) — a counting <see cref="IReadDb"/>, a counting <see cref="INodeLoadReader"/>, an
/// in-memory health cache and a <see cref="FakeClock"/>.
/// </summary>
public sealed class NodeSelectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PicksHighestFreeSlots()
    {
        var rig = new Rig();
        var busy = rig.AddNode("alpha", capacity: 4, inFlight: 3);
        var idle = rig.AddNode("beta", capacity: 4, inFlight: 1);

        var picked = await rig.PickAsync();

        picked.Value.Id.ShouldBe(idle.Id);
        busy.ShouldNotBeNull();
    }

    [Fact]
    public async Task TieBreak_ByName()
    {
        var rig = new Rig();
        rig.AddNode("node-b", capacity: 2, inFlight: 0);
        rig.AddNode("node-a", capacity: 2, inFlight: 0);
        rig.AddNode("Node-0", capacity: 2, inFlight: 0);

        var picked = await rig.PickAsync();

        // Ordinal: an upper-case letter sorts before every lower-case one.
        picked.Value.Name.ShouldBe("Node-0");
    }

    [Fact]
    public async Task TieBreak_TwoEqualNodes_ReturnsLexicographicallyFirst()
    {
        var rig = new Rig();
        rig.AddNode("node-b", capacity: 2, inFlight: 0);
        rig.AddNode("node-a", capacity: 2, inFlight: 0);

        (await rig.PickAsync()).Value.Name.ShouldBe("node-a");
    }

    [Fact]
    public async Task IgnoresStaleHealth_Over90s()
    {
        var rig = new Rig();
        rig.AddNode("stale", capacity: 2, inFlight: 0, pulledAt: Now - TimeSpan.FromSeconds(91));
        rig.AddNode("fresh", capacity: 2, inFlight: 1);

        (await rig.PickAsync()).Value.Name.ShouldBe("fresh");
    }

    [Fact]
    public async Task Health_Exactly90sOld_IsStillFresh()
    {
        var rig = new Rig();
        rig.AddNode("edge", capacity: 1, inFlight: 0, pulledAt: Now - TimeSpan.FromSeconds(90));

        (await rig.PickAsync()).IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task IgnoresNodeWithoutAnyHealthAnswer()
    {
        var rig = new Rig();
        rig.AddNode("silent", capacity: 2, inFlight: 0, withHealth: false);

        (await rig.PickAsync()).FirstError.Code.ShouldBe(ErrorCodes.RenderWorkerUnavailable);
    }

    [Fact]
    public async Task IgnoresAcceptingFalse()
    {
        var rig = new Rig();
        rig.AddNode("closed", capacity: 2, inFlight: 0, accepting: false);

        (await rig.PickAsync()).FirstError.Code.ShouldBe(ErrorCodes.RenderWorkerUnavailable);
    }

    [Fact]
    public async Task IgnoresNotHttpOk_503()
    {
        var rig = new Rig();
        rig.AddNode("proxy-down", capacity: 2, inFlight: 0, httpOk: false);

        (await rig.PickAsync()).FirstError.Code.ShouldBe(ErrorCodes.RenderWorkerUnavailable);
    }

    [Fact]
    public async Task IgnoresNonActiveStates()
    {
        var rig = new Rig();
        var draining = rig.AddNode("draining", capacity: 2, inFlight: 0);
        draining.SetOperatorState(WorkerNodeState.Draining).IsError.ShouldBeFalse();
        var down = rig.AddNode("down", capacity: 2, inFlight: 0);
        down.MarkDown(Now).IsError.ShouldBeFalse();

        (await rig.PickAsync()).FirstError.Code.ShouldBe(ErrorCodes.RenderWorkerUnavailable);
    }

    [Fact]
    public async Task IgnoresNodeWithZeroFreeSlots()
    {
        var rig = new Rig();
        rig.AddNode("full", capacity: 2, inFlight: 2);
        var open = rig.AddNode("open", capacity: 2, inFlight: 1);

        (await rig.PickAsync()).Value.Id.ShouldBe(open.Id);
    }

    [Fact]
    public async Task CapsByHealthMaxConcurrentTasks()
    {
        var rig = new Rig();

        // capacity 2 in the registry, but the node itself only accepts 1 and already runs 1.
        rig.AddNode("capped", capacity: 2, inFlight: 1, maxConcurrentTasks: 1);

        (await rig.PickAsync()).FirstError.Code.ShouldBe(ErrorCodes.RenderWorkerUnavailable);
    }

    [Fact]
    public async Task NoCandidate_ReturnsRenderWorkerUnavailable()
    {
        var rig = new Rig();

        var picked = await rig.PickAsync();

        picked.IsError.ShouldBeTrue();
        picked.FirstError.Code.ShouldBe("render_worker_unavailable");
    }

    [Fact]
    public async Task IncrementsDispatchUnavailableCounter()
    {
        var rig = new Rig();
        rig.AddNode("full", capacity: 1, inFlight: 1);
        var total = 0L;
        using var listener = Listen(rig.Metrics, NodeMetrics.DispatchUnavailableInstrumentName, (value, _) => total += value);

        await rig.PickAsync();
        total.ShouldBe(1);

        await rig.PickAsync();
        total.ShouldBe(2);

        rig.AddNode("free", capacity: 1, inFlight: 0);
        await rig.PickAsync();
        total.ShouldBe(2);
    }

    [Fact]
    public async Task FreeSlotsNeverNegative()
    {
        // An over-committed node (in_flight above the cap, e.g. after the cap was lowered) reports 0, not -1.
        NodeMetrics.FreeSlots(capacity: 2, maxConcurrentTasks: 2, inFlight: 5).ShouldBe(0);
        NodeMetrics.FreeSlots(capacity: 2, maxConcurrentTasks: 1, inFlight: 1).ShouldBe(0);
        NodeMetrics.FreeSlots(capacity: 4, maxConcurrentTasks: 2, inFlight: 0).ShouldBe(2);

        var rig = new Rig();
        rig.AddNode("over", capacity: 2, inFlight: 5);
        await rig.PickAsync();
        var gauge = rig.ReadGauge(NodeMetrics.FreeSlotsInstrumentName);

        gauge["over"].ShouldBe(0);
    }

    [Fact]
    public async Task FreeSlotsGauge_ReflectsCacheAndLoad_WithoutTouchingTheDatabase()
    {
        var rig = new Rig();
        rig.AddNode("a", capacity: 4, inFlight: 1, maxConcurrentTasks: 2);
        rig.AddNode("b", capacity: 1, inFlight: 0);
        await rig.PickAsync();
        var queriesAfterPick = rig.Db.Queries;
        var loadReadsAfterPick = rig.Load.Reads;

        var gauge = rig.ReadGauge(NodeMetrics.FreeSlotsInstrumentName);

        gauge["a"].ShouldBe(1);
        gauge["b"].ShouldBe(1);
        rig.Db.Queries.ShouldBe(queriesAfterPick);
        rig.Load.Reads.ShouldBe(loadReadsAfterPick);
    }

    [Fact]
    public async Task PickJudgesFreshness_AtTheInstantTheReadsFinished()
    {
        // CR-2: a snapshot 89 s old when the pick starts is 91 s old when the (slow) load read returns,
        // so it is stale at the decision and must be refused.
        var rig = new Rig();
        rig.AddNode("aging", capacity: 2, inFlight: 0, pulledAt: Now - TimeSpan.FromSeconds(89));
        rig.Load.OnRead = () => rig.Clock.Advance(TimeSpan.FromSeconds(2));

        (await rig.PickAsync()).FirstError.Code.ShouldBe(ErrorCodes.RenderWorkerUnavailable);
    }

    [Fact]
    public async Task FreeSlotsGauge_ReportsZeroForNodesFailingTheHealthPredicate()
    {
        // CR-3: stale, refusing and 503 nodes advertise no slot; a healthy node keeps its real value.
        var rig = new Rig();
        rig.AddNode("stale", capacity: 4, inFlight: 0, pulledAt: Now - TimeSpan.FromSeconds(91));
        rig.AddNode("closed", capacity: 4, inFlight: 0, accepting: false);
        rig.AddNode("down503", capacity: 4, inFlight: 0, httpOk: false);
        rig.AddNode("healthy", capacity: 4, inFlight: 1);
        await rig.PickAsync();

        var gauge = rig.ReadGauge(NodeMetrics.FreeSlotsInstrumentName);

        gauge["stale"].ShouldBe(0);
        gauge["closed"].ShouldBe(0);
        gauge["down503"].ShouldBe(0);
        gauge["healthy"].ShouldBe(3);
    }

    [Fact]
    public async Task FreeSlotsGauge_DropsToZeroOnceTheSnapshotAgesOut()
    {
        var rig = new Rig();
        rig.AddNode("quiet", capacity: 4, inFlight: 0);
        await rig.PickAsync();
        rig.ReadGauge(NodeMetrics.FreeSlotsInstrumentName)["quiet"].ShouldBe(4);

        rig.Clock.Advance(TimeSpan.FromSeconds(91));

        rig.ReadGauge(NodeMetrics.FreeSlotsInstrumentName)["quiet"].ShouldBe(0);
    }

    [Fact]
    public void CacheGauges_ReadTheCachedGpuAndDiskBlocks()
    {
        var rig = new Rig();
        rig.AddNode("gpu", capacity: 1, inFlight: 0);

        rig.ReadGauge(NodeMetrics.GpuUtilInstrumentName)["gpu"].ShouldBe(37);
        rig.ReadGauge(NodeMetrics.GpuMemUsedInstrumentName)["gpu"].ShouldBe(2048.0 * 1024 * 1024);
        rig.ReadGauge(NodeMetrics.GpuTempInstrumentName)["gpu"].ShouldBe(64);
        rig.ReadGauge(NodeMetrics.DiskUsedInstrumentName)["gpu"].ShouldBe(41);
    }

    [Fact]
    public async Task PickIssuesOneNodeQueryAndOneGroupedLoadRead()
    {
        var rig = new Rig();
        for (var i = 0; i < 5; i++)
        {
            rig.AddNode($"n{i}", capacity: 2, inFlight: i % 2);
        }

        await rig.PickAsync();

        rig.Db.Queries.ShouldBe(1);
        rig.Load.Reads.ShouldBe(1);
    }

    [Fact]
    public async Task SelectorAndDriftReaderShareOneLoadImplementation()
    {
        // The poller's drift check and the selector both read INodeLoadReader: the same fixture yields the
        // same derived count through the port the selector calls.
        var rig = new Rig();
        var node = rig.AddNode("a", capacity: 4, inFlight: 3);

        var derived = await rig.Load.InFlightByNodeAsync(CancellationToken.None);
        await rig.PickAsync();

        derived[node.Id].ShouldBe(3);
        NodeMetrics.FreeSlots(node.Capacity, 4, derived[node.Id]).ShouldBe(1);
    }

    private static MeterListener Listen(NodeMetrics metrics, string instrument, Action<long, KeyValuePair<string, object?>[]> onCount)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (candidate, l) =>
        {
            if (ReferenceEquals(candidate.Meter, metrics.Meter) && candidate.Name == instrument)
            {
                l.EnableMeasurementEvents(candidate);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) => onCount(value, tags.ToArray()));
        listener.Start();
        return listener;
    }

    private sealed class Rig
    {
        private readonly InMemoryNodeHealthCache _cache = new();
        private readonly FakeClock _clock = new(Now);

        public Rig()
        {
            Db = new CountingReadDb(_nodes);
            Load = new CountingLoadReader(_inFlight);
            Metrics = new NodeMetrics(_cache, _clock, new RenderNodesOptions());
            Selector = new NodeSelector(
                Db,
                _cache,
                Load,
                _clock,
                new RenderNodesOptions(),
                Metrics,
                new NodeDispatchLogGate(),
                NullLogger<NodeSelector>.Instance);
        }

        public FakeClock Clock => _clock;

        public CountingReadDb Db { get; }

        public CountingLoadReader Load { get; }

        public NodeMetrics Metrics { get; }

        private NodeSelector Selector { get; }

        private readonly List<WorkerNode> _nodes = [];
        private readonly Dictionary<Guid, int> _inFlight = [];

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

        public Task<ErrorOr<WorkerNode>> PickAsync() => Selector.PickAsync(CancellationToken.None);

        public Dictionary<string, double> ReadGauge(string instrument)
        {
            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            using var listener = new MeterListener();
            listener.InstrumentPublished = (candidate, l) =>
            {
                if (ReferenceEquals(candidate.Meter, Metrics.Meter) && candidate.Name == instrument)
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

    private sealed class InMemoryNodeHealthCache : INodeHealthCache
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

    private sealed class CountingLoadReader(Dictionary<Guid, int> counts) : INodeLoadReader
    {
        public int Reads { get; private set; }

        /// <summary>Runs inside each read, so a test can let time pass while the selector awaits the database.</summary>
        public Action? OnRead { get; set; }

        public Task<IReadOnlyDictionary<Guid, int>> InFlightByNodeAsync(CancellationToken cancellationToken)
        {
            Reads++;
            OnRead?.Invoke();
            return Task.FromResult<IReadOnlyDictionary<Guid, int>>(new Dictionary<Guid, int>(counts));
        }
    }

    private sealed class CountingReadDb(List<WorkerNode> nodes) : IReadDb
    {
        public int Queries { get; private set; }

        public IQueryable<TEntity> Query<TEntity>()
            where TEntity : class
        {
            Queries++;
            return new AsyncEnumerable<TEntity>((IEnumerable<TEntity>)(object)nodes);
        }
    }

    // The EF async extensions (ToListAsync) need an IAsyncQueryProvider; this is the minimal in-memory one.
    private sealed class AsyncEnumerable<T>(IEnumerable<T> source) : IOrderedQueryable<T>, IAsyncQueryProvider, IAsyncEnumerable<T>
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

        public IQueryable CreateQuery(Expression expression) => new AsyncEnumerable<T>(_query.Provider.CreateQuery<T>(expression));

        public IQueryable<TElement> CreateQuery<TElement>(Expression expression) =>
            new AsyncEnumerable<TElement>(_query.Provider.CreateQuery<TElement>(expression));

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
