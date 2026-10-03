using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.MptAdapter.Health;
using NightTales.MptAdapter.Tests.Fakes;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using Shouldly;
using Xunit;

namespace NightTales.MptAdapter.Tests;

/// <summary>D7 / D19 Q1: <c>/v1/health</c> reads caches only; a source ages out after 3 x its interval (T40).</summary>
public sealed class HealthCacheTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Health_ReadsCachesOnly_NoProbeCalls()
    {
        await using var rig = await HealthRig.StartAsync();
        await rig.SampleAllAsync();
        var before = Counts(rig);

        for (var i = 0; i < 5; i++)
        {
            await rig.Source.GetAsync(CancellationToken.None);
        }

        Counts(rig).ShouldBe(before);
    }

    [Fact]
    public async Task SecondCallWithinInterval_NoProbe()
    {
        await using var rig = await HealthRig.StartAsync();
        await rig.SampleAllAsync();
        var first = await rig.Source.GetAsync(CancellationToken.None);
        var before = Counts(rig);

        rig.Clock.Advance(Interval - TimeSpan.FromSeconds(1));
        var second = await rig.Source.GetAsync(CancellationToken.None);

        Counts(rig).ShouldBe(before);
        second.Status.ShouldBe(first.Status);
    }

    [Fact]
    public async Task StaleSource_CountsFailed_AfterThreeIntervals()
    {
        await using var rig = await HealthRig.StartAsync();
        await rig.SampleAllAsync();

        rig.Clock.Advance(Interval * 3);
        (await rig.Source.GetAsync(CancellationToken.None)).Reasons.ShouldBeEmpty("exactly 3 intervals is still fresh");

        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        var stale = await rig.Source.GetAsync(CancellationToken.None);

        stale.Reasons.ShouldContain(NodeHealthReasons.MptUnreachable);
        stale.Reasons.ShouldContain(NodeHealthReasons.TtsProxyNotOk);
        stale.Reasons.ShouldContain(NodeHealthReasons.DiskLow);
        stale.Reasons.ShouldContain(NodeHealthReasons.GpuUnavailable);
        stale.Reasons.ShouldNotContain(NodeHealthReasons.ConfigMismatch, "assets refresh every 600 s, not 15 s");
        stale.Status.ShouldBe(RenderNodeHealthStatuses.Error);
    }

    [Fact]
    public async Task StatusOk_IffNoReasons()
    {
        await using var rig = await HealthRig.StartAsync();
        await rig.SampleAllAsync();

        var ok = await rig.Source.GetAsync(CancellationToken.None);
        ok.Reasons.ShouldBeEmpty();
        ok.Status.ShouldBe(RenderNodeHealthStatuses.Ok);
        ok.Capacity.Accepting.ShouldBeTrue();

        rig.Proxy.Throw = true;
        await rig.ProxySampler.SampleAsync(CancellationToken.None);
        var error = await rig.Source.GetAsync(CancellationToken.None);
        error.Reasons.ShouldNotBeEmpty();
        error.Status.ShouldBe(RenderNodeHealthStatuses.Error);
        error.Capacity.Accepting.ShouldBeFalse();
    }

    [Fact]
    public async Task HttpStatus200Or503()
    {
        await using var rig = await HealthRig.StartAsync();
        await rig.SampleAllAsync();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.None);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<ITaskStore>(new SqliteTaskStore(
            new SqliteStoreOptions(rig.Options.TasksDbPath),
            LoggerFactory.Create(_ => { }).CreateLogger<SqliteTaskStore>()));
        builder.Services.AddSingleton<INodeHealthSource>(rig.Source);
        builder.Services.AddSingleton<IRenderBackend>(new NoBackend());
        builder.Services.AddSingleton<IImagePlanSink>(new FakeImagePlanSink());
        builder.Services.AddRenderNodeServer(builder.Configuration.GetSection(RenderNodeServerOptions.SectionName));
        builder.Services.AddSingleton<IOptions<RenderNodeServerOptions>>(
            Microsoft.Extensions.Options.Options.Create(
                new RenderNodeServerOptions { ApiKey = AdapterHarness.AdapterApiKey, MaxConcurrentTasks = 2 }));

        await using var app = builder.Build();
        app.UseRenderNodeServer();
        app.MapRenderNodeV1();
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var http = new HttpClient { BaseAddress = new Uri(address) };

        (await GetHealthAsync(http)).ShouldBe(HttpStatusCode.OK);

        rig.Proxy.Throw = true;
        await rig.ProxySampler.SampleAsync(CancellationToken.None);
        (await GetHealthAsync(http)).ShouldBe(HttpStatusCode.ServiceUnavailable);

        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    // Built by concatenation so the gitleaks pre-commit rule does not read fixture values as secrets.
    private static readonly string KeyA = "env-key-a-" + new string('a', 26);
    private static readonly string KeyB = "env-key-b-" + new string('b', 26);
    private static readonly string KeyS = "section-key-" + new string('s', 26);

    /// <summary>CR-1: the middleware authenticates with the key and the NEXT key the startup checks validated.</summary>
    [Fact]
    public async Task EnvKeys_AuthenticateThroughMiddleware_WhenSectionIsEmpty()
    {
        await using var rig = await HealthRig.StartAsync();
        await rig.SampleAllAsync();
        await using var app = await StartWithKeysAsync(
            rig, [], new() { ["ADAPTER_API_KEY"] = KeyA, ["ADAPTER_API_KEY_NEXT"] = KeyB });
        using var http = HttpFor(app);

        (await GetHealthAsync(http, KeyA)).ShouldBe(HttpStatusCode.OK);
        (await GetHealthAsync(http, KeyB)).ShouldBe(HttpStatusCode.OK);
        (await GetHealthAsync(http, "wrong-key-0123456789abcdef0123456789")).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SectionKey_WinsOverEnv()
    {
        await using var rig = await HealthRig.StartAsync();
        await rig.SampleAllAsync();
        await using var app = await StartWithKeysAsync(
            rig, new() { ["RenderNodeServer:ApiKey"] = KeyS }, new() { ["ADAPTER_API_KEY"] = KeyA });
        using var http = HttpFor(app);

        (await GetHealthAsync(http, KeyS)).ShouldBe(HttpStatusCode.OK);
        (await GetHealthAsync(http, KeyA)).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SectionKey_WithEnvNext_RotationWorks()
    {
        await using var rig = await HealthRig.StartAsync();
        await rig.SampleAllAsync();
        await using var app = await StartWithKeysAsync(
            rig, new() { ["RenderNodeServer:ApiKey"] = KeyS }, new() { ["ADAPTER_API_KEY_NEXT"] = KeyB });
        using var http = HttpFor(app);

        (await GetHealthAsync(http, KeyS)).ShouldBe(HttpStatusCode.OK);
        (await GetHealthAsync(http, KeyB)).ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<WebApplication> StartWithKeysAsync(
        HealthRig rig, Dictionary<string, string?> section, Dictionary<string, string?> environment)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.None);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(section);
        builder.Services.AddSingleton<ITaskStore>(new SqliteTaskStore(
            new SqliteStoreOptions(rig.Options.TasksDbPath),
            LoggerFactory.Create(_ => { }).CreateLogger<SqliteTaskStore>()));
        builder.Services.AddSingleton<INodeHealthSource>(rig.Source);
        builder.Services.AddSingleton<IRenderBackend>(new NoBackend());
        builder.Services.AddSingleton<IImagePlanSink>(new FakeImagePlanSink());
        builder.Services.AddRenderNodeServer(builder.Configuration);
        builder.Services.AddServerKeyEnvDefaults(name => environment.GetValueOrDefault(name));

        var app = builder.Build();
        app.UseRenderNodeServer();
        app.MapRenderNodeV1();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static HttpClient HttpFor(WebApplication app) => new()
    {
        BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()),
    };

    private static async Task<HttpStatusCode> GetHealthAsync(HttpClient http, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RenderNodeRoutes.Health);
        request.Headers.TryAddWithoutValidation("x-api-key", key);
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private static async Task<HttpStatusCode> GetHealthAsync(HttpClient http)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RenderNodeRoutes.Health);
        request.Headers.TryAddWithoutValidation("x-api-key", AdapterHarness.AdapterApiKey);
        using var response = await http.SendAsync(request);
        return response.StatusCode;
    }

    private static (int Mpt, int Process, int Proxy, int Disk) Counts(HealthRig rig) =>
        (rig.Mpt.RequestCount, rig.Runner.Calls.Count, rig.Proxy.Count, rig.Disk.Reads);

    private sealed class NoBackend : IRenderBackend
    {
        public Task<ErrorOr.ErrorOr<BackendAccepted>> SubmitAsync(
            RenderTaskRecord record, MptSubmitFields mpt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CancelAsync(RenderTaskRecord record, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ErrorOr.ErrorOr<OutputFile>> OpenOutputAsync(
            Guid taskId, OutputKind kind, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ErrorOr.ErrorOr<ErrorOr.Deleted>> DeleteAsync(
            RenderTaskRecord record, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

/// <summary>The fixtures' shape (D7 / D18): same keys at every level; <c>tts_proxy</c> kept byte-for-byte.</summary>
public sealed class HealthContractTests
{
    [Fact]
    public async Task Ok_MatchesFixture()
    {
        await using var rig = await HealthRig.StartAsync();
        await rig.SampleAllAsync();

        Keys(Serialize(await rig.Source.GetAsync(CancellationToken.None)))
            .ShouldBe(Keys(FixtureFiles.Read("render-node-health.v1.ok.json")));
    }

    [Fact]
    public async Task ProxyDown_MatchesFixture()
    {
        await using var rig = await HealthRig.StartAsync();
        rig.Proxy.Throw = true;
        await rig.SampleAllAsync();
        var health = await rig.Source.GetAsync(CancellationToken.None);

        health.Reasons.ShouldBe([NodeHealthReasons.TtsProxyNotOk]);
        Keys(Serialize(health)).ShouldBe(Keys(FixtureFiles.Read("render-node-health.v1.proxy-down.json")));
    }

    [Fact]
    public async Task DiskLow_MatchesFixture()
    {
        await using var rig = await HealthRig.StartAsync();
        rig.Disk.Figures = new DiskFigures(100, 10);
        await rig.SampleAllAsync();
        var health = await rig.Source.GetAsync(CancellationToken.None);

        health.Reasons.ShouldBe([NodeHealthReasons.DiskLow]);
        health.Disk.UsedPct.ShouldBe(90);
        Keys(Serialize(health)).ShouldBe(Keys(FixtureFiles.Read("render-node-health.v1.disk-low.json")));
    }

    [Fact]
    public async Task TtsProxy_KeptByteForByte()
    {
        await using var rig = await HealthRig.StartAsync();
        rig.Proxy.Body = "{\"status\":\"ok\",\"reasons\":[],\"extra\":{\"keep\":[1,2,3]}}";
        await rig.SampleAllAsync();

        var health = await rig.Source.GetAsync(CancellationToken.None);

        health.TtsProxy.GetRawText().ShouldBe(rig.Proxy.Body);
    }

    private static string Serialize(RenderNodeHealthV1 health) =>
        JsonSerializer.Serialize(health, RenderNodeJson.Strict);

    /// <summary>Every property path, sorted: the shape without the values.</summary>
    private static string[] Keys(string json)
    {
        using var document = JsonDocument.Parse(json);
        var paths = new List<string>();
        Walk(document.RootElement, string.Empty, paths);
        paths.Sort(StringComparer.Ordinal);
        return [.. paths];
    }

    private static void Walk(JsonElement element, string prefix, List<string> paths)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            // The proxy body is the proxy's own shape (04 D8), not part of this contract.
            var path = prefix + "/" + property.Name;
            paths.Add(path);
            if (path != "/tts_proxy")
            {
                Walk(property.Value, path, paths);
            }
        }
    }
}

/// <summary>D7 GPU source: <c>nvidia-smi</c> CSV, fail to <c>gpu_unavailable</c>, always a <c>sampled_at</c>.</summary>
public sealed class GpuHealthTests
{
    [Fact]
    public async Task CsvParsed_GpuBlock()
    {
        await using var rig = await HealthRig.StartAsync();
        await rig.GpuSampler.SampleAsync(CancellationToken.None);

        var gpu = rig.GpuSampler.Cache.Current!.Value;
        gpu.Ok.ShouldBeTrue();
        gpu.UtilPct.ShouldBe(37);
        gpu.MemUsedMb.ShouldBe(1024);
        gpu.MemTotalMb.ShouldBe(24576);
        gpu.TempC.ShouldBe(55);
        rig.Runner.Calls.Single().Arguments.ShouldBe(GpuSampler.Arguments);
    }

    [Fact]
    public async Task NvidiaSmiMissing_GpuUnavailable()
    {
        await using var rig = await HealthRig.StartAsync();
        rig.Runner.Script = (_, _) => throw new System.ComponentModel.Win32Exception("not found");
        await rig.SampleAllAsync();

        var gpu = rig.GpuSampler.Cache.Current!.Value;
        gpu.Ok.ShouldBeFalse();
        gpu.UtilPct.ShouldBeNull();
        (await rig.Source.GetAsync(CancellationToken.None)).Reasons.ShouldContain(NodeHealthReasons.GpuUnavailable);

        rig.Runner.Script = (name, _) => new ProcessResult(0, "Unable to determine the device handle\n");
        await rig.GpuSampler.SampleAsync(CancellationToken.None);
        rig.GpuSampler.Cache.Current!.Value.Ok.ShouldBeFalse("an error string is not four integers");
    }

    [Fact]
    public async Task SampledAt_Set()
    {
        await using var rig = await HealthRig.StartAsync();
        await rig.GpuSampler.SampleAsync(CancellationToken.None);
        rig.GpuSampler.Cache.Current!.Value.SampledAt.ShouldBe(rig.Clock.GetUtcNow());

        rig.Runner.Script = (_, _) => new ProcessResult(1, string.Empty);
        rig.Clock.Advance(TimeSpan.FromSeconds(15));
        await rig.GpuSampler.SampleAsync(CancellationToken.None);
        rig.GpuSampler.Cache.Current!.Value.SampledAt.ShouldBe(rig.Clock.GetUtcNow());
    }
}

/// <summary>D7 codec source: start-only, never an exit.</summary>
public sealed class CodecCheckTests
{
    [Fact]
    public async Task EncodersWithoutConfiguredCodec_CodecUnavailable()
    {
        await using var rig = await HealthRig.StartAsync(o => o.VideoCodec = "h264_nvenc");
        await rig.SampleAllAsync();

        rig.CodecProbe.Cache.Current!.Value.Available.ShouldBeFalse();
        (await rig.Source.GetAsync(CancellationToken.None)).Reasons.ShouldBe([NodeHealthReasons.CodecUnavailable]);
    }

    [Fact]
    public async Task Libx264Present_NoReason()
    {
        await using var rig = await HealthRig.StartAsync();
        await rig.SampleAllAsync();

        rig.CodecProbe.Cache.Current!.Value.Available.ShouldBeTrue();
        (await rig.Source.GetAsync(CancellationToken.None)).Reasons.ShouldNotContain(NodeHealthReasons.CodecUnavailable);
    }

    [Fact]
    public async Task FfmpegMissing_CodecUnavailableNoExit()
    {
        await using var rig = await HealthRig.StartAsync();
        rig.Runner.Script = (_, _) => throw new System.ComponentModel.Win32Exception("no ffmpeg");

        await Should.NotThrowAsync(() => rig.CodecProbe.SampleAsync(CancellationToken.None));

        rig.CodecProbe.Cache.Current!.Value.Available.ShouldBeFalse();
    }
}

/// <summary>D7 assets source: single-flight, 600 s, scripts only from the sampler.</summary>
public sealed class AssetsJobTests
{
    [Fact]
    public async Task SingleFlight_ConcurrentRefreshRunsOnce()
    {
        await using var rig = await HealthRig.StartAsync();
        rig.Runner.Delay = TimeSpan.FromMilliseconds(200);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => rig.AssetsSampler.SampleAsync(CancellationToken.None)));

        rig.Runner.CallsTo("check-fonts.sh").ShouldBe(1);
        rig.Runner.CallsTo("check-music.sh").ShouldBe(1);
    }

    [Fact]
    public async Task FontMissing_Reason()
    {
        await using var rig = await HealthRig.StartAsync();
        rig.Runner.Script = (name, args) => name.EndsWith("check-fonts.sh", StringComparison.Ordinal)
            ? new ProcessResult(1, string.Empty)
            : new ProcessResult(0, FakeProcessRunner.MusicJson);
        await rig.SampleAllAsync();

        var health = await rig.Source.GetAsync(CancellationToken.None);

        health.Reasons.ShouldContain(NodeHealthReasons.FontMissing);
        health.Reasons.ShouldNotContain(NodeHealthReasons.MusicMissing);
        health.Assets.Ok.ShouldBeFalse();
    }

    [Fact]
    public async Task MusicMissing_Listed()
    {
        await using var rig = await HealthRig.StartAsync();
        rig.Runner.Script = (name, args) => name.EndsWith("check-music.sh", StringComparison.Ordinal)
            // check-music.sh exits 1 on a failed check and still prints the JSON report (01 D9).
            ? new ProcessResult(1, "{\"ok\": false, \"catalog_revision\": 3, \"music_missing\": [\"track-7\"], \"unlicensed\": [], \"catalog_errors\": 0}")
            : new ProcessResult(0, FakeProcessRunner.FontOutput);
        await rig.SampleAllAsync();

        var health = await rig.Source.GetAsync(CancellationToken.None);

        health.Reasons.ShouldContain(NodeHealthReasons.MusicMissing);
        health.Assets.MusicMissing.ShouldBe(["track-7"]);
        health.Assets.MusicRevision.ShouldBe(3);
    }

    [Fact]
    public async Task MusicJson_WithNullRevision_IsFailedHealth_WithoutAnException()
    {
        await using var rig = await HealthRig.StartAsync();
        // check-music.sh prints "catalog_revision": null when the catalog is unreadable (exit 1).
        rig.Runner.Script = (name, args) => name.EndsWith("check-music.sh", StringComparison.Ordinal)
            ? new ProcessResult(1, "{\"ok\": false, \"catalog_revision\": null, \"music_missing\": [], \"unlicensed\": [\"x.mp3\"], \"catalog_errors\": 0}")
            : new ProcessResult(0, FakeProcessRunner.FontOutput);
        await rig.SampleAllAsync();

        var health = await rig.Source.GetAsync(CancellationToken.None);

        health.Assets.Ok.ShouldBeFalse();
        health.Assets.MusicRevision.ShouldBeNull();
    }

    [Fact]
    public async Task CheckedAt_Added_NoScriptRerun()
    {
        await using var rig = await HealthRig.StartAsync();
        await rig.AssetsSampler.SampleAsync(CancellationToken.None);
        var calls = rig.Runner.Calls.Count;

        var health = await rig.Source.GetAsync(CancellationToken.None);

        health.Assets.CheckedAt.ShouldBe(rig.Clock.GetUtcNow());
        rig.Runner.Calls.Count.ShouldBe(calls);
    }
}

/// <summary>D7 config source: the node profile is whisper on cuda.</summary>
public sealed class ConfigProbeTests
{
    [Fact]
    public async Task WhisperDeviceCpu_ConfigMismatch()
    {
        await using var rig = await HealthRig.StartAsync();
        await File.WriteAllTextAsync(rig.Options.MptConfigPath, "[whisper]\ndevice = \"cpu\"\ncompute_type = \"int8\"\n", TestContext.Current.CancellationToken);
        await rig.SampleAllAsync();

        var health = await rig.Source.GetAsync(CancellationToken.None);

        health.Reasons.ShouldContain(NodeHealthReasons.ConfigMismatch);
        health.Mpt.WhisperDevice.ShouldBe("cpu");
        health.Mpt.ConfigProfile.ShouldBeNull();
    }

    [Fact]
    public async Task CudaProfile_NoReason()
    {
        await using var rig = await HealthRig.StartAsync();
        await rig.SampleAllAsync();

        var health = await rig.Source.GetAsync(CancellationToken.None);

        health.Reasons.ShouldNotContain(NodeHealthReasons.ConfigMismatch);
        health.Mpt.WhisperDevice.ShouldBe("cuda");
        health.Mpt.ConfigProfile.ShouldBe(ConfigProbe.NodeProfile);
        health.Mpt.VideoCodec.ShouldBe("libx264");
        health.Mpt.StartedAt.ShouldNotBeNull();
    }
}

/// <summary>CR-3 / CR-5: the assets block carries the checked font and the music report on both exit codes.</summary>
public sealed class AssetsContractTests
{
    [Fact]
    public async Task Ok_AssetsFieldValuesMatchFixture()
    {
        await using var rig = await HealthRig.StartAsync();
        await rig.SampleAllAsync();

        var actual = Assets(JsonSerializer.Serialize(await rig.Source.GetAsync(CancellationToken.None), RenderNodeJson.Strict));
        var expected = Assets(FixtureFiles.Read("render-node-health.v1.ok.json"));

        actual.Select(p => p.Key).Order(StringComparer.Ordinal).ShouldBe(expected.Select(p => p.Key).Order(StringComparer.Ordinal));
        foreach (var (name, value) in expected)
        {
            actual[name]?.ToJsonString().ShouldBe(value?.ToJsonString(), name);
        }
    }

    [Fact]
    public async Task FontCheckFails_FontAbsent_AndReasonListed()
    {
        await using var rig = await HealthRig.StartAsync();
        var inner = rig.Runner.Script;
        rig.Runner.Script = (name, args) => name.EndsWith("check-fonts.sh", StringComparison.Ordinal)
            ? new ProcessResult(1, "font FAIL x not loadable\n")
            : inner(name, args);
        await rig.SampleAllAsync();

        var health = await rig.Source.GetAsync(CancellationToken.None);

        health.Assets.Ok.ShouldBeFalse();
        health.Assets.Font.ShouldBeNull();
        health.Reasons.ShouldContain(NodeHealthReasons.FontMissing);
    }

    /// <summary>The assets block without <c>checked_at</c> (a timestamp), keyed by property.</summary>
    private static Dictionary<string, JsonNode?> Assets(string json)
    {
        var assets = JsonNode.Parse(json)!["assets"]!.AsObject();
        return assets.Where(p => p.Key != "checked_at").ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }
}

/// <summary>CR-4: D7's fixed 15 s cadence for MPT / proxy / disk; <c>GpuSampleSeconds</c> drives the GPU source only.</summary>
public sealed class SamplerCadenceTests
{
    [Fact]
    public async Task GpuInterval_DrivesOnlyTheGpuSource()
    {
        await using var rig = await HealthRig.StartAsync(options => options.GpuSampleSeconds = 7);

        rig.GpuSampler.Interval.ShouldBe(TimeSpan.FromSeconds(7));
        rig.MptSampler.Interval.ShouldBe(TimeSpan.FromSeconds(15));
        rig.ProxySampler.Interval.ShouldBe(TimeSpan.FromSeconds(15));
        rig.DiskSampler.Interval.ShouldBe(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task GpuGoesStaleAt3xItsInterval_OthersKeepTheir45Seconds()
    {
        await using var rig = await HealthRig.StartAsync(options => options.GpuSampleSeconds = 7);
        await rig.SampleAllAsync();

        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        var health = await rig.Source.GetAsync(CancellationToken.None);

        health.Reasons.ShouldContain(NodeHealthReasons.GpuUnavailable);
        health.Reasons.ShouldNotContain(NodeHealthReasons.MptUnreachable);
        health.Reasons.ShouldNotContain(NodeHealthReasons.DiskLow);
    }
}

/// <summary>SEC-6: the proxy body is capped; an over-cap body is a failed sample.</summary>
public sealed class ProxyBodyCapTests
{
    [Fact]
    public async Task OverCapBody_FailedSample()
    {
        await using var rig = await HealthRig.StartAsync();
        rig.Proxy.Body = "{\"status\":\"ok\",\"reasons\":[],\"pad\":\"" + new string('x', ProxySampler.MaxBodyBytes + 1) + "\"}";

        await rig.ProxySampler.SampleAsync(CancellationToken.None);

        rig.ProxySampler.Cache.Current!.Value.Ok.ShouldBeFalse();
    }

    [Fact]
    public async Task UnderCapBody_Accepted()
    {
        await using var rig = await HealthRig.StartAsync();
        rig.Proxy.Body = "{\"status\":\"ok\",\"reasons\":[],\"pad\":\"" + new string('x', 1024) + "\"}";

        await rig.ProxySampler.SampleAsync(CancellationToken.None);

        rig.ProxySampler.Cache.Current!.Value.Ok.ShouldBeTrue();
    }
}

/// <summary>SEC-1 / PERF-1 / SEC-6 on the real runner, with throwaway shell scripts (Unix only).</summary>
public sealed class SystemProcessRunnerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nts-run-" + Guid.NewGuid().ToString("N"));

    public SystemProcessRunnerTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    [Fact]
    public async Task Child_SeesOnlyTheAllowList_NoKeyReachesIt()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // NTS_MPT_API_KEY is owned by the assembly's TestEnvironment (MptClient reads it): checked, never changed.
        string[] secrets = ["ADAPTER_API_KEY", "ADAPTER_API_KEY_NEXT", "NODE_OPENAI_IMAGE_KEY"];
        var mptKey = Environment.GetEnvironmentVariable("NTS_MPT_API_KEY");
        var script = WriteScript("#!/bin/sh\n/usr/bin/env\n");
        foreach (var name in secrets)
        {
            Environment.SetEnvironmentVariable(name, "secret-of-" + name);
        }

        try
        {
            var result = await new SystemProcessRunner().RunAsync(
                script, [], TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

            result.ExitCode.ShouldBe(0);
            result.StandardOutput.ShouldContain("PATH=" + SystemProcessRunner.ChildPath);
            result.StandardOutput.ShouldNotContain("secret-of-");
            result.StandardOutput.ShouldNotContain("NTS_MPT_API_KEY");
            if (!string.IsNullOrEmpty(mptKey))
            {
                result.StandardOutput.ShouldNotContain(mptKey);
            }
            foreach (var name in secrets)
            {
                result.StandardOutput.ShouldNotContain(name);
            }
        }
        finally
        {
            foreach (var name in secrets)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
    }

    [Fact]
    public async Task CallerCancellation_KillsTheChild_AndRethrowsCancellation()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var pidFile = Path.Combine(_directory, "pid");
        var script = WriteScript("#!/bin/sh\necho $$ > \"$1\"\nexec /bin/sleep 60\n");
        using var cancel = new CancellationTokenSource();
        var run = new SystemProcessRunner().RunAsync(script, [pidFile], TimeSpan.FromSeconds(60), cancel.Token);
        await WaitForAsync(() => File.Exists(pidFile) && File.ReadAllText(pidFile).EndsWith('\n'));
        var pid = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);

        await cancel.CancelAsync();
        var caught = await Capture(run);

        caught.ShouldBeAssignableTo<OperationCanceledException>();
        await WaitForAsync(() => !IsAlive(pid));
    }

    [Fact]
    public async Task BudgetExpiry_KillsTheChild_AndThrowsTimeout()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var pidFile = Path.Combine(_directory, "pid");
        var script = WriteScript("#!/bin/sh\necho $$ > \"$1\"\nexec /bin/sleep 60\n");

        var caught = await Capture(new SystemProcessRunner().RunAsync(
            script, [pidFile], TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken));

        caught.ShouldBeOfType<TimeoutException>();
        var pid = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        await WaitForAsync(() => !IsAlive(pid));
    }

    [Fact]
    public async Task OverCapStdout_IsAFailedRun()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var script = WriteScript("#!/bin/sh\n/usr/bin/head -c 2000000 /dev/zero\n");

        var caught = await Capture(new SystemProcessRunner().RunAsync(
            script, [], TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));

        caught.ShouldBeOfType<IOException>();
    }

    [Fact]
    public async Task OverCapStdout_WithStderrHeldOpen_FailsFastAndLeavesNoChild()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var pidFile = Path.Combine(_directory, "pid");
        var script = WriteScript("#!/bin/sh\necho $$ > \"$1\"\n/usr/bin/head -c 2000000 /dev/zero\nexec /bin/sleep 60\n");
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var caught = await Capture(new SystemProcessRunner().RunAsync(
            script, [pidFile], TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        caught.ShouldBeOfType<IOException>();
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        var pid = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        await WaitForAsync(() => !IsAlive(pid));
    }

    private static async Task<Exception?> Capture(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the condition did not become true in time");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    private string WriteScript(string body)
    {
        var path = Path.Combine(_directory, "probe-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(path, body);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}

/// <summary>CR-2: a change to a watched file refreshes a sampler without waiting for its (here 10 min) tick.</summary>
public sealed class HealthSamplerChangeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nts-watch-" + Guid.NewGuid().ToString("N"));

    public HealthSamplerChangeTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    [Fact]
    public async Task ChangeToWatchedFile_RefreshesBeforeTheNextTick()
    {
        var watched = Path.Combine(_directory, "FONTS.csv");
        var sampler = new CountingSampler(watched);
        var service = new HealthSamplerService([sampler], TimeProvider.System)
        {
            ChangeDebounce = TimeSpan.FromMilliseconds(50),
        };
        await service.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitForAsync(() => sampler.Count == 1);
            await Task.Delay(300, TestContext.Current.CancellationToken);

            await File.WriteAllTextAsync(watched, "changed", TestContext.Current.CancellationToken);

            await WaitForAsync(() => sampler.Count >= 2);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task BurstOfWrites_WithinTheDebounce_IsOneExtraSample()
    {
        var watched = Path.Combine(_directory, "FONTS.csv");
        var sampler = new CountingSampler(watched);
        var service = new HealthSamplerService([sampler], TimeProvider.System)
        {
            ChangeDebounce = TimeSpan.FromMilliseconds(600),
        };
        await service.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitForAsync(() => sampler.Count == 1);
            await Task.Delay(300, TestContext.Current.CancellationToken);

            for (var i = 0; i < 5; i++)
            {
                await File.WriteAllTextAsync(watched, "v" + i, TestContext.Current.CancellationToken);
                await Task.Delay(40, TestContext.Current.CancellationToken);
            }

            await WaitForAsync(() => sampler.Count >= 2);
            await Task.Delay(1500, TestContext.Current.CancellationToken);

            sampler.Count.ShouldBe(2);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WatchedFileUnderADirectoryCreatedAfterStart_StillRefreshes()
    {
        var watched = Path.Combine(_directory, "later", "deeper", "music-tracks.v1.json");
        var sampler = new CountingSampler(watched);
        var service = new HealthSamplerService([sampler], TimeProvider.System)
        {
            ChangeDebounce = TimeSpan.FromMilliseconds(50),
        };
        await service.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitForAsync(() => sampler.Count == 1);
            await Task.Delay(300, TestContext.Current.CancellationToken);

            Directory.CreateDirectory(Path.GetDirectoryName(watched)!);
            await WaitForAsync(() => sampler.Count >= 2);
            await Task.Delay(300, TestContext.Current.CancellationToken);

            // The re-armed watcher now sees the file itself.
            var before = sampler.Count;
            await File.WriteAllTextAsync(watched, "x", TestContext.Current.CancellationToken);
            await WaitForAsync(() => sampler.Count > before);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the condition did not become true in time");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    private sealed class CountingSampler(string watched) : IHealthSampler
    {
        private int _count;

        internal int Count => Volatile.Read(ref _count);

        public string Name => "counting";

        public TimeSpan? Interval => TimeSpan.FromMinutes(10);

        public IReadOnlyList<string> WatchedPaths => [watched];

        public Task SampleAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return Task.CompletedTask;
        }
    }
}
