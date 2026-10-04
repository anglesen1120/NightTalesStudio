using System.Net;
using System.Text;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.Options;
using NightTales.Fetcher.InternalApi;
using Shouldly;
using Xunit;

namespace NightTales.Fetcher.UnitTests;

/// <summary>02.5.2-T3 step 3: the typed errors, the retry rule and the token hygiene of the internal API client.</summary>
public sealed class FetcherApiClientTests : IDisposable
{
    private const string Token = "unit-test-secret-token-0123456789abcdef";

    private readonly FakeTime _time = new() { FireImmediately = static _ => true };
    private readonly CapturingLogger<FetcherApiClient> _log = new();
    private readonly FakeHandler _handler = new();
    private readonly Guid _taskId = Guid.NewGuid();

    public void Dispose() => _handler.Dispose();

    private FetcherApiClient Client() => new(
        new HttpClient(_handler) { BaseAddress = new Uri("http://127.0.0.1:8081") },
        Options.Create(new FetcherApiOptions { BaseUrl = "http://127.0.0.1:8081", Token = Token }),
        _time,
        new FetcherMetrics(),
        _log);

    private static FetchResultBody Done() => new("unit-worker", "done", 200, "https://news.example.com/a", "Tiêu đề", "text/html", "Nội dung", new string('a', 64));

