using ErrorOr;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;

namespace NightTales.RenderNode.Tests.Fakes;

/// <summary>The scripted image-plan seam: every register/release is counted and one report can be scripted.</summary>
internal sealed class FakeImagePlanSink : IImagePlanSink
{
    internal int RegisterCount { get; private set; }

    internal int ReleaseCount { get; private set; }

    internal ImagePlanV1? LastPlan { get; private set; }

    internal Guid? LastReleasedTaskId { get; private set; }

    /// <summary>What <see cref="BuildReport"/> answers (null = nothing to report).</summary>
    internal ImageReportV1? Report { get; set; }

    /// <inheritdoc />
    public void Register(Guid taskId, ImagePlanV1 plan)
    {
        RegisterCount++;
        LastPlan = plan;
    }

    /// <inheritdoc />
    public void Release(Guid taskId)
    {
        ReleaseCount++;
        LastReleasedTaskId = taskId;
    }

    /// <inheritdoc />
    public ImageReportV1? BuildReport(Guid taskId) => Report;
}

/// <summary>
/// The scripted health seam of 11 README D7: a settable body and a switch that makes the source throw, so
/// "health not ok → 503 and no insert" and "source throws → fail closed" are both reachable.
/// </summary>
internal sealed class ScriptedHealthSource : INodeHealthSource
{
    internal RenderNodeHealthV1? Health { get; set; }

    internal bool Throws { get; set; }

    internal int CallCount { get; private set; }

    /// <inheritdoc />
    public ValueTask<RenderNodeHealthV1> GetAsync(CancellationToken cancellationToken)
    {
        CallCount++;
        if (Throws)
        {
            throw new InvalidOperationException("health source unavailable");
        }

        return ValueTask.FromResult(Health ?? RenderNodeHealthFixture.Ok());
    }
}

/// <summary>The fifth seam (12 CR-19): scripted probe answers plus a counter, so the route's wiring is asserted.</summary>
internal sealed class FakeImageProbeBackend : IImageProbeBackend
{
    internal int CallCount { get; private set; }

    internal ErrorOr<ProbeImageResultV1> Result { get; set; } = new ProbeImageResultV1(null, true, null);

    /// <inheritdoc />
    public Task<ErrorOr<ProbeImageResultV1>> ProbeAsync(CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(Result);
    }
}
