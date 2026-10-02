using Microsoft.Extensions.Options;
using NightTales.MockMpt.Fixtures;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;

namespace NightTales.MockMpt.Health;

/// <summary>
/// The mock's health seam (11 README D7): a cached <c>render-node-health.v1</c> body built from in-process
/// values only — never MPT, ffmpeg or <c>nvidia-smi</c> (D19 Q1). The capacity block is live
/// (<c>max_concurrent_tasks</c> from <c>MOCK_MAX_CONCURRENT</c>, <c>active_tasks</c> from the fake backend);
/// every other block is the frozen <c>render-node-health.v1.ok</c> fixture, so the mock reports the same shapes
/// a real node does.
/// </summary>
/// <remarks>
/// <c>MOCK_FIXTURE</c> naming a health fixture replays that body verbatim instead, which is how a developer
/// sees the <c>proxy-down</c>/<c>disk-low</c> shapes without breaking anything.
/// </remarks>
public sealed class MockNodeHealthSource : INodeHealthSource
{
    private readonly RenderNodeHealthV1 _healthy;
    private readonly RenderNodeHealthV1? _replay;
    private readonly MockHealthState _state;
    private readonly int _maxConcurrentTasks;

    public MockNodeHealthSource(
        IOptions<RenderNodeServerOptions> options,
        MockHealthState state,
        FixtureSet fixtures,
        MockMptOptions mockOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(fixtures);
        ArgumentNullException.ThrowIfNull(mockOptions);

        _healthy = fixtures.Health(FixtureSet.OkHealthId)
            ?? throw new MockStartupException($"fixture '{FixtureSet.OkHealthId}.json' is missing (05 D14)");
        _replay = fixtures.Health(mockOptions.Fixture);
        _state = state;
        _maxConcurrentTasks = options.Value.MaxConcurrentTasks;
    }

    /// <inheritdoc />
    public ValueTask<RenderNodeHealthV1> GetAsync(CancellationToken cancellationToken)
    {
        if (_replay is not null)
        {
            return ValueTask.FromResult(_replay);
        }

        // A healthy mock: `status = ok` with an empty reason list, so dispatch accepts it (11 README D13).
        var body = _healthy with
        {
            Capacity = new CapacityV1(_maxConcurrentTasks, _state.ActiveTasks, Accepting: true),
        };
        return ValueTask.FromResult(body);
    }
}
