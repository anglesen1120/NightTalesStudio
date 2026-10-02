using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.MptAdapter.Health;
using NightTales.MptAdapter.ImageRelay;
using NightTales.MptAdapter.Tests.Fakes;
using NightTales.MptAdapter.Tls;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using Shouldly;
using Xunit;

namespace NightTales.MptAdapter.Tests;

/// <summary>A node certificate on disk, the way <c>make-adapter-cert.sh</c> leaves it (D11).</summary>
internal sealed class CertFiles : IDisposable
{
    internal CertFiles(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        Directory = Path.Combine(Path.GetTempPath(), "nts-cert-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        CertPath = Path.Combine(Directory, "adapter.crt");
        KeyPath = Path.Combine(Directory, "adapter.key");

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=nts-adapter-test", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(notBefore, notAfter);
        File.WriteAllText(CertPath, certificate.ExportCertificatePem());
        File.WriteAllText(KeyPath, key.ExportPkcs8PrivateKeyPem());
        Sha256 = certificate.GetCertHashString(HashAlgorithmName.SHA256);
    }

    internal string Directory { get; }

    internal string CertPath { get; }

    internal string KeyPath { get; }

    internal string Sha256 { get; }

    internal static CertFiles Valid() =>
        new(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90));

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}

internal sealed class FakeHostEnvironment(string name) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;

    public string ApplicationName { get; set; } = "NightTales.MptAdapter";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>S3 / D19: the fatal startup checks, with their fixed messages (exit 2).</summary>
public sealed class StartupKeyTests : IDisposable
{
    private const string Key = "adapter-key-0123456789abcdef0123456789";

    private readonly CertFiles _cert = CertFiles.Valid();
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "nts-data-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _cert.Dispose();
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    [Fact]
    public void AdapterKeyEqualsMptKey_Exit2() =>
        Run(Env(("NTS_MPT_API_KEY", Key))).ShouldBe(StartupKeyChecks.KeyEqualsMptKeyMessage);

    [Fact]
    public void AdapterKeyEqualsProxyKey_Exit2() =>
        Run(Env(("NTS_PROXY_API_KEY", Key))).ShouldBe(StartupKeyChecks.KeyEqualsProxyKeyMessage);

    [Fact]
    public void AdapterKeyEqualsImageKey_Exit2() =>
        Run(Env(("NODE_OPENAI_IMAGE_KEY", Key))).ShouldBe(StartupKeyChecks.KeyEqualsImageKeyMessage);

    [Fact]
    public void AdapterKeyEqualsNext_Exit2() =>
        Run(Env(("ADAPTER_API_KEY_NEXT", Key))).ShouldBe(StartupKeyChecks.KeyEqualsNextMessage);

    [Fact]
    public void MissingAdapterKey_Exit2() =>
        Run(Env(), withKey: false).ShouldBe(StartupKeyChecks.MissingKeyMessage);

    [Fact]
    public void DistinctKeys_Starts()
    {
        Run(Env(
                ("NTS_MPT_API_KEY", "other-1"),
                ("NTS_PROXY_API_KEY", "other-2"),
                ("NODE_OPENAI_IMAGE_KEY", "other-3"),
                ("ADAPTER_API_KEY_NEXT", "other-4")),
            out var certificate).ShouldBeNull();

        certificate.ShouldNotBeNull();
        certificate.Sha256Fingerprint.ShouldBe(_cert.Sha256, StringCompareShould.IgnoreCase);
        certificate.Certificate.Dispose();
    }

    [Fact]
    public void FixedMessagesNeverCarryAKey()
    {
        StartupKeyChecks.KeyEqualsMptKeyMessage.ShouldNotContain(Key);
        Run(Env(("NTS_MPT_API_KEY", Key)))!.ShouldNotContain(Key);
    }

    [Fact]
    public void NonFatalProbes_DoNotBlockStart()
    {
        // Missing ffmpeg, nvidia-smi, relay and a bad whisper config are health reasons, never an exit.
        Run(Env(), out var certificate, extra: new Dictionary<string, string?>
        {
            ["Adapter:FfmpegPath"] = "/nonexistent/ffmpeg",
            ["Adapter:NvidiaSmiPath"] = "/nonexistent/nvidia-smi",
            ["Adapter:MptConfigPath"] = "/nonexistent/config.toml",
            ["ImageRelay:Enabled"] = "true",
        }).ShouldBeNull();
        certificate?.Certificate.Dispose();
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] values)
    {
        var map = values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        return name => map.GetValueOrDefault(name);
    }

    private string? Run(Func<string, string?> environment, bool withKey = true) =>
        Run(environment, out var certificate, withKey: withKey).Also(_ => certificate?.Certificate.Dispose());

    private string? Run(
        Func<string, string?> environment,
        out LoadedCertificate? certificate,
        bool withKey = true,
        Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Adapter:ListenAddress"] = "10.20.30.40",
            ["Adapter:DataDir"] = _dataDir,
            ["Adapter:CertPath"] = _cert.CertPath,
            ["Adapter:KeyPath"] = _cert.KeyPath,
        };
        if (withKey)
        {
            values["RenderNodeServer:ApiKey"] = Key;
        }

        foreach (var (k, v) in extra ?? [])
        {
            values[k] = v;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return StartupKeyChecks.Run(configuration, environment, isDevelopment: false, TimeProvider.System, out certificate);
    }
}

