using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NightTales.TestKit.Rendering;

/// <summary>
/// A real HTTPS listener on an ephemeral loopback port that presents a test certificate and answers
/// <c>GET /v1/health</c> (11.3.3-T1 plan §9): the only way to exercise the pinned-TLS callback of
/// <c>RenderNodeConnectionFactory</c> is a real handshake, so the factory tests talk to this instead of a
/// fake handler. No GPU, no external network, no node (D18).
/// </summary>
/// <remarks>
/// The listener is Kestrel bound to <c>127.0.0.1:0</c> with the certificate supplied by
/// <see cref="TestCertificates"/>, so the certificate's fingerprint is the pin the factory must accept and
/// an expired one is refused by the same code path production runs.
/// </remarks>
public sealed class FakeRenderNodeServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly X509Certificate2 _certificate;
    private readonly ConcurrentQueue<RecordedNodeRequest> _requests = new();

    private FakeRenderNodeServer(WebApplication app, X509Certificate2 certificate)
    {
        _app = app;
        _certificate = certificate;
    }

    /// <summary>The base URL of the listener, e.g. <c>https://127.0.0.1:53123/</c>.</summary>
    public Uri BaseUrl { get; private set; } = null!;

    /// <summary>The SHA-256 fingerprint the client must pin to accept this server.</summary>
    public string PinSha256 => TestCertificates.PinSha256(_certificate);

    /// <summary>The status the next <c>/v1/health</c> answers with.</summary>
    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

    /// <summary>The body the next <c>/v1/health</c> answers with.</summary>
    public string Body { get; set; } = "{}";

    /// <summary>When set, <c>/v1/health</c> answers 302 to this location instead of <see cref="StatusCode"/>.</summary>
    public Uri? RedirectLocation { get; set; }

    /// <summary>Every request the listener received, in arrival order.</summary>
    public IReadOnlyList<RecordedNodeRequest> Requests => [.. _requests];

    /// <summary>Starts the listener on an ephemeral loopback port.</summary>
    /// <param name="certificate">The certificate the listener presents; its fingerprint is the pin.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    public static async Task<FakeRenderNodeServer> StartAsync(
        X509Certificate2 certificate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));

        var app = builder.Build();
        var server = new FakeRenderNodeServer(app, certificate);

        app.Run(server.HandleAsync);
        await app.StartAsync(cancellationToken);

        var addresses = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel reported no bound address.");

        server.BaseUrl = new Uri($"{addresses.Addresses.Single().TrimEnd('/')}/");

        return server;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    private async Task HandleAsync(HttpContext context)
    {
        _requests.Enqueue(new RecordedNodeRequest(
            context.Request.Method,
            context.Request.Path.Value ?? "/",
            context.Request.Headers.TryGetValue("x-api-key", out var key) ? key.ToString() : null));

        if (RedirectLocation is { } location)
        {
            context.Response.StatusCode = StatusCodes.Status302Found;
            context.Response.Headers.Location = location.ToString();

            return;
        }

        context.Response.StatusCode = (int)StatusCode;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsync(Body, context.RequestAborted).ConfigureAwait(false);
    }
}
