using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Rendering;
using NightTales.Domain.Rendering;
using NightTales.Infrastructure.Rendering;
using NightTales.RenderNode.Contract;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Rendering;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;
using static NightTales.TestKit.Rendering.ManualClockDrive;

namespace NightTales.Api.IntegrationTests.Rendering;

/// <summary>
/// QA probes of <c>MptRenderClient</c> (09.6.2-T1, independent of the implementation's own tests): the
/// answers a node can send that no happy-path test covers — an unparsable or oversized 200, an unknown field
/// in a v1 reply, every unsafe task id, the raw <c>Retry-After</c> forms, the <c>adapter_node_unhealthy</c>
/// 503 through the retry pipeline, 429/409/401 on the DELETE, a breaker that must stay closed on a mix, and
/// a cancellation that lands inside the retry backoff. Every assertion is on what the caller receives, never
/// on a wire echo.
/// </summary>
public sealed class RenderClientQaTests
{
    private const string TaskId = "3f7b9c2e-1a44-4d0f-9e21-6c5b8a7d0e11";

    private const string SubmitFixture = "render-submit.v1.landscape-vi.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A 200 whose body is not the contract type: contract drift, never a retry (task §Failure handling).</summary>
    [Fact]
    public async Task Submit_200UnparsableBody_IsTerminalAlertingAndNotRetried()
    {
        var handler = new HeaderNodeHandler(ScriptedAnswer.Ok(200, "{\"task_id\": \"not-a-guid\""));
        var time = new ManualTimeProvider();
        using var provider = Provider(handler, time);
        var client = provider.GetRequiredService<IRenderClient>();

        var submitted = await DriveAsync(client.SubmitAsync(Node(), SubmitBody(), CancellationToken.None), time);

        handler.Requests.Count.ShouldBe(1, "a 200 is not a retryable outcome, whatever its body says");
        var failure = FailureOf(submitted);
        failure.Kind.ShouldBe(RenderClientFailureKind.TerminalFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        failure.Alert.ShouldBeTrue();
        failure.Detail.ShouldBeNull();
    }

    /// <summary>A 200 with no body at all is the same unreadable answer, and is not retried either.</summary>
    [Fact]
    public async Task GetStatus_200EmptyBody_IsTerminalAlertingAndNotRetried()
    {
        var handler = new HeaderNodeHandler(ScriptedAnswer.Ok(200, string.Empty));
        var time = new ManualTimeProvider();
        using var provider = Provider(handler, time);
        var client = provider.GetRequiredService<IRenderClient>();

        var status = await DriveAsync(client.GetStatusAsync(Node(), TaskId, CancellationToken.None), time);

        handler.Requests.Count.ShouldBe(1);
        var failure = FailureOf(status);
        failure.Kind.ShouldBe(RenderClientFailureKind.TerminalFailure);
        failure.Alert.ShouldBeTrue();
    }

    /// <summary>
    /// An answer over the client's read cap is buffered nowhere and parses as no body, so it fails closed
    /// instead of letting a node make the Worker hold an unbounded status (BD16).
    /// </summary>
    [Fact]
    public async Task GetStatus_OversizedBody_IsTerminalAlertingAndNotRetried()
    {
        var oversized = "{\"schema\":\"render-status.v1\",\"pad\":\"" + new string('a', 2 * 1024 * 1024) + "\"}";
        var handler = new HeaderNodeHandler(ScriptedAnswer.Ok(200, oversized));
        var time = new ManualTimeProvider();
        using var provider = Provider(handler, time);
        var client = provider.GetRequiredService<IRenderClient>();

        var status = await DriveAsync(client.GetStatusAsync(Node(), TaskId, CancellationToken.None), time);

        handler.Requests.Count.ShouldBe(1);
        var failure = FailureOf(status);
        failure.Kind.ShouldBe(RenderClientFailureKind.TerminalFailure);
        failure.Alert.ShouldBeTrue();
    }

    /// <summary>
    /// The read cap of a status answer is 64 KiB and inclusive: a body of exactly that size is read and parsed,
    /// and one byte more is refused as an unreadable answer — terminal, alerting and never retried.
    /// </summary>
    /// <param name="bytes">The body size under test.</param>
    /// <param name="accepted">Whether the client must read it.</param>
    [Theory]
    [InlineData(64 * 1024, true)]
    [InlineData((64 * 1024) + 1, false)]
    public async Task GetStatus_AnswerAtTheReadCap_IsRead_OneByteOverIsRefused(int bytes, bool accepted)
    {
        var handler = new HeaderNodeHandler(ScriptedAnswer.Ok(200, StatusBodyOfSize(bytes)));
        var time = new ManualTimeProvider();
        using var provider = Provider(handler, time);
        var client = provider.GetRequiredService<IRenderClient>();

        var status = await client.GetStatusAsync(Node(), TaskId, CancellationToken.None).WaitAsync(RealTimeBudget, Ct);

        if (accepted)
        {
            status.IsError.ShouldBeFalse(status.FirstError.Description);
            status.Value.State.ShouldBe("succeeded");
        }
        else
        {
            var failure = FailureOf(status);
            failure.Kind.ShouldBe(RenderClientFailureKind.TerminalFailure);
            failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderNodeError);
            failure.Alert.ShouldBeTrue();
            handler.Requests.ShouldHaveSingleItem("an unreadable 2xx is contract drift, never a retry");
        }
    }

    /// <summary>
    /// A field a newer adapter adds must not break a running Worker (11 README D4, 05 CR-3): the production
    /// profile is the tolerant one, so both a status and an accepted reply with an unknown member are read.
    /// </summary>
    [Fact]
    public async Task Replies_WithAnUnknownField_AreTolerated()
    {
        var status = StatusFixture();
        var patched = JsonNode.Parse(status)!.AsObject();
        patched["added_by_a_newer_adapter"] = 17;
        patched["another"] = new JsonArray("a", "b");

        var accepted = JsonSerializer.Serialize(
            new AdapterAcceptedV1(Guid.Parse(TaskId), "queued"), RenderNodeJson.Tolerant);
        var acceptedPatched = JsonNode.Parse(accepted)!.AsObject();
        acceptedPatched["surprise"] = "tolerated";

        var handler = new HeaderNodeHandler(
            ScriptedAnswer.Ok(200, patched.ToJsonString()),
            ScriptedAnswer.Ok(202, acceptedPatched.ToJsonString()));

        var time = new ManualTimeProvider();
        using var provider = Provider(handler, time);
        var client = provider.GetRequiredService<IRenderClient>();

        var read = await client.GetStatusAsync(Node(), TaskId, CancellationToken.None).WaitAsync(RealTimeBudget, Ct);
        read.IsError.ShouldBeFalse(read.FirstError.Description);
        read.Value.State.ShouldBe("succeeded");

        var submitted = await client.SubmitAsync(Node(), SubmitBody(), CancellationToken.None).WaitAsync(RealTimeBudget, Ct);
        submitted.IsError.ShouldBeFalse(submitted.FirstError.Description);
        submitted.Value.TaskId.ShouldBe(Guid.Parse(TaskId));
    }