internal static class TestExtensions
{
    internal static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}

/// <summary>D6 listener rules, D11 certificate, T44 / T57.</summary>
public sealed class TlsTests
{
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public void ListenAddressAny_RefusesToStart(string address) =>
        TlsPrivateAddressGuard.Check(address, isDevelopment: false).ShouldBe(TlsPrivateAddressGuard.AnyAddressMessage);

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("203.0.113.9")]
    [InlineData("2001:db8::1")]
    public void ListenAddressPublic_RefusesToStart(string address) =>
        TlsPrivateAddressGuard.Check(address, isDevelopment: true).ShouldBe(TlsPrivateAddressGuard.PublicAddressMessage);

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.4.2")]
    [InlineData("192.168.1.10")]
    public void PrivateAddress_Binds8090Tls(string address)
    {
        TlsPrivateAddressGuard.Check(address, isDevelopment: false).ShouldBeNull();
        new AdapterOptions().Port.ShouldBe(8090);
        TlsPrivateAddressGuard.Parse(address).ShouldBe(IPAddress.Parse(address));
    }

    [Fact]
    public void LoopbackBinding_OnlyInDevelopment()
    {
        TlsPrivateAddressGuard.Check("127.0.0.1", isDevelopment: false).ShouldBe(TlsPrivateAddressGuard.LoopbackMessage);
        TlsPrivateAddressGuard.Check("127.0.0.1", isDevelopment: true).ShouldBeNull();
        TlsPrivateAddressGuard.Check(string.Empty, isDevelopment: true).ShouldBe(TlsPrivateAddressGuard.MissingMessage);
        TlsPrivateAddressGuard.Check("node-1.internal", isDevelopment: true).ShouldBe(TlsPrivateAddressGuard.NotAnIpMessage);
    }

    [Fact]
    public void FingerprintAndNotAfter_LoggedOnce()
    {
        using var files = CertFiles.Valid();
        TlsValidator.TryLoad(files.CertPath, files.KeyPath, TimeProvider.System, out var loaded).ShouldBeNull();
        using var certificate = loaded!.Certificate;
        var logs = new ListLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));

        TlsValidator.LogFingerprint(factory.CreateLogger("tls"), loaded);

        var lines = logs.Records.Where(r => r.Contains("adapter_tls_certificate", StringComparison.Ordinal)).ToList();
        lines.Count.ShouldBe(1);
        lines[0].ShouldContain(files.Sha256, Case.Insensitive);
        lines[0].ShouldContain(loaded.NotAfter.Year.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void CertificateMissing_Exit2()
    {
        TlsValidator.TryLoad("/nonexistent/adapter.crt", "/nonexistent/adapter.key", TimeProvider.System, out var loaded)
            .ShouldBe(TlsValidator.MissingMessage);
        loaded.ShouldBeNull();
    }

    [Fact]
    public void CertificateUnparsableOrExpired_Refused()
    {
        using var files = CertFiles.Valid();
        File.WriteAllText(files.CertPath, "not a certificate");
        TlsValidator.TryLoad(files.CertPath, files.KeyPath, TimeProvider.System, out _)
            .ShouldBe(TlsValidator.UnparsableMessage);

        using var expired = new CertFiles(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-1));
        TlsValidator.TryLoad(expired.CertPath, expired.KeyPath, TimeProvider.System, out _)
            .ShouldBe(TlsValidator.ExpiredMessage);
    }
}

