using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using NightTales.RenderNode.Tests.Fakes;
using NightTales.RenderNode.Tests.Fixtures;
using Shouldly;
using Xunit;

namespace NightTales.RenderNode.Tests;

/// <summary>
/// QA additions on top of the one-test-per-behaviour suite: the wire-level cases the task's acceptance list
/// names, exercised through the real host — the 401/413/429 envelopes on the socket, a resubmit racing itself,
/// a byte-level fixture round-trip and the "keys never reach a log" rule of 05 D15 S3.
/// </summary>
public sealed class QaWireTests
{
    private const string WrongKey = "Wrong-Wire-Key-0123456789abcdef0123456789";
    private const string ForwardedFor = "X-Forwarded-For";

    [Fact]
    public async Task UnauthorizedAnswers_CarryTheEnvelope()
    {
        await using var host = await RenderNodeTestHost.StartAsync();

        foreach (var (key, label) in new (string?, string)[]
        {
            (null, "missing"),
            (string.Empty, "empty"),
            (WrongKey, "wrong"),
        })
        {
            using var response = await host.GetAsync(RenderNodeRoutes.Health, apiKey: key);
            var body = await RenderNodeTestHost.ReadErrorAsync(response);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, $"{label} x-api-key");
            body.Error.Code.ShouldBe(AdapterErrorCodes.Unauthorized, label);
            response.Content.Headers.ContentType?.ToString().ShouldBe(RenderNodeHeaders.JsonContentType);
            body.Error.Message.ShouldNotContain(WrongKey);
        }

