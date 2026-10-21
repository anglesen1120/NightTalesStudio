using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NightTales.MptAdapter.Tests.Fakes;
using NightTales.MptAdapter.Tls;
using Shouldly;
using Xunit;

namespace NightTales.MptAdapter.Tests;

/// <summary>
/// QA edge cases for 11.3.5-T4 (independent QA pass): the loopback MPT URL grammar, the listener guard's
/// address forms, and the certificate loader's key-pair and NotAfter boundaries.
/// </summary>
public sealed class QaMptBaseUrlTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("http://127.0.0.2:8080/")]
    [InlineData("HTTP://127.0.0.1:8080/")]
    [InlineData("http://[::1]:8080/")]
    [InlineData("http://[::ffff:127.0.0.1]:8080/")]
    [InlineData("http://127.0.0.1/")]
    public void LoopbackRoot_Accepted(string url) =>
        Should.NotThrow(() => AdapterOptions.ParseLoopbackMptBaseUrl(url));

    [Theory]
    [InlineData("http://127.0.0.1:8080?x=1")]
    [InlineData("http://127.0.0.1:8080/?x=1")]
    [InlineData("http://127.0.0.1:8080/#frag")]
    [InlineData("http://127.0.0.1:8080#frag")]
    [InlineData("http://127.0.0.1:8080//")]
    [InlineData("http://127.0.0.1:8080/api")]
    [InlineData("http://127.0.0.1:8080/api/")]
    [InlineData("http://[::1]:8080/v1")]
    public void NonRootPathQueryFragment_Rejected(string url) =>
        Should.Throw<ArgumentException>(() => AdapterOptions.ParseLoopbackMptBaseUrl(url))
            .Message.ShouldContain("MPT root");

    [Theory]
    [InlineData("http://localhost:8080/")]
    [InlineData("http://localhost.:8080/")]
    [InlineData("http://127.0.0.1.:8080/")]
    [InlineData("http://LOCALHOST:8080/")]
    [InlineData("http://0.0.0.0:8080/")]
    [InlineData("http://[::]:8080/")]
    [InlineData("http://10.0.0.5:8080/")]
    [InlineData("http://[fe80::1%25eth0]:8080/")]
    [InlineData("https://127.0.0.1:8080/")]
    [InlineData("ftp://127.0.0.1/")]
    [InlineData("http://127.0.0.1.nip.io:8080/")]
    public void NotLoopbackIpHttp_Rejected(string url) =>
        Should.Throw<ArgumentException>(() => AdapterOptions.ParseLoopbackMptBaseUrl(url))
            .Message.ShouldContain("loopback");

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    public void Malformed_ThrowsUriFormat(string url) =>
        Should.Throw<UriFormatException>(() => AdapterOptions.ParseLoopbackMptBaseUrl(url));

    /// <summary>On Unix <c>/relative</c> parses as <c>file:///relative</c>: refused as non-http, not a crash.</summary>
    [Fact]
    public void UnixAbsolutePath_ParsedAsFileUri_Rejected() =>
        Should.Throw<ArgumentException>(() => AdapterOptions.ParseLoopbackMptBaseUrl("/relative"));

    /// <summary>
    /// Expected: user-info in an MPT base URL is rejected (a credential in config would travel in a URL and the
    /// key is meant to be only <c>NTS_MPT_API_KEY</c>). Written for the correct behaviour; see the QA report.
    /// </summary>
    [Fact]
    public void UserInfo_Rejected() =>
        Should.Throw<ArgumentException>(() => AdapterOptions.ParseLoopbackMptBaseUrl("http://user:pw@127.0.0.1:8080/"));
}