/// <summary>D6: keys, defaults, <c>ValidateOnStart</c>, and the loopback-only MPT URL (QA89-LB-01).</summary>
public sealed class ConfigBindingTests
{
    [Fact]
    public void Defaults_AsD6()
    {
        var options = new AdapterOptions();

        options.Port.ShouldBe(8090);
        options.DataDir.ShouldBe("/var/lib/nts/adapter");
        options.MptBaseUrl.ShouldBe("http://127.0.0.1:8080");
        options.MptRoot.ShouldBe("/opt/nts/current/mpt");
        options.PollSeconds.ShouldBe(2);
        options.CheckConcurrency.ShouldBe(1);
        options.CheckTimeoutSeconds.ShouldBe(900);
        options.AssetsRefreshSeconds.ShouldBe(600);
        options.GpuSampleSeconds.ShouldBe(15);
        options.FfmpegPath.ShouldBe("/usr/bin/ffmpeg");
        options.ProxyHealthUrl.ShouldBe("http://127.0.0.1:8001/health");
        options.ListenAddress.ShouldBeEmpty();
        options.ResolveMptThreads(2).ShouldBe(Math.Max(1, Environment.ProcessorCount / 2));
    }

    [Fact]
    public void DoubleUnderscoreEnv_BindsAllAdapterKeys()
    {
        var names = new Dictionary<string, string>
        {
            ["NTSBIND_Adapter__ListenAddress"] = "10.1.2.3",
            ["NTSBIND_Adapter__Port"] = "9443",
            ["NTSBIND_Adapter__DataDir"] = "/data",
            ["NTSBIND_Adapter__MptBaseUrl"] = "http://127.0.0.1:9999",
            ["NTSBIND_Adapter__MptRoot"] = "/mpt",
            ["NTSBIND_Adapter__MptThreads"] = "6",
            ["NTSBIND_Adapter__PollSeconds"] = "5",
            ["NTSBIND_Adapter__CheckConcurrency"] = "3",
            ["NTSBIND_Adapter__CheckTimeoutSeconds"] = "60",
            ["NTSBIND_Adapter__AssetsRefreshSeconds"] = "30",
            ["NTSBIND_Adapter__GpuSampleSeconds"] = "7",
            ["NTSBIND_Adapter__FfmpegPath"] = "/bin/ffmpeg",
            ["NTSBIND_Adapter__VideoCodec"] = "h264_nvenc",
            ["NTSBIND_Adapter__ProxyHealthUrl"] = "http://127.0.0.1:1/h",
        };
        try
        {
            foreach (var (name, value) in names)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            var configuration = new ConfigurationBuilder().AddEnvironmentVariables("NTSBIND_").Build();
            var options = new AdapterOptions();
            configuration.GetSection(AdapterOptions.SectionName).Bind(options);

            options.ListenAddress.ShouldBe("10.1.2.3");
            options.Port.ShouldBe(9443);
            options.DataDir.ShouldBe("/data");
            options.MptBaseUrl.ShouldBe("http://127.0.0.1:9999");
            options.MptRoot.ShouldBe("/mpt");
            options.MptThreads.ShouldBe(6);
            options.PollSeconds.ShouldBe(5);
            options.CheckConcurrency.ShouldBe(3);
            options.CheckTimeoutSeconds.ShouldBe(60);
            options.AssetsRefreshSeconds.ShouldBe(30);
            options.GpuSampleSeconds.ShouldBe(7);
            options.FfmpegPath.ShouldBe("/bin/ffmpeg");
            options.VideoCodec.ShouldBe("h264_nvenc");
            options.ProxyHealthUrl.ShouldBe("http://127.0.0.1:1/h");
        }
        finally
        {
            foreach (var name in names.Keys)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
    }

    [Fact]
    public async Task ValidateOnStart_MissingRequiredFails()
    {
        using var host = BuildHost("Production", new Dictionary<string, string?>());

        var failure = await Should.ThrowAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        failure.Message.ShouldContain("Adapter:ListenAddress");
    }

    [Theory]
    [InlineData("http://10.0.0.5:8080")]
    [InlineData("http://localhost:8080")]
    [InlineData("https://127.0.0.1:8080")]
    [InlineData("http://[2001:db8::1]:8080")]
    public async Task ValidateOnStart_RejectsNonLoopbackMptBaseUrl(string url)
    {
        using var host = BuildHost("Production", Valid(("Adapter:MptBaseUrl", url)));

        var failure = await Should.ThrowAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        failure.Message.ShouldContain("loopback");
        failure.Message.ShouldNotContain(url, Case.Sensitive, "the message names the key, never the value");
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/foo")]
    [InlineData("http://127.0.0.1:8080/api/v1")]
    [InlineData("http://127.0.0.1:8080/?x=1")]
    public async Task ValidateOnStart_RejectsNonRootMptBaseUrlPath(string url)
    {
        using var host = BuildHost("Production", Valid(("Adapter:MptBaseUrl", url)));

        var failure = await Should.ThrowAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        failure.Message.ShouldContain("MPT root");
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("http://[::1]:8080/")]
    public void RootLoopbackMptBaseUrl_Accepted(string url) =>
        new AdapterOptionsValidator(new FakeHostEnvironment("Production"))
            .Validate(null, ValidOptions(o => o.MptBaseUrl = url)).Succeeded.ShouldBeTrue();

    [Fact]
    public void LoopbackListen_AllowedOnlyInDevelopment()
    {
        void Loopback(AdapterOptions o) => o.ListenAddress = "127.0.0.1";

        new AdapterOptionsValidator(new FakeHostEnvironment("Development"))
            .Validate(null, ValidOptions(Loopback)).Succeeded.ShouldBeTrue();
        foreach (var name in new[] { "Staging", "Production" })
        {
            var result = new AdapterOptionsValidator(new FakeHostEnvironment(name)).Validate(null, ValidOptions(Loopback));
            result.Failed.ShouldBeTrue(name);
            result.FailureMessage.ShouldContain("Adapter:ListenAddress");
        }
    }

    private static AdapterOptions ValidOptions(Action<AdapterOptions> configure)
    {
        var options = new AdapterOptions { ListenAddress = "10.20.30.40" };
        configure(options);
        return options;
    }

    private static Dictionary<string, string?> Valid(params (string Key, string Value)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["Adapter:ListenAddress"] = "10.20.30.40",
            ["Adapter:DataDir"] = Path.Combine(Path.GetTempPath(), "nts-bind-" + Guid.NewGuid().ToString("N")),
            ["RenderNodeServer:ApiKey"] = "adapter-key-0123456789abcdef0123456789",
        };
        foreach (var (key, value) in overrides)
        {
            values[key] = value;
        }

        return values;
    }

    private static IHost BuildHost(string environment, Dictionary<string, string?> configuration)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environment });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Services.AddMptAdapter(builder.Configuration);
        return builder.Build();
    }
}