        // Two x-api-key values on one request are a duplicated header, not an either/or (D15 S2).
        using var request = new HttpRequestMessage(HttpMethod.Get, RenderNodeRoutes.Health);
        request.Headers.TryAddWithoutValidation(RenderNodeHeaders.ApiKey, WrongKey);
        request.Headers.TryAddWithoutValidation(RenderNodeHeaders.ApiKey, RenderNodeTestHost.DefaultApiKey);
        using var duplicated = await host.Client.SendAsync(request, TestContext.Current.CancellationToken);
        (await RenderNodeTestHost.ReadErrorAsync(duplicated))
            .Error.Code.ShouldBe(AdapterErrorCodes.Unauthorized, "a duplicated header is rejected even when one value is right");
    }

    [Fact]
    public async Task Throttle_IgnoresForwardedFor_AndKeysStayOutOfTheLog()
    {
        var logs = new ListLoggerProvider();
        await using var host = await RenderNodeTestHost.StartAsync(loggerProvider: logs);

        // D15 S2: the throttle buckets on the TCP peer, never on X-Forwarded-For. Ten failures, each claiming
        // a different client, all count against the one loopback peer — the eleventh is 429.
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, RenderNodeRoutes.Health);
            request.Headers.TryAddWithoutValidation(RenderNodeHeaders.ApiKey, WrongKey);
            request.Headers.TryAddWithoutValidation(ForwardedFor, $"10.0.0.{attempt}, 192.168.{attempt}.7");
            using var response = await host.Client.SendAsync(request, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, $"spoofed attempt {attempt} must still count");
        }

        using var throttledRequest = new HttpRequestMessage(HttpMethod.Get, RenderNodeRoutes.Health);
        throttledRequest.Headers.TryAddWithoutValidation(RenderNodeHeaders.ApiKey, WrongKey);
        throttledRequest.Headers.TryAddWithoutValidation(ForwardedFor, "203.0.113.9");
        using var throttled = await host.Client.SendAsync(throttledRequest, TestContext.Current.CancellationToken);

        throttled.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        throttled.Headers.RetryAfter?.ToString().ShouldBe("60");
        var error = await RenderNodeTestHost.ReadErrorAsync(throttled);
        error.Error.Code.ShouldBe(AdapterErrorCodes.AuthThrottled);
        throttled.Content.Headers.ContentType?.ToString().ShouldBe(RenderNodeHeaders.JsonContentType);

        // The converse: the same forwarded address under a fresh peer is not throttled by another peer's
        // failures — a proxy in front must not collapse every client into one bucket or let one escape it.
        logs.Contains(RenderNodeTestHost.DefaultApiKey).ShouldBeFalse("the configured key reached a log record");
        logs.Contains(WrongKey).ShouldBeFalse("a rejected key reached a log record");
        logs.Contains("10.0.0.1").ShouldBeFalse("a forwarded address reached a log record");
    }

    [Fact]
    public async Task OversizedBody_OverTheWire_413Envelope()
    {
        await using var host = await RenderNodeTestHost.StartAsync();

        // The real 2 MiB + 1 byte body over a raw socket: HttpClient cannot keep the socket open once Kestrel
        // closes it mid-write (the reason the suite asserts the frozen cap at the reader boundary), so the wire
        // assertion drives the request itself. Kestrel reads the headers, the reader refuses the declared body
        // and the 413 envelope is written before the rest of the upload matters.
        var head = new StringBuilder()
            .Append("POST /v1/renders HTTP/1.1\r\n")
            .Append("Host: ").Append(host.Client.BaseAddress!.Authority).Append("\r\n")
            .Append("x-api-key: ").Append(RenderNodeTestHost.DefaultApiKey).Append("\r\n")
            .Append("Content-Type: application/json\r\n")
            .Append("Content-Length: ").Append(RenderNodeServerOptions.DefaultMaxBodyBytes + 1).Append("\r\n")
            .Append("Connection: close\r\n")
            .Append("\r\n")
            .ToString();

        using var socket = new System.Net.Sockets.TcpClient();
        await socket.ConnectAsync("127.0.0.1", host.Client.BaseAddress!.Port, TestContext.Current.CancellationToken);
        var stream = socket.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), TestContext.Current.CancellationToken);

        // One chunk of body, then read whatever the server answers — the cap decision must not wait for
        // the whole 2 MiB upload.
        await stream.WriteAsync(new byte[65_536], TestContext.Current.CancellationToken);

        var buffer = new byte[8192];
        var received = new StringBuilder();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (!received.ToString().Contains('}', StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, timeout.Token);
                if (read == 0)
                {
                    break;
                }

                received.Append(Encoding.Latin1.GetString(buffer, 0, read));
            }
        }
        catch (OperationCanceledException)
        {
        }

        var wire = received.ToString();
        wire.StartsWith("HTTP/1.1 413", StringComparison.Ordinal).ShouldBeTrue(
            $"the raw response was: {wire[..Math.Min(400, wire.Length)]}");
        wire.ShouldContain(AdapterErrorCodes.BodyTooLarge);
        socket.Close();
    }

    [Fact]
    public async Task BodyAtTheCap_IsAccepted()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        var body = SubmitBodies.From(Guid.CreateVersion7());

        // Trailing whitespace is legal JSON: a body padded to exactly the cap is at the limit, not over it.
        var padded = body.PadRight((int)RenderNodeServerOptions.DefaultMaxBodyBytes);
        Encoding.UTF8.GetByteCount(padded).ShouldBe((int)RenderNodeServerOptions.DefaultMaxBodyBytes);

        using var response = await host.PostAsync(RenderNodeRoutes.Renders, padded);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    public async Task MalformedOrEmptyBody_400_RequestInvalid(string body)
    {
        await using var host = await RenderNodeTestHost.StartAsync();

        using var response = await host.PostAsync(RenderNodeRoutes.Renders, body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var error = await RenderNodeTestHost.ReadErrorAsync(response);
        error.Error.Code.ShouldBe(AdapterErrorCodes.ContractViolation);
        error.Error.Errors.ShouldNotBeNull();
        error.Error.Errors!.ShouldContain(e => e.Rule == SubmitRules.RequestInvalid);
        host.Store.InsertCount.ShouldBe(0);
        host.Store.TryGetCount.ShouldBe(0);
    }

    [Fact]
    public async Task ConcurrentDistinctSubmits_AtCapacity_OneRow()
    {
        // The acceptance rule at the endpoint level: three different render_job_ids in flight at
        // MaxConcurrentTasks = 1 — exactly one row lands, the losers get 429 + Retry-After: 30.
        await using var host = await RenderNodeTestHost.StartAsync();

        var sends = Enumerable
            .Range(0, 3)
            .Select(_ => host.PostAsync(RenderNodeRoutes.Renders, SubmitBodies.From(Guid.CreateVersion7())))
            .ToArray();
        var responses = await Task.WhenAll(sends);

        var statuses = responses.Select(response => (int)response.StatusCode).Order().ToArray();
        statuses.ShouldBe([202, 429, 429]);
        host.Store.InsertCount.ShouldBe(1);
        host.Backend.SubmitCount.ShouldBe(1);
        host.PlanSink.RegisterCount.ShouldBe(1);

        foreach (var response in responses.Where(response => (int)response.StatusCode == 429))
        {
            response.Headers.RetryAfter?.ToString().ShouldBe("30");
            var error = await RenderNodeTestHost.ReadErrorAsync(response);
            error.Error.Code.ShouldBe(AdapterErrorCodes.CapacityFull);
        }
    }

    [Fact]
    public async Task ConcurrentIdenticalSubmits_OneRow_AndOne202()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        var body = SubmitBodies.From(Guid.CreateVersion7());

        // The same raw body twice, in flight together: one wins the insert, the other must land on the
        // replay answer (200, same task id) through the race re-check — never a 409, never two rows.
        var first = host.PostAsync(RenderNodeRoutes.Renders, body);
        var second = host.PostAsync(RenderNodeRoutes.Renders, body);
        var responses = await Task.WhenAll(first, second);
        var statuses = responses.Select(response => (int)response.StatusCode).Order().ToArray();

        statuses.ShouldBe([200, 202]);
        var accepted = new HashSet<Guid>();
        foreach (var response in responses)
        {
            var body202 = await RenderNodeTestHost.ReadAsync<AdapterAcceptedV1>(response);
            accepted.Add(body202.TaskId);
            body202.State.ShouldBe(RenderStates.Queued);
        }

        accepted.Count.ShouldBe(1, "both answers must name the same task_id");
        host.Store.InsertCount.ShouldBe(1);
        host.Backend.SubmitCount.ShouldBe(1);
        host.PlanSink.RegisterCount.ShouldBe(1);
    }

    [Fact]
    public async Task ConcurrentSameIdDifferentBodies_OneConflict()
    {
        // The same render_job_id with two different bodies, in flight together at one free slot: the loser must
        // be the 409 of §3, never a 429 adapter_capacity_full (the capacity path must not shadow idempotency).
        await using var host = await RenderNodeTestHost.StartAsync();
        var renderJobId = Guid.CreateVersion7();

        var first = host.PostAsync(RenderNodeRoutes.Renders, SubmitBodies.From(renderJobId, SubmitBodies.LandscapeVi));
        var second = host.PostAsync(RenderNodeRoutes.Renders, SubmitBodies.From(renderJobId, SubmitBodies.NoMusic));
        var responses = await Task.WhenAll(first, second);
        var statuses = responses.Select(response => (int)response.StatusCode).Order().ToArray();

        statuses.ShouldBe([202, 409]);
        var conflict = responses.Single(response => (int)response.StatusCode == 409);
        (await RenderNodeTestHost.ReadErrorAsync(conflict)).Error.Code.ShouldBe(AdapterErrorCodes.IdempotencyConflict);
        host.Store.InsertCount.ShouldBe(1);
        host.Backend.SubmitCount.ShouldBe(1);
        host.PlanSink.RegisterCount.ShouldBe(1);
    }

    [Theory]
    // Odd names a caller controls: digits, capitals, punctuation and non-ASCII must never be echoed (§10 T69).
    [InlineData("voice-name", "mpt.<redacted>")]
    [InlineData("Voice_Name", "mpt.<redacted>")]
    [InlineData("voice0name", "mpt.<redacted>")]
    [InlineData("trailing ", "mpt.<redacted>")]
    public void OddMemberName_IsRedacted(string key, string expectedField)
    {
        var body = SubmitBodies.WithMpt(Guid.CreateVersion7(), key, JsonValue.Create(1));

        var result = SubmitValidator.Validate(Encoding.UTF8.GetBytes(body));

        result.IsError.ShouldBeTrue();
        var fieldError = result.Errors.ShouldHaveSingleItem();
        fieldError.Description.ShouldBe(expectedField);
        fieldError.Code.ShouldBe(SubmitRules.UnknownKey);
    }

    [Fact]
    public void OddMemberName_IsRedacted_WhenItIsLongAndNonAscii()
    {
        var key = "k" + new string('\u00e9', 200) + new string('k', 300);
        var body = SubmitBodies.WithMpt(Guid.CreateVersion7(), key, JsonValue.Create(1));

        var result = SubmitValidator.Validate(Encoding.UTF8.GetBytes(body));

        result.IsError.ShouldBeTrue();
        var single = result.Errors.ShouldHaveSingleItem();
        single.Description.ShouldBe("mpt.<redacted>");
        single.Description.ShouldNotContain('\u00e9'.ToString());
    }

    [Fact]
    public void EchoableBoundaryKeys_AreEchoed()
    {
        // The boundary of ^[a-z_]{1,64}$: a 64-character lowercase key is echoed verbatim, a 65th pushes it redacted.
        var exact = new string('q', 64);
        var over = exact + "q";

        var exactResult = SubmitValidator.Validate(
            Encoding.UTF8.GetBytes(SubmitBodies.WithMpt(Guid.CreateVersion7(), exact, JsonValue.Create(1))));
        var overResult = SubmitValidator.Validate(
            Encoding.UTF8.GetBytes(SubmitBodies.WithMpt(Guid.CreateVersion7(), over, JsonValue.Create(1))));

        exactResult.Errors.ShouldHaveSingleItem().Description.ShouldBe($"mpt.{exact}");
        overResult.Errors.ShouldHaveSingleItem().Description.ShouldBe("mpt.<redacted>");
    }

    [Fact]
    public void Tolerant_AcceptsDuplicateProperty_LastWins()
    {
        // Tolerant is the Worker's poll reader (05 CR-3): a duplicate member must never break a poll — last wins.
        var status = FixtureFiles.Read("render-status.v1.queued.json")
            .Replace(
                "\"percent\": 0",
                "\"percent\": 40, \"percent\": 0",
                StringComparison.Ordinal);

        var parsed = JsonSerializer.Deserialize<RenderStatusV1>(status, RenderNodeJson.Tolerant);

        parsed.ShouldNotBeNull();
        parsed.Percent.ShouldBe(0, "the last duplicate wins in Tolerant mode");
        Should.Throw<JsonException>(
            () => JsonSerializer.Deserialize<RenderStatusV1>(status, RenderNodeJson.Strict));
    }

}