    /// <summary>
    /// An adapter code this client does not know fails closed through the real path (BD6): the envelope's own
    /// code is the only thing that survives into the failure's <c>Detail</c>, the call is terminal with an
    /// alert, and it is not retried. A DELETE is used so the request count is one whatever the pipeline does.
    /// </summary>
    [Fact]
    public async Task UnknownAdapterCode_IsTerminalWithTheCodeAsDetail()
    {
        var handler = new HeaderNodeHandler(ScriptedAnswer.Ok(400, Envelope("adapter_surprise")));
        var time = new ManualTimeProvider();
        using var provider = Provider(handler, time);
        var client = provider.GetRequiredService<IRenderClient>();

        var deleted = await DriveAsync(client.DeleteAsync(Node(), TaskId, CancellationToken.None), time);

        handler.Requests.ShouldHaveSingleItem();
        var failure = FailureOf(deleted);
        failure.Kind.ShouldBe(RenderClientFailureKind.TerminalFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        failure.Detail.ShouldBe("adapter_surprise");
        failure.Alert.ShouldBeTrue();
    }

    /// <summary>
    /// Every id that is not a plain adapter id is refused before any URL is built (S8/A03): a separator, a
    /// traversal, an escape, whitespace, unicode, nothing at all and something over the bound.
    /// </summary>
    /// <param name="taskId">The id a caller offers.</param>
    [Theory]
    [InlineData("3f7b9c2e/1a44")]
    [InlineData("..")]
    [InlineData("../../etc/passwd")]
    [InlineData("%2f")]
    [InlineData("%2e%2e")]
    [InlineData("3f7b9c2e 1a44")]
    [InlineData("\u2028")]
    [InlineData("caf\u00e9-1a44")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("3f7b9c2e-1a44-4d0f-9e21-6c5b8a7d0e11-3f7b9c2e-1a44-4d0f-9e21-6c5b")]
    public async Task UnsafeTaskId_IsRefusedBeforeAnyRequest(string taskId)
    {
        var handler = new HeaderNodeHandler(ScriptedAnswer.Ok(200, "{}"));
        var time = new ManualTimeProvider();
        using var provider = Provider(handler, time);
        var client = provider.GetRequiredService<IRenderClient>();

        var status = await client.GetStatusAsync(Node(), taskId, CancellationToken.None).WaitAsync(RealTimeBudget, Ct);
        var cancelled = await client.CancelAsync(Node(), taskId, CancellationToken.None).WaitAsync(RealTimeBudget, Ct);
        var deleted = await client.DeleteAsync(Node(), taskId, CancellationToken.None).WaitAsync(RealTimeBudget, Ct);

        foreach (var failure in new[] { FailureOf(status), FailureOf(cancelled), FailureOf(deleted) })
        {
            failure.Kind.ShouldBe(RenderClientFailureKind.TerminalFailure);
            failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderNodeError);
            failure.Detail.ShouldBe("render_task_id_invalid");
            failure.Alert.ShouldBeTrue("an id that is not an adapter id is a bug or an injection attempt");
        }

        handler.Requests.ShouldBeEmpty("no URL is built for a refused id");
    }

