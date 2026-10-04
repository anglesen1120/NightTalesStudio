using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using NightTales.Api.Endpoints.Internal;
using NightTales.Api.Internal.Fetcher;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Features.Research;

/// <summary>
/// Hardening of the internal fetcher listener (02.5.2-T2 fix pass): the body cap on real Kestrel (QA-1), strict JSON
/// numbers (QA-3) and the global limiter that sits in front of the per-worker budget (QA-4, SEC-2). The in-process test
/// host has no Kestrel limit, so the 413 mapping is exercised on the endpoint's body reader directly.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FetchInternalHardeningTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContextToken.Value;

    private ApiFactory NewFactory()
    {
        new FetchTaskSeed(Database).ResetTasks();

        return new ApiFactory(Database, new FakeClock(FetchTaskSeed.Start)).WithFetcherInternalApi();
    }

    private static async Task<ReadOnlyMemory<byte>?> ReadBodyAsync(HttpContext http, int limit)
    {
        var method = typeof(FetcherEndpoints).GetMethod("ReadBodyAsync", BindingFlags.NonPublic | BindingFlags.Static);
        method.ShouldNotBeNull();
        var task = (Task)method.Invoke(null, [http, limit])!;
        await task;

        return (ReadOnlyMemory<byte>?)task.GetType().GetProperty("Result")!.GetValue(task);
    }

    [Fact]
    public async Task ReadBody_KestrelThrows413_WhileStreaming_IsAnsweredAsOverTheCap_NotAnException()
    {
        var feature = new BodySizeFeature();
        var http = new DefaultHttpContext();
        http.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
        http.Request.Body = new ThrowingStream(new BadHttpRequestException("too large", StatusCodes.Status413PayloadTooLarge));

        var result = await ReadBodyAsync(http, 4_096);

        result.ShouldBeNull();
        feature.MaxRequestBodySize.ShouldBe(4_096, "Kestrel's own cap for the request is the configured MaxBodyBytes, not its 1 MiB default");
    }

    [Fact]
    public async Task ReadBody_OtherBadRequests_AreNotSwallowedAs413()
    {
        var http = new DefaultHttpContext();
        http.Request.Body = new ThrowingStream(new BadHttpRequestException("bad chunk", StatusCodes.Status400BadRequest));

        await Should.ThrowAsync<BadHttpRequestException>(() => ReadBodyAsync(http, 4_096));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1_024)]
    [InlineData(4_096)]
    public async Task ReadBody_AtTheCap_IsServed_AndOneByteOverIsNot(int length)
    {
        var limit = Math.Max(length, 1_024);
        var exact = new byte[limit];
        var over = new byte[limit + 1];
        foreach (var (bytes, declare) in new[] { (exact, true), (exact, false), (over, true), (over, false) })
        {
            var http = new DefaultHttpContext();
            http.Request.Body = new MemoryStream(bytes);
            if (declare)
            {
                http.Request.ContentLength = bytes.Length;
            }

            var result = await ReadBodyAsync(http, limit);

            if (bytes.Length > limit)
            {
                result.ShouldBeNull($"declared={declare}");
            }
            else
            {
                result.ShouldNotBeNull($"declared={declare}");
                result.Value.Length.ShouldBe(limit);
            }
        }
    }

    [Fact]
    public async Task Lease_QuotedNumbers_AreRejected_OnEveryRoute()
    {
        using var factory = NewFactory();
        using var client = factory.InternalClient();
        var task = Guid.NewGuid();

        using var lease = await FetchTaskSeed.PostRawAsync(client, FetchTaskSeed.Prefix + "/lease", "{\"workerId\":\"fetcher-1\",\"max\":\"1\"}");
        using var result = await FetchTaskSeed.PostRawAsync(
            client,
            FetchTaskSeed.Route(task, "result"),
            "{\"workerId\":\"fetcher-1\",\"status\":\"failed\",\"errorCode\":\"source_unreachable\",\"httpStatus\":\"200\"}");

        lease.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        result.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GlobalConcurrency_NinthInFlightRequest_Is429_AndTheSlotsAreReleased()
    {
        using var factory = NewFactory();
        using var client = factory.InternalClient();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = Enumerable.Range(0, FetcherRateLimiter.MaxConcurrentRequests)
            .Select(_ => HoldAsync())
            .ToArray();

        // The held requests enter the pipeline asynchronously; probe until the ceiling is seen.
        HttpResponseMessage? refused = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (refused is null && DateTime.UtcNow < deadline)
        {
            var probe = await FetchTaskSeed.LeaseAsync(client, $"probe-{Guid.NewGuid():N}"[..20]);
            if (probe.StatusCode == HttpStatusCode.TooManyRequests)
            {
                refused = probe;
            }
            else
            {
                probe.Dispose();
                await Task.Delay(25, Ct);
            }
        }

        refused.ShouldNotBeNull("the global in-flight ceiling must refuse the request over it");
        refused.Dispose();

        gate.SetResult();
        foreach (var response in await Task.WhenAll(held))
        {
            using (response)
            {
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
            }
        }

        using var after = await FetchTaskSeed.LeaseAsync(client);
        after.StatusCode.ShouldBe(HttpStatusCode.OK, "every slot is released when the request ends");

        // A probe of this test can hold a slot for an instant while a held request arrives; that held request is
        // refused (correctly) and sent again until it is admitted and gated.
        async Task<HttpResponseMessage> HoldAsync()
        {
            while (true)
            {
                var response = await client.PostAsync(FetchTaskSeed.Prefix + "/lease", new GatedContent("{\"workerId\":\"fetcher-1\",\"max\":1}"u8.ToArray(), gate.Task), Ct);
                if (response.StatusCode != HttpStatusCode.TooManyRequests || gate.Task.IsCompleted)
                {
                    return response;
                }

                response.Dispose();
                await Task.Delay(10, Ct);
            }
        }
    }

    [Fact]
    public void GlobalConcurrency_TheNinthSlot_IsRefusedWithoutQueueing_AndFreedSlotsAreReusable()
    {
        var limiter = new FetcherRateLimiter(new FetcherInternalOptions());
        var slots = new List<IDisposable>();
        for (var i = 0; i < FetcherRateLimiter.MaxConcurrentRequests; i++)
        {
            limiter.TryEnter(out var slot, out _).ShouldBeTrue();
            slots.Add(slot!);
        }

        limiter.TryEnter(out var refused, out var retryAfter).ShouldBeFalse();
        refused.ShouldBeNull();
        retryAfter.ShouldBeGreaterThanOrEqualTo(1);

        slots[0].Dispose();
        limiter.TryEnter(out var again, out _).ShouldBeTrue();
        again!.Dispose();
        slots.Skip(1).ToList().ForEach(slot => slot.Dispose());
        limiter.Dispose();
    }

    [Fact]
    public void GlobalWindow_RefusesAfterThePerMinuteBudget_AndReleasesTheInFlightSlot()
    {
        using var limiter = new FetcherRateLimiter(new FetcherInternalOptions());

        for (var i = 0; i < FetcherRateLimiter.MaxRequestsPerMinuteGlobal; i++)
        {
            limiter.TryEnter(out var slot, out _).ShouldBeTrue($"request {i}");
            slot!.Dispose();
        }

        limiter.TryEnter(out var refused, out var retryAfter).ShouldBeFalse();
        refused.ShouldBeNull();
        retryAfter.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void WorkerPartitions_InvalidIds_ShareOneBudget()
    {
        using var limiter = new FetcherRateLimiter(new FetcherInternalOptions { MaxRequestsPerMinutePerWorker = 3 });
        var refused = 0;

        for (var i = 0; i < 12; i++)
        {
            if (!limiter.TryAcquire($"bad id {i}", out _))
            {
                refused++;
            }
        }

        refused.ShouldBe(9);
        limiter.TryAcquire("fetcher-1", out _).ShouldBeTrue("a valid id keeps its own budget");
        limiter.TryAcquire("worker\n", out _).ShouldBeFalse("a trailing newline is not a valid id and joins the shared partition");
    }

    private sealed class BodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;

        public long? MaxRequestBodySize { get; set; } = 1_048_576;
    }

    private sealed class ThrowingStream(Exception exception) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw exception;

        public override int Read(byte[] buffer, int offset, int count) => throw exception;

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A body of unknown length that sends its first byte, then waits for the gate before the rest.</summary>
    private sealed class GatedContent : HttpContent
    {
        private readonly byte[] _bytes;
        private readonly Task _gate;

        public GatedContent(byte[] bytes, Task gate)
        {
            _bytes = bytes;
            _gate = gate;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(_bytes.AsMemory(0, 1), Ct);
            await stream.FlushAsync(Ct);
            await _gate.WaitAsync(Ct);
            await stream.WriteAsync(_bytes.AsMemory(1), Ct);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }
    }
}
