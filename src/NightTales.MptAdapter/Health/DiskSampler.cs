using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.RenderNode.Contract;

namespace NightTales.MptAdapter.Health;

/// <summary>The two numbers every disk figure derives from.</summary>
internal readonly record struct DiskFigures(long TotalBytes, long FreeBytes);

/// <summary>The one filesystem-statistics call behind every disk figure (D20 H16): no second stat, no <c>du</c>.</summary>
internal interface IDiskStats
{
    DiskFigures Read(string path);
}

/// <summary><see cref="DriveInfo"/> is <c>statvfs</c> on Linux.</summary>
internal sealed class SystemDiskStats : IDiskStats
{
    public DiskFigures Read(string path)
    {
        var drive = new DriveInfo(path);
        return new DiskFigures(drive.TotalSize, drive.AvailableFreeSpace);
    }
}

/// <summary>
/// D7's disk source: one <see cref="IDiskStats.Read"/> on <c>Adapter:DiskPath</c> every 15 s. <c>used_pct</c> is
/// floored, and <c>disk_low</c> is <c>used_pct ≥ 85</c>. A path that cannot be read is a failed sample
/// (<c>ok = false</c>, the <c>disk_low</c> reason) — a node that cannot see its disk must not take work.
/// </summary>
internal sealed class DiskSampler(
    IDiskStats stats,
    IOptions<AdapterOptions> options,
    TimeProvider clock,
    ILogger<DiskSampler> log) : IHealthSampler
{
    /// <summary>D7: the used percentage from which the node reports <c>disk_low</c>.</summary>
    internal const int LowAtUsedPct = 85;

    internal SourceCache<DiskHealthV1> Cache { get; } =
        new(SamplerCadence.Fixed, clock);

    public string Name => "disk";

    public TimeSpan? Interval => Cache.Interval;

    public Task SampleAsync(CancellationToken cancellationToken)
    {
        try
        {
            var figures = stats.Read(options.Value.DiskPath);
            var used = figures.TotalBytes <= 0
                ? 100
                : (int)Math.Clamp((figures.TotalBytes - figures.FreeBytes) * 100 / figures.TotalBytes, 0, 100);
            Cache.Set(new DiskHealthV1(used < LowAtUsedPct, figures.FreeBytes, used));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log.SamplerFailed(Name, exception.GetType().Name);
            Cache.Set(new DiskHealthV1(false, 0, 100));
        }

        return Task.CompletedTask;
    }
}