    /// <summary>
    /// The bound is inclusive: a 64-character unreserved id is a legal adapter id and travels on the route
    /// unescaped. The answer the node then sends names a different task — the fixture's task id is a UUID and
    /// this id is not one — so the client refuses it as a node fault, which is the same fail-closed rule the
    /// mismatch case pins.
    /// </summary>
    [Fact]
    public async Task MaxLengthUnreservedTaskId_TravelsOnTheRoute_AndAForeignStatusIsRefused()
    {
        var id = new string('a', 64);
        var handler = new HeaderNodeHandler(ScriptedAnswer.Ok(200, ContractFixtures.Read("render-status.v1.running-voice.json")));
        var time = new ManualTimeProvider();
        using var provider = Provider(handler, time);
        var client = provider.GetRequiredService<IRenderClient>();

        var status = await client.GetStatusAsync(Node(), id, CancellationToken.None).WaitAsync(RealTimeBudget, Ct);

        handler.Requests.ShouldHaveSingleItem().Path.ShouldBe($"/v1/renders/{id}");
        var failure = FailureOf(status);
        failure.Kind.ShouldBe(RenderClientFailureKind.TerminalFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        failure.Detail.ShouldBe("render_task_mismatch");
        failure.Alert.ShouldBeTrue();
    }

    /// <summary>
    /// The raw <c>Retry-After</c> forms through the real client (11 T6, task §Failure handling): integer
    /// seconds are honoured, and a missing, negative, unparsable or HTTP-date value never becomes a zero
    /// wait — it becomes the 30 s default, clamped to 300 s.
    /// </summary>
    /// <param name="header">The raw header the node sends, or <c>null</c> for none.</param>
    /// <param name="expectedSeconds">The wait the caller must receive.</param>
    [Theory]
    [InlineData("30", 30)]
    [InlineData("0", 1)]
    [InlineData("1", 1)]
    [InlineData("-5", 30)]
    [InlineData("99999", 300)]
    [InlineData("abc", 30)]
    [InlineData("12.5", 30)]
    [InlineData("Wed, 21 Oct 2015 07:28:00 GMT", 30)]
    [InlineData(null, 30)]
    public async Task RateLimited_RetryAfterForms_ReachTheCaller(string? header, int expectedSeconds)
    {
        var handler = new HeaderNodeHandler(
            ScriptedAnswer.WithRetryAfter(429, Envelope(AdapterErrorCodes.CapacityFull), header));
        var time = new ManualTimeProvider();
        using var provider = Provider(handler, time);
        var client = provider.GetRequiredService<IRenderClient>();

        var submitted = await DriveAsync(client.SubmitAsync(Node(), SubmitBody(), CancellationToken.None), time);

        handler.Requests.Count.ShouldBe(1, "a 429 is the capacity signal and is never retried (BD6, 11 T6)");
        var failure = FailureOf(submitted);
        failure.Kind.ShouldBe(RenderClientFailureKind.Deferred);
        failure.RetryAfter.ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
    }

    /// <summary>
    /// <c>adapter_node_unhealthy</c> (503) is in the pipeline's retry predicate, so one call costs four
    /// requests before it is mapped; the mapping itself is the table's <c>Deferred(RetryNodes:HealthPollSeconds)</c>.
    /// </summary>
    [Fact]
    public async Task NodeUnhealthy503_OneCall_IsRetriedThenDeferredWithTheConfiguredHealthPoll()
    {
        var handler = new HeaderNodeHandler(
            ScriptedAnswer.Ok(503, Envelope(AdapterErrorCodes.NodeUnhealthy)));
        var time = new ManualTimeProvider();
        using var provider = Provider(handler, time, settings: new Dictionary<string, string?>
        {
            ["RenderNodes:HealthPollSeconds"] = "12",
        });
        var client = provider.GetRequiredService<IRenderClient>();

        var status = await DriveAsync(client.GetStatusAsync(Node(), TaskId, CancellationToken.None), time);

        handler.Requests.Count.ShouldBe(4, "503 is retryable, so the first attempt plus three retries reach the node");
        var failure = FailureOf(status);
        failure.Kind.ShouldBe(RenderClientFailureKind.Deferred);
        failure.RetryAfter.ShouldBe(TimeSpan.FromSeconds(12), "the configured HealthPollSeconds, not the 30 s default");
    }

    /// <summary>
    /// QA probe of the task §Design table: <c>adapter_node_unhealthy</c> is documented as
    /// <c>Deferred(RetryNodes:HealthPollSeconds)</c> — "pick another node later" (09 README BD6, and the
    /// mapping 09.6.3-T1 step 5 switches on). The first call does defer (four requests, the configured 12 s),
    /// but the second one does not: the 503s are retryable <em>and</em> breaker failures, so the fifth of them
    /// opens the process-wide breaker and this call answers <see cref="RenderClientFailureKind.TransientFailure"/>
    /// without a request — the deferral becomes a job-level retry that spends the retry budget.
    /// Pessimistically, the retryable 503s outbid the deferral. Pinned here as the behaviour that exists, so a
    /// fix flips this test deliberately (QA report 09.6.2-T1-QA1; owner/TL decision).
    /// </summary>
    [Fact]
    public async Task NodeUnhealthy503_SecondCallAfterTheBreakerOpens_IsTransientNotDeferred()
    {
        var handler = new HeaderNodeHandler(
            ScriptedAnswer.Ok(503, Envelope(AdapterErrorCodes.NodeUnhealthy)));
        var time = new ManualTimeProvider();
        using var provider = Provider(handler, time, settings: new Dictionary<string, string?>
        {
            ["RenderNodes:HealthPollSeconds"] = "12",
        });
        var client = provider.GetRequiredService<IRenderClient>();

        var first = await DriveAsync(client.GetStatusAsync(Node(), TaskId, CancellationToken.None), time);
        FailureOf(first).Kind.ShouldBe(RenderClientFailureKind.Deferred);
        handler.Requests.Count.ShouldBe(4);

        var second = await DriveAsync(client.GetStatusAsync(Node(), TaskId, CancellationToken.None), time);

        handler.Requests.Count.ShouldBe(5, "the fifth retryable outcome is what the breaker opens on");
        var failure = FailureOf(second);
        failure.Kind.ShouldBe(RenderClientFailureKind.TransientFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderNodeError);
    }

    /// <summary>A DELETE's 429 carries the adapter's <c>Retry-After</c>, and the no-retry pipeline never repeats it.</summary>
    [Fact]
    public async Task Delete429_IsDeferredWithTheHeader_AndNotRetried()
    {
        var handler = new HeaderNodeHandler(
            ScriptedAnswer.WithRetryAfter(429, Envelope(AdapterErrorCodes.CapacityFull), "45"));
        var time = new ManualTimeProvider();
        using var provider = Provider(handler, time);
        var client = provider.GetRequiredService<IRenderClient>();

        var deleted = await DriveAsync(client.DeleteAsync(Node(), TaskId, CancellationToken.None), time);

        handler.Requests.ShouldHaveSingleItem().Method.ShouldBe("DELETE");
        var failure = FailureOf(deleted);
        failure.Kind.ShouldBe(RenderClientFailureKind.Deferred);
        failure.RetryAfter.ShouldBe(TimeSpan.FromSeconds(45));
    }

    /// <summary><c>adapter_task_busy</c> on a DELETE defers the cleanup by the default 30 s (task §Design table).</summary>
    [Fact]
    public async Task DeleteTaskBusy409_IsDeferredThirtySeconds()
    {
        var handler = new HeaderNodeHandler(
            ScriptedAnswer.Ok(409, Envelope(AdapterErrorCodes.TaskBusy)));
        var time = new ManualTimeProvider();
        using var provider = Provider(handler, time);
        var client = provider.GetRequiredService<IRenderClient>();

        var deleted = await DriveAsync(client.DeleteAsync(Node(), TaskId, CancellationToken.None), time);

        handler.Requests.ShouldHaveSingleItem();
        var failure = FailureOf(deleted);
        failure.Kind.ShouldBe(RenderClientFailureKind.Deferred);
        failure.RetryAfter.ShouldBe(TimeSpan.FromSeconds(30));
    }

    /// <summary>A rotated key and a forbidden route both end the job and alert, and neither is retried.</summary>
    /// <param name="status">The status the node answers with.</param>
    /// <param name="code">The envelope's code, or <c>null</c> for an answer with no envelope code.</param>
    [Theory]
    [InlineData(401, AdapterErrorCodes.Unauthorized)]
    [InlineData(403, null)]
    public async Task Delete401Or403_IsTerminalAlertingAndNotRetried(int status, string? code)
    {
        var handler = new HeaderNodeHandler(ScriptedAnswer.Ok(status, code is null ? "{}" : Envelope(code)));
        var time = new ManualTimeProvider();
        using var provider = Provider(handler, time);
        var client = provider.GetRequiredService<IRenderClient>();

        var deleted = await DriveAsync(client.DeleteAsync(Node(), TaskId, CancellationToken.None), time);

        handler.Requests.ShouldHaveSingleItem();
        var failure = FailureOf(deleted);
        failure.Kind.ShouldBe(RenderClientFailureKind.TerminalFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        failure.Alert.ShouldBeTrue();
    }

    /// <summary>
    /// The breaker must not open on a mix (W9: at P volume a 1.0 ratio means "nothing succeeded"): one
    /// success inside the window keeps it closed, so the next failing call still reaches the node.
    /// </summary>
    [Fact]
    public async Task CircuitBreaker_StaysClosed_OnAMixOfSuccessAndFailure()
    {
        var handler = new HeaderNodeHandler(ScriptedAnswer.Ok(500, Envelope(AdapterErrorCodes.InternalError)));
        var time = new ManualTimeProvider();
        using var provider = Provider(handler, time);
        var client = provider.GetRequiredService<IRenderClient>();

        var failing = await DriveAsync(client.GetStatusAsync(Node(), TaskId, CancellationToken.None), time);
        FailureOf(failing).Kind.ShouldBe(RenderClientFailureKind.TransientFailure);
        handler.Requests.Count.ShouldBe(4, "four failures of one call");

        handler.Rescript(ScriptedAnswer.Ok(200, StatusFixture()));
        var ok = await DriveAsync(client.GetStatusAsync(Node(), TaskId, CancellationToken.None), time);
        ok.IsError.ShouldBeFalse(ok.FirstError.Description);

        handler.Rescript(ScriptedAnswer.Ok(500, Envelope(AdapterErrorCodes.InternalError)));
        var after = await DriveAsync(client.GetStatusAsync(Node(), TaskId, CancellationToken.None), time);

        FailureOf(after).Kind.ShouldBe(RenderClientFailureKind.TransientFailure);
        handler.Requests.Count.ShouldBe(
            9,
            "4 failures + 1 success is a 0.8 ratio, so the breaker stays closed and the retry budget reaches the node");
    }

    /// <summary>An open breaker is a transient node failure and makes no request at all (BD6).</summary>
    [Fact]
    public async Task OpenCircuit_IsTransientAndMakesNoRequest()
    {
        var handler = new HeaderNodeHandler(ScriptedAnswer.Ok(500, Envelope(AdapterErrorCodes.InternalError)));
        var time = new ManualTimeProvider();
        using var provider = Provider(handler, time);
        var client = provider.GetRequiredService<IRenderClient>();

        await DriveAsync(client.GetStatusAsync(Node(), TaskId, CancellationToken.None), time);
        await DriveAsync(client.GetStatusAsync(Node(), TaskId, CancellationToken.None), time);
        var requestsAtOpen = handler.Requests.Count;

        var open = await DriveAsync(client.GetStatusAsync(Node(), TaskId, CancellationToken.None), time);

        handler.Requests.Count.ShouldBe(requestsAtOpen, "the pipeline refuses to try at all");
        var failure = FailureOf(open);
        failure.Kind.ShouldBe(RenderClientFailureKind.TransientFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        failure.Alert.ShouldBeFalse();
    }

    /// <summary>
    /// A cancellation that lands while the pipeline waits in its retry backoff ends the call as an
    /// <see cref="OperationCanceledException"/> and spends no further attempt (task §Design).
    /// </summary>
    [Fact]
    public async Task CallerCancellation_DuringRetryBackoff_StopsRetrying()
    {
        var handler = new HeaderNodeHandler(ScriptedAnswer.Transport());
        var time = new ManualTimeProvider();
        using var provider = Provider(handler, time);
        var client = provider.GetRequiredService<IRenderClient>();
        using var cancellation = new CancellationTokenSource();

        var call = client.GetStatusAsync(Node(), TaskId, cancellation.Token);

        // Real time and bounded: a spin on Task.Yield can be starved on a loaded box, and the test would then
        // cancel a call that never reached the node at all.
        await WaitUntilAsync(() => handler.Requests.Count == 1, "the first attempt reached the node");

        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await call.WaitAsync(RealTimeBudget, Ct));
        handler.Requests.Count.ShouldBe(1, "the backoff wait is abandoned, never a second attempt");
    }

    /// <summary>
    /// The secret rule of BD16/S4 across all four calls at once: the sentinel key and base URL reach no log
    /// line anywhere, with a positive control so an empty capture cannot pass the test.
    /// </summary>
    [Fact]
    public async Task ApiKeyAndBaseUrl_ReachNoLogLine_AcrossAllFourCalls()
    {
        const string sentinel = "qa-sentinel-key-88c1";
        var url = new Uri("https://qa-sentinel-node.invalid:8443/");
        var accepted = JsonSerializer.Serialize(
            new AdapterAcceptedV1(Guid.Parse(TaskId), "queued"), RenderNodeJson.Tolerant);

        var handler = new HeaderNodeHandler(
            ScriptedAnswer.Ok(202, accepted),
            ScriptedAnswer.Ok(200, StatusFixture()),
            ScriptedAnswer.Ok(202, string.Empty),
            ScriptedAnswer.Ok(204, string.Empty));

        var logs = new CapturingLogProvider();
        var time = new ManualTimeProvider();
        using var provider = Provider(handler, time, logs, sentinel, url);
        var client = provider.GetRequiredService<IRenderClient>();
        var node = Node("sentinel");

        (await client.SubmitAsync(node, SubmitBody(), CancellationToken.None).WaitAsync(RealTimeBudget, Ct)).IsError.ShouldBeFalse();
        (await client.GetStatusAsync(node, TaskId, CancellationToken.None).WaitAsync(RealTimeBudget, Ct)).IsError.ShouldBeFalse();
        (await client.CancelAsync(node, TaskId, CancellationToken.None).WaitAsync(RealTimeBudget, Ct)).IsError.ShouldBeFalse();
        (await client.DeleteAsync(node, TaskId, CancellationToken.None).WaitAsync(RealTimeBudget, Ct)).IsError.ShouldBeFalse();

        handler.Requests.Count.ShouldBe(4);
        foreach (var request in handler.Requests)
        {
            request.ApiKey.ShouldBe(sentinel, "every node call travels on the connection factory's client (F13)");
        }

        logs.Lines.ShouldNotBeEmpty("the positive control: an empty capture must not pass this test");
        foreach (var line in logs.Lines)
        {
            line.ShouldNotContain(sentinel);
            line.ShouldNotContain("qa-sentinel-node.invalid");
            line.ShouldNotContain(url.ToString());
        }
    }

    /// <summary>
    /// The start-up gate itself, not only its unit test: a <c>Render</c> value the validator refuses must fail
    /// the host's boot (09 README BD6/W9), which proves <c>AddRenderClients</c> registered the validator.
    /// </summary>
    [Fact]
    public void UnusableRenderSetting_FailsTheBoot()
    {
        using var provider = Provider(
            new HeaderNodeHandler(ScriptedAnswer.Ok(200, "{}")),
            new ManualTimeProvider(),
            settings: new Dictionary<string, string?>
            {
                ["Render:Client:AttemptSeconds"] = "0",
            });

        var exception = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<RenderClientOptions>>().Value);

        exception.Message.ShouldContain("Render:Client:AttemptSeconds");
    }

    /// <summary>The node every call is aimed at; the scripted factory supplies the transport.</summary>
    /// <param name="name">The node's name.</param>
    /// <returns>The node.</returns>
    private static WorkerNode Node(string name = "alpha") => WorkerNode.Create(
        name,
        new Uri("https://alpha.example/"),
        capacity: 2,
        tlsCertSha256: new string('A', WorkerNode.TlsCertSha256Length),
        gpuModel: null).Value;

    /// <summary>A real submit body from the frozen D14 fixtures.</summary>
    /// <returns>The body.</returns>
    private static AdapterSubmitRequestV1 SubmitBody() =>
        JsonSerializer.Deserialize<AdapterSubmitRequestV1>(
            ContractFixtures.Read(SubmitFixture), RenderNodeJson.Tolerant)!;

    /// <summary>The one error envelope of the contract, built from its own types.</summary>
    /// <param name="code">The adapter code.</param>
    /// <returns>The envelope as JSON.</returns>
    private static string Envelope(string code) =>
        JsonSerializer.Serialize(
            new AdapterErrorV1(new AdapterErrorBody(code, "The node refused the call.", null)),
            RenderNodeJson.Tolerant);

    /// <summary>Reads the typed failure out of a failed call, or fails the test.</summary>
    /// <typeparam name="T">The call's success type.</typeparam>
    /// <param name="result">The call's result.</param>
    /// <returns>The failure it carries.</returns>
    private static RenderClientFailure FailureOf<T>(ErrorOr<T> result)
    {
        result.IsError.ShouldBeTrue("the call was expected to fail");
        RenderClientErrors.TryGetFailure(result.FirstError, out var failure)
            .ShouldBeTrue($"a render-client error always carries its typed failure ({result.FirstError.Code})");

        return failure!;
    }

    /// <summary>
    /// The host the client is built in: the production <c>AddRenderClients</c> registration over the
    /// scripted transport, a clock the test owns and the <c>RenderNodes</c> section bound from memory.
    /// </summary>
    /// <param name="handler">The scripted node the factory hands out.</param>
    /// <param name="time">The clock the pipelines and the client run on.</param>
    /// <param name="logs">A capturing logger provider, when the test asserts on log lines.</param>
    /// <param name="apiKey">The key the node client carries, when a test proves one never leaks.</param>
    /// <param name="baseAddress">The base address to hand out instead of the node's own, for the sentinel case.</param>
    /// <param name="settings">Extra configuration, such as <c>RenderNodes:HealthPollSeconds</c>.</param>
    /// <returns>The provider to resolve the client from.</returns>
    private static ServiceProvider Provider(
        HttpMessageHandler handler,
        TimeProvider time,
        ILoggerProvider? logs = null,
        string? apiKey = null,
        Uri? baseAddress = null,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        if (logs is not null)
        {
            services.AddSingleton(logs);
        }

        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(time);
        services.AddSingleton<IRenderNodeConnectionFactory>(
            new ScriptedNodeConnectionFactory(handler, apiKey, baseAddress));
        services.AddOptions<RenderNodesOptions>().BindConfiguration(RenderNodesOptions.SectionName);
        services.AddRenderClients(configuration);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// A frozen status fixture whose <c>task_id</c> is the requested one, patched in memory: the fixture is
    /// never edited, and the client refuses a status that names another task (M6).
    /// </summary>
    /// <returns>The status body as JSON.</returns>
    private static string StatusFixture()
    {
        var status = JsonNode.Parse(ContractFixtures.Read("render-status.v1.succeeded.json"))!.AsObject();
        status["task_id"] = TaskId;

        return status.ToJsonString();
    }

    /// <summary>
    /// A readable status body of exactly <paramref name="bytes"/> bytes: the fixture plus a padding member a
    /// tolerant reader ignores, so a test can sit on the client's read cap instead of around it.
    /// </summary>
    /// <param name="bytes">The exact body size in bytes.</param>
    /// <returns>The status body as JSON.</returns>
    private static string StatusBodyOfSize(int bytes)
    {
        var status = JsonNode.Parse(StatusFixture())!.AsObject();
        status["pad"] = string.Empty;
        status["pad"] = new string('a', bytes - Encoding.UTF8.GetByteCount(status.ToJsonString()));

        var body = status.ToJsonString();
        Encoding.UTF8.GetByteCount(body).ShouldBe(bytes, "the padded status must sit exactly on the size under test");

        return body;
    }

    /// <summary>Captures every log line of the host, exception text included, so a leak cannot hide in one.</summary>
    private sealed class CapturingLogProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        /// <summary>Every line the host logged, formatted.</summary>
        public IReadOnlyList<string> Lines => [.. _lines];

        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName) => new Sink(categoryName, _lines);

        /// <inheritdoc />
        public void Dispose()
        {
        }

        private sealed class Sink(string category, ConcurrentQueue<string> lines) : ILogger
        {
            /// <inheritdoc />
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            /// <inheritdoc />
            public bool IsEnabled(LogLevel logLevel) => true;

            /// <inheritdoc />
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)}");
                if (exception is not null)
                {
                    lines.Enqueue($"{logLevel} {category}: {exception}");
                }
            }
        }
    }

    /// <summary>
    /// One scripted answer of a fake node with the raw headers it sends: the implementation's
    /// <see cref="ScriptedNodeHandler"/> scripts a status and a body only, and the <c>Retry-After</c> forms
    /// are only reachable with a raw header. The last answer repeats, exactly as that fixture's does.
    /// </summary>
    /// <param name="Status">The status to answer with; meaningless for a failing answer.</param>
    /// <param name="Body">The body to answer with.</param>
    /// <param name="Headers">The raw response headers, when the answer carries one.</param>
    /// <param name="Failure">The exception to raise before any answer arrives, or <c>null</c>.</param>
    private sealed record ScriptedAnswer(
        int Status,
        string Body,
        IReadOnlyDictionary<string, string>? Headers,
        Func<CancellationToken, Exception>? Failure)
    {
        /// <summary>A plain answer.</summary>
        /// <param name="status">The status.</param>
        /// <param name="body">The body.</param>
        /// <returns>The answer.</returns>
        public static ScriptedAnswer Ok(int status, string body) => new(status, body, null, null);

        /// <summary>An answer carrying one raw header, exactly as the node sends it.</summary>
        /// <param name="status">The status.</param>
        /// <param name="body">The body.</param>
        /// <param name="retryAfter">The raw <c>Retry-After</c> value, or <c>null</c> for none.</param>
        /// <returns>The answer.</returns>
        public static ScriptedAnswer WithRetryAfter(int status, string body, string? retryAfter) =>
            new(status, body, retryAfter is null
                ? null
                : new Dictionary<string, string>(StringComparer.Ordinal) { ["Retry-After"] = retryAfter }, null);

        /// <summary>A transport failure whose message names the request URI, as the real one does.</summary>
        /// <returns>The answer.</returns>
        public static ScriptedAnswer Transport() =>
            new(0, string.Empty, null, _ => new HttpRequestException(
                "No connection could be made because the target machine actively refused it: the node"));
    }

    /// <summary>The header-aware scripted node: it records every call and repeats its last answer.</summary>
    /// <param name="answers">At least one answer.</param>
    private sealed class HeaderNodeHandler(params ScriptedAnswer[] answers) : HttpMessageHandler
    {
        private readonly List<ScriptedAnswer> _answers = [.. answers];
        private readonly List<RecordedCall> _requests = [];
        private readonly Lock _gate = new();
        private int _served;

        /// <summary>Every call this node received, in order.</summary>
        public IReadOnlyList<RecordedCall> Requests
        {
            get
            {
                lock (_gate)
                {
                    return [.. _requests];
                }
            }
        }

        /// <summary>Replaces the script, so one client instance can see a failure and then a success.</summary>
        /// <param name="steps">The new script; the last step repeats.</param>
        public void Rescript(params ScriptedAnswer[] steps)
        {
            lock (_gate)
            {
                _answers.Clear();
                _answers.AddRange(steps);
                _served = 0;
            }
        }

        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var apiKey = request.Headers.TryGetValues("x-api-key", out var keys) ? keys.FirstOrDefault() : null;

            ScriptedAnswer answer;
            lock (_gate)
            {
                _requests.Add(new RecordedCall(
                    request.Method.Method, request.RequestUri?.PathAndQuery ?? string.Empty, apiKey, body));
                answer = _answers[Math.Min(_served, _answers.Count - 1)];
                _served++;
            }

            if (answer.Failure is { } failure)
            {
                throw failure(cancellationToken);
            }

            var response = new HttpResponseMessage((HttpStatusCode)answer.Status)
            {
                Content = new StringContent(answer.Body, Encoding.UTF8, "application/json"),
            };

            if (answer.Headers is { } headers)
            {
                foreach (var (name, value) in headers)
                {
                    response.Headers.TryAddWithoutValidation(name, value);
                }
            }

            return response;
        }

        /// <summary>One recorded call.</summary>
        /// <param name="Method">The HTTP verb.</param>
        /// <param name="Path">The path and query of the request URI.</param>
        /// <param name="ApiKey">The <c>x-api-key</c> header the client sent, when it sent one.</param>
        /// <param name="Body">The request body, when it had one.</param>
        public sealed record RecordedCall(string Method, string Path, string? ApiKey, string? Body);
    }
}

