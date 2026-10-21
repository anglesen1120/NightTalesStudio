using System.Text.Json;
using NightTales.RenderNode.Contract;

namespace NightTales.TestKit.Rendering;

/// <summary>
/// Builds <c>render-node-health.v1</c> bodies for the fake node (11.3.3-T1 plan §9, 05 README D4): they are
/// serialised from the contract records themselves, so a body the tests serve is exactly a shape the
/// contract defines and the poller's <see cref="RenderNodeJson.Tolerant"/> reader accepts.
/// </summary>
public static class RenderNodeHealthBodies
{
    /// <summary>A 200/503-shaped <c>ok</c> body with no reasons.</summary>
    /// <param name="activeTasks">The reported <c>capacity.active_tasks</c> (drives the drift check).</param>
    /// <param name="maxConcurrentTasks">The reported <c>capacity.max_concurrent_tasks</c>.</param>
    /// <param name="gpuUtilPct">The reported GPU utilisation, or <c>null</c>.</param>
    public static string Ok(int activeTasks = 1, int maxConcurrentTasks = 2, int? gpuUtilPct = 12) =>
        Serialize("ok", [], activeTasks, maxConcurrentTasks, gpuUtilPct);

    /// <summary>A 503-shaped body whose <c>status</c> is <c>error</c> and which carries <paramref name="reasons"/>.</summary>
    /// <param name="reasons">The wire reasons, from <see cref="NodeHealthReasons"/>.</param>
    public static string Degraded(params string[] reasons) =>
        Serialize("error", reasons, activeTasks: 0, maxConcurrentTasks: 2, gpuUtilPct: null);

    /// <summary>A 200 body that is not valid JSON, so the poller treats it as no answer.</summary>
    public const string Unparseable = "{ this is not json";

    private static string Serialize(
        string status,
        IReadOnlyList<string> reasons,
        int activeTasks,
        int maxConcurrentTasks,
        int? gpuUtilPct)
    {
        using var proxy = JsonDocument.Parse("""{"schema":"proxy-health.v1","status":"ok"}""");

        var health = new RenderNodeHealthV1(
            Schema: "render-node-health.v1",
            Status: status,
            Reasons: reasons,
            Capacity: new CapacityV1(maxConcurrentTasks, activeTasks, Accepting: status == "ok"),
            Mpt: new MptHealthV1(true, "3d5f4e421927d61f3eac729cf4b711ac0b69d688", "node", null, "cuda", "h264_nvenc", 4),
            TtsProxy: proxy.RootElement.Clone(),
            ImageRelay: new ImageRelayHealthV1(true, 5, 0),
            Assets: new AssetsHealthV1(true, "BeVietnamPro-Bold.ttf", 1, [], null),
            Disk: new DiskHealthV1(true, 120_000_000_000, 41),
            Gpu: new GpuHealthV1(gpuUtilPct is not null, gpuUtilPct, 2048, 24564, 48, null),
            Adapter: new AdapterInfoV1("0.1.0", DateTimeOffset.UnixEpoch));

        return JsonSerializer.Serialize(health, RenderNodeJson.Tolerant);
    }
}
