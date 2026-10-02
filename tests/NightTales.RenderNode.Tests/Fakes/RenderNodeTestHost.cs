using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using Xunit;

namespace NightTales.RenderNode.Tests.Fakes;

/// <summary>
/// The core under test in a real Kestrel on <c>http://127.0.0.1:0</c> (no <c>TestServer</c> package is on the
/// pinned list, and a real listener is what proves routing, the per-endpoint body cap and the response headers).
/// One host per test: the fakes and the store start empty, so no test can be reordered into another's state, and
/// every port is ephemeral.
/// </summary>
internal sealed class RenderNodeTestHost : IAsyncDisposable
{
    /// <summary>A 32-byte key, the shortest the D15 S3 validator accepts.</summary>
    internal const string DefaultApiKey = "0123456789abcdef0123456789abcdef";

    private readonly WebApplication _app;

    private RenderNodeTestHost(WebApplication app) => _app = app;

    internal RenderNodeServerOptions ServerOptions { get; private init; } = new();

    internal CountingTaskStore Store { get; private init; } = new();

    internal FakeRenderBackend Backend { get; private init; } = new();

    internal FakeImagePlanSink PlanSink { get; private init; } = new();

    internal ScriptedHealthSource Health { get; private init; } = new();

    internal FakeImageProbeBackend Probe { get; private init; } = new();

    internal TestTimeProvider Clock { get; private init; } = new();

    internal required HttpClient Client { get; init; }

    internal static async Task<RenderNodeTestHost> StartAsync(
        Action<RenderNodeServerOptions>? configure = null,
        bool withProbeBackend = true,
        ILoggerProvider? loggerProvider = null)
    {
        var options = new RenderNodeServerOptions
        {
            ApiKey = DefaultApiKey,
            MaxBodyBytes = RenderNodeServerOptions.DefaultMaxBodyBytes,
            AuthFailuresPerMinute = 10,
            MaxConcurrentTasks = 1,
        };
        configure?.Invoke(options);

        var clock = new TestTimeProvider();
        var store = new CountingTaskStore();
        var backend = new FakeRenderBackend();
        var planSink = new FakeImagePlanSink();
        var health = new ScriptedHealthSource();
        var probe = new FakeImageProbeBackend();

        var builder = WebApplication.CreateBuilder();
        if (loggerProvider is null)
        {
            builder.Logging.SetMinimumLevel(LogLevel.None);
        }
        else
        {
            // The QA "keys never appear in logs" check: real logging is on at Trace — the level at which
            // framework request logging would surface a header — and captured in memory.
            builder.Logging.SetMinimumLevel(LogLevel.Trace).AddProvider(loggerProvider);
        }
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton<ITaskStore>(store);
        builder.Services.AddSingleton<IRenderBackend>(backend);
        builder.Services.AddSingleton<IImagePlanSink>(planSink);
        builder.Services.AddSingleton<INodeHealthSource>(health);
        if (withProbeBackend)
        {
            // 11.3.5-T2 registers the MPT-backed probe; without it the route must answer 503 (12 CR-19).
            builder.Services.AddSingleton<IImageProbeBackend>(probe);
        }

        builder.Services.AddRenderNodeServer(builder.Configuration.GetSection(RenderNodeServerOptions.SectionName));

        // Last registration wins for IOptions<T>, so the test's instance is what every endpoint reads.
        builder.Services.AddSingleton<IOptions<RenderNodeServerOptions>>(Options.Create(options));

        var app = builder.Build();
        app.UseRenderNodeServer();
        app.MapRenderNodeV1();

        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        var address = addresses?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("The test host did not bind an address.");
        var client = new HttpClient { BaseAddress = new Uri(address) };

        return new RenderNodeTestHost(app)
        {
            Client = client,
            ServerOptions = options,
            Store = store,
            Backend = backend,
            PlanSink = planSink,
            Health = health,
            Probe = probe,
            Clock = clock,
        };
    }

    /// <summary>Sends a request with the contract's <c>x-api-key</c> header (or without it).</summary>
    internal Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        HttpContent? content = null,
        string? apiKey = DefaultApiKey)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        if (apiKey is not null)
        {
            request.Headers.TryAddWithoutValidation(RenderNodeHeaders.ApiKey, apiKey);
        }

        return Client.SendAsync(request);
    }

    internal Task<HttpResponseMessage> GetAsync(string path, string? apiKey = DefaultApiKey) =>
        SendAsync(HttpMethod.Get, path, content: null, apiKey);

    internal Task<HttpResponseMessage> PostAsync(string path, string body, string? apiKey = DefaultApiKey) =>
        SendAsync(HttpMethod.Post, path, Json(body), apiKey);

    /// <summary>POSTs a body and lets the caller abort the request (the client-disconnect path).</summary>
    internal Task<HttpResponseMessage> PostAsync(string path, string body, CancellationToken cancellationToken, string? apiKey = DefaultApiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = Json(body) };
        if (apiKey is not null)
        {
            request.Headers.TryAddWithoutValidation(RenderNodeHeaders.ApiKey, apiKey);
        }

        return Client.SendAsync(request, cancellationToken);
    }

    internal Task<HttpResponseMessage> DeleteAsync(string path, string? apiKey = DefaultApiKey) =>
        SendAsync(HttpMethod.Delete, path, content: null, apiKey);

    /// <summary>Puts a row in the store, so a task-scoped route has something to read.</summary>
    internal async Task SeedAsync(RenderTaskRecord record)
    {
        var inserted = await Store.InsertAsync(record, CancellationToken.None);
        if (!inserted)
        {
            throw new InvalidOperationException("The test seeded the same task id twice.");
        }
    }

    internal static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    internal static async Task<AdapterErrorV1> ReadErrorAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonSerializer.Deserialize<AdapterErrorV1>(text, RenderNodeJson.Strict)
            ?? throw new InvalidOperationException($"The response body is not an envelope: {text}");
    }

    internal static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonSerializer.Deserialize<T>(text, RenderNodeJson.Strict)
            ?? throw new InvalidOperationException($"The response body is not a {typeof(T).Name}: {text}");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