public sealed class QaListenGuardTests
{
    [Theory]
    [InlineData("::1")]
    [InlineData("[::1]")]
    [InlineData("127.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    public void Loopback_OnlyInDevelopment(string address)
    {
        TlsPrivateAddressGuard.Check(address, isDevelopment: true).ShouldBeNull();
        TlsPrivateAddressGuard.Check(address, isDevelopment: false).ShouldBe(TlsPrivateAddressGuard.LoopbackMessage);
    }

    [Theory]
    [InlineData("::ffff:0.0.0.0")]
    [InlineData("0.0.0.0")]
    [InlineData("[::]")]
    public void AnyAddress_RefusedEvenInDevelopment(string address) =>
        TlsPrivateAddressGuard.Check(address, isDevelopment: true).ShouldBe(TlsPrivateAddressGuard.AnyAddressMessage);

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.0")]
    [InlineData("192.169.0.1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("255.255.255.255")]
    public void NonPrivate_Refused(string address) =>
        TlsPrivateAddressGuard.Check(address, isDevelopment: false).ShouldNotBeNull();

    [Theory]
    [InlineData("172.16.0.0")]
    [InlineData("172.31.255.255")]
    [InlineData("fd12:3456::1")]
    [InlineData("fc00::1")]
    [InlineData("::ffff:10.0.0.5")]
    public void Private_Accepted(string address) =>
        TlsPrivateAddressGuard.Check(address, isDevelopment: false).ShouldBeNull();

    [Theory]
    [InlineData("localhost")]
    [InlineData("node.internal")]
    [InlineData("10.0.0.5.")]
    [InlineData("10.0.0.5:8090")]
    public void NotAnIpLiteral_Refused(string address) =>
        TlsPrivateAddressGuard.Check(address, isDevelopment: true).ShouldNotBeNull();

    [Fact]
    public void Check_AndParse_AgreeOnEveryAcceptedForm()
    {
        foreach (var address in new[] { "10.0.0.5", "[fd00::1]", "::ffff:192.168.0.9" })
        {
            TlsPrivateAddressGuard.Check(address, false).ShouldBeNull(address);
            Should.NotThrow(() => TlsPrivateAddressGuard.Parse(address), address);
        }
    }
}

public sealed class QaCertificateTests
{
    private static readonly DateTimeOffset NotAfter = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WrongKeyPair_Refused_NotLoaded()
    {
        using var files = new CertFiles(NotAfter.AddDays(-100), NotAfter);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(files.KeyPath, other.ExportPkcs8PrivateKeyPem());

        var failure = TlsValidator.TryLoad(files.CertPath, files.KeyPath, new ManualTimeProvider(NotAfter.AddDays(-5)), out var loaded);

        failure.ShouldNotBeNull();
        loaded.ShouldBeNull();
    }

    [Fact]
    public void KeyOfDifferentAlgorithm_Refused()
    {
        using var files = new CertFiles(NotAfter.AddDays(-100), NotAfter);
        using var rsa = RSA.Create(2048);
        File.WriteAllText(files.KeyPath, rsa.ExportPkcs8PrivateKeyPem());

        TlsValidator.TryLoad(files.CertPath, files.KeyPath, new ManualTimeProvider(NotAfter.AddDays(-5)), out var loaded)
            .ShouldNotBeNull();
        loaded.ShouldBeNull();
    }

    [Fact]
    public void NotAfterBoundary_ExpiresTomorrow_Ok_ThenExpired()
    {
        using var files = new CertFiles(NotAfter.AddDays(-100), NotAfter);

        TlsValidator.TryLoad(files.CertPath, files.KeyPath, new ManualTimeProvider(NotAfter.AddDays(-1)), out var tomorrow)
            .ShouldBeNull();
        tomorrow!.NotAfter.ShouldBe(NotAfter);
        tomorrow.Certificate.Dispose();

        TlsValidator.TryLoad(files.CertPath, files.KeyPath, new ManualTimeProvider(NotAfter), out var atBoundary)
            .ShouldBeNull("NotAfter itself is still valid (RFC 5280: valid through NotAfter inclusive)");
        atBoundary!.Certificate.Dispose();

        TlsValidator.TryLoad(files.CertPath, files.KeyPath, new ManualTimeProvider(NotAfter.AddSeconds(1)), out var expired)
            .ShouldBe(TlsValidator.ExpiredMessage);
        expired.ShouldBeNull();
    }

    [Fact]
    public void NotBeforeBoundary_NotYetValid()
    {
        using var files = new CertFiles(NotAfter.AddDays(-100), NotAfter);
        var notBefore = NotAfter.AddDays(-100);

        TlsValidator.TryLoad(files.CertPath, files.KeyPath, new ManualTimeProvider(notBefore.AddSeconds(-1)), out _)
            .ShouldBe(TlsValidator.NotYetValidMessage);
        TlsValidator.TryLoad(files.CertPath, files.KeyPath, new ManualTimeProvider(notBefore), out var ok).ShouldBeNull();
        ok!.Certificate.Dispose();
    }

    [Fact]
    public void Fingerprint_IsUpperHexSha256OfDer_NoColons()
    {
        using var files = new CertFiles(NotAfter.AddDays(-100), NotAfter);

        TlsValidator.TryLoad(files.CertPath, files.KeyPath, new ManualTimeProvider(NotAfter.AddDays(-1)), out var loaded)
            .ShouldBeNull();

        loaded!.Sha256Fingerprint.ShouldBe(files.Sha256);
        loaded.Sha256Fingerprint.ShouldMatch("^[0-9A-F]{64}$");
        loaded.Certificate.Dispose();
    }

    [Fact]
    public void GarbageCert_And_EmptyFiles_Refused_NotThrown()
    {
        using var files = new CertFiles(NotAfter.AddDays(-100), NotAfter);
        var clock = new ManualTimeProvider(NotAfter.AddDays(-1));

        File.WriteAllText(files.CertPath, "-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----\n");
        TlsValidator.TryLoad(files.CertPath, files.KeyPath, clock, out _).ShouldBe(TlsValidator.UnparsableMessage);

        File.WriteAllText(files.CertPath, string.Empty);
        TlsValidator.TryLoad(files.CertPath, files.KeyPath, clock, out _).ShouldBe(TlsValidator.UnparsableMessage);
    }

    [Fact]
    public void DirectoryInsteadOfFile_Refused_NotThrown()
    {
        using var files = new CertFiles(NotAfter.AddDays(-100), NotAfter);

        // File.Exists is false for a directory, so this is the "missing" path rather than a crash.
        TlsValidator.TryLoad(files.Directory, files.KeyPath, new ManualTimeProvider(NotAfter.AddDays(-1)), out _)
            .ShouldBe(TlsValidator.MissingMessage);
    }

    /// <summary>SEC-3: a key a group or other can read is refused with a fixed message, not loaded.</summary>
    [Fact]
    public void KeyAccessibleByGroup_Refused()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var files = new CertFiles(NotAfter.AddDays(-100), NotAfter);
        File.SetUnixFileMode(files.KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        TlsValidator.TryLoad(files.CertPath, files.KeyPath, new ManualTimeProvider(NotAfter.AddDays(-5)), out var loaded)
            .ShouldBe(TlsValidator.KeyModeMessage);
        loaded.ShouldBeNull();
    }

    [Fact]
    public void Key0600_Loads()
    {
        using var files = new CertFiles(NotAfter.AddDays(-100), NotAfter);

        TlsValidator.TryLoad(files.CertPath, files.KeyPath, new ManualTimeProvider(NotAfter.AddDays(-5)), out var loaded)
            .ShouldBeNull();
        loaded!.Certificate.Dispose();
    }

    /// <summary>SEC-3: a file the process may not open is the fixed "unreadable" message, never an unhandled exception.</summary>
    [Fact]
    public void UnreadableCertFile_FixedMessage_NotThrown()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var files = new CertFiles(NotAfter.AddDays(-100), NotAfter);
        File.SetUnixFileMode(files.CertPath, UnixFileMode.None);
        try
        {
            File.OpenRead(files.CertPath).Dispose();
            return; // Running as a user the mode does not bind (root): nothing to prove.
        }
        catch (UnauthorizedAccessException)
        {
            // Expected: the validator must turn this into a message.
        }

        TlsValidator.TryLoad(files.CertPath, files.KeyPath, new ManualTimeProvider(NotAfter.AddDays(-5)), out _)
            .ShouldBe(TlsValidator.UnreadableMessage);
    }
}

/// <summary>QA-3 / SEC-5: the options validator's loopback proxy URL and absolute tool paths.</summary>
public sealed class QaOptionsValidatorTests
{
    [Fact]
    public void Defaults_AreValid_AndNvidiaSmiDefaultsToUsrBin()
    {
        var options = new AdapterOptions { ListenAddress = "10.20.30.40" };

        options.NvidiaSmiPath.ShouldBe("/usr/bin/nvidia-smi");
        Failures(options).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("http://10.0.0.5:8001/health")]
    [InlineData("http://example.com/health")]
    [InlineData("https://127.0.0.1:8001/health")]
    [InlineData("http://localhost:8001/health")]
    [InlineData("not a url")]
    public void ProxyHealthUrl_NotLoopbackHttp_Refused(string url) =>
        Failures(new AdapterOptions { ListenAddress = "10.20.30.40", ProxyHealthUrl = url })
            .ShouldContain(f => f.Contains("Adapter:ProxyHealthUrl", StringComparison.Ordinal));

    [Theory]
    [InlineData("http://127.0.0.1:8001/health")]
    [InlineData("http://[::1]:8001/health")]
    public void ProxyHealthUrl_Loopback_Accepted(string url) =>
        Failures(new AdapterOptions { ListenAddress = "10.20.30.40", ProxyHealthUrl = url }).ShouldBeEmpty();

    [Fact]
    public void RelativeToolPaths_Refused()
    {
        var failures = Failures(new AdapterOptions
        {
            ListenAddress = "10.20.30.40",
            NvidiaSmiPath = "nvidia-smi",
            FfmpegPath = "ffmpeg",
        });

        failures.ShouldContain(f => f.Contains("Adapter:NvidiaSmiPath", StringComparison.Ordinal));
        failures.ShouldContain(f => f.Contains("Adapter:FfmpegPath", StringComparison.Ordinal));
    }

    private static List<string> Failures(AdapterOptions options) =>
        [.. new AdapterOptionsValidator(new FakeHostEnvironment("Production")).Validate(null, options).Failures ?? []];
}

/// <summary>QA-2 / SEC-4: the real <c>AdapterHost.RunAsync</c>, driven by arguments (no process-wide environment).</summary>
public sealed class QaHostRunTests : IDisposable
{
    private const string Key = "adapter-key-0123456789abcdef0123456789";
    private const string NextKey = "adapter-next-key-0123456789abcdef012345";

    private readonly CertFiles _cert = CertFiles.Valid();
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "nts-run-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _cert.Dispose();
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    [Fact]
    public async Task InvalidOptions_ExitTwo_NotAnUnhandledException()
    {
        var exit = await AdapterHost.RunAsync(Args(FreePort(), "--Adapter:PollSeconds=0"), TestContext.Current.CancellationToken);

        exit.ShouldBe(StartupKeyChecks.ExitCode);
    }

    [Fact]
    public async Task ConfigDeclaredKestrelEndpoint_ExitTwo()
    {
        var exit = await AdapterHost.RunAsync(
            Args(FreePort(), "--Kestrel:Endpoints:Extra:Url=http://127.0.0.1:" + FreePort()),
            TestContext.Current.CancellationToken);

        exit.ShouldBe(StartupKeyChecks.ExitCode);
    }

    /// <summary>
    /// <c>--urls</c> is the same hosting setting as <c>ASPNETCORE_URLS</c>: it must not open a second listener.
    /// </summary>
    [Fact]
    public async Task HostingUrls_AreIgnored_OnlyTheGuardedEndpointBinds()
    {
        var port = FreePort();
        var extra = FreePort();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = AdapterHost.RunAsync(Args(port, "--urls", "http://127.0.0.1:" + extra), stop.Token);
        try
        {
            await WaitUntilServingAsync(port, run);

            using var probe = new TcpClient();
            await Should.ThrowAsync<SocketException>(
                async () => await probe.ConnectAsync(IPAddress.Loopback, extra, TestContext.Current.CancellationToken));
        }
        finally
        {
            await stop.CancelAsync();
        }

        (await run).ShouldBe(0);
    }

    private string[] Args(int port, params string[] extra) =>
    [
        "--environment", "Development",
        "--Adapter:ListenAddress=127.0.0.1",
        "--Adapter:Port=" + port,
        "--Adapter:DataDir=" + _dataDir,
        "--Adapter:CertPath=" + _cert.CertPath,
        "--Adapter:KeyPath=" + _cert.KeyPath,
        "--RenderNodeServer:ApiKey=" + Key,
        // Pin NEXT too: the child-environment test sets a short ADAPTER_API_KEY_NEXT process-wide in parallel.
        "--RenderNodeServer:ApiKeyNext=" + NextKey,
        .. extra,
    ];

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitUntilServingAsync(int port, Task<int> run)
    {
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var http = new HttpClient(handler);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            run.IsCompleted.ShouldBeFalse("the host exited before it served");
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the host did not start serving in time");
            try
            {
                using var response = await http.GetAsync(
                    $"https://127.0.0.1:{port}/v1/health", TestContext.Current.CancellationToken);
                return;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }
    }
}
