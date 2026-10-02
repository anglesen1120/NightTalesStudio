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

    Task SampleAsync(CancellationToken cancellationToken);
}
