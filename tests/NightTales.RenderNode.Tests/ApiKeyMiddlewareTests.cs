using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using NightTales.RenderNode.Tests.Fakes;
using Shouldly;
using Xunit;

namespace NightTales.RenderNode.Tests;

/// <summary>
/// The first stage of every request (05.4.1-T2 §1, D15 S2/S3) driven directly on a <c>DefaultHttpContext</c>, so
/// the throttle's sliding window can be crossed with a test clock instead of a real minute. One missing, empty,
/// duplicated and wrong key, one NEXT key, one valid key from a throttled address and the eleventh failure are
/// the behaviours the contract fixes.
/// </summary>
public sealed class ApiKeyMiddlewareTests
{
    private const string ApiKey = "0123456789abcdef0123456789abcdef";
    private const string NextApiKey = "fedcba9876543210fedcba9876543210";

    [Fact]
    public async Task MissingKey_401()
    {
        var next = new CountingNext();
        var context = Context();
        await Create(next).InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        (await ReadErrorAsync(context)).Error.Code.ShouldBe(AdapterErrorCodes.Unauthorized);
        next.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task EmptyKeyValue_401()
    {
        var next = new CountingNext();
        var context = Context();
        context.Request.Headers[RenderNodeHeaders.ApiKey] = string.Empty;
        await Create(next).InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        next.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task WrongKey_401()
    {
        var next = new CountingNext();
        var context = Context();
        context.Request.Headers[RenderNodeHeaders.ApiKey] = new string('x', 32);
        await Create(next).InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        next.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task DuplicatedHeader_401()
    {
        var next = new CountingNext();
        var context = Context();
        context.Request.Headers[RenderNodeHeaders.ApiKey] = new StringValues(new[] { ApiKey, ApiKey });
        await Create(next).InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        next.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task ValidKey_IsPassedThrough()
    {
        var next = new CountingNext();
        var context = Context();
        context.Request.Headers[RenderNodeHeaders.ApiKey] = ApiKey;
        await Create(next).InvokeAsync(context);

        next.Calls.ShouldBe(1);
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task NextKey_Accepted()
    {
        var next = new CountingNext();
        var context = Context();
        context.Request.Headers[RenderNodeHeaders.ApiKey] = NextApiKey;
        await Create(next, apiKeyNext: NextApiKey).InvokeAsync(context);

        next.Calls.ShouldBe(1);
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task UnsetNextKey_IsNotAnEmptyStringMatch()
    {
        var next = new CountingNext();
        var context = Context();
        context.Request.Headers[RenderNodeHeaders.ApiKey] = string.Empty;
        await Create(next, apiKeyNext: null).InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        next.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task EleventhFailureInAMinute_429_RetryAfter60()
    {
        var clock = new TestTimeProvider();
        var throttle = new AuthFailureThrottle(10, clock);

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            var rejected = Context();
            await Create(new CountingNext(), throttle: throttle).InvokeAsync(rejected);
            rejected.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized, $"failure {attempt} is still a 401");
        }

        var eleventh = Context();
        await Create(new CountingNext(), throttle: throttle).InvokeAsync(eleventh);

        eleventh.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        eleventh.Response.Headers[RenderNodeHeaders.RetryAfter].ToString().ShouldBe("60");
        (await ReadErrorAsync(eleventh)).Error.Code.ShouldBe(AdapterErrorCodes.AuthThrottled);

        // A valid key from a throttled address is still served (D15 S2/§10 T68): only failures are rejected.
        var valid = new CountingNext();
        var validContext = Context();
        validContext.Request.Headers[RenderNodeHeaders.ApiKey] = ApiKey;
        await Create(valid, throttle: throttle).InvokeAsync(validContext);
        valid.Calls.ShouldBe(1);
        validContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);

        // The window slides: after a minute the address is no longer throttled.
        clock.Advance(TimeSpan.FromSeconds(61));
        var later = Context();
        await Create(new CountingNext(), throttle: throttle).InvokeAsync(later);
        later.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    private static ApiKeyMiddleware Create(CountingNext next, string? apiKeyNext = null, AuthFailureThrottle? throttle = null) =>
        new(
            _ =>
            {
                next.Calls++;
                return Task.CompletedTask;
            },
            Options.Create(new RenderNodeServerOptions { ApiKey = ApiKey, ApiKeyNext = apiKeyNext }),
            throttle ?? new AuthFailureThrottle(10, new TestTimeProvider()));

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<AdapterErrorV1> ReadErrorAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return JsonSerializer.Deserialize<AdapterErrorV1>(text, RenderNodeJson.Strict)
            ?? throw new InvalidOperationException($"Not an envelope: {text}");
    }

    private sealed class CountingNext
    {
        internal int Calls { get; set; }
    }
}
