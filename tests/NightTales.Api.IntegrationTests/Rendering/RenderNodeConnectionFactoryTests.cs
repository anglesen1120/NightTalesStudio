using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NightTales.Api.IntegrationTests.Seeding;
using NightTales.Application.Rendering;
using NightTales.Domain.Common;
using NightTales.Domain.Rendering;
using NightTales.Infrastructure.Rendering;
using NightTales.RenderNode.Contract;
using NightTales.TestKit.Rendering;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Rendering;

/// <summary>
/// The pinned-TLS connection factory of 11 README D13/F13/S3 (11.3.3-T1 plan §9, D20 H12): it talks to a
/// real HTTPS listener over a self-signed certificate, so the pin comparison, the expiry refusal, the
/// redirect policy, the <c>x-api-key</c> header and the certificate-expiry gauge are exercised by an actual
/// handshake. The authentication cases are refused before any socket is opened.
/// </summary>
/// <remarks>
/// The listener is Kestrel on an ephemeral loopback port (<see cref="FakeRenderNodeServer"/>): no external
/// node, no GPU and no outbound network (D18).
/// </remarks>
[Trait("Category", "Integration")]
public sealed class RenderNodeConnectionFactoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
    private const string ApiKeyValue = "alpha-key-value";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AcceptsExactlyPinnedSha256()
    {
        var clock = new FakeClock(Start);
        using var metrics = new NodeMetrics();
        using var certificate = TestCertificates.Server(Start.AddDays(-1), Start.AddDays(30));
        await using var server = await FakeRenderNodeServer.StartAsync(certificate, Ct);
        server.Body = RenderNodeHealthBodies.Ok();

        var factory = Factory(Environments.Production, metrics, clock);
        var node = Node("alpha", server.BaseUrl, server.PinSha256);
        using var client = factory.Create(node);

        using var response = await client.GetAsync(RenderNodeRoutes.Health, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "the handshake used exactly the pinned certificate");
    }

    [Fact]
    public async Task AcceptsPinRegardlessOfCase()
    {
        var clock = new FakeClock(Start);
        using var metrics = new NodeMetrics();
        using var certificate = TestCertificates.Server(Start.AddDays(-1), Start.AddDays(30));
        await using var server = await FakeRenderNodeServer.StartAsync(certificate, Ct);
        server.Body = RenderNodeHealthBodies.Ok();

        var node = Node("alpha", server.BaseUrl, server.PinSha256.ToLowerInvariant());
        node.TlsCertSha256.ShouldBe(server.PinSha256, "the domain normalises a lower-case pin to upper-case hex");
        using var client = Factory(Environments.Production, metrics, clock).Create(node);

        (await client.GetAsync(RenderNodeRoutes.Health, Ct)).StatusCode.ShouldBe(
            HttpStatusCode.OK,
            "a lower-case pin still matches the certificate (D13 compares case-insensitively)");
    }

    [Fact]
    public async Task SendsApiKeyHeader()
    {
        var clock = new FakeClock(Start);
        using var metrics = new NodeMetrics();
        using var certificate = TestCertificates.Server(Start.AddDays(-1), Start.AddDays(30));
        await using var server = await FakeRenderNodeServer.StartAsync(certificate, Ct);
        server.Body = RenderNodeHealthBodies.Ok();

        using var client = Factory(Environments.Production, metrics, clock).Create(Node("alpha", server.BaseUrl, server.PinSha256));
        (await client.GetAsync(RenderNodeRoutes.Health, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        server.Requests.ShouldHaveSingleItem().ApiKey.ShouldBe(
            ApiKeyValue,
            "the adapter key lives only in the client's x-api-key default header (D11/S4)");
    }

    [Fact]
    public async Task RejectsOtherCertificate()
    {
        var clock = new FakeClock(Start);
        using var metrics = new NodeMetrics();
        using var certificate = TestCertificates.Server(Start.AddDays(-1), Start.AddDays(30));
        await using var server = await FakeRenderNodeServer.StartAsync(certificate, Ct);

        // A valid 64-hex pin that is not this certificate's fingerprint: a rotation that has not been
        // published yet must fail closed, never fall back to unvalidated TLS.
        var node = Node("alpha", server.BaseUrl, new string('A', WorkerNode.TlsCertSha256Length));
        using var client = Factory(Environments.Production, metrics, clock).Create(node);

        await Should.ThrowAsync<HttpRequestException>(() => client.GetAsync(RenderNodeRoutes.Health, Ct));
        server.Requests.ShouldBeEmpty("the callback refused the certificate during the handshake");
    }

    [Fact]
    public async Task RejectsExpiredCertificate()
    {
        var clock = new FakeClock(Start);
        using var metrics = new NodeMetrics();
        using var certificate = TestCertificates.Server(Start.AddDays(-2), Start.AddMinutes(-1));
        await using var server = await FakeRenderNodeServer.StartAsync(certificate, Ct);

        var node = Node("alpha", server.BaseUrl, server.PinSha256);
        using var client = Factory(Environments.Production, metrics, clock).Create(node);

        await Should.ThrowAsync<HttpRequestException>(
            () => client.GetAsync(RenderNodeRoutes.Health, Ct),
            "a pinned certificate whose NotAfter is in the past is refused, not accepted unvalidated");
    }

    [Fact]
    public async Task EmitsCertExpiryDaysGauge()
    {
        var clock = new FakeClock(Start);
        using var metrics = new NodeMetrics();
        using var certificate = TestCertificates.Server(Start.AddDays(-1), Start.AddDays(10));
        await using var server = await FakeRenderNodeServer.StartAsync(certificate, Ct);
        server.Body = RenderNodeHealthBodies.Ok();

        var expiryDays = double.NaN;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (ReferenceEquals(instrument.Meter, metrics.Meter))
            {
                owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
        {
            if (string.Equals(instrument.Name, NodeMetrics.CertExpiryDaysInstrumentName, StringComparison.Ordinal))
            {
                expiryDays = measurement;
            }
        });
        listener.Start();

        var node = Node("alpha", server.BaseUrl, server.PinSha256);
        using (var client = Factory(Environments.Production, metrics, clock).Create(node))
        {
            (await client.GetAsync(RenderNodeRoutes.Health, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        listener.RecordObservableInstruments();

        expiryDays.ShouldBe(10d, "the gauge reports whole days to the presented certificate's NotAfter");
    }

    [Fact]
    public async Task DisablesRedirects()
    {
        var clock = new FakeClock(Start);
        using var metrics = new NodeMetrics();
        using var certificate = TestCertificates.Server(Start.AddDays(-1), Start.AddDays(30));
        await using var server = await FakeRenderNodeServer.StartAsync(certificate, Ct);
        server.RedirectLocation = new Uri(server.BaseUrl, "/v1/elsewhere");

        var node = Node("alpha", server.BaseUrl, server.PinSha256);
        using var client = Factory(Environments.Production, metrics, clock).Create(node);

        using var response = await client.GetAsync(RenderNodeRoutes.Health, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Found, "the client returns the redirect instead of following it");
        server.Requests.Count.ShouldBe(1, "following it would silently leave the pinned host (D13)");
    }

    [Fact]
    public void RefusesHttpOutsideDevelopment()
    {
        var clock = new FakeClock(Start);
        using var metrics = new NodeMetrics();
        var factory = Factory(Environments.Production, metrics, clock);
        var node = Node("mock", new Uri("http://127.0.0.1:8090/"), pin: null);

        var exception = Should.Throw<InvalidOperationException>(() => factory.Create(node));

        exception.Message.ShouldContain("https", Case.Insensitive);
        exception.Message.ShouldContain("mock", Case.Sensitive);
    }

    [Fact]
    public void AllowsHttpInDevelopment()
    {
        var clock = new FakeClock(Start);
        using var metrics = new NodeMetrics();
        var factory = Factory(Environments.Development, metrics, clock);
        var node = Node("mock", new Uri("http://127.0.0.1:8090/"), pin: null);

        using var client = factory.Create(node);

        client.BaseAddress.ShouldBe(node.BaseUrl, "the Development mock node is the one http:// exception (F26)");
    }

    [Fact]
    public void NeverLogsKey()
    {
        var clock = new FakeClock(Start);
        using var metrics = new NodeMetrics();
        var factory = Factory(Environments.Production, metrics, clock, ("alpha", ApiKeyValue));

        // The key reaches exactly one place: the created client's default header.
        using (var client = factory.Create(Node("alpha", new Uri("https://alpha.example/"), new string('A', 64))))
        {
            client.DefaultRequestHeaders.GetValues(RenderNodeConnectionFactory.ApiKeyHeaderName)
                .ShouldBe([ApiKeyValue]);
        }

        // A missing key names the variable, never a value — not even another node's key.
        var exception = Should.Throw<InvalidOperationException>(
            () => factory.Create(Node("beta", new Uri("https://beta.example/"), new string('B', 64))));

        exception.Message.ShouldContain(RenderNodeApiKey.VariableName("beta"), Case.Sensitive);
        exception.Message.ShouldNotContain(ApiKeyValue, Case.Sensitive);
    }

    private static RenderNodeConnectionFactory Factory(
        string environment,
        NodeMetrics metrics,
        IClock clock,
        params (string Name, string Value)[] keys)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [RenderNodeApiKey.VariableName("alpha")] = ApiKeyValue,
            [RenderNodeApiKey.VariableName("mock")] = "mock-key-value",
        };

        foreach (var (name, value) in keys)
        {
            settings[RenderNodeApiKey.VariableName(name)] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return new RenderNodeConnectionFactory(
            configuration,
            new TestHostEnvironment { EnvironmentName = environment },
            metrics,
            clock);
    }

    private static WorkerNode Node(string name, Uri baseUrl, string? pin) =>
        WorkerNode.Create(name, baseUrl, capacity: 2, tlsCertSha256: pin, gpuModel: null).Value;
}
