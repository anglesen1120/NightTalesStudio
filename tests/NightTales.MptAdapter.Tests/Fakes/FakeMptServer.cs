using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NightTales.MptAdapter.Tests.Fakes;

/// <summary>One recorded MPT request: what the adapter sent, including the two headers D6 fixes.</summary>
internal sealed record RecordedRequest(string Method, string Path, string? ApiKey, string? TaskId, string Body);

/// <summary>
/// The fake MPT of D18: a real in-process Kestrel serving the four routes the adapter uses — <c>GET /ping</c>,
/// <c>POST /api/v1/videos</c>, <c>GET /api/v1/tasks/{id}</c> and <c>DELETE /api/v1/tasks/{id}</c> — with the
/// shapes of 05.4.1-T1's samples (S11/S12/S13). It scripts the task dict (state, progress, <c>failed_stage</c>,
/// <c>error</c>), 404s, delete answers, a redirect and slow responses, and it records every request so a test
/// can assert that a poll did <em>not</em> happen.
/// </summary>
internal sealed class FakeMptServer : IAsyncDisposable
{
    internal const string DefaultApiKey = TestEnvironment.MptApiKey;

    /// <summary>A value MPT really returns in its task dict; a status or a log must never carry it (S7).</summary>
    internal const string SecretScript = "SECRET-SCRIPT-TEXT";

    /// <summary>The other content value MPT returns in the task dict (S7).</summary>
    internal const string SecretTerm = "SECRET-TERM-TEXT";

    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, MptScript> _tasks = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private int _requestCount;

    private FakeMptServer(WebApplication app) => _app = app;

    internal Uri BaseAddress { get; private set; } = new("http://127.0.0.1:0");

    /// <summary>The key the fake accepts; the adapter sends <c>NTS_MPT_API_KEY</c>.</summary>
    internal string ApiKey { get; set; } = DefaultApiKey;

    /// <summary>True makes every guarded route answer 401, as after a key rotation on MPT's side.</summary>
    internal bool RejectEveryKey { get; set; }

    /// <summary>Delays every task/submit answer, for the read-timeout and back-off tests.</summary>
    internal TimeSpan ResponseDelay { get; set; }

    /// <summary>
    /// A per-task status delay on top of <see cref="ResponseDelay"/>, so one task's status call can be held
    /// in flight while another's is answered (the poll-concurrency and cancel-race tests).
    /// </summary>
    internal ConcurrentDictionary<string, TimeSpan> TaskDelays { get; } = new(StringComparer.Ordinal);

    /// <summary>Holds one task's status answer for <paramref name="delay"/>.</summary>
    internal void DelayTask(string mptTaskId, TimeSpan delay) => TaskDelays[mptTaskId] = delay;

    /// <summary>
    /// When set, the task route answers a body of exactly this declared length (SEC-01's declared shape).
    /// </summary>
    internal long? OversizeDeclaredBytes { get; set; }

    /// <summary>When true, the task route answers a chunked body over the adapter's cap (SEC-01).</summary>
    internal bool OversizeChunked { get; set; }

    /// <summary>The task id the fake mints for <c>POST /api/v1/videos</c>.</summary>
    internal string SubmitTaskId { get; set; } = "51ace7c8-bc37-44fd-ad2f-839293c7df7c";

    /// <summary>The status the delete route answers (S13: 200, 404 or 409).</summary>
    internal int DeleteStatus { get; set; } = 200;

    /// <summary>When set, the task route answers 302 to this path.</summary>
    internal string? RedirectTo { get; set; }

    /// <summary>Every request the adapter made, in arrival order.</summary>
    internal IReadOnlyList<RecordedRequest> Requests => [.. _requests];

    /// <summary>How many requests reached the fake at all.</summary>
    internal int RequestCount => Volatile.Read(ref _requestCount);

    internal IReadOnlyList<RecordedRequest> TaskRequests =>
        [.. _requests.Where(request => request.Path.StartsWith("/api/v1/tasks", StringComparison.Ordinal))];

