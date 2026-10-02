using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NightTales.Api.Http;
using NightTales.Api.IntegrationTests.Auth;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.RateLimiting;
using NightTales.Application.Common.Errors;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;
using ProblemResponse = NightTales.Api.IntegrationTests.Http.ProblemResponse;

namespace NightTales.Api.IntegrationTests.RateLimiting;

/// <summary>
/// The four rate-limit policies of 09.3.5-T1 (09 README BD14, 09 W3 CR-7) against the real host: the
/// <c>auth</c> wall on the login route and its 429 body, the per-user partition of <c>paid</c> and
/// <c>hub</c>, the address fallback of a user-keyed policy whose caller carries no <c>sub</c>, the
/// incident switch, the redaction of the rejection log, and the framework's own reclamation of idle
/// partitions.
/// </summary>
/// <remarks>
/// <para>
/// Every test builds its own <see cref="ApiFactory"/>, so every test gets its own limiter: the
/// partition store lives in the host, and two tests cannot exhaust each other's window.
/// </para>
/// <para>
/// The requests come from <see cref="ApiFactory.PeerClient"/> wherever the partition depends on the
/// caller's address: the in-process <c>TestServer</c> has no connection feature, so without a pinned
/// peer every request would share the resolver's <c>UnknownPeer</c> partition.
/// </para>
/// </remarks>
public sealed class RateLimitTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The peer of the address-keyed tests: outside every configured network, so the forwarded-headers middleware ignores the headers.</summary>
    private const string PeerA = "203.0.113.10";

    /// <summary>A second peer, for the assertions that prove two addresses do not share a bucket.</summary>
    private const string PeerB = "203.0.113.11";

    /// <summary>The address of the raw-value scan of the rejection log.</summary>
    private const string LoggedPeer = "203.0.113.12";

    /// <summary>The `auth` policy's shipped limit: ten requests per minute per resolved address (BD14).</summary>
    private const int AuthPermits = 10;

    /// <summary>The `hub` policy's shipped limit: ten tickets per minute per user (BD14).</summary>
    private const int HubPermits = 10;

    /// <summary>The `paid` policy's shipped limit: thirty requests per minute per user (BD14).</summary>
    private const int PaidPermits = 30;

    /// <summary>The `Retry-After` the shipped configuration advertises (BD14, <c>RateLimiting:RetryAfterSeconds</c>).</summary>
    private static readonly TimeSpan ShippedRetryAfter =
        TimeSpan.FromSeconds(RateLimitingOptions.DefaultRetryAfterSeconds);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AuthPolicy_EleventhRequestInMinute_Is429()
    {
        using var factory = new ApiFactory(Database, new FakeClock());
        using var client = factory.PeerClient(PeerA);

        using var rejected = await ExhaustAuthAsync(client, UnknownEmail());

        rejected.StatusCode.ShouldBe(
            HttpStatusCode.TooManyRequests,
            "the first ten attempts are answered by the login handler, the eleventh by the limiter");
    }

    [Fact]
    public async Task RejectedResponse_IsProblemDetailsWithCodeAndRetryAfter()
    {
        using var factory = new ApiFactory(Database, new FakeClock());
        using var client = factory.PeerClient(PeerA);

        using var rejected = await ExhaustAuthAsync(client, UnknownEmail());

        rejected.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        rejected.Headers.RetryAfter.ShouldNotBeNull("09 README D7: a 429 advertises when to come back");
        rejected.Headers.RetryAfter!.Delta.ShouldBe(ShippedRetryAfter);

        var body = JsonSerializer.Deserialize<ProblemResponse>(
            await rejected.Content.ReadAsStringAsync(Ct),
            ProblemResponse.Options);

        body.ShouldNotBeNull();
        body.Code.ShouldBe(ErrorCodes.RateLimited);
        body.Type.ShouldBe(ErrorCatalogue.ProblemType(ErrorCodes.RateLimited));
        body.Status.ShouldBe(StatusCodes.Status429TooManyRequests);
        body.Title.ShouldBe(RateLimitRejection.TooManyRequestsTitle);
        body.TraceId.ShouldNotBeNullOrWhiteSpace();
        body.Metadata.ShouldNotBeNull();
        body.Metadata!["retryAfterSeconds"].GetInt32().ShouldBe(RateLimitingOptions.DefaultRetryAfterSeconds);
    }

    [Fact]
    public async Task Rejection_DoesNotReachTheHandler()
    {
        var sink = new CapturingLoggerProvider();
        using var factory = new ApiFactory(Database, new FakeClock()).WithLoggerProvider(sink);
        using var client = factory.PeerClient(PeerA);

        using var rejected = await ExhaustAuthAsync(client, UnknownEmail());

        rejected.StatusCode.ShouldBe(
            HttpStatusCode.TooManyRequests,
            "the eleven attempts exhaust the window and the last one is refused");

        // The counter is the D4 pipeline's own line, one per message the pipeline ran: the host only
        // renders it through this test's sink, so a sibling test class cannot move it.
        sink.Lines.Count(line => line.Contains(LoginRequestName, StringComparison.Ordinal))
            .ShouldBe(AuthPermits, "the rejected request runs no handler, so the pipeline stops at ten");
    }

    [Fact]
    public async Task PaidPolicy_IsPerUser_NotPerIp()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        var userA = Guid.CreateVersion7();
        var userB = Guid.CreateVersion7();

        // One address, two users: a partition on the address would refuse the second one as well.
        using var clientA = factory.PeerClient(PeerA, userA);
        using var clientB = factory.PeerClient(PeerA, userB);

        for (var attempt = 1; attempt <= PaidPermits; attempt++)
        {
            using var accepted = await clientA.PostAsync(ProbeEndpoints.PaidRateLimitPath, null, Ct);
            accepted.StatusCode.ShouldBe(HttpStatusCode.OK, $"request {attempt} is inside the thirty-per-minute window");
        }

        using var over = await clientA.PostAsync(ProbeEndpoints.PaidRateLimitPath, null, Ct);
        over.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, "the thirty-first request of one user is refused");

        using var other = await clientB.PostAsync(ProbeEndpoints.PaidRateLimitPath, null, Ct);
        other.StatusCode.ShouldBe(HttpStatusCode.OK, "another user behind the same address has a bucket of their own");
    }

    [Fact]
    public async Task PaidPolicy_SlidingWindow_SmoothsTheBoundary()
    {
        // The in-box limiters expose no TimeProvider (verified against the .NET 10 assembly), so no clock
        // can advance a segment: the boundary is driven by TryReplenish() on a non-auto-replenishing
        // instance built from this policy's own options. One segment of four is 250 ms.
        var policy = new RateLimitPolicyOptions { PermitLimit = 4, WindowSeconds = 1, SegmentsPerWindow = 4 };
        var options = policy.ToSlidingWindowOptions();
        options.AutoReplenishment = false;

        using var limiter = new SlidingWindowRateLimiter(options);

        limiter.AttemptAcquire(4).IsAcquired.ShouldBeTrue("the window admits its permit limit");
        limiter.AttemptAcquire(1).IsAcquired.ShouldBeFalse("and not one more");

        // Every segment but the one the permits were taken in is empty, so the boundary returns nothing:
        // a client that burst at the end of a segment cannot burst again the moment the next one starts.
        for (var segment = 1; segment < policy.SegmentsPerWindow; segment++)
        {
            await Task.Delay(SegmentDelay, Ct);

            limiter.TryReplenish().ShouldBeTrue();
            limiter.AttemptAcquire(1).IsAcquired.ShouldBeFalse(
                $"segment {segment} of the window carried no permit to give back");
        }

        await Task.Delay(SegmentDelay, Ct);
        limiter.TryReplenish().ShouldBeTrue();

        limiter.AttemptAcquire(4).IsAcquired.ShouldBeTrue(
            "a full window later the permits the burst consumed are back, and never more than the limit");
    }

    [Fact]
    public async Task HubPolicy_TicketCap_AndAnonymousFallback()
    {
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var ticketClient = factory.PeerClient(PeerA, Guid.CreateVersion7());

        for (var ticket = 1; ticket <= HubPermits; ticket++)
        {
            using var accepted = await ticketClient.PostAsync(ProbeEndpoints.HubRateLimitPath, null, Ct);
            accepted.StatusCode.ShouldBe(HttpStatusCode.OK, $"ticket {ticket} is inside the ten-per-minute window");
        }

        using var over = await ticketClient.PostAsync(ProbeEndpoints.HubRateLimitPath, null, Ct);
        over.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, "the eleventh ticket of one user is refused");

        // A caller without a `sub` cannot fall back to one shared bucket — every anonymous caller would
        // exhaust every other one. The fallback partition is the resolved address, exactly like `auth`.
        using var anonymousA = factory.PeerClient(PeerA);
        using var anonymousB = factory.PeerClient(PeerB);

        for (var ticket = 1; ticket <= HubPermits; ticket++)
        {
            using var accepted = await anonymousA.PostAsync(ProbeEndpoints.HubRateLimitPath, null, Ct);
            accepted.StatusCode.ShouldBe(HttpStatusCode.OK, $"anonymous ticket {ticket} is inside the window");
        }

        using var anonymousOver = await anonymousA.PostAsync(ProbeEndpoints.HubRateLimitPath, null, Ct);
        anonymousOver.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        using var stranger = await anonymousB.PostAsync(ProbeEndpoints.HubRateLimitPath, null, Ct);
        stranger.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            "a second address-less caller has a bucket of their own, not the one everyone shares");
    }

    [Fact]
    public async Task AnonPolicy_IsPerResolvedIp()
    {
        using var factory = new ApiFactory(Database, new FakeClock())
            .WithSetting($"{RateLimitingOptions.SectionName}:{nameof(RateLimitingOptions.Anon)}:PermitLimit", "1")
            .MapEndpoints(ProbeEndpoints.Map);

        using var first = factory.PeerClient(PeerA);
        using var second = factory.PeerClient(PeerB);

        using var accepted = await first.PostAsync(ProbeEndpoints.AnonRateLimitPath, null, Ct);
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var over = await first.PostAsync(ProbeEndpoints.AnonRateLimitPath, null, Ct);
        over.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, "the anonymous policy counts the resolved address");

        using var stranger = await second.PostAsync(ProbeEndpoints.AnonRateLimitPath, null, Ct);
        stranger.StatusCode.ShouldBe(HttpStatusCode.OK, "another address is inside its own window");
    }

    [Fact]
    public async Task Disabled_IsPassThrough()
    {
        using var factory = new ApiFactory(Database, new FakeClock())
            .WithSetting($"{RateLimitingOptions.SectionName}:{nameof(RateLimitingOptions.Enabled)}", "false");
        using var client = factory.PeerClient(PeerA);
        var email = UnknownEmail();

        // Twice the `auth` window: the incident switch must never answer 429, and must never answer 500
        // for a policy the endpoints still name (which is why the policies stay registered).
        for (var attempt = 1; attempt <= (AuthPermits * 2) + 1; attempt++)
        {
            var (response, _) = await AuthTestFlow.LoginAsync(client, email, AuthTestFlow.Password, Ct);
            using (response)
            {
                response.StatusCode.ShouldBe(
                    HttpStatusCode.Unauthorized,
                    $"attempt {attempt}: with RateLimiting:Enabled=false the limiter is a pass-through");
            }
        }
    }

    [Fact]
    public async Task RejectedRequest_LogsThePolicyAndNoRawAddressOrUser()
    {
        var sink = new CapturingLoggerProvider();
        var user = Guid.CreateVersion7();

        using var factory = new ApiFactory(Database, new FakeClock())
            .WithLoggerProvider(sink)
            .WithSetting($"{RateLimitingOptions.SectionName}:{nameof(RateLimitingOptions.Hub)}:PermitLimit", "1")
            .MapEndpoints(ProbeEndpoints.Map);

        using var client = factory.PeerClient(LoggedPeer, user);

        using var accepted = await client.PostAsync(ProbeEndpoints.HubRateLimitPath, null, Ct);
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var rejected = await client.PostAsync(ProbeEndpoints.HubRateLimitPath, null, Ct);
        rejected.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        var limiterLines = sink.Lines
            .Where(line => line.Contains(RejectionLogMarker, StringComparison.Ordinal))
            .ToList();

        limiterLines.ShouldHaveSingleItem("one rejection is one line (BD16)");
        var line = limiterLines[0];
        line.ShouldContain($"policy={RateLimitPolicies.Hub}");
        line.ShouldNotContain(LoggedPeer, Case.Sensitive, "BD16: the limiter never logs a raw address");
        line.ShouldNotContain(user.ToString(), Case.Sensitive, "BD16: the limiter never logs a raw user id");
        line.ShouldContain(
            RateLimitPartitionKeys.UserPrefix,
            Case.Sensitive,
            "the logged key is the partition key the limiter used, in its hashed form");
    }

    [Fact]
    public async Task IdlePartitions_AreReclaimed_OrStoreIsSizeLimited()
    {
        // [RULED — README §B10 WT48]: no MaxPartitions option exists; the framework's own store reclaims an
        // idle partition. The proof is the partition factory running a second time for the same key: a
        // partition that were merely replenished windows would never be rebuilt, and one that were never
        // evicted would leak a limiter per address.
        var policy = new RateLimitPolicyOptions { PermitLimit = 1, WindowSeconds = 1 };
        var created = new List<string>();

        using var store = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetFixedWindowLimiter(key, _ =>
            {
                created.Add(key);
                return policy.ToFixedWindowOptions();
            }));

        store.AttemptAcquire(PeerA, 1).IsAcquired.ShouldBeTrue();
        store.AttemptAcquire(PeerA, 1).IsAcquired.ShouldBeFalse("the one-permit window of this partition is spent");
        created.Count.ShouldBe(1);

        // The framework's heartbeat runs every 100 ms and evicts a partition whose limiter has been idle
        // for more than ten seconds, so the wait is the window plus that idle limit plus a margin.
        await Task.Delay(TimeSpan.FromSeconds(13), Ct);

        store.AttemptAcquire(PeerA, 1).IsAcquired.ShouldBeTrue(
            "the partition was reclaimed, so this request meets a fresh window");
        created.Count.ShouldBe(2, "the factory ran again, which is what proves the eviction");
    }

    /// <summary>The request-type name the D4 pipeline logs for a login, one line per handled message.</summary>
    private const string LoginRequestName = "LoginCommand";

    /// <summary>How the one line of a rejected request starts (09.3.5-T1 Design, BD16).</summary>
    private const string RejectionLogMarker = "rate.limit.rejected";

    /// <summary>One segment of the sliding test's window: 1 s cut into four.</summary>
    private static readonly TimeSpan SegmentDelay = TimeSpan.FromMilliseconds(400);

    private static string UnknownEmail() => $"nobody-{Guid.CreateVersion7():N}@nighttales.test";

    /// <summary>
    /// Sends the ten login attempts the `auth` policy admits and returns the answer of the eleventh,
    /// asserting that every admitted one really was answered by the login handler.
    /// </summary>
    private static async Task<HttpResponseMessage> ExhaustAuthAsync(HttpClient client, string email)
    {
        for (var attempt = 1; attempt <= AuthPermits; attempt++)
        {
            var (accepted, cookie) = await AuthTestFlow.LoginAsync(client, email, AuthTestFlow.Password, Ct);
            using (accepted)
            {
                accepted.StatusCode.ShouldBe(
                    HttpStatusCode.Unauthorized,
                    $"attempt {attempt} is inside the ten-per-minute window (09 README BD14)");
                cookie.ShouldBeNull("a refused login sets no refresh cookie");
            }
        }

        var (rejected, _) = await AuthTestFlow.LoginAsync(client, email, AuthTestFlow.Password, Ct);

        return rejected;
    }

    /// <summary>Keeps every rendered line, so a test can scan what the host logged (no mocking library).</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];
        private readonly Lock _gate = new();

        internal IReadOnlyList<string> Lines
        {
            get
            {
                lock (_gate)
                {
                    return [.. _messages];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private void Capture(string rendered)
        {
            lock (_gate)
            {
                _messages.Add(rendered);
            }
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                owner.Capture(formatter(state, exception));
            }
        }
    }
}