/// <summary>
/// QA probes of <c>RenderDownloadClient</c> (09.6.2-T1): the size-cap boundaries at and one byte over the
/// allowance, an absent or lying <c>Content-Length</c>, a zero-length body, the subtitle cap's default, a
/// missing or oddly cased media type, every <c>X-Content-SHA256</c> state, a destination stream that fails,
/// an already-cancelled token, and every 3xx status the client must not follow.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RenderDownloadQaTests
{
    private const string ApiKeyValue = "qa-node-key-0001";
    private const string TaskId = "0192f0aa-1111-7000-8000-000000000000";
    private const string VideoType = "video/mp4";
    private const string SubRipType = "application/x-subrip; charset=utf-8";
    private const string Sha256Header = "X-Content-SHA256";

    private static readonly Uri NodeUrl = new("https://qa.example/");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A body exactly the size of the cap is inside it; one byte more is refused before any read.</summary>
    /// <param name="kind">The output kind.</param>
    /// <param name="cap">The caller's ceiling.</param>
    /// <param name="accepted">Whether the body of that size must be accepted.</param>
    [Theory]
    [InlineData(RenderOutputKind.Video, 1_024L, true)]
    [InlineData(RenderOutputKind.Video, 1_025L, false)]
    [InlineData(RenderOutputKind.Subtitles, 1_024L, true)]
    [InlineData(RenderOutputKind.Subtitles, 1_025L, false)]
    public async Task Download_ContentLengthAtOrOverTheCap(RenderOutputKind kind, long bodyBytes, bool accepted)
    {
        var content = new SyntheticDownloadContent(bodyBytes, kind == RenderOutputKind.Video ? VideoType : SubRipType);
        using var rig = CreateRig(_ => Accepted(content, SyntheticDownloadContent.ExpectedSha256(bodyBytes)));
        using var destination = new CountingWriteStream();

        var result = await rig.Client.DownloadAsync(Node(), TaskId, kind, destination, 1_024L, Ct).WaitAsync(RealTimeBudget, Ct);

        if (accepted)
        {
            result.IsError.ShouldBeFalse(result.FirstError.Description);
            result.Value.Bytes.ShouldBe(bodyBytes);
            destination.Bytes.ShouldBe(bodyBytes);
        }
        else
        {
            var failure = FailureOf(result);
            failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderOutputInvalid);
            failure.Detail.ShouldBe("render_output_too_large");
            content.BytesServed.ShouldBe(0L, "a declared length over the cap is refused before a single byte is read");
            destination.Bytes.ShouldBe(0L);
        }
    }

    /// <summary>A node that lies about a length it declares shorter than the bytes it serves is caught by the running count.</summary>
    [Fact]
    public async Task Download_DeclaredLengthShorterThanTheBody_IsAbortedByTheRunningCount()
    {
        // 512 declared (inside the cap), 4 096 actually served: only the count of arriving bytes bounds it.
        using var rig = CreateRig(_ => Accepted(Lying(declared: 512L, actualBytes: 4_096)));
        using var destination = new CountingWriteStream();

        var result = await rig.Client.DownloadAsync(Node(), TaskId, RenderOutputKind.Video, destination, 1_024L, Ct).WaitAsync(RealTimeBudget, Ct);

        var failure = FailureOf(result);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderOutputInvalid);
        failure.Detail.ShouldBe("render_output_too_large");
        destination.Bytes.ShouldBeLessThanOrEqualTo(1_024L);
    }

    /// <summary>
    /// The mirror case: a node that declares more than it serves is not an error — the digest covers the
    /// bytes that actually arrived.
    /// </summary>
    [Fact]
    public async Task Download_DeclaredLengthLongerThanTheBody_ReportsTheBytesThatArrived()
    {
        var content = Lying(declared: 4_096L, actualBytes: 512);
        using var rig = CreateRig(
            _ => Accepted(content, Sha256Of(Lying(declared: 4_096L, actualBytes: 512))),
            new RenderClientOptions { MaxVideoBytes = 64L * 1_024L * 1_024L });

        using var destination = new CountingWriteStream();
        var result = await rig.Client.DownloadAsync(Node(), TaskId, RenderOutputKind.Video, destination, 8_192L, Ct).WaitAsync(RealTimeBudget, Ct);

        result.IsError.ShouldBeFalse(result.FirstError.Description);
        result.Value.Bytes.ShouldBe(512L);
        destination.Bytes.ShouldBe(512L);
    }

    /// <summary>A zero-byte output is a legal answer: the digest is the empty-input SHA-256 and nothing is written.</summary>
    [Fact]
    public async Task Download_ZeroLengthBody_IsAccepted()
    {
        var content = new SyntheticDownloadContent(0L, VideoType);
        using var rig = CreateRig(_ => Accepted(content, SyntheticDownloadContent.ExpectedSha256(0L)));
        using var destination = new CountingWriteStream();

        var result = await rig.Client.DownloadAsync(Node(), TaskId, RenderOutputKind.Video, destination, 1_024L, Ct).WaitAsync(RealTimeBudget, Ct);

        result.IsError.ShouldBeFalse(result.FirstError.Description);
        result.Value.Bytes.ShouldBe(0L);
        result.Value.Sha256.ShouldBe(SyntheticDownloadContent.ExpectedSha256(0L));
        result.Value.Sha256.ShouldBe("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        destination.Bytes.ShouldBe(0L);
    }

    /// <summary>The configured subtitle ceiling is the BD6 2 MiB, independent of a generous caller ceiling.</summary>
    [Theory]
    [InlineData(2_097_152L, true)]
    [InlineData(2_097_153L, false)]
    public async Task Download_SubtitleCap_IsTwoMiBByDefault(long bodyBytes, bool accepted)
    {
        var content = new SyntheticDownloadContent(bodyBytes, SubRipType);
        using var rig = CreateRig(_ => Accepted(content), new RenderClientOptions());
        using var destination = new CountingWriteStream();

        var result = await rig.Client.DownloadAsync(
            Node(), TaskId, RenderOutputKind.Subtitles, destination, long.MaxValue, Ct).WaitAsync(RealTimeBudget, Ct);

        if (accepted)
        {
            result.IsError.ShouldBeFalse(result.FirstError.Description);
            result.Value.Bytes.ShouldBe(bodyBytes);
        }
        else
        {
            FailureOf(result).Detail.ShouldBe("render_output_too_large");
            content.BytesServed.ShouldBe(0L);
        }
    }

    /// <summary>The media type is matched without case, and a charset parameter is tolerated (05 D4).</summary>
    [Theory]
    [InlineData("VIDEO/MP4")]
    [InlineData("Video/Mp4")]
    [InlineData("video/mp4")]
    public async Task Download_MediaTypeCase_IsIgnored(string mediaType)
    {
        const long Size = 512L;
        var content = new SyntheticDownloadContent(Size, mediaType);
        using var rig = CreateRig(_ => Accepted(content, SyntheticDownloadContent.ExpectedSha256(Size)));

        using var destination = new CountingWriteStream();
        var result = await rig.Client.DownloadAsync(Node(), TaskId, RenderOutputKind.Video, destination, 1_024L, Ct).WaitAsync(RealTimeBudget, Ct);

        result.IsError.ShouldBeFalse(result.FirstError.Description);
        result.Value.Bytes.ShouldBe(Size);
    }

    /// <summary>An answer with no media type at all is invalid, never assumed to be the kind asked for.</summary>
    [Fact]
    public async Task Download_MissingContentType_IsOutputInvalid()
    {
        using var rig = CreateRig(_ =>
        {
            var content = new ByteArrayContent(new byte[64]);
            content.Headers.ContentLength = 64L;

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        using var destination = new CountingWriteStream();
        var result = await rig.Client.DownloadAsync(Node(), TaskId, RenderOutputKind.Video, destination, 1_024L, Ct).WaitAsync(RealTimeBudget, Ct);

        var failure = FailureOf(result);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderOutputInvalid);
        failure.Detail.ShouldBe("render_output_content_type_mismatch");
        destination.Bytes.ShouldBe(0L);
    }

    /// <summary>The declared digest is trimmed and compared case-insensitively; a malformed one is a mismatch.</summary>
    /// <param name="declared">The raw header value the node sends.</param>
    /// <param name="matches">Whether the digest describes the body the node served.</param>
    [Theory]
    [InlineData("exact-upper", true)]
    [InlineData("padded", true)]
    [InlineData("malformed", false)]
    [InlineData("empty-string", false)]
    public async Task Download_ContentSha256Header_States(string declared, bool matches)
    {
        const long Size = 4_096L;
        var digest = SyntheticDownloadContent.ExpectedSha256(Size);
        var header = declared switch
        {
            "exact-upper" => digest.ToUpperInvariant(),
            "padded" => $"  {digest}  ",
            "malformed" => "zz-not-a-hash",
            _ => string.Empty,
        };

        var content = new SyntheticDownloadContent(Size, VideoType);
        using var rig = CreateRig(
            _ => Accepted(content, header),
            new RenderClientOptions { MaxVideoBytes = 64L * 1_024L * 1_024L });
        using var destination = new CountingWriteStream();

        var result = await rig.Client.DownloadAsync(Node(), TaskId, RenderOutputKind.Video, destination, 1_024L * 64, Ct).WaitAsync(RealTimeBudget, Ct);

        if (matches)
        {
            result.IsError.ShouldBeFalse(result.FirstError.Description);
            result.Value.Sha256.ShouldBe(digest);
        }
        else
        {
            var failure = FailureOf(result);
            failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderOutputInvalid);
            failure.Detail.ShouldBe("render_output_sha256_mismatch");
        }
    }

    /// <summary>
    /// A destination that fails is the caller's own stream (the ingest's temp file), so its
    /// <see cref="IOException"/> leaves this class as it came in and is never reported as a node fault.
    /// </summary>
    [Fact]
    public async Task Download_DestinationThrows_PropagatesTheCallersIOException()
    {
        var content = new SyntheticDownloadContent(4_096L, VideoType);
        using var rig = CreateRig(
            _ => Accepted(content),
            new RenderClientOptions { MaxVideoBytes = 64L * 1_024L * 1_024L });
        using var destination = new ThrowingWriteStream(new IOException("There is not enough space on the disk."));

        Exception? thrown = null;
        var outcome = "the call never returned";
        try
        {
            var result = await rig.Client.DownloadAsync(
                Node(), TaskId, RenderOutputKind.Video, destination, 1_024L * 64, Ct).WaitAsync(RealTimeBudget, Ct);

            outcome = result.IsError
                ? $"the call answered '{result.FirstError.Description}'"
                : "the call reported a successful download";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            thrown = exception;
            outcome = $"the call threw {exception.GetType().Name}";
        }

        // The prerequisite of the assertion below: the body must actually have been streamed into the caller's
        // stream, otherwise "nothing was thrown" would have nothing to do with swallowing a destination fault.
        content.BytesServed.ShouldBeGreaterThan(
            0L, "the failure must happen while the body is being written, never before the read starts");
        thrown.ShouldBeOfType<IOException>(
            $"{outcome} instead of letting the caller's own stream failure out: a destination write that fails "
            + "is the ingest's temp file (05 D4, task §Failure handling), so swallowing it would report a "
            + "truncated file as a finished download");
    }

    /// <summary>
    /// An already-cancelled caller token ends the call with the caller's own <see cref="OperationCanceledException"/>
    /// before the node is touched at all: no request is sent and no byte of the node's body is read (D7).
    /// </summary>
    [Fact]
    public async Task Download_AlreadyCancelledToken_ThrowsAndReadsNothing()
    {
        // A body that stalls on its very first read: if the call ever reached the body, the fixture's stream
        // would observe that read (Stalled) before the token ended it.
        var content = new SyntheticDownloadContent(1_024L, VideoType, declareLength: false, stallAfterBytes: 0);
        using var rig = CreateRig(_ => Accepted(content));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var destination = new CountingWriteStream();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await rig.Client.DownloadAsync(
                Node(), TaskId, RenderOutputKind.Video, destination, 1_024L, cancellation.Token).WaitAsync(RealTimeBudget, Ct));

        rig.Handler.Requests.ShouldBeEmpty("a call that arrives already cancelled never touches the node (D7)");
        content.Stalled.IsCompleted.ShouldBeFalse("the body of a cancelled call is never opened");
        content.BytesServed.ShouldBe(0L, "a cancelled call never reads the node's body");
        destination.Bytes.ShouldBe(0L);
    }

    /// <summary>Every redirect status is refused rather than followed, whatever the Location says (05 D4).</summary>
    /// <param name="status">The 3xx status.</param>
    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task Download_RedirectStatuses_AreNeverFollowed(int status)
    {
        var calls = 0;
        using var rig = CreateRig(_ =>
        {
            Interlocked.Increment(ref calls);

            var response = new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "text/plain"),
            };
            response.Headers.Location = new Uri("https://elsewhere.example/v1/renders/x/video");

            return response;
        });

        using var destination = new CountingWriteStream();
        var result = await rig.Client.DownloadAsync(Node(), TaskId, RenderOutputKind.Video, destination, 1_024L, Ct).WaitAsync(RealTimeBudget, Ct);

        var failure = FailureOf(result);
        failure.Kind.ShouldBe(RenderClientFailureKind.TerminalFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        failure.Alert.ShouldBeTrue();
        calls.ShouldBe(1, "the Location was never requested");
        destination.Bytes.ShouldBe(0L);
    }

    /// <summary>A 206 is an accepted 2xx answer; the client reads the bytes the node chose to serve.</summary>
    [Fact]
    public async Task Download_206PartialContent_IsAccepted()
    {
        const long Size = 512L;
        var content = new SyntheticDownloadContent(Size, VideoType);
        using var rig = CreateRig(_ => new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = content,
        });

        using var destination = new CountingWriteStream();
        var result = await rig.Client.DownloadAsync(Node(), TaskId, RenderOutputKind.Video, destination, 1_024L, Ct).WaitAsync(RealTimeBudget, Ct);

        result.IsError.ShouldBeFalse(result.FirstError.Description);
        result.Value.Bytes.ShouldBe(Size);
    }

    /// <summary>A denied download ends the job and alerts, and the key is still sent on the request.</summary>
    /// <param name="status">The status.</param>
    /// <param name="code">The envelope code, or <c>null</c>.</param>
    [Theory]
    [InlineData(401, AdapterErrorCodes.Unauthorized)]
    [InlineData(403, null)]
    public async Task Download_401Or403_IsTerminalAlerting(int status, string? code)
    {
        using var rig = CreateRig(_ => Failed((HttpStatusCode)status, code));

        using var destination = new CountingWriteStream();
        var result = await rig.Client.DownloadAsync(Node(), TaskId, RenderOutputKind.Video, destination, 1_024L, Ct).WaitAsync(RealTimeBudget, Ct);

        var failure = FailureOf(result);
        failure.Kind.ShouldBe(RenderClientFailureKind.TerminalFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        failure.Alert.ShouldBeTrue();

        rig.Handler.Requests.ShouldHaveSingleItem().ApiKey.ShouldBe(
            ApiKeyValue, "a failed download still travels on the node's own client (F13)");
    }

    /// <summary>The key is sent on every download request and reaches no log line, with a positive control.</summary>
    [Fact]
    public async Task Download_ApiKeyIsSent_AndReachesNoLogLine()
    {
        const long Size = 256L;
        var content = new SyntheticDownloadContent(Size, VideoType);
        using var rig = CreateRig(
            _ => Accepted(content, SyntheticDownloadContent.ExpectedSha256(Size)),
            captureLogs: true);

        using var destination = new CountingWriteStream();
        var result = await rig.Client.DownloadAsync(Node(), TaskId, RenderOutputKind.Video, destination, 1_024L, Ct).WaitAsync(RealTimeBudget, Ct);

        result.IsError.ShouldBeFalse(result.FirstError.Description);
        rig.Handler.Requests.ShouldHaveSingleItem().ApiKey.ShouldBe(ApiKeyValue);

        var text = rig.LogText;
        text.ShouldNotBeEmpty("the positive control: a completed download is logged");
        text.ShouldContain("render-node.download.completed");
        text.ShouldNotContain(ApiKeyValue, Case.Sensitive);
        text.ShouldNotContain("qa.example", Case.Sensitive);
    }

    private static WorkerNode Node(string name = "alpha") =>
        WorkerNode.Create(name, NodeUrl, capacity: 1, tlsCertSha256: new string('A', 64), gpuModel: null).Value;

    /// <summary>A body that declares one length and serves another, which no honest node does (BD6).</summary>
    /// <param name="declared">The length the node announces.</param>
    /// <param name="actualBytes">The bytes the node actually serves.</param>
    /// <returns>The content.</returns>
    private static ByteArrayContent Lying(long declared, int actualBytes)
    {
        var content = new ByteArrayContent(new byte[actualBytes]);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(VideoType);
        content.Headers.ContentLength = declared;

        return content;
    }

    /// <summary>The digest of what <see cref="Lying"/> serves: its zero bytes, since every byte is zero.</summary>
    /// <param name="content">The content to hash.</param>
    /// <returns>The lowercase hex SHA-256 of the bytes it serves.</returns>
    private static string Sha256Of(HttpContent content)
    {
        var bytes = content.ReadAsByteArrayAsync().GetAwaiter().GetResult();

        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    private static HttpResponseMessage Accepted(HttpContent content, string? sha256 = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };

        if (sha256 is not null)
        {
            response.Headers.TryAddWithoutValidation(Sha256Header, sha256);
        }

        return response;
    }

    private static HttpResponseMessage Failed(HttpStatusCode status, string? code)
    {
        var body = code is null
            ? "{\"message\":\"no envelope here\"}"
            : $"{{\"error\":{{\"code\":\"{code}\",\"message\":\"The adapter refused the download (05 D4 S4).\"}}}}";

        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private static RenderClientFailure FailureOf(ErrorOr<DownloadResult> result)
    {
        result.IsError.ShouldBeTrue("the download was expected to fail");
        RenderClientErrors.TryGetFailure(result.FirstError, out var failure)
            .ShouldBeTrue($"a download client error always carries its typed failure ({result.FirstError.Code})");

        return failure!;
    }

    private static DownloadRig CreateRig(
        Func<HttpRequestMessage, HttpResponseMessage> answer,
        RenderClientOptions? options = null,
        bool captureLogs = false)
    {
        var handler = new SyntheticDownloadHandler(answer);
        var factory = new SyntheticDownloadNodeFactory(handler, ApiKeyValue);
        var time = new ManualTimeProvider();

        CapturingLogProvider? sink = null;
        ILoggerFactory? loggers = null;
        ILogger<RenderDownloadClient> logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<RenderDownloadClient>.Instance;

        if (captureLogs)
        {
            sink = new CapturingLogProvider();
            loggers = LoggerFactory.Create(builder => builder.AddProvider(sink));
            logger = loggers.CreateLogger<RenderDownloadClient>();
        }

        var client = new RenderDownloadClient(
            factory,
            Options.Create(options ?? new RenderClientOptions { MaxVideoBytes = 1_024L, MaxSubtitleBytes = 1_024L }),
            Options.Create(new RenderNodesOptions()),
            time,
            logger);

        return new DownloadRig(client, handler, sink, loggers, factory);
    }

    /// <summary>One download test's node, logger capture and the client under test; disposing releases them all.</summary>
    private sealed record DownloadRig(
        RenderDownloadClient Client,
        SyntheticDownloadHandler Handler,
        CapturingLogProvider? Sink,
        ILoggerFactory? Loggers,
        SyntheticDownloadNodeFactory Factory) : IDisposable
    {
        /// <summary>Every captured line as one text.</summary>
        public string LogText => Sink?.Text ?? string.Empty;

        public void Dispose()
        {
            Loggers?.Dispose();
            Factory.Dispose();
        }
    }

    /// <summary>Keeps every rendered log line, so a test can scan it for a leaked key or URL (BD16).</summary>
    private sealed class CapturingLogProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];
        private readonly Lock _gate = new();

        internal string Text
        {
            get
            {
                lock (_gate)
                {
                    return string.Join('\n', _messages);
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Sink(this);

        public void Dispose()
        {
        }

        private sealed class Sink(CapturingLogProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);

                lock (owner._gate)
                {
                    owner._messages.Add(formatter(state, exception));
                }
            }
        }
    }

    /// <summary>A destination whose write fails, standing in for the ingest's temp file on a full disk.</summary>
    private sealed class ThrowingWriteStream(IOException failure) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => throw failure;

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) => throw failure;

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>
/// QA probes of the 09.6.2-T1 wiring the two hosts compose: the real <c>appsettings.json</c> of the Api and
/// the Worker must carry the BD6 values, and both composition roots (the Api's <c>Program</c> and the
/// Worker's <c>AddWorkerServices</c>) must resolve <see cref="IRenderClient"/> and
/// <see cref="IRenderDownloadClient"/> — a missing <c>TimeProvider</c>, connection factory or metrics
/// registration is exactly the regression this catches.
/// </summary>
public sealed class RenderClientCompositionQaTests : IntegrationTest
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public RenderClientCompositionQaTests(TestDatabase database)
        : base(database)
    {
    }

    /// <summary>The two host settings files carry every BD6 client value, not only the ones a test sets.</summary>
    /// <param name="relativePath">The settings file, relative to the repository root.</param>
    [Theory]
    [InlineData("src/NightTales.Api/appsettings.json")]
    [InlineData("src/NightTales.Worker/appsettings.json")]
    public void AppSettings_CarryTheBd6RenderValues(string relativePath)
    {
        var file = Path.Combine(RepositoryRoot(), relativePath);
        File.Exists(file).ShouldBeTrue($"{file} must exist");

        // The repo's appsettings.json carries // comments, so the reader must skip them (a plain
        // JsonNode.Parse throws on the first comment).
        var render = JsonNode.Parse(
            File.ReadAllText(file),
            nodeOptions: null,
            documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            })!["Render"];
        render.ShouldNotBeNull($"{relativePath} must configure the Render section");

        render!["Client"]!["AttemptSeconds"]!.GetValue<int>().ShouldBe(10);
        render["Client"]!["TotalSeconds"]!.GetValue<int>().ShouldBe(30);
        render["Client"]!["MaxRetries"]!.GetValue<int>().ShouldBe(3);
        render["Client"]!["CircuitBreaker"]!["MinimumThroughput"]!.GetValue<int>().ShouldBe(5);
        render["Client"]!["CircuitBreaker"]!["FailureRatio"]!.GetValue<double>().ShouldBe(1.0);
        render["Client"]!["CircuitBreaker"]!["SamplingDurationSeconds"]!.GetValue<int>().ShouldBe(60);
        render["Client"]!["CircuitBreaker"]!["BreakDurationSeconds"]!.GetValue<int>().ShouldBe(30);
        render["Download"]!["IdleSeconds"]!.GetValue<int>().ShouldBe(60);
        render["DownloadMaxMinutes"]!.GetValue<int>().ShouldBe(20);
        render["MaxVideoBytes"]!.GetValue<long>().ShouldBe(2_147_483_648L);
        render["MaxSubtitleBytes"]!.GetValue<long>().ShouldBe(2_097_152L);
    }

    /// <summary>The Api's own composition root resolves both clients and binds those values.</summary>
    [Fact]
    public void ApiCompositionRoot_ResolvesBothClients_WithTheBoundOptions()
    {
        using var factory = new ApiFactory(Database, new FakeClock());

        var client = factory.Services.GetRequiredService<IRenderClient>();
        var download = factory.Services.GetRequiredService<IRenderDownloadClient>();
        var options = factory.Services.GetRequiredService<IOptionsMonitor<RenderClientOptions>>().CurrentValue;

        client.ShouldBeAssignableTo<MptRenderClient>();
        download.ShouldBeAssignableTo<RenderDownloadClient>();
        options.Client.AttemptSeconds.ShouldBe(10);
        options.Client.TotalSeconds.ShouldBe(30);
        options.Client.MaxRetries.ShouldBe(3);
        options.Client.CircuitBreaker.MinimumThroughput.ShouldBe(5);
        options.Client.CircuitBreaker.FailureRatio.ShouldBe(1.0);
        options.Client.CircuitBreaker.SamplingDurationSeconds.ShouldBe(60);
        options.Client.CircuitBreaker.BreakDurationSeconds.ShouldBe(30);
        options.Download.IdleSeconds.ShouldBe(60);
        options.DownloadMaxMinutes.ShouldBe(20);
        options.MaxVideoBytes.ShouldBe(2_147_483_648L);
        options.MaxSubtitleBytes.ShouldBe(2_097_152L);
    }

    /// <summary>
    /// The Worker's composition root resolves both clients too. The harness deliberately keeps the Worker's
    /// <c>appsettings.json</c> out of the host, so this is also the proof that every key has a usable default.
    /// </summary>
    [Fact]
    public async Task WorkerCompositionRoot_ResolvesBothClients_WithTheDefaultOptions()
    {
        await using var host = new WorkerHost(Database, new FakeClock());
        await host.StartAsync(TestContext.Current.CancellationToken).WaitAsync(RealTimeBudget, Ct);

        var client = host.Services.GetRequiredService<IRenderClient>();
        var download = host.Services.GetRequiredService<IRenderDownloadClient>();
        var options = host.Services.GetRequiredService<IOptionsMonitor<RenderClientOptions>>().CurrentValue;

        client.ShouldBeAssignableTo<MptRenderClient>();
        download.ShouldBeAssignableTo<RenderDownloadClient>();
        options.Client.TotalSeconds.ShouldBe(30);
        options.Client.MaxRetries.ShouldBe(3);
        options.Download.IdleSeconds.ShouldBe(60);
        options.MaxVideoBytes.ShouldBe(2_147_483_648L);
        options.MaxSubtitleBytes.ShouldBe(2_097_152L);
    }

    /// <summary>Walks up from the test binary to the repository root that owns <c>src/</c>.</summary>
    /// <returns>The repository root.</returns>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "NightTales.Api", "appsettings.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"The repository root was not found above '{AppContext.BaseDirectory}'.");
    }
}
