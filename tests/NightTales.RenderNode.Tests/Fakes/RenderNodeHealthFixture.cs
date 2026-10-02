using System.Text.Json;
using NightTales.RenderNode.Contract;

namespace NightTales.RenderNode.Tests.Fakes;

/// <summary>
/// A valid <c>render-node-health.v1</c> body for the scripted health source: the eight blocks of 05.4.1-T2 §4
/// with the types of the frozen fixtures, so a health-driven test never fails on the body instead of on the rule.
/// </summary>
internal static class RenderNodeHealthFixture
{
    private static readonly DateTimeOffset Started = new(2026, 9, 26, 14, 50, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Sampled = new(2026, 9, 26, 14, 50, 10, TimeSpan.Zero);

    internal static RenderNodeHealthV1 Ok() => Build(RenderNodeHealthStatuses.Ok, []);

    internal static RenderNodeHealthV1 Error(params string[] reasons) => Build(RenderNodeHealthStatuses.Error, reasons);

    private static RenderNodeHealthV1 Build(string status, IReadOnlyList<string> reasons)
    {
        var ok = string.Equals(status, RenderNodeHealthStatuses.Ok, StringComparison.Ordinal);

        using var proxy = JsonDocument.Parse(
            """{"schema":"proxy-health.v1","status":"ok","reasons":[],"vieneu":{"ok":true}}""");

        return new RenderNodeHealthV1(
            RenderNodeSchemas.RenderNodeHealth,
            status,
            reasons,
            new CapacityV1(MaxConcurrentTasks: 2, ActiveTasks: ok ? 0 : 1, Accepting: ok),
            new MptHealthV1(
                Ok: true,
                Commit: "3d5f4e421927d61f3eac729cf4b711ac0b69d688",
                ConfigProfile: "node",
                StartedAt: Started,
                WhisperDevice: "cuda",
                VideoCodec: "h264_nvenc",
                ThreadsPerTask: 4),
            proxy.RootElement.Clone(),
            new ImageRelayHealthV1(Ok: true, TokensAvailable: 5, ActivePlans: 0),
            new AssetsHealthV1(
                Ok: true,
                Font: "BeVietnamPro-Bold.ttf",
                MusicRevision: 1,
                MusicMissing: [],
                CheckedAt: Sampled),
            new DiskHealthV1(Ok: true, TaskDirFreeBytes: 120_000_000_000, UsedPct: 41),
            new GpuHealthV1(
                Ok: true,
                UtilPct: 12,
                MemUsedMb: 2048,
                MemTotalMb: 24564,
                TempC: 48,
                SampledAt: Sampled),
            new AdapterInfoV1(Version: "0.1.0", StartedAt: Started));
    }
}
