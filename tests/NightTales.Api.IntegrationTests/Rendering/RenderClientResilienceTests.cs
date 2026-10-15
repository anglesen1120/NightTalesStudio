using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Rendering;
using NightTales.Domain.Rendering;
using NightTales.Infrastructure.Rendering;
using NightTales.RenderNode.Contract;
using NightTales.TestKit.Rendering;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Rendering;

/// <summary>
/// The two render-node pipelines of 09.6.2-T1 (09 README BD6, WF08/W9) over the scripted node: what is
/// retried, what is deliberately not, and how the breaker opens and half-opens on a clock the test owns
/// (<see cref="ManualTimeProvider"/>), so no test ever sleeps.
/// </summary>
public sealed class RenderClientResilienceTests
{
    private const string TaskId = "3f7b9c2e-1a44-4d0f-9e21-6c5b8a7d0e11";

    private const string SubmitFixture = "render-submit.v1.landscape-vi.json";

    [Fact]
    public async Task DeleteIsNotRetried()
    {
        var handler = new ScriptedNodeHandler(NodeHttpStep.Transport());
        var time = new ManualTimeProvider();
        using var provider = Provider(handler, time);
        var client = provider.GetRequiredService<IRenderClient>();

        var deleted = await DriveAsync(client.DeleteAsync(Node(), TaskId, CancellationToken.None), time);

        deleted.IsError.ShouldBeTrue();
        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].Method.ShouldBe("DELETE");
        handler.Requests[0].Path.ShouldBe($"/v1/renders/{TaskId}");

        RenderClientErrors.TryGetFailure(deleted.Errors[0], out var failure).ShouldBeTrue();
        failure!.Kind.ShouldBe(RenderClientFailureKind.TransientFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderNodeError);

        // The positive control on the very same fake: the retrying pipeline calls the node four times
        // (the first attempt plus three retries) for the same transport failure a DELETE may not repeat.
        handler.Rescript(NodeHttpStep.Transport());
        var status = await DriveAsync(client.GetStatusAsync(Node(), TaskId, CancellationToken.None), time);

