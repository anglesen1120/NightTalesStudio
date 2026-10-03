using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.RenderNode.Contract;

namespace NightTales.MptAdapter.Health;

/// <summary>
/// D7's GPU source: <c>nvidia-smi --query-gpu=utilization.gpu,memory.used,memory.total,temperature.gpu
/// --format=csv,noheader,nounits</c> every 15 s, as a list of arguments (no shell). A missing binary, a failing
/// exit, a timeout or any line that is not four integers (an error string, <c>[N/A]</c>) is
/// <c>ok = false</c> with null values and the <c>gpu_unavailable</c> reason; <c>sampled_at</c> is always set.
/// Only the first GPU line is read: a node has one.
/// </summary>
internal sealed class GpuSampler(
    IProcessRunner runner,
    IOptions<AdapterOptions> options,
    TimeProvider clock,
    ILogger<GpuSampler> log) : IHealthSampler
{
    internal static readonly string[] Arguments =
    [
        "--query-gpu=utilization.gpu,memory.used,memory.total,temperature.gpu",
        "--format=csv,noheader,nounits",
    ];

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    internal SourceCache<GpuHealthV1> Cache { get; } =
        new(TimeSpan.FromSeconds(options.Value.GpuSampleSeconds), clock);

    public string Name => "gpu";

    public TimeSpan? Interval => Cache.Interval;

    public async Task SampleAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        try
        {
            var result = await runner
                .RunAsync(options.Value.NvidiaSmiPath, Arguments, ProbeTimeout, cancellationToken)
                .ConfigureAwait(false);
            Cache.Set(result.ExitCode == 0 && Parse(result.StandardOutput, now) is { } parsed ? parsed : Failed(now));
        }
        catch (Exception exception) when (exception is Win32Exception or TimeoutException or IOException
            or InvalidOperationException)
        {
            log.SamplerFailed(Name, exception.GetType().Name);
            Cache.Set(Failed(now));
        }
    }

    /// <summary>The CSV parse of the first line; null when it is not four integers.</summary>
    internal static GpuHealthV1? Parse(string output, DateTimeOffset sampledAt)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        var fields = line?.Split(',', StringSplitOptions.TrimEntries);
        if (fields is not { Length: 4 }
            || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var util)
            || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var used)
            || !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var total)
            || !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var temp))
        {
            return null;
        }

        return new GpuHealthV1(true, util, used, total, temp, sampledAt);
    }

    private static GpuHealthV1 Failed(DateTimeOffset sampledAt) => new(false, null, null, null, null, sampledAt);
}
