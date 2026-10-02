using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
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
            ? new ProcessResult(0, "{\"ok\": false, \"catalog_revision\": 3, \"music_missing\": [\"track-7\"], \"unlicensed\": [], \"catalog_errors\": 0}")
            : new ProcessResult(0, "ok");
        await rig.SampleAllAsync();

        var health = await rig.Source.GetAsync(CancellationToken.None);

        health.Reasons.ShouldContain(NodeHealthReasons.MusicMissing);
        health.Assets.MusicMissing.ShouldBe(["track-7"]);
        health.Assets.MusicRevision.ShouldBe(3);
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
