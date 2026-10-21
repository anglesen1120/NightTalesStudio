namespace NightTales.MptAdapter.Health;

/// <summary>
/// One health probe that owns its <c>SourceCache</c>. <c>HealthSamplerService</c> drives every sampler: a periodic
/// one at <see cref="Interval"/>, a start-only one (null interval) once. A sampler converts its own failures into
/// a failed sample, so the cache always says why a source is not ok (D7 "fail closed with the source's reason").
/// </summary>
internal interface IHealthSampler
{
    string Name { get; }

    TimeSpan? Interval { get; }

    /// <summary>Files or directories whose change triggers an immediate refresh (D7 "on change"); none by default.</summary>
    IReadOnlyList<string> WatchedPaths => [];

    Task SampleAsync(CancellationToken cancellationToken);
}

/// <summary>D7's fixed cadence of the MPT, proxy and disk sources; only the GPU source follows <c>Adapter:GpuSampleSeconds</c>.</summary>
internal static class SamplerCadence
{
    internal static readonly TimeSpan Fixed = TimeSpan.FromSeconds(15);
}
