using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.MockMpt.Fixtures;
using NightTales.MockMpt.Images;
using NightTales.MockMpt.Media;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using Shouldly;
using Xunit;

namespace NightTales.MockMpt.Tests.TestHost;

/// <summary>
/// The mock under test in a real Kestrel on <c>http://127.0.0.1:0</c>: the same <c>MockHost.Create</c> the
/// native <c>dotnet run</c> uses, so every test exercises the real wiring, routing, headers and ticker. One
/// host per test: the in-memory store starts empty, so no test can be reordered into another's state.
/// </summary>
internal sealed class MockMptTestHost : IAsyncDisposable
{
    /// <summary>A 32-byte key, the shortest the D15 S3 validator accepts.</summary>
    internal const string ApiKey = "0123456789abcdef0123456789abcdef";

    private readonly WebApplication _app;

    private MockMptTestHost(WebApplication app, MockMptOptions options)
    {
        _app = app;
        Options = options;
        Media = app.Services.GetRequiredService<CannedMediaSet>();
        Fixtures = app.Services.GetRequiredService<FixtureSet>();
        PlanSink = app.Services.GetRequiredService<IImagePlanSink>().ShouldBeOfType<MockImageReport>();
    }

    internal MockMptOptions Options { get; }

    internal CannedMediaSet Media { get; }

    internal FixtureSet Fixtures { get; }

    internal MockImageReport PlanSink { get; }

    internal HttpClient Client { get; private init; } = null!;

    internal IServiceProvider Services => _app.Services;

    /// <summary>The fast defaults: a 1-second render so the progress theories stay quick.</summary>
    internal static MockMptOptions DefaultOptions => new()
    {
        ApiKey = ApiKey,
        RenderSeconds = 1,
        VideoSeconds = 10,
        MaxConcurrentTasks = 2,
        FixturesDirectory = FixtureFiles.Root,
        MediaDirectory = MediaCache.Directory,
        Fixture = null,
    };

    internal static async Task<MockMptTestHost> StartAsync(MockMptOptions? options = null)
    {
        var effective = options ?? DefaultOptions;
        var app = MockHost.Create(effective, "http://127.0.0.1:0", LogLevel.Warning);
        await app.StartAsync(TestContext.Current.CancellationToken);

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        var address = addresses?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("The mock host did not bind an address.");

        return new MockMptTestHost(app, effective)
        {
            Client = new HttpClient { BaseAddress = new Uri(address) },
        };
    }

    internal Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        HttpContent? content = null,
        string? apiKey = ApiKey)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        if (apiKey is not null)
        {
            request.Headers.TryAddWithoutValidation(RenderNodeHeaders.ApiKey, apiKey);
        }

        return Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    internal Task<HttpResponseMessage> GetAsync(string path, string? apiKey = ApiKey) =>
        SendAsync(HttpMethod.Get, path, content: null, apiKey);

    internal Task<HttpResponseMessage> PostAsync(string path, string body, string? apiKey = ApiKey) =>
        SendAsync(HttpMethod.Post, path, Json(body), apiKey);

    internal Task<HttpResponseMessage> DeleteAsync(string path, string? apiKey = ApiKey) =>
        SendAsync(HttpMethod.Delete, path, content: null, apiKey);

    /// <summary>POSTs a body and returns the task id of the accepted 202.</summary>
    internal async Task<string> SubmitAsync(string body)
    {
        using var response = await PostAsync(RenderNodeRoutes.Renders, body);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var accepted = await ReadAsync<AdapterAcceptedV1>(response);
        return accepted.TaskId.ToString();
    }

    internal async Task<RenderStatusV1> StatusAsync(string taskId)
    {
        using var response = await GetAsync($"/v1/renders/{taskId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await ReadAsync<RenderStatusV1>(response);
    }

    /// <summary>Polls every 100 ms until the task is terminal; returns every observed status in order.</summary>
    internal async Task<IReadOnlyList<RenderStatusV1>> PollUntilTerminalAsync(string taskId, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        var statuses = new List<RenderStatusV1>();
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await StatusAsync(taskId);
            statuses.Add(status);
            if (RenderStates.IsTerminal(status.State))
            {
                return statuses;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException($"task {taskId} did not become terminal before the timeout");
    }

    internal static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    internal static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonSerializer.Deserialize<T>(text, RenderNodeJson.Strict)
            ?? throw new InvalidOperationException($"The response body is not a {typeof(T).Name}: {text}");
    }

    internal static async Task<AdapterErrorV1> ReadErrorAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonSerializer.Deserialize<AdapterErrorV1>(text, RenderNodeJson.Strict)
            ?? throw new InvalidOperationException($"The response body is not an envelope: {text}");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>
/// The canned media for the whole test run: generated once with the pinned <c>ffmpeg -f lavfi</c> recipe into a
/// temp directory (never the repository), then reused by every host (11 F25).
/// </summary>
internal static class MediaCache
{
    private static readonly Lazy<CannedMediaSet> Cached = new(
        () => CannedMediaSet.Ensure(Path.Combine(Path.GetTempPath(), "nts-mock-mpt-tests", "media")),
        LazyThreadSafetyMode.ExecutionAndPublication);

    internal static CannedMediaSet Value => Cached.Value;

    internal static string Directory => Value.Directory;
}
