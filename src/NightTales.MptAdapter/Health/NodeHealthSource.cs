using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;

namespace NightTales.MptAdapter.Health;

/// <summary>One probe result and the instant it was taken.</summary>
internal sealed record Sample<T>(T Value, DateTimeOffset SampledAt);

/// <summary>
/// The cached holder of one health source (11 README D7): the last value, its <c>sampled_at</c> and the interval
/// it is refreshed at. A periodic source older than 3 × its interval — or never sampled — is failed (T40);
/// a start-only source (<paramref name="interval"/> null) is judged by its probe result alone, but a source that
/// was never probed is failed too, so a missing probe can never look healthy.
/// </summary>
internal sealed class SourceCache<T>(TimeSpan? interval, TimeProvider clock)
    where T : class
{
    /// <summary>How many intervals a periodic sample may age before it counts as failed (D7, T40).</summary>
    internal const int StaleAfterIntervals = 3;

    private volatile Sample<T>? _sample;

    internal TimeSpan? Interval { get; } = interval;

    internal Sample<T>? Current => _sample;

    internal void Set(T value) => _sample = new Sample<T>(value, clock.GetUtcNow());

    /// <summary>True when there is no usable sample: never taken, or older than 3 × the interval.</summary>
    internal bool IsStale =>
        _sample is not { } sample
        || (Interval is { } every && clock.GetUtcNow() - sample.SampledAt > every * StaleAfterIntervals);
}

/// <summary>
/// The adapter's <see cref="INodeHealthSource"/> (11.3.5-T4, D7): composes <c>render-node-health.v1</c> from the
/// per-source caches and nothing else. It never starts a probe — the sampler service owns every probe — so a
/// health request costs no MPT, proxy, <c>nvidia-smi</c> or script call (D19 Q1). Only the local task count and
/// the in-process relay state are read per request.
/// </summary>
internal sealed class NodeHealthSource(
    MptSampler mpt,
    ProxySampler proxy,
    DiskSampler disk,
    GpuSampler gpu,
    AssetsSampler assets,
    CodecProbe codec,
    ConfigProbe config,
    IImageRelayHealthProvider relay,
    ITaskStore store,
    IOptions<AdapterOptions> options,
    IOptions<RenderNodeServerOptions> serverOptions,
    BundleInfo bundle,
    TimeProvider clock) : INodeHealthSource
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private static readonly string Version =
        typeof(NodeHealthSource).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    private readonly DateTimeOffset _startedAt = clock.GetUtcNow();

    public async ValueTask<RenderNodeHealthV1> GetAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var reasons = new List<string>();

        // MPT: stale or never probed counts as unreachable; an answered probe keeps its own reason.
        var mptSample = mpt.Cache.Current;
        var mptProbe = mptSample?.Value;
        var mptOk = !mpt.Cache.IsStale && mptProbe is { Reachable: true, Authorized: true };
        if (mpt.Cache.IsStale || mptProbe is { Reachable: false })
        {
            reasons.Add(NodeHealthReasons.MptUnreachable);
        }
        else if (mptProbe is { Authorized: false })
        {
            reasons.Add(NodeHealthReasons.MptUnauthorized);
        }

        // Proxy: the body is kept byte-for-byte; a stale or not-ok body is tts_proxy_not_ok.
        var proxySample = proxy.Cache.Current?.Value;
        if (proxy.Cache.IsStale || proxySample is not { Ok: true })
        {
            reasons.Add(NodeHealthReasons.TtsProxyNotOk);
        }

        var relayHealth = relay.Read();
        if (!relayHealth.Ok)
        {
            reasons.Add(NodeHealthReasons.ImageRelayNotOk);
        }

        // Assets: a stale job is an unknown state, so it fails closed as config_mismatch (never silently ok).
        var assetsProbe = assets.Cache.Current?.Value;
        var assetsHealth = assetsProbe?.Health ?? new AssetsHealthV1(false, null, null, [], null);
        if (assets.Cache.IsStale || assetsProbe is null)
        {
            reasons.Add(NodeHealthReasons.ConfigMismatch);
            assetsHealth = assetsHealth with { Ok = false };
        }
        else
        {
            if (!assetsProbe.FontOk)
            {
                reasons.Add(NodeHealthReasons.FontMissing);
            }

            if (!assetsProbe.MusicOk)
            {
                reasons.Add(NodeHealthReasons.MusicMissing);
            }
        }

        var diskProbe = disk.Cache.Current?.Value;
        var diskHealth = diskProbe is null || disk.Cache.IsStale
            ? new DiskHealthV1(false, 0, 100)
            : diskProbe;
        if (disk.Cache.IsStale || !diskHealth.Ok)
        {
            reasons.Add(NodeHealthReasons.DiskLow);
        }

        var gpuProbe = gpu.Cache.Current?.Value;
        var gpuHealth = gpuProbe is null || gpu.Cache.IsStale
            ? new GpuHealthV1(false, null, null, null, null, gpuProbe?.SampledAt)
            : gpuProbe;
        if (gpu.Cache.IsStale || !gpuHealth.Ok)
        {
            reasons.Add(NodeHealthReasons.GpuUnavailable);
        }

        // Start-only sources are judged by their probe result only (T40).
        var configProbe = config.Cache.Current?.Value;
        if (configProbe is not { Ok: true })
        {
            reasons.Add(NodeHealthReasons.ConfigMismatch);
        }

        var codecProbe = codec.Cache.Current?.Value;
        if (codecProbe is not { Available: true })
        {
            reasons.Add(NodeHealthReasons.CodecUnavailable);
        }

        var distinct = reasons.Distinct(StringComparer.Ordinal).ToList();
        var ok = distinct.Count == 0;

        var max = serverOptions.Value.MaxConcurrentTasks;
        var active = await store.CountNonTerminalAsync(cancellationToken).ConfigureAwait(false);

        return new RenderNodeHealthV1(
            Schema: RenderNodeSchemas.RenderNodeHealth,
            Status: ok ? RenderNodeHealthStatuses.Ok : RenderNodeHealthStatuses.Error,
            Reasons: distinct,
            Capacity: new CapacityV1(max, active, ok && active < max),
            Mpt: new MptHealthV1(
                Ok: mptOk,
                Commit: bundle.MptCommit,
                ConfigProfile: configProbe?.Profile,
                StartedAt: mptProbe?.StartedAt,
                WhisperDevice: configProbe?.WhisperDevice,
                VideoCodec: settings.VideoCodec,
                ThreadsPerTask: settings.ResolveMptThreads(max)),
            TtsProxy: proxySample?.Body ?? EmptyObject,
            ImageRelay: relayHealth,
            Assets: assetsHealth,
            Disk: diskHealth,
            Gpu: gpuHealth,
            Adapter: new AdapterInfoV1(Version, _startedAt));
    }
}
