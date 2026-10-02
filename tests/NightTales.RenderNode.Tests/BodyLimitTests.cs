using System.Net;
using Microsoft.AspNetCore.Http;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using NightTales.RenderNode.Tests.Fakes;
using NightTales.RenderNode.Tests.Fixtures;
using Shouldly;
using Xunit;

namespace NightTales.RenderNode.Tests;

/// <summary>
/// The 2 MiB body cap of 05.4.1-T2 §1 (05 D15 S2) and the capacity 429 of §4 (F07/C10), both over the wire.
/// </summary>
/// <remarks>
/// The frozen cap is 2,097,152 bytes and the acceptance rule is "one byte over the cap is a 413". Pushing
/// 2 MiB + 1 through a real socket makes Kestrel close the connection while the client is still writing, which
/// surfaces as a broken pipe instead of the response, so the boundary itself is asserted against the reader that
/// enforces it (<see cref="RequestBodyReaderCapTests"/>) and the wire test proves the envelope over a socket with a
/// cap small enough that the response is always delivered. Both use the same code path and the same limit.
/// </remarks>
public sealed class BodyLimitTests
{
    private const long WireCap = 65_536;

    [Fact]
    public async Task RequestBodyReader_RefusesOneByteOverTheFrozenCap()
    {
        RenderNodeServerOptions.DefaultMaxBodyBytes.ShouldBe(2_097_152);

        var atCap = await ReadAsync(RenderNodeServerOptions.DefaultMaxBodyBytes);
        var overCap = await ReadAsync(RenderNodeServerOptions.DefaultMaxBodyBytes + 1);

        atCap.ShouldNotBeNull();
        overCap.ShouldBeNull();
    }

    [Fact]
    public async Task OneByteOverTheCap_413()
    {
        await using var host = await RenderNodeTestHost.StartAsync(options => options.MaxBodyBytes = WireCap);
        var oversized = new string('x', (int)WireCap + 1);

        var response = await host.PostAsync(RenderNodeRoutes.Renders, oversized);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await RenderNodeTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.BodyTooLarge);
        host.Store.InsertCount.ShouldBe(0);
        host.Backend.SubmitCount.ShouldBe(0);
    }

    [Fact]
    public async Task OversizedBodyWithoutKey_401BeforeTheBodyIsRead()
    {
        // §1 request order: auth runs before the body cap, so an oversized body with no key is 401, not 413.
        await using var host = await RenderNodeTestHost.StartAsync(options => options.MaxBodyBytes = WireCap);
        var oversized = new string('x', (int)WireCap + 1);

        var response = await host.PostAsync(RenderNodeRoutes.Renders, oversized, apiKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RenderNodeTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task AtCapacity_429_RetryAfter30()
    {
        await using var host = await RenderNodeTestHost.StartAsync(options => options.MaxConcurrentTasks = 1);

        var accepted = await host.PostAsync(RenderNodeRoutes.Renders, SubmitBodies.From(Guid.CreateVersion7()));
        var refused = await host.PostAsync(RenderNodeRoutes.Renders, SubmitBodies.From(Guid.CreateVersion7()));

        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        refused.Headers.TryGetValues(RenderNodeHeaders.RetryAfter, out var retryAfter).ShouldBeTrue();
        retryAfter!.Single().ShouldBe("30");
        (await RenderNodeTestHost.ReadErrorAsync(refused)).Error.Code.ShouldBe(AdapterErrorCodes.CapacityFull);
        host.Store.InsertCount.ShouldBe(1);
        host.Backend.SubmitCount.ShouldBe(1);
    }

    [Fact]
    public async Task UndeclaredLength_StopsAtTheCap()
    {
        // A chunked body has no Content-Length to check first, so the reader must stop on the bytes it has read.
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(new byte[(int)WireCap + 1]);

        var body = await RequestBodyReader.ReadAsync(context.Request, WireCap, CancellationToken.None);

        body.ShouldBeNull();
    }

    [Fact]
    public void DeclaredBody_IsReadWithoutASecondCopy()
    {
        // PERF-03: a declared Content-Length sizes the read buffer, so the reader must not build the body
        // through a growing MemoryStream and then shrink it with ToArray. Two declared reads of the same size
        // must not allocate much more than the body itself (a warm pool makes the second one nearly free).
        const int size = 500_000;

        _ = MeasureDeclaredRead(size);
        MeasureDeclaredRead(size).ShouldBeLessThan(size + (size / 2));
    }

    /// <summary>
    /// Drives one declared read on a dedicated thread and returns how many bytes that thread allocated for it,
    /// so the measurement is not diluted by the test runner's own thread.
    /// </summary>
    private static long MeasureDeclaredRead(int size)
    {
        long allocated = 0;
        var length = -1;
        var thread = new Thread(() =>
        {
            var context = DeclaredContext(new byte[size]);
            var before = GC.GetAllocatedBytesForCurrentThread();
#pragma warning disable xUnit1031 // the read is driven on this thread so the allocation counter stays on it
            var body = RequestBodyReader
                .ReadAsync(context.Request, RenderNodeServerOptions.DefaultMaxBodyBytes, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
#pragma warning restore xUnit1031
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            length = body?.Memory.Length ?? -1;
        });
        thread.Start();
        thread.Join();

        length.ShouldBe(size);
        return allocated;
    }

    private static DefaultHttpContext DeclaredContext(byte[] payload)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentLength = payload.Length;
        context.Request.Body = new MemoryStream(payload);
        return context;
    }

    private static async Task<RequestBodyBytes?> ReadAsync(long declaredLength)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentLength = declaredLength;
        context.Request.Body = new MemoryStream();

        return await RequestBodyReader.ReadAsync(
            context.Request,
            RenderNodeServerOptions.DefaultMaxBodyBytes,
            CancellationToken.None);
    }
}