        status.IsError.ShouldBeTrue();
        handler.Requests.Count.ShouldBe(5);
        handler.Requests[4].Method.ShouldBe("GET");
    }

    [Fact]
    public async Task GetStatus_RetriesThreeTimes_ThenFails()
    {
        var handler = new ScriptedNodeHandler(NodeHttpStep.Answer(500, Envelope(AdapterErrorCodes.InternalError)));
        var time = new ManualTimeProvider();
        using var provider = Provider(handler, time);
        var client = provider.GetRequiredService<IRenderClient>();

        var status = await DriveAsync(client.GetStatusAsync(Node(), TaskId, CancellationToken.None), time);

        status.IsError.ShouldBeTrue();
        handler.Requests.Count.ShouldBe(4);
        handler.Requests.Select(request => request.Path).Distinct().Count().ShouldBe(1);
        handler.Requests[0].Path.ShouldBe($"/v1/renders/{TaskId}");

        RenderClientErrors.TryGetFailure(status.Errors[0], out var failure).ShouldBeTrue();
        failure!.Kind.ShouldBe(RenderClientFailureKind.TransientFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderNodeError);
    }

    [Fact]
    public async Task SubmitAndCancel_AreRetried()
    {
        var handler = new ScriptedNodeHandler(NodeHttpStep.Answer(500, Envelope(AdapterErrorCodes.InternalError)));
        var time = new ManualTimeProvider();
        using var provider = Provider(handler, time);
        var client = provider.GetRequiredService<IRenderClient>();

        var submitted = await DriveAsync(client.SubmitAsync(Node(), SubmitBody(), CancellationToken.None), time);

        submitted.IsError.ShouldBeTrue();
        handler.Requests.Count.ShouldBe(4);
        handler.Requests[0].Method.ShouldBe("POST");
        handler.Requests[0].Path.ShouldBe("/v1/renders");
        handler.Requests[0].Body.ShouldBe(handler.Requests[3].Body);

        var cancelled = await DriveAsync(client.CancelAsync(Node(), TaskId, CancellationToken.None), time);

        cancelled.IsError.ShouldBeTrue();
        handler.Requests.Count.ShouldBe(8);
        handler.Requests[4].Method.ShouldBe("POST");
        handler.Requests[4].Path.ShouldBe($"/v1/renders/{TaskId}/cancel");
    }

    [Fact]
    public async Task RateLimited_IsDeferredAndNeverRetried()
    {
        var handler = new ScriptedNodeHandler(NodeHttpStep.Answer(429, Envelope(AdapterErrorCodes.CapacityFull)));
        var time = new ManualTimeProvider();
        using var provider = Provider(handler, time);
        var client = provider.GetRequiredService<IRenderClient>();

        var submitted = await DriveAsync(client.SubmitAsync(Node(), SubmitBody(), CancellationToken.None), time);

        submitted.IsError.ShouldBeTrue();

        // 429 is the capacity signal, not a transient fault: the caller reschedules after Retry-After, so
        // the pipeline must not spend a retry on it (BD6, 11 T6).
        handler.Requests.Count.ShouldBe(1);

        RenderClientErrors.TryGetFailure(submitted.Errors[0], out var failure).ShouldBeTrue();
        failure!.Kind.ShouldBe(RenderClientFailureKind.Deferred);
        failure.RetryAfter.ShouldBe(TimeSpan.FromSeconds(30));
        failure.Alert.ShouldBeFalse();
        failure.OutcomeCode.ShouldBeNull();
    }

    [Fact]
    public async Task CircuitBreaker_OpensAfterFiveFailures()
    {
        var handler = new ScriptedNodeHandler(NodeHttpStep.Answer(500, Envelope(AdapterErrorCodes.InternalError)));
        var time = new ManualTimeProvider();
        using var provider = Provider(handler, time);
        var client = provider.GetRequiredService<IRenderClient>();

        var first = await DriveAsync(client.GetStatusAsync(Node(), TaskId, CancellationToken.None), time);
        first.IsError.ShouldBeTrue();
        handler.Requests.Count.ShouldBe(4);

        // The fifth failure of the sampling window opens the breaker, so the rest of this call's retries are
        // refused without a request at all.
        var second = await DriveAsync(client.GetStatusAsync(Node(), TaskId, CancellationToken.None), time);
        second.IsError.ShouldBeTrue();
        handler.Requests.Count.ShouldBe(5);

        var open = await DriveAsync(client.GetStatusAsync(Node(), TaskId, CancellationToken.None), time);
        open.IsError.ShouldBeTrue();
        handler.Requests.Count.ShouldBe(5);
        RenderClientErrors.TryGetFailure(open.Errors[0], out var opened).ShouldBeTrue();
        opened!.Kind.ShouldBe(RenderClientFailureKind.TransientFailure);

        // BreakDuration is 30 s: after it the breaker half-opens and lets exactly one probe through, and a
        // success closes it again.
        handler.Rescript(NodeHttpStep.Json(200, StatusBody()));
        time.Advance(TimeSpan.FromSeconds(31));

        var closed = await DriveAsync(client.GetStatusAsync(Node(), TaskId, CancellationToken.None), time);

        closed.IsError.ShouldBeFalse();
        handler.Requests.Count.ShouldBe(6);
    }

    [Fact]
    public async Task CallerCancellation_IsNotRetried()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new ScriptedNodeHandler(NodeHttpStep.Fails(token =>
        {
            cancellation.Cancel();
            return new OperationCanceledException(token);
        }));

        var time = new ManualTimeProvider();
        using var provider = Provider(handler, time);
        var client = provider.GetRequiredService<IRenderClient>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetStatusAsync(Node(), TaskId, cancellation.Token));

        handler.Requests.Count.ShouldBe(1);
    }

    /// <summary>The node every call is aimed at; the factory supplies the transport, so only the name matters.</summary>
    /// <returns>The node.</returns>
    private static WorkerNode Node() => WorkerNode.Create(
        "alpha",
        new Uri("https://alpha.example/"),
        capacity: 2,
        tlsCertSha256: new string('A', WorkerNode.TlsCertSha256Length),
        gpuModel: null).Value;

    /// <summary>A real submit body from the frozen D14 fixtures, so no test invents a wire shape.</summary>
    /// <returns>The body.</returns>
    private static AdapterSubmitRequestV1 SubmitBody() =>
        JsonSerializer.Deserialize<AdapterSubmitRequestV1>(
            ContractFixtures.Read(SubmitFixture), RenderNodeJson.Tolerant)!;

    /// <summary>A status body the client can read, from the fixtures.</summary>
    /// <returns>The status.</returns>
    private static RenderStatusV1 StatusBody() =>
        JsonSerializer.Deserialize<RenderStatusV1>(
            ContractFixtures.Read("render-status.v1.succeeded.json"), RenderNodeJson.Tolerant)!;

    /// <summary>The one error envelope of the contract, built from its own types.</summary>
    /// <param name="code">The adapter code.</param>
    /// <returns>The envelope as JSON.</returns>
    private static string Envelope(string code) =>
        JsonSerializer.Serialize(
            new AdapterErrorV1(new AdapterErrorBody(code, "The node refused the call.", null)),
            RenderNodeJson.Tolerant);

    /// <summary>
    /// The host the client is built in: the real <c>AddRenderClients</c> registration over the scripted
    /// transport and a clock the test owns, with the <c>Render</c> section bound from an in-memory source.
    /// </summary>
    /// <param name="handler">The scripted node the factory hands out.</param>
    /// <param name="time">The clock the pipelines and the client run on.</param>
    /// <param name="logs">A capturing logger provider, when the test asserts on log lines.</param>
    /// <returns>The provider to resolve the client from.</returns>
    private static ServiceProvider Provider(
        ScriptedNodeHandler handler,
        TimeProvider time,
        ILoggerProvider? logs = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        if (logs is not null)
        {
            services.AddSingleton(logs);
        }

        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(time);
        services.AddSingleton<IRenderNodeConnectionFactory>(new ScriptedNodeConnectionFactory(handler));
        services.AddOptions<RenderNodesOptions>();
        services.AddRenderClients(configuration);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Settles one call by advancing the manual clock: the retry backoff, the attempt timeout and the total
    /// timeout are all timers on it, so the call completes in virtual milliseconds.
    /// </summary>
    /// <typeparam name="T">The call's success type.</typeparam>
    /// <param name="call">The call to settle.</param>
    /// <param name="time">The clock to advance.</param>
    /// <returns>The call's result.</returns>
    private static async Task<T> DriveAsync<T>(Task<T> call, ManualTimeProvider time)
    {
        for (var step = 0; step < 400 && !call.IsCompleted; step++)
        {
            time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Yield();
        }

        call.IsCompleted.ShouldBeTrue("the call must finish within the advanced virtual time");
        return await call;
    }
}