/// <summary>D6: redirects are never followed; the MPT client has one construction path.</summary>
public sealed class MptClientConstructionTests
{
    [Fact]
    public async Task MptHttpClient_DoesNotFollowRedirects()
    {
        await using var mpt = await FakeMptServer.StartAsync();
        mpt.RedirectTo = "/ping";
        var options = new AdapterOptions { MptBaseUrl = mpt.BaseAddress.ToString() };
        using var http = MptHttpClient.Create(options);

        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri(mpt.BaseAddress, "/api/v1/tasks/51ace7c8-bc37-44fd-ad2f-839293c7df7c"));
        request.Headers.TryAddWithoutValidation(MptClient.ApiKeyHeader, FakeMptServer.DefaultApiKey);
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        mpt.Requests.Select(r => r.Path).ShouldNotContain("/ping");
    }

    [Fact]
    public void HostBuildsHttpClientsOnlyThroughMptHttpClientCreate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "NightTales.MptAdapter")))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull();
        var sources = Directory.EnumerateFiles(
            Path.Combine(directory.FullName, "src", "NightTales.MptAdapter"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        var offenders = sources
            .Where(f => File.ReadAllText(f).Contains("new SocketsHttpHandler", StringComparison.Ordinal)
                || File.ReadAllText(f).Contains("new HttpClientHandler", StringComparison.Ordinal)
                || File.ReadAllText(f).Contains("AddHttpClient", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        offenders.ShouldBe(["MptClient.cs"], "only MptHttpClient (in MptClient.cs) may build the handler");
        File.ReadAllText(Path.Combine(directory.FullName, "src", "NightTales.MptAdapter", "AdapterHost.cs"))
            .ShouldNotContain("new HttpClient(");
    }
}

/// <summary>03.4.3-T1 wiring seam: the relay host is not part of this build.</summary>
public sealed class RelayWiringTests
{
    [Fact]
    public void Disabled_NoopSink_ImagesOmitted()
    {
        var sink = new NoOpImagePlanSink();
        var id = Guid.NewGuid();

        Should.NotThrow(() => sink.Register(id, null!));
        sink.BuildReport(id).ShouldBeNull();
        Should.NotThrow(() => sink.Release(id));
        new DisabledImageRelayHealth().Read().Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task EnabledWithoutRelayHost_ReportedNotOk_NoDispatch()
    {
        await using var rig = await HealthRig.StartAsync(relay: new MissingImageRelayHealth());
        await rig.SampleAllAsync();

        var health = await rig.Source.GetAsync(TestContext.Current.CancellationToken);

        health.Reasons.ShouldBe([NodeHealthReasons.ImageRelayNotOk]);
        health.ImageRelay.Ok.ShouldBeFalse();
        health.Capacity.Accepting.ShouldBeFalse();
    }
}