    [Fact]
    public async Task Lease_MapsUnauthorizedToFatal()
    {
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var leased = await Client().LeaseAsync("unit-worker", 4, TestContext.Current.CancellationToken);

        leased.IsError.ShouldBeTrue();
        leased.FirstError.Code.ShouldBe(FetcherApiErrors.UnauthorizedCode);
        leased.FirstError.Type.ShouldBe(ErrorType.Unauthorized);
        FetcherApiErrors.IsRetryable(leased.FirstError).ShouldBeFalse();
        _handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Lease_ParsesTheTaskList()
    {
        var id = Guid.NewGuid();
        _handler.Respond = _ => Json(HttpStatusCode.OK, $$"""[{"taskId":"{{id}}","kind":"fetch_source","url":"https://news.example.com/a","attempt":2,"leaseUntil":"2026-10-05T10:00:00+00:00","userAgent":"ua"}]""");

        var leased = await Client().LeaseAsync("unit-worker", 4, TestContext.Current.CancellationToken);

        leased.IsError.ShouldBeFalse();
        var task = leased.Value.Single();
        task.TaskId.ShouldBe(id);
        task.Kind.ShouldBe("fetch_source");
        task.Attempt.ShouldBe(2);
        _handler.Requests.Single().Path.ShouldBe("/internal/fetcher/v1/tasks/lease");
        _handler.Requests.Single().Body.ShouldBe("""{"workerId":"unit-worker","max":4}""");
    }

    [Fact]
    public async Task Heartbeat_409_ReturnsLeaseLost()
    {
        _handler.Respond = _ => Json(HttpStatusCode.Conflict, """{"code":"fetch_lease_lost"}""");

        var beat = await Client().HeartbeatAsync(_taskId, "unit-worker", TestContext.Current.CancellationToken);

        beat.IsError.ShouldBeTrue();
        beat.FirstError.Code.ShouldBe(FetcherApiErrors.LeaseLostCode);
        _handler.Requests.Single().Path.ShouldBe($"/internal/fetcher/v1/tasks/{_taskId:D}/heartbeat");
    }

    [Fact]
    public async Task Heartbeat_404_ReturnsTaskNotFound()
    {
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        var beat = await Client().HeartbeatAsync(_taskId, "unit-worker", TestContext.Current.CancellationToken);

        beat.FirstError.Code.ShouldBe(FetcherApiErrors.TaskNotFoundCode);
    }

    [Fact]
    public async Task Result_400_ReturnsValidationCode()
    {
        _handler.Respond = _ => Json(HttpStatusCode.BadRequest, """{"type":"about:blank","status":400,"code":"source_no_text"}""", "application/problem+json");

        var posted = await Client().PostResultAsync(_taskId, Done(), TestContext.Current.CancellationToken);

        posted.IsError.ShouldBeTrue();
        posted.FirstError.Type.ShouldBe(ErrorType.Validation);
        posted.FirstError.Code.ShouldBe("source_no_text");
        _handler.Requests.Count.ShouldBe(1, "a 400 is deterministic and never retried");
    }

    [Fact]
    public async Task Result_5xx_RetriesThreeTimesThenGivesUp()
    {
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        var posted = await Client().PostResultAsync(_taskId, Done(), TestContext.Current.CancellationToken);

        posted.IsError.ShouldBeTrue();
        posted.FirstError.Code.ShouldBe(FetcherApiErrors.UnavailableCode);
        FetcherApiErrors.IsRetryable(posted.FirstError).ShouldBeTrue();
        _handler.Requests.Count.ShouldBe(3, "three attempts in all");
        _time.Delays.Count.ShouldBe(2, "a back-off between attempts, none after the last");
        _time.Delays.ShouldAllBe(delay => delay >= TimeSpan.FromMilliseconds(250) && delay <= TimeSpan.FromMilliseconds(1_500));
    }

    [Fact]
    public async Task Result_NetworkFailureThenSuccess_Retries()
    {
        _handler.Respond = request => _handler.Requests.Count == 1
            ? throw new HttpRequestException("connection reset")
            : new HttpResponseMessage(HttpStatusCode.OK);

        var posted = await Client().PostResultAsync(_taskId, Done(), TestContext.Current.CancellationToken);

        posted.IsError.ShouldBeFalse();
        _handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Result_409_DoesNotRetry()
    {
        _handler.Respond = _ => Json(HttpStatusCode.Conflict, """{"code":"fetch_lease_lost"}""");

        var posted = await Client().PostResultAsync(_taskId, Done(), TestContext.Current.CancellationToken);

        posted.FirstError.Code.ShouldBe(FetcherApiErrors.LeaseLostCode);
        _handler.Requests.Count.ShouldBe(1);
        _time.Delays.ShouldBeEmpty();
    }

    [Fact]
    public async Task Result_Body_IsWebJsonWithRelaxedEscapingAndNoNulls()
    {
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK);

        await Client().PostResultAsync(_taskId, Done(), TestContext.Current.CancellationToken);

        var body = _handler.Requests.Single().Body;
        body.ShouldContain("Nội dung", Case.Sensitive, "non-ASCII text is not \\u-escaped (the 1 MiB cap counts bytes)");
        body.ShouldNotContain("\\u");
        using var document = JsonDocument.Parse(body);
        document.RootElement.TryGetProperty("errorCode", out _).ShouldBeFalse("null members are omitted");
        document.RootElement.GetProperty("textSha256").GetString().ShouldBe(new string('a', 64));
    }

    [Fact]
    public async Task Result_OverOneMiB_IsNeverSent()
    {
        var body = Done() with { Text = new string('x', FetcherApiClient.MaxBodyBytes) };

        var posted = await Client().PostResultAsync(_taskId, body, TestContext.Current.CancellationToken);

        posted.FirstError.Code.ShouldBe("source_too_large");
        _handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task AuthorizationHeader_NotInLoggedRequest()
    {
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var client = Client();

        await client.LeaseAsync("unit-worker", 1, TestContext.Current.CancellationToken);
        await client.PostResultAsync(_taskId, Done(), TestContext.Current.CancellationToken);

        _handler.Requests.ShouldAllBe(request => request.AuthorizationValues.Count == 1 && request.AuthorizationValues[0] == "Bearer " + Token,
            "exactly one Authorization: Bearer header per request");
        _log.Messages.ShouldNotBeEmpty();
        _log.Messages.ShouldAllBe(message => !message.Contains(Token, StringComparison.Ordinal) && !message.Contains("Bearer", StringComparison.OrdinalIgnoreCase));
        _handler.Requests.ShouldAllBe(request => !request.Path.Contains(Token, StringComparison.Ordinal) && !request.Body.Contains(Token, StringComparison.Ordinal),
            "the token is never in the URL or the body");
    }

    [Fact]
    public async Task TokenNeverInExceptionMessage()
    {
        // A transport exception whose own message carries the token and the URL (as a misbehaving handler could).
        _handler.Respond = _ => throw new HttpRequestException($"failed http://127.0.0.1:8081/x with Bearer {Token}");
        var client = Client();

        var lease = await client.LeaseAsync("unit-worker", 1, TestContext.Current.CancellationToken);
        var post = await client.PostResultAsync(_taskId, Done(), TestContext.Current.CancellationToken);
        _handler.Respond = _ => throw new TaskCanceledException($"timed out for {Token}");
        var timeout = await client.HeartbeatAsync(_taskId, "unit-worker", TestContext.Current.CancellationToken);

        foreach (var result in new[] { lease.Errors, post.Errors, timeout.Errors })
        {
            result.ShouldAllBe(error => error.Code == FetcherApiErrors.UnavailableCode
                && !error.Description.Contains(Token, StringComparison.Ordinal)
                && !error.Description.Contains("127.0.0.1", StringComparison.Ordinal));
        }

        _log.Messages.ShouldAllBe(message => !message.Contains(Token, StringComparison.Ordinal) && !message.Contains("127.0.0.1", StringComparison.Ordinal));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, mediaType) };

    private sealed record Recorded(string Path, string Body, IReadOnlyList<string> AuthorizationValues);

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly List<Recorded> _requests = [];

        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = static _ => new HttpResponseMessage(HttpStatusCode.OK);

        public IReadOnlyList<Recorded> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var authorization = request.Headers.TryGetValues("Authorization", out var values) ? values.ToList() : [];
            lock (_requests)
            {
                _requests.Add(new Recorded(request.RequestUri!.AbsolutePath, body, authorization));
            }

            return Respond(request);
        }
    }
}
