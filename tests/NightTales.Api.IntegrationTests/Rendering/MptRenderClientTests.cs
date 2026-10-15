using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
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
using static NightTales.TestKit.Rendering.ManualClockDrive;

namespace NightTales.Api.IntegrationTests.Rendering;

/// <summary>
/// <c>MptRenderClient</c> against the scripted node and the frozen D14 bodies (09.6.2-T1 task §Tests): the
/// three calls' wire shapes, the mock-mpt <c>MOCK_FAIL_MODE</c> bodies as the client deserialises them, the
/// faults that never produce an answer (a refused handshake, a body that resets mid-read, a node that cannot
/// be called at all), and the one rule that runs through all of them — neither the node's key nor its base URL
/// ever reaches a log line or a failure (BD16/S4).
/// </summary>
public sealed class MptRenderClientTests
{
    private const string TaskId = "3f7b9c2e-1a44-4d0f-9e21-6c5b8a7d0e11";

    private const string SubmitFixture = "render-submit.v1.landscape-vi.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Submit_MapsAdapterAcceptedV1()
    {
        var taskId = Guid.NewGuid();
        var handler = new ScriptedNodeHandler(NodeHttpStep.Json(202, new AdapterAcceptedV1(taskId, "queued")));
        using var provider = Provider(new ScriptedNodeConnectionFactory(handler), new ManualTimeProvider());
        var client = provider.GetRequiredService<IRenderClient>();

        var submitted = await client.SubmitAsync(Node(), SubmitBody(), CancellationToken.None).WaitAsync(RealTimeBudget, Ct);

        submitted.IsError.ShouldBeFalse();
        submitted.Value.TaskId.ShouldBe(taskId);
        submitted.Value.State.ShouldBe("queued");
        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].Method.ShouldBe("POST");
        handler.Requests[0].Path.ShouldBe("/v1/renders");
    }

    [Fact]
    public async Task Submit_RetryReplaysSameRenderJobId_AdapterAccepts()
    {
        var taskId = Guid.NewGuid();
        var handler = new ScriptedNodeHandler(
            NodeHttpStep.Answer(500, Envelope(AdapterErrorCodes.InternalError)),
            NodeHttpStep.Json(202, new AdapterAcceptedV1(taskId, "queued")));

        var time = new ManualTimeProvider();
        using var provider = Provider(new ScriptedNodeConnectionFactory(handler), time);
        var client = provider.GetRequiredService<IRenderClient>();

        var submitted = await DriveAsync(client.SubmitAsync(Node(), SubmitBody(), CancellationToken.None), time);

        submitted.IsError.ShouldBeFalse();
        submitted.Value.TaskId.ShouldBe(taskId);
        handler.Requests.Count.ShouldBe(2);

        // The retry must replay the very same bytes, or the adapter's idempotency check sees a second job.
        var renderJobId = JsonNode.Parse(ContractFixtures.Read(SubmitFixture))!["render_job_id"]!.GetValue<string>();
        handler.Requests[0].Body.ShouldNotBeNull().ShouldContain(renderJobId);
        handler.Requests[1].Body.ShouldBe(handler.Requests[0].Body);
    }

    [Fact]
    public async Task GetStatus_MapsRenderStatusV1()
    {
        var handler = new ScriptedNodeHandler(NodeHttpStep.Answer(200, StatusFixture("render-status.v1.succeeded.json")));
        using var provider = Provider(new ScriptedNodeConnectionFactory(handler), new ManualTimeProvider());
        var client = provider.GetRequiredService<IRenderClient>();

        var status = await client.GetStatusAsync(Node(), TaskId, CancellationToken.None).WaitAsync(RealTimeBudget, Ct);

        status.IsError.ShouldBeFalse();
        status.Value.Schema.ShouldBe("render-status.v1");
        status.Value.State.ShouldBe("succeeded");
        status.Value.Output.ShouldNotBeNull();
        handler.Requests[0].Method.ShouldBe("GET");
        handler.Requests[0].Path.ShouldBe($"/v1/renders/{TaskId}");
    }

    [Fact]
    public async Task GetStatus_404_OnSubmittedTask_IsRenderLost()
    {
        var handler = new ScriptedNodeHandler(NodeHttpStep.Answer(404, Envelope(AdapterErrorCodes.TaskUnknown)));
        using var provider = Provider(new ScriptedNodeConnectionFactory(handler), new ManualTimeProvider());
        var client = provider.GetRequiredService<IRenderClient>();

        var status = await client.GetStatusAsync(Node(), TaskId, CancellationToken.None).WaitAsync(RealTimeBudget, Ct);

        status.IsError.ShouldBeTrue();
        handler.Requests.Count.ShouldBe(1);

        RenderClientErrors.TryGetFailure(status.Errors[0], out var failure).ShouldBeTrue();
        failure!.Kind.ShouldBe(RenderClientFailureKind.TerminalFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderLost);
        failure.Alert.ShouldBeFalse();
    }

    /// <summary>
    /// The bodies the mock's <c>MOCK_FAIL_MODE</c> values serve (11.1.2-T1 D5), as this client deserialises
    /// them: a failed status carries its outcome code, a timed-out render is still a running status, and a
    /// failing quality check is a <em>successful</em> status — the policy of 11.3.3-T1 judges the check block,
    /// never this client. What this proves is the status body's deserialisation and the state and outcome the
    /// caller reads back; the mock's own replay tests prove the node served those bytes.
    /// </summary>
    /// <param name="mode">The mock's fail mode the body belongs to.</param>
    /// <param name="fixture">The frozen body the mock serves for it.</param>
    /// <param name="outcomeCode">The code the failed status must carry.</param>
    [Theory]
    [InlineData("tts_failed", "render-status.v1.failed-tts.json", RenderOutcomeCodes.TtsFailed)]
    [InlineData("assembly_failed", "render-status.v1.failed-assembly.json", RenderOutcomeCodes.RenderAssemblyFailed)]
    [InlineData("node_error", "render-status.v1.failed-tts.json", RenderOutcomeCodes.RenderNodeError)]
    [InlineData("timeout", "render-status.v1.running-voice.json", null)]
    [InlineData("quality:loudness", "render-status.v1.succeeded.json", null)]
    public async Task MockFailModeBody_IsDeserialisedByTheClient_WithTheExpectedStateAndOutcome(
        string mode,
        string fixture,
        string? outcomeCode)
    {
        // The fixture's own task_id is never the id the caller asks for, and the client refuses a status that
        // names another task, so the id is patched in memory here (the frozen fixture is never edited).
        var json = StatusFixture(fixture);
        if (mode == "node_error")
        {
            var patched = JsonNode.Parse(json)!.AsObject();
            patched["outcome"]!["code"] = RenderOutcomeCodes.RenderNodeError;
            json = patched.ToJsonString();
        }

        var handler = new ScriptedNodeHandler(NodeHttpStep.Answer(200, json));
        using var provider = Provider(new ScriptedNodeConnectionFactory(handler), new ManualTimeProvider());
        var client = provider.GetRequiredService<IRenderClient>();

        var status = await client.GetStatusAsync(Node(), TaskId, CancellationToken.None).WaitAsync(RealTimeBudget, Ct);

        status.IsError.ShouldBeFalse(
            $"MOCK_FAIL_MODE={mode} is a status the client reads, not a failure: "
            + string.Join(", ", status.Errors.Select(error => error.Code)));

        switch (mode)
        {
            case "timeout":
                status.Value.State.ShouldBe("running");
                break;
            case "quality:loudness":
                status.Value.State.ShouldBe("succeeded");
                status.Value.Check.ShouldNotBeNull();
                break;
            default:
                status.Value.State.ShouldBe("failed");
                status.Value.Outcome.ShouldNotBeNull();
                status.Value.Outcome!.Code.ShouldBe(outcomeCode);
                break;
        }
    }

    /// <summary>The sixth fail mode: the node has no such task at all, which is a lost render (05 D5).</summary>
    [Fact]
    public async Task MockFailMode_LostTask_IsRenderLost()
    {
        var handler = new ScriptedNodeHandler(NodeHttpStep.Answer(404, Envelope(AdapterErrorCodes.TaskUnknown)));
        using var provider = Provider(new ScriptedNodeConnectionFactory(handler), new ManualTimeProvider());
        var client = provider.GetRequiredService<IRenderClient>();

        var status = await client.GetStatusAsync(Node(), TaskId, CancellationToken.None).WaitAsync(RealTimeBudget, Ct);

        status.IsError.ShouldBeTrue();
        RenderClientErrors.TryGetFailure(status.Errors[0], out var failure).ShouldBeTrue();
        failure!.Kind.ShouldBe(RenderClientFailureKind.TerminalFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderLost);
    }

    /// <summary>
    /// The secret rule of BD16/S4 across every way a call can end: success, a rotated key (401), a node fault
    /// (500), a transport failure and an open breaker. The sentinel node's key and base URL are unique strings,
    /// and every log line, error code, error sentence and exception message is searched for them.
    /// </summary>
    [Fact]
    public async Task ApiKeyAndBaseUrl_NeverAppearInLogsOrFailures()
    {
        const string key = "sentinel-node-key-7f31c9d4";
        const string host = "sentinel-node-9f3a.invalid";
        var url = new Uri($"https://{host}:8443/");

        // The first call succeeds on its own first attempt, so nothing here waits on the clock: a failure
        // script at this point would park the call on the retry backoff, which only DriveAsync advances.
        var handler = new ScriptedNodeHandler(NodeHttpStep.Json(202, new AdapterAcceptedV1(Guid.Parse(TaskId), "queued")));
        var logs = new CapturingLogger();
        var time = new ManualTimeProvider();
        using var provider = Provider(new ScriptedNodeConnectionFactory(handler, key, url), time, logs);
        var client = provider.GetRequiredService<IRenderClient>();
        var node = Node("sentinel");

        var reasoning = new List<ErrorOr.Error>();

        // A success answers with a body the client reads; it must log nothing that identifies the node.
        var accepted = await client.SubmitAsync(node, SubmitBody(), CancellationToken.None).WaitAsync(RealTimeBudget, Ct);
        accepted.IsError.ShouldBeFalse();

        // A rotated key: terminal and alerting.
        handler.Rescript(NodeHttpStep.Json(401, Envelope(AdapterErrorCodes.Unauthorized)));
        var unauthorized = await DriveAsync(client.GetStatusAsync(node, TaskId, CancellationToken.None), time);
        unauthorized.IsError.ShouldBeTrue();
        reasoning.AddRange(unauthorized.Errors);

        // A node fault: 500 for the whole retry budget.
        handler.Rescript(NodeHttpStep.Answer(500, Envelope(AdapterErrorCodes.InternalError)));
        var failed = await DriveAsync(client.GetStatusAsync(node, TaskId, CancellationToken.None), time);
        failed.IsError.ShouldBeTrue();
        reasoning.AddRange(failed.Errors);

        // A transport failure whose exception message names the URL, exactly as the real socket failure does.
        handler.Rescript(NodeHttpStep.Transport(url));
        var unreachable = await DriveAsync(client.GetStatusAsync(node, TaskId, CancellationToken.None), time);
        unreachable.IsError.ShouldBeTrue();
        reasoning.AddRange(unreachable.Errors);

        // The breaker opens on the fifth failure, so the last call never reaches the node.
        handler.Rescript(NodeHttpStep.Answer(500, Envelope(AdapterErrorCodes.InternalError)));
        await DriveAsync(client.GetStatusAsync(node, TaskId, CancellationToken.None), time);
        var openCircuit = await DriveAsync(client.GetStatusAsync(node, TaskId, CancellationToken.None), time);
        openCircuit.IsError.ShouldBeTrue();
        reasoning.AddRange(openCircuit.Errors);

        // The positive control: the client did log, so an empty capture cannot make this test pass.
        logs.Lines.ShouldNotBeEmpty();
        logs.Lines.Count(line => line.Contains("render node", StringComparison.OrdinalIgnoreCase)).ShouldBeGreaterThan(0);
        reasoning.Count.ShouldBeGreaterThan(0);

        foreach (var line in logs.Lines)
        {
            line.ShouldNotContain(key);
            line.ShouldNotContain(host);
            line.ShouldNotContain(url.ToString());
        }

        foreach (var error in reasoning)
        {
            error.Code.ShouldNotContain(key);
            error.Code.ShouldNotContain(host);
            error.Description.ShouldNotContain(key);
            error.Description.ShouldNotContain(host);
        }
    }

    /// <summary>
    /// A node row that cannot produce a client — no pinned certificate, no key or an http URL outside
    /// Development — is refused by the factory, so nothing is attempted, the breaker counts nothing and the
    /// caller gets the terminal, alerting misconfiguration. The factory's own message names the node and its
    /// configuration entry, so not one byte of it reaches the log or the failure (BD16/S4).
    /// </summary>
    [Fact]
    public async Task UnusableNode_IsTerminalAlerting_WithoutRetryingOrCountingAgainstTheBreaker()
    {
        const string refusal =
            "The render node 'alpha' has no pinned certificate; RenderNodes:Nodes[0]:TlsCertSha256 is required outside Development.";
        const string entry = "TlsCertSha256";

        var misconfigured = true;
        var taskId = Guid.NewGuid();
        var handler = new ScriptedNodeHandler(NodeHttpStep.Json(202, new AdapterAcceptedV1(taskId, "queued")));
        var logs = new CapturingLogger();
        var factory = new ScriptedNodeConnectionFactory(
            handler,
            createFailure: _ => misconfigured ? new InvalidOperationException(refusal) : null);

        using var provider = Provider(factory, new ManualTimeProvider(), logs);
        var client = provider.GetRequiredService<IRenderClient>();

        // Six refusals: the breaker's minimum throughput is five, so a failure the breaker counted would have
        // opened it and the call below could never reach the node.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var refused = await client.SubmitAsync(Node(), SubmitBody(), CancellationToken.None).WaitAsync(RealTimeBudget, Ct);

            var failure = FailureOf(refused);
            failure.Kind.ShouldBe(RenderClientFailureKind.TerminalFailure);
            failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderNodeError);
            failure.Detail.ShouldBe("render_node_misconfigured");
            failure.Alert.ShouldBeTrue();
        }

        handler.Requests.ShouldBeEmpty("no client can be built, so no request may be sent");
        factory.Clients.ShouldBe(0);

        // The positive control: once the row is fixed the very next call reaches the node and succeeds, so the
        // six refusals were counted nowhere the breaker could see them.
        misconfigured = false;
        var accepted = await client.SubmitAsync(Node(), SubmitBody(), CancellationToken.None).WaitAsync(RealTimeBudget, Ct);

        accepted.IsError.ShouldBeFalse(accepted.FirstError.Description);
        handler.Requests.ShouldHaveSingleItem().Method.ShouldBe("POST");

        logs.Lines.Count(line => line.Contains("could not be built", StringComparison.Ordinal)).ShouldBeGreaterThan(
            0, "the positive control: the refusal is logged, so the leak assertions below mean something");
        foreach (var line in logs.Lines)
        {
            line.ShouldNotContain(refusal);
            line.ShouldNotContain(entry);
        }
    }

    /// <summary>A body that resets mid-read is the transport failing, so the status call retries and the caller is told to retry.</summary>
    [Fact]
    public async Task GetStatus_BodyResetsMidRead_IsTransientAfterRetries()
    {
        var handler = new ScriptedNodeHandler(NodeHttpStep.Content(200, () => new ResettingBodyContent(64)));
        var time = new ManualTimeProvider();
        using var provider = Provider(new ScriptedNodeConnectionFactory(handler), time);
        var client = provider.GetRequiredService<IRenderClient>();

        var status = await DriveAsync(client.GetStatusAsync(Node(), TaskId, CancellationToken.None), time);

        var failure = FailureOf(status);
        failure.Kind.ShouldBe(RenderClientFailureKind.TransientFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        failure.Alert.ShouldBeFalse();
        handler.Requests.Count.ShouldBe(4, "a mid-body reset is retryable, so the first attempt plus three retries reach the node");
    }

    /// <summary>The same reset on the submit: the retry budget is spent and no exception escapes the client.</summary>
    [Fact]
    public async Task Submit_BodyResetsMidRead_IsTransientAfterRetries()
    {
        var handler = new ScriptedNodeHandler(NodeHttpStep.Content(202, () => new ResettingBodyContent(64)));
        var time = new ManualTimeProvider();
        using var provider = Provider(new ScriptedNodeConnectionFactory(handler), time);
        var client = provider.GetRequiredService<IRenderClient>();

        var submitted = await DriveAsync(client.SubmitAsync(Node(), SubmitBody(), CancellationToken.None), time);

        var failure = FailureOf(submitted);
        failure.Kind.ShouldBe(RenderClientFailureKind.TransientFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        failure.Alert.ShouldBeFalse();
        handler.Requests.Count.ShouldBe(4);
    }

    /// <summary>
    /// A refused pinned handshake is the node's configuration, not its availability: it is never retried and
    /// never counted against the breaker, and it ends the job with the terminal, alerting tls failure without
    /// the node's URL or the exception's text reaching a log line (BD16/S4).
    /// </summary>
    [Fact]
    public async Task RefusedTlsHandshake_IsTerminalAlertingAndNotRetried()
    {
        const string host = "pinned-node-4f21.invalid";
        var url = new Uri($"https://{host}:8443/");
        var handler = new ScriptedNodeHandler(NodeHttpStep.TlsFailure(url));
        var logs = new CapturingLogger();
        var time = new ManualTimeProvider();
        using var provider = Provider(new ScriptedNodeConnectionFactory(handler), time, logs);
        var client = provider.GetRequiredService<IRenderClient>();

        var status = await client.GetStatusAsync(Node(), TaskId, CancellationToken.None).WaitAsync(RealTimeBudget, Ct);

        handler.Requests.ShouldHaveSingleItem("a refused handshake is not a fault another attempt can fix");
        var failure = FailureOf(status);
        failure.Kind.ShouldBe(RenderClientFailureKind.TerminalFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        failure.Detail.ShouldBe("render_node_tls_failure");
        failure.Alert.ShouldBeTrue();

        logs.Lines.Count(line => line.Contains("tls_failure", StringComparison.Ordinal)).ShouldBeGreaterThan(
            0, "the positive control: the failure is logged under its fixed reason");
        foreach (var line in logs.Lines)
        {
            line.ShouldNotContain(url.ToString());
            line.ShouldNotContain(host);
            line.ShouldNotContain("remote certificate", Case.Insensitive);
        }
    }

    /// <summary>
    /// An answer whose declared length is over the client's cap is refused before a single byte is read, so a
    /// node cannot make the Worker pull a body it will throw away (BD16).
    /// </summary>
    [Fact]
    public async Task GetStatus_ContentLengthOverTheCap_IsRefusedBeforeTheBodyIsRead()
    {
        var content = new ProbeContent(128 * 1024);
        var handler = new ScriptedNodeHandler(NodeHttpStep.Content(200, () => content));
        var time = new ManualTimeProvider();
        using var provider = Provider(new ScriptedNodeConnectionFactory(handler), time);
        var client = provider.GetRequiredService<IRenderClient>();

        var status = await client.GetStatusAsync(Node(), TaskId, CancellationToken.None).WaitAsync(RealTimeBudget, Ct);

        content.Reads.ShouldBe(0, "a declared length over the cap is refused before the read starts");
        var failure = FailureOf(status);
        failure.Kind.ShouldBe(RenderClientFailureKind.TerminalFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        failure.Alert.ShouldBeTrue();
        handler.Requests.ShouldHaveSingleItem("an unreadable 2xx is contract drift, never a retry");
    }

    /// <summary>The same cap where the node declares no length at all: only the count of arriving bytes bounds it.</summary>
    [Fact]
    public async Task GetStatus_UndeclaredBodyOverTheCap_IsRefusedByTheRunningCount()
    {
        var content = new ProbeContent(128 * 1024, declareLength: false);
        var handler = new ScriptedNodeHandler(NodeHttpStep.Content(200, () => content));
        var time = new ManualTimeProvider();
        using var provider = Provider(new ScriptedNodeConnectionFactory(handler), time);
        var client = provider.GetRequiredService<IRenderClient>();

        var status = await client.GetStatusAsync(Node(), TaskId, CancellationToken.None).WaitAsync(RealTimeBudget, Ct);

        content.Reads.ShouldBeGreaterThan(0, "with no declared length the cap can only be enforced while reading");
        var failure = FailureOf(status);
        failure.Kind.ShouldBe(RenderClientFailureKind.TerminalFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        failure.Alert.ShouldBeTrue();
    }

    /// <summary>
    /// The cancel and the delete carry nothing this client reads, so a 2xx body is never pulled off the wire:
    /// even an answer far over the cap costs those two calls nothing.
    /// </summary>
    [Fact]
    public async Task CancelAndDelete_TwoHundredSuccess_DoNotReadTheAnswerBody()
    {
        var cancelBody = new ProbeContent(128 * 1024);
        var deleteBody = new ProbeContent(128 * 1024);
        var handler = new ScriptedNodeHandler(
            NodeHttpStep.Content(202, () => cancelBody),
            NodeHttpStep.Content(204, () => deleteBody));

        var time = new ManualTimeProvider();
        using var provider = Provider(new ScriptedNodeConnectionFactory(handler), time);
        var client = provider.GetRequiredService<IRenderClient>();

        var cancelled = await client.CancelAsync(Node(), TaskId, CancellationToken.None).WaitAsync(RealTimeBudget, Ct);
        var deleted = await client.DeleteAsync(Node(), TaskId, CancellationToken.None).WaitAsync(RealTimeBudget, Ct);

        cancelled.IsError.ShouldBeFalse(cancelled.FirstError.Description);
        deleted.IsError.ShouldBeFalse(deleted.FirstError.Description);
        cancelBody.Reads.ShouldBe(0, "a cancel's 2xx body is never read");
        deleteBody.Reads.ShouldBe(0, "a delete's 2xx body is never read");
        handler.Requests.Count.ShouldBe(2);
    }

    /// <summary>
    /// A code that is not the adapter's <c>^[a-z][a-z_]{0,63}$</c> shape is no code at all: it becomes the
    /// code-less fail-closed terminal failure instead of travelling into the failure's detail, so a node cannot
    /// put anything of its own into a caller-readable field (BD16/S4).
    /// </summary>
    /// <param name="code">The code the node puts in its envelope.</param>
    [Theory]
    [InlineData("adapter-SURPRISE")]
    [InlineData("../../etc/passwd")]
    [InlineData("adapter_surprise\n")]
    [InlineData("1adapter")]
    public async Task UnsafeAdapterCode_IsDroppedAndFailsClosed(string code)
    {
        var handler = new ScriptedNodeHandler(NodeHttpStep.Answer(400, Envelope(code)));
        var time = new ManualTimeProvider();
        using var provider = Provider(new ScriptedNodeConnectionFactory(handler), time);
        var client = provider.GetRequiredService<IRenderClient>();

        var deleted = await client.DeleteAsync(Node(), TaskId, CancellationToken.None).WaitAsync(RealTimeBudget, Ct);

        var failure = FailureOf(deleted);
        failure.Kind.ShouldBe(RenderClientFailureKind.TerminalFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        failure.Detail.ShouldBeNull("only a code the adapter may send is ever kept");
        failure.Alert.ShouldBeTrue();
    }

    /// <summary>
    /// A status that names another task is a node answering a question nobody asked, so it fails closed and
    /// alerts instead of being read as this task's state (task §Failure handling).
    /// </summary>
    [Fact]
    public async Task GetStatus_StatusForAnotherTask_IsTerminalAlertingAndNotRetried()
    {
        var other = StatusFixture("render-status.v1.succeeded.json", Guid.NewGuid().ToString("D"));
        var handler = new ScriptedNodeHandler(NodeHttpStep.Answer(200, other));
        var logs = new CapturingLogger();
        var time = new ManualTimeProvider();
        using var provider = Provider(new ScriptedNodeConnectionFactory(handler), time, logs);
        var client = provider.GetRequiredService<IRenderClient>();

        var status = await client.GetStatusAsync(Node(), TaskId, CancellationToken.None).WaitAsync(RealTimeBudget, Ct);

        var failure = FailureOf(status);
        failure.Kind.ShouldBe(RenderClientFailureKind.TerminalFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        failure.Detail.ShouldBe("render_task_mismatch");
        failure.Alert.ShouldBeTrue();
        handler.Requests.ShouldHaveSingleItem("a mismatched status is not a retryable outcome");
        logs.Lines.ShouldContain(line => line.Contains("render_task_mismatch", StringComparison.Ordinal));
    }

    /// <summary>
    /// A state that is not the adapter's <c>^[a-z_]{1,32}$</c> code shape never reaches a debug line: it is
    /// logged as the literal <c>invalid</c>, while the accepted value the caller reads keeps exactly what the
    /// node sent (BD16/S4).
    /// </summary>
    /// <param name="state">The raw state the node answers with.</param>
    [Theory]
    [InlineData("queued")]
    [InlineData("Queued")]
    [InlineData("state with spaces")]
    [InlineData("queued\nx-api-key")]
    [InlineData("")]
    public async Task AcceptedState_IsLoggedOnlyWhenItIsACode(string state)
    {
        var handler = new ScriptedNodeHandler(
            NodeHttpStep.Json(202, new AdapterAcceptedV1(Guid.Parse(TaskId), state)));
        var logs = new CapturingLogger();
        using var provider = Provider(new ScriptedNodeConnectionFactory(handler), new ManualTimeProvider(), logs);
        var client = provider.GetRequiredService<IRenderClient>();

        var submitted = await client.SubmitAsync(Node(), SubmitBody(), CancellationToken.None).WaitAsync(RealTimeBudget, Ct);

        submitted.IsError.ShouldBeFalse(submitted.FirstError.Description);
        submitted.Value.State.ShouldBe(state);
        AssertStateLogged(logs, state);
    }

    /// <summary>The same rule on the status line, whose state the caller also reads unchanged.</summary>
    /// <param name="state">The raw state the node answers with.</param>
    [Theory]
    [InlineData("running")]
    [InlineData("Running")]
    [InlineData("x-api-key-9f3a")]
    [InlineData("")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task StatusState_IsLoggedOnlyWhenItIsACode(string state)
    {
        var json = JsonNode.Parse(StatusFixture("render-status.v1.running-voice.json"))!.AsObject();
        json["state"] = state;

        var handler = new ScriptedNodeHandler(NodeHttpStep.Answer(200, json.ToJsonString()));
        var logs = new CapturingLogger();
        using var provider = Provider(new ScriptedNodeConnectionFactory(handler), new ManualTimeProvider(), logs);
        var client = provider.GetRequiredService<IRenderClient>();

        var status = await client.GetStatusAsync(Node(), TaskId, CancellationToken.None).WaitAsync(RealTimeBudget, Ct);

        status.IsError.ShouldBeFalse(status.FirstError.Description);
        status.Value.State.ShouldBe(state);
        AssertStateLogged(logs, state);
    }

    /// <summary>The node a call is aimed at; the scripted factory supplies the transport.</summary>
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

    /// <summary>The host the client is built in: the real registration over the scripted transport.</summary>
    /// <param name="factory">The factory that hands out the client, with the key and URL a test chooses.</param>
    /// <param name="time">The clock the pipelines run on.</param>
    /// <param name="logs">A capturing logger provider, when the test asserts on log lines.</param>
    /// <returns>The provider to resolve the client from.</returns>
    private static ServiceProvider Provider(
        IRenderNodeConnectionFactory factory,
        TimeProvider time,
        ILoggerProvider? logs = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug));
        if (logs is not null)
        {
            services.AddSingleton(logs);
        }

        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(time);
        services.AddSingleton(factory);
        services.AddOptions<RenderNodesOptions>();
        services.AddRenderClients(configuration);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// A frozen status fixture with its <c>task_id</c> replaced in memory: the fixture is never edited, and the
    /// client fails closed on a status that names another task, so a test that wants the body read must serve
    /// it for the task it asks about.
    /// </summary>
    /// <param name="name">The fixture file name.</param>
    /// <param name="taskId">The task id to serve; the requested one by default.</param>
    /// <returns>The status body as JSON.</returns>
    private static string StatusFixture(string name, string taskId = TaskId)
    {
        var status = JsonNode.Parse(ContractFixtures.Read(name))!.AsObject();
        status["task_id"] = taskId;

        return status.ToJsonString();
    }

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
    /// Asserts a debug line carries the state only when it is a code: a code is echoed, anything else becomes
    /// the literal <c>invalid</c> and appears in no line at all.
    /// </summary>
    /// <param name="logs">The captured lines.</param>
    /// <param name="state">The raw state the node sent.</param>
    private static void AssertStateLogged(CapturingLogger logs, string state)
    {
        var isCode = state.Length is > 0 and <= 32 && state.All(character => char.IsAsciiLetterLower(character) || character == '_');

        if (isCode)
        {
            logs.Lines.Count(line => line.Contains(state, StringComparison.Ordinal)).ShouldBeGreaterThan(
                0, "a state that is a code is exactly what a debug line may carry");

            return;
        }

        logs.Lines.Count(line => line.Contains("invalid", StringComparison.Ordinal)).ShouldBeGreaterThan(
            0, "a state that is not a code is logged as the literal invalid");

        if (state.Length > 0)
        {
            foreach (var line in logs.Lines)
            {
                line.ShouldNotContain(state);
            }
        }
    }

    /// <summary>Captures every log line of the host, exception text included, so a leak cannot hide in one.</summary>
    private sealed class CapturingLogger : ILoggerProvider
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
}