    internal static async Task<FakeMptServer> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.None);
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        var server = new FakeMptServer(app);

        app.MapGet("/ping", async (HttpContext context) =>
        {
            await server.RecordAsync(context).ConfigureAwait(false);
            await WriteAsync(context, StatusCodes.Status200OK, """{"status":200,"message":"pong"}""").ConfigureAwait(false);
        });

        app.MapPost("/api/v1/videos", async (HttpContext context) =>
        {
            await server.RecordAsync(context).ConfigureAwait(false);
            if (!server.Authorized(context))
            {
                await WriteUnauthorizedAsync(context).ConfigureAwait(false);
                return;
            }

            await server.DelayAsync().ConfigureAwait(false);
            await WriteAsync(context, StatusCodes.Status200OK, server.SubmitAnswer()).ConfigureAwait(false);
        });

        app.MapGet("/api/v1/tasks", async (HttpContext context) =>
        {
            await server.RecordAsync(context).ConfigureAwait(false);
            if (!server.Authorized(context))
            {
                await WriteUnauthorizedAsync(context).ConfigureAwait(false);
                return;
            }

            await server.DelayAsync().ConfigureAwait(false);
            await WriteAsync(context, StatusCodes.Status200OK, """{"status":200,"message":"success","data":[]}""")
                .ConfigureAwait(false);
        });

        app.MapGet("/api/v1/tasks/{id}", async (HttpContext context, string id) =>
        {
            await server.RecordAsync(context).ConfigureAwait(false);
            if (!server.Authorized(context))
            {
                await WriteUnauthorizedAsync(context).ConfigureAwait(false);
                return;
            }

            await server.DelayAsync(id).ConfigureAwait(false);

            if (server.OversizeDeclaredBytes is not null || server.OversizeChunked)
            {
                await server.WriteOversizeAsync(context).ConfigureAwait(false);
                return;
            }

            if (server.RedirectTo is { } location)
            {
                context.Response.StatusCode = StatusCodes.Status302Found;
                context.Response.Headers.Location = location;
                return;
            }

            if (!server._tasks.TryGetValue(id, out var script))
            {
                await WriteAsync(context, StatusCodes.Status404NotFound, NotFoundAnswer()).ConfigureAwait(false);
                return;
            }

            await WriteAsync(context, StatusCodes.Status200OK, TaskAnswer(id, script)).ConfigureAwait(false);
        });

        app.MapDelete("/api/v1/tasks/{id}", async (HttpContext context, string id) =>
        {
            await server.RecordAsync(context).ConfigureAwait(false);
            if (!server.Authorized(context))
            {
                await WriteUnauthorizedAsync(context).ConfigureAwait(false);
                return;
            }

            await server.DelayAsync().ConfigureAwait(false);

            if (server.DeleteStatus == StatusCodes.Status200OK)
            {
                server._tasks.TryRemove(id, out _);
            }

            await WriteAsync(context, server.DeleteStatus, server.DeleteAnswer()).ConfigureAwait(false);
        });

        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        var address = addresses?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("The fake MPT did not bind an address.");

        server.BaseAddress = new Uri(address);
        return server;
    }

    /// <summary>Scripts (or re-scripts) one task's observable state; the id is MPT's own.</summary>
    internal void Script(
        string mptTaskId,
        int? state,
        int progress,
        string? failedStage = null,
        string? error = null,
        IReadOnlyList<string>? warnings = null) =>
        _tasks[mptTaskId] = new MptScript(state, progress, failedStage, error, warnings);

    /// <summary>S12: after a restart MPT answers 404 — the record is simply gone.</summary>
    internal void Forget(string mptTaskId) => _tasks.TryRemove(mptTaskId, out _);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    internal async Task<RecordedRequest?> WaitForTaskRequestAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var match = _requests.FirstOrDefault(
                request => string.Equals(request.Path, path, StringComparison.Ordinal));
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>Waits until at least <paramref name="minimum"/> task requests have arrived.</summary>
    internal async Task<bool> WaitForTaskRequestCountAsync(int minimum, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline && TaskRequests.Count < minimum)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }

        return TaskRequests.Count >= minimum;
    }

    private static string TaskAnswer(string id, MptScript script) =>
        new JsonObject
        {
            ["status"] = 200,
            ["message"] = "success",
            ["data"] = new JsonObject
            {
                ["task_id"] = id,
                ["state"] = script.State is { } state ? JsonValue.Create(state) : null,
                ["progress"] = script.Progress,
                ["failed_stage"] = script.FailedStage,
                ["error"] = script.Error,
                ["warnings"] = script.Warnings is { } warnings
                    ? new JsonArray([.. warnings.Select(warning => JsonValue.Create(warning))])
                    : null,

                // The values 05.4.1-T1 measured in MPT's real task dict: the adapter must ignore them (S7).
                ["script"] = SecretScript,
                ["terms"] = new JsonArray(JsonValue.Create(SecretTerm)),
                ["audio_file"] = "<task_dir>/audio.mp3",
                ["materials"] = new JsonArray(JsonValue.Create("<task_dir>/openai-image-x.png.mp4")),
            },
        }.ToJsonString();

    private string SubmitAnswer() =>
        new JsonObject
        {
            ["status"] = 200,
            ["message"] = "success",
            ["data"] = new JsonObject
            {
                ["task_id"] = SubmitTaskId,
                ["state"] = 4,
                ["progress"] = 0,
                ["failed_stage"] = null,
                ["error"] = null,
                ["warnings"] = null,
            },
        }.ToJsonString();

    private static string NotFoundAnswer() => """{"status":404,"message":"fake-mpt: task not found"}""";

    private string DeleteAnswer() => DeleteStatus switch
    {
        StatusCodes.Status404NotFound => """{"status":404,"message":"fake-mpt: task not found"}""",
        StatusCodes.Status409Conflict => """{"status":409,"message":"fake-mpt: task is still running"}""",
        _ => """{"status":200,"message":"success","data":null}""",
    };

    private bool Authorized(HttpContext context) =>
        !RejectEveryKey && string.Equals(
            context.Request.Headers[MptClient.ApiKeyHeader].ToString(),
            ApiKey,
            StringComparison.Ordinal);

    private async Task RecordAsync(HttpContext context)
    {
        Interlocked.Increment(ref _requestCount);

        var body = string.Empty;
        if (string.Equals(context.Request.Method, "POST", StringComparison.Ordinal))
        {
            using var reader = new StreamReader(context.Request.Body);
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        _requests.Enqueue(new RecordedRequest(
            context.Request.Method,
            context.Request.Path.Value ?? string.Empty,
            Header(context, MptClient.ApiKeyHeader),
            Header(context, MptClient.TaskIdHeader),
            body));
    }

    private static string? Header(HttpContext context, string name)
    {
        var value = context.Request.Headers[name].ToString();
        return value.Length == 0 ? null : value;
    }

    private Task DelayAsync() =>
        ResponseDelay > TimeSpan.Zero ? Task.Delay(ResponseDelay) : Task.CompletedTask;

    private Task DelayAsync(string mptTaskId)
    {
        var delay = TaskDelays.TryGetValue(mptTaskId, out var perTask) ? perTask : ResponseDelay;
        return delay > TimeSpan.Zero ? Task.Delay(delay) : Task.CompletedTask;
    }

    /// <summary>
    /// Writes a body over <c>MptClient.MaxResponseBodyBytes</c>: with a declared length when
    /// <see cref="OversizeDeclaredBytes"/> is set, otherwise chunked (no <c>Content-Length</c>) so only a
    /// counted read can stop it. A client that rejects the answer stops reading, so the write may be cut off.
    /// </summary>
    private async Task WriteOversizeAsync(HttpContext context)
    {
        context.Response.ContentType = "application/json";

        try
        {
            const int Chunk = 64 * 1024;
            var buffer = new byte[Chunk];
            Array.Fill(buffer, (byte)'x');

            if (OversizeDeclaredBytes is { } declared)
            {
                context.Response.ContentLength = declared;
                long remaining = declared;
                while (remaining > 0)
                {
                    var write = (int)Math.Min(buffer.Length, remaining);
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, write)).ConfigureAwait(false);
                    remaining -= write;
                }

                return;
            }

            long written = 0;
            while (written <= MptClient.MaxResponseBodyBytes)
            {
                await context.Response.Body.WriteAsync(buffer).ConfigureAwait(false);
                written += buffer.Length;
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            // The adapter stopped reading at its cap and closed the connection: the test already has its answer.
        }
    }

    private static Task WriteUnauthorizedAsync(HttpContext context) =>
        WriteAsync(context, StatusCodes.Status401Unauthorized, """{"status":401,"message":"invalid API key"}""");

    private static async Task WriteAsync(HttpContext context, int status, string json)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(json).ConfigureAwait(false);
    }

    /// <summary>One scripted MPT task dict, the five fields the adapter reads.</summary>
    private sealed record MptScript(
        int? State,
        int Progress,
        string? FailedStage,
        string? Error,
        IReadOnlyList<string>? Warnings);
}
