using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Api.Http;
using NightTales.Api.RateLimiting;
using NightTales.Application.Common.Errors;
using Shouldly;
using Xunit;
using ProblemResponse = NightTales.Api.IntegrationTests.Http.ProblemResponse;

namespace NightTales.Api.IntegrationTests.RateLimiting;

/// <summary>
/// The one rejection the limiter writes (09.3.5-T1 design, 09 README W8/D7/BD16): the wait it advertises
/// is the configured window — never a computed, possibly negative, remainder — the body is the Api's
/// problem shape, and the counter carries the closed <c>policy</c> tag.
/// </summary>
/// <remarks>
/// No host is needed: the delegate reads its inputs from the request it is handed — the endpoint's
/// policy metadata, the configured limits and the lease the limiter already refused — so a real
/// <see cref="FixedWindowRateLimiter"/> supplies the lease and a bare <see cref="DefaultHttpContext"/> the
/// request. The ASP.NET Core callback exposes no reset instant (W8), which is the reason the value is a
/// configuration key at all.
/// </remarks>
public sealed class RateLimitRejectionTests
{
    /// <summary>A <c>Retry-After</c> above every shipped window, so the header cannot pass by coincidence.</summary>
    private const int RetryAfterSeconds = 90;

    private const int WindowSeconds = 60;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RetryAfter_IsTheConfiguredWindow_AndNeverNegative()
    {
        var options = new RateLimitingOptions { RetryAfterSeconds = RetryAfterSeconds };
        using var metrics = new RateLimitMetrics();
        var rejections = new List<string>();
        using var listener = ListenForRejections(metrics, rejections);
        var rejection = new RateLimitRejection(
            options,
            metrics,
            new ClientIpResolver(),
            NullLogger<RateLimitRejection>.Instance);
        using var services = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        var http = RejectedRequest(services);

        using var lease = RefusedLease(out var limiter);
        using (limiter)
        {
            await rejection.WriteAsync(new OnRejectedContext { HttpContext = http, Lease = lease }, Ct);
        }

        http.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        http.Response.Headers["Retry-After"].ToString().ShouldBe(
            RetryAfterSeconds.ToString(CultureInfo.InvariantCulture),
            "the header is the policy window (W8), never the remainder of the window that is still running");

        http.Response.Body.Position = 0;
        var body = JsonSerializer.Deserialize<ProblemResponse>(http.Response.Body, ProblemResponse.Options);

        body.ShouldNotBeNull();
        body.Code.ShouldBe(ErrorCodes.RateLimited);
        body.Type.ShouldBe(ErrorCatalogue.ProblemType(ErrorCodes.RateLimited));
        body.Status.ShouldBe(StatusCodes.Status429TooManyRequests);
        body.Title.ShouldBe(RateLimitRejection.TooManyRequestsTitle);
        body.TraceId.ShouldNotBeNullOrWhiteSpace();
        body.Metadata.ShouldNotBeNull();
        body.Metadata!["retryAfterSeconds"].GetInt32().ShouldBe(RetryAfterSeconds);

        rejections.ShouldBe(
            new[] { RateLimitPolicies.Auth },
            Case.Sensitive,
            "one rejection is counted once, against the policy the endpoint carries");
    }

    [Fact]
    public async Task Rejection_WithoutAnEndpointPolicy_IsCountedOnceButUnattributed()
    {
        var options = new RateLimitingOptions { RetryAfterSeconds = RetryAfterSeconds };
        using var metrics = new RateLimitMetrics();
        var rejections = new List<string>();
        using var listener = ListenForRejections(metrics, rejections);
        var rejection = new RateLimitRejection(
            options,
            metrics,
            new ClientIpResolver(),
            NullLogger<RateLimitRejection>.Instance);
        using var services = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        var http = new DefaultHttpContext
        {
            RequestServices = services,
            Response = { Body = new MemoryStream() },
        };

        using var lease = RefusedLease(out var limiter);
        using (limiter)
        {
            await rejection.WriteAsync(new OnRejectedContext { HttpContext = http, Lease = lease }, Ct);
        }

        rejections.ShouldBe(
            new[] { RateLimitMetrics.UnattributedPolicy },
            Case.Sensitive,
            "the tag vocabulary is closed (BD16): a rejection with no policy metadata is 'unattributed', never a key");
    }

    [Fact]
    public async Task Rejection_OfACancelledRequest_IsStillCounted()
    {
        var options = new RateLimitingOptions { RetryAfterSeconds = RetryAfterSeconds };
        using var metrics = new RateLimitMetrics();
        var rejections = new List<string>();
        using var listener = ListenForRejections(metrics, rejections);
        var rejection = new RateLimitRejection(
            options,
            metrics,
            new ClientIpResolver(),
            NullLogger<RateLimitRejection>.Instance);
        using var services = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        var http = RejectedRequest(services);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        using var lease = RefusedLease(out var limiter);
        using (limiter)
        {
            await rejection.WriteAsync(new OnRejectedContext { HttpContext = http, Lease = lease }, cancelled.Token);
        }

        http.Response.StatusCode.ShouldBe(
            StatusCodes.Status200OK,
            "no body is written into a request whose caller is already gone");
        rejections.ShouldBe(
            new[] { RateLimitPolicies.Auth },
            Case.Sensitive,
            "a rejection stays a rejection after the client disconnects: the counter is what an operator scrapes");
    }

    /// <summary>A 429 request whose endpoint carries <paramref name="policy"/>.</summary>
    private static DefaultHttpContext RejectedRequest(IServiceProvider services)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = services,
            Response = { Body = new MemoryStream() },
        };

        http.SetEndpoint(new Endpoint(
            requestDelegate: null,
            new EndpointMetadataCollection(new EnableRateLimitingAttribute(RateLimitPolicies.Auth)),
            displayName: "rate-limit probe"));

        return http;
    }

    /// <summary>A lease a one-permit window refused, and the limiter that refused it (disposed by the caller).</summary>
    private static RateLimitLease RefusedLease(out FixedWindowRateLimiter limiter)
    {
        limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 1,
            Window = TimeSpan.FromSeconds(WindowSeconds),
            QueueLimit = 0,
            AutoReplenishment = true,
        });

        using var admitted = limiter.AttemptAcquire(1);
        admitted.IsAcquired.ShouldBeTrue("the first request of the window is admitted, the second is the rejection");

        var refused = limiter.AttemptAcquire(1);
        refused.IsAcquired.ShouldBeFalse();

        return refused;
    }

    /// <summary>Listens on the host's own meter, so the tag the listener sees is what a scrape would see.</summary>
    private static MeterListener ListenForRejections(RateLimitMetrics metrics, List<string> rejections)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, owner) =>
            {
                if (ReferenceEquals(instrument.Meter, metrics.Meter)
                    && string.Equals(
                        instrument.Name,
                        RateLimitMetrics.RejectionsCounterName,
                        StringComparison.Ordinal))
                {
                    owner.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (string.Equals(tag.Key, RateLimitMetrics.PolicyTagName, StringComparison.Ordinal))
                {
                    rejections.Add(tag.Value as string ?? string.Empty);
                }
            }
        });

        listener.Start();

        return listener;
    }
}
