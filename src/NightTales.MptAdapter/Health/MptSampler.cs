using ErrorOr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NightTales.MptAdapter.Health;

/// <summary>The MPT probe result: liveness, whether the node's key is accepted, and MPT's process start.</summary>
internal sealed record MptProbe(bool Reachable, bool Authorized, DateTimeOffset? StartedAt);

/// <summary>
/// D7's MPT source: <c>GET /ping</c> plus the authenticated <c>GET /api/v1/tasks?page=1&amp;page_size=1</c>, every
/// <c>Adapter:GpuSampleSeconds</c> (15 s). Both calls go through <see cref="MptClient"/>, so the key, the loopback
/// base address and the no-redirect handler are the ones the poller uses. <c>mpt.started_at</c> is
/// <c>MptProcessStartReader</c>'s value (T2), sampled here so a health request never runs <c>systemctl</c>.
/// </summary>
internal sealed class MptSampler(
    MptClient client,
    IMptProcessStartReader startReader,
    IOptions<AdapterOptions> options,
    TimeProvider clock,
    ILogger<MptSampler> log) : IHealthSampler
{
    internal SourceCache<MptProbe> Cache { get; } =
        new(TimeSpan.FromSeconds(options.Value.GpuSampleSeconds), clock);

    public string Name => "mpt";

    public TimeSpan? Interval => Cache.Interval;

    public async Task SampleAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reachable = await client.PingAsync(cancellationToken).ConfigureAwait(false);
            var authorized = false;
            if (reachable)
            {
                var key = await client.CheckApiKeyAsync(cancellationToken).ConfigureAwait(false);
                authorized = !key.IsError;

                // Only a 401/403 is "unauthorized"; any other answer means MPT is not serving properly.
                if (key.IsError && key.FirstError.Type != ErrorType.Unauthorized)
                {
                    reachable = false;
                }
            }

            var startedAt = await startReader.ReadAsync(cancellationToken).ConfigureAwait(false);
            Cache.Set(new MptProbe(reachable, authorized, startedAt));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            log.SamplerFailed(Name, exception.GetType().Name);
            Cache.Set(new MptProbe(false, false, null));
        }
    }
}
