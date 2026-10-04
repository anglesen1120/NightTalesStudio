using System.Diagnostics.Metrics;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Fetcher.Core.Fetching;
using NightTales.Fetcher.InternalApi;
using Shouldly;
using Xunit;

namespace NightTales.Fetcher.UnitTests;

/// <summary>02.5.2-T3 Design §FetcherLoop: lease accounting, idle poll, exits, fault isolation, shutdown, T105.</summary>
public sealed class FetcherLoopTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(60);

    private readonly FakeApi _api = new();
    private readonly FakeExit _exit = new();
    private readonly FakeTime _time = new()
    {
        // The loop's poll and back-off delays run at once; the T105 bound and the heartbeat period wait for Advance.
        FireImmediately = delay => delay != Bound && delay != Heartbeat,
    };

    private readonly FetcherOptions _options = new() { MaxLeasesPlatformWide = 4 };

    private static LeasedTask Task_(int n, string kind = "fetch_source") =>
        new(new Guid(n, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]), kind, $"https://news.example.com/article-{n}", 1, DateTimeOffset.UnixEpoch, null, null);

    private static ErrorOr<IReadOnlyList<LeasedTask>> Lease(params LeasedTask[] tasks) => tasks;

    private FetcherLoop Loop(ISourceFetcher fetcher, ISourceFinderRunner? finder = null, ILogger<FetcherLoop>? logger = null) => new(
        _api,
        fetcher,
        new FakePublicSuffixList(),
        Options.Create(_options),
        Options.Create(new FetcherLoopOptions()),
        Options.Create(new FetcherApiOptions { BaseUrl = "http://127.0.0.1:8081", Token = "unit-test-token-0123456789abcdef0123", WorkerId = "unit-worker" }),
        _time,
        new FetcherMetrics(),
        _exit,
        logger ?? NullLogger<FetcherLoop>.Instance,
        finder);

    private static FakeSourceFetcher Blocking(TaskCompletionSource release, List<CancellationToken>? seen = null) => new(async (_, ct) =>
    {
        seen?.Add(ct);
        await release.Task.WaitAsync(ct);

        return FakeSourceFetcher.Page();
    });

    [Fact]
    public async Task Loop_LeasesOnlyUpToCapMinusInFlight()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetcher = Blocking(release);
        _api.OnLease = call => call == 1 ? Lease(Task_(1), Task_(2), Task_(3)) : Lease();
        var loop = Loop(fetcher);

        await loop.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Wait.UntilAsync(() => _api.LeaseMax.Count >= 3, "three lease calls");

            _api.LeaseMax[0].ShouldBe(4, "nothing in flight yet: the whole cap");
            _api.LeaseMax.Skip(1).ShouldAllBe(max => max == 1, "three tasks in flight leave one slot of the cap of four");
            await Wait.UntilAsync(() => fetcher.Calls == 3, "three fetches started");
        }
        finally
        {
            release.SetResult();
            await loop.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Loop_Idle_DoesNotSpin()
    {
        var loop = Loop(new FakeSourceFetcher((_, _) => ValueTask.FromResult<ErrorOr<FetchedSource>>(FakeSourceFetcher.Page())));

        await loop.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Wait.UntilAsync(() => _api.LeaseCalls >= 4, "four lease calls");
        }
        finally
        {
            await loop.StopAsync(TestContext.Current.CancellationToken);
        }

        // Every empty lease is followed by exactly the 2 s idle delay before the next call.
        var delays = _time.Delays.Where(delay => delay != Heartbeat && delay != Bound).ToList();
        delays.ShouldNotBeEmpty();
        delays.ShouldAllBe(delay => delay == TimeSpan.FromSeconds(2));
        delays.Count.ShouldBeGreaterThanOrEqualTo(_api.LeaseCalls - 1);
        _api.Posts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Loop_FiveConsecutiveApiFailures_Exits()
    {
        _api.OnLease = _ => FetcherApiErrors.Unavailable();
        var loop = Loop(new FakeSourceFetcher((_, _) => ValueTask.FromResult<ErrorOr<FetchedSource>>(FakeSourceFetcher.Page())));

        await loop.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Wait.UntilAsync(() => _exit.Calls.Count > 0, "an exit request");
            await Wait.SettleAsync();
        }
        finally
        {
            await loop.StopAsync(TestContext.Current.CancellationToken);
        }

        _exit.Calls.Count.ShouldBe(1);
        _exit.Calls[0].Code.ShouldBe(1);
        _api.LeaseCalls.ShouldBe(5, "the fifth consecutive failure ends the loop, no sixth call");
    }

    [Fact]
    public async Task Loop_401OnLease_ExitsImmediately()
    {
        _api.OnLease = _ => FetcherApiErrors.Unauthorized;
        var loop = Loop(new FakeSourceFetcher((_, _) => ValueTask.FromResult<ErrorOr<FetchedSource>>(FakeSourceFetcher.Page())));

        await loop.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Wait.UntilAsync(() => _exit.Calls.Count > 0, "an exit request");
            await Wait.SettleAsync();
        }
        finally
        {
            await loop.StopAsync(TestContext.Current.CancellationToken);
        }

        _exit.Calls.Count.ShouldBe(1);
        _exit.Calls[0].Code.ShouldBe(1);
        _api.LeaseCalls.ShouldBe(1, "a refused token is never retried");
        _time.Delays.ShouldBeEmpty("no back-off before the exit");
    }

    [Fact]
    public async Task Loop_FaultedTask_DoesNotKillLoop()
    {
        var first = Task_(1);
        var second = Task_(2);
        _api.OnLease = call => call switch
        {
            1 => Lease(first),
            2 => Lease(second),
            _ => Lease(),
        };

        // Task 1: the fetcher throws, and so does the result POST (a fault outside every inner catch). Task 2 is normal.
        _api.OnPost = (taskId, _) => taskId == first.TaskId ? throw new InvalidOperationException("post exploded") : Result.Success;
        var fetcher = new FakeSourceFetcher((request, _) => request.Url.AbsolutePath.EndsWith("-1", StringComparison.Ordinal)
            ? throw new InvalidOperationException("fetch exploded")
            : ValueTask.FromResult<ErrorOr<FetchedSource>>(FakeSourceFetcher.Page(request.Url.ToString())));
        var loop = Loop(fetcher);

        await loop.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Wait.UntilAsync(() => _api.Posts.Any(post => post.TaskId == second.TaskId), "the second task's result");

            var secondPost = _api.Posts.Single(post => post.TaskId == second.TaskId).Body;
            secondPost.Status.ShouldBe("done");
            var firstPost = _api.Posts.Single(post => post.TaskId == first.TaskId).Body;
            firstPost.Status.ShouldBe("failed");
            firstPost.ErrorCode.ShouldBe("source_unreachable");
            loop.ExecuteTask!.IsCompleted.ShouldBeFalse("the loop is still leasing");
            _exit.Calls.ShouldBeEmpty();
        }
        finally
        {
            await loop.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Loop_Shutdown_CancelsInFlightAndStopsLeasing()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<CancellationToken>();
        var fetcher = Blocking(release, seen);
        _api.OnLease = call => call == 1 ? Lease(Task_(1)) : Lease();
        var loop = Loop(fetcher);

        await loop.StartAsync(TestContext.Current.CancellationToken);
        await Wait.UntilAsync(() => fetcher.Calls == 1, "the fetch started");
        await loop.StopAsync(TestContext.Current.CancellationToken);

        seen.Single().IsCancellationRequested.ShouldBeTrue("the in-flight fetch is cancelled on shutdown");
        _api.Posts.ShouldBeEmpty("a shutdown never posts a result: the lease expires and the sweeper requeues");
        var leasesAtStop = _api.LeaseCalls;
        await Wait.SettleAsync();
        _api.LeaseCalls.ShouldBe(leasesAtStop, "leasing stopped");
        _exit.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Loop_FetchSource_PastT105Bound_PostsNothing()
    {
        var seen = new List<CancellationToken>();
        var fetcher = Blocking(new TaskCompletionSource(), seen);
        _api.OnLease = call => call == 1 ? Lease(Task_(1)) : Lease();
        var loop = Loop(fetcher);

        await loop.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Wait.UntilAsync(() => fetcher.Calls == 1, "the fetch started");
            await Wait.UntilAsync(() => _time.Delays.Contains(Bound), "the T105 bound armed (TotalTimeout 10 s + ExtractTimeout 5 s)");

            _time.Advance(Bound);

            await Wait.UntilAsync(() => seen.Single().IsCancellationRequested, "the fetch cancelled by the bound");
            await Wait.SettleAsync();
            _api.Posts.ShouldBeEmpty("an expired fetch_source posts no result");
            _exit.Calls.ShouldBeEmpty();
            loop.ExecuteTask!.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            await loop.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Loop_MalformedTaskUrl_PostsSourceUnreachable()
    {
        var bad = Task_(1) with { Url = "not a url" };
        _api.OnLease = call => call == 1 ? Lease(bad) : Lease();
        var loop = Loop(new FakeSourceFetcher((_, _) => ValueTask.FromResult<ErrorOr<FetchedSource>>(FakeSourceFetcher.Page())));

        await loop.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Wait.UntilAsync(() => _api.Posts.Count == 1, "a result");
            _api.Posts[0].Body.Status.ShouldBe("failed");
            _api.Posts[0].Body.ErrorCode.ShouldBe("source_unreachable");
            _exit.Calls.ShouldBeEmpty();
        }
        finally
        {
            await loop.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Loop_FetchThrowsOperationCanceledOfItsOwn_PostsFailedResult()
    {
        _api.OnLease = call => call == 1 ? Lease(Task_(1)) : Lease();
        var loop = Loop(new FakeSourceFetcher((_, _) => throw new OperationCanceledException("per-fetch timeout")));

        await loop.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Wait.UntilAsync(() => _api.Posts.Count == 1, "a failed result");
            _api.Posts[0].Body.Status.ShouldBe("failed");
            _api.Posts[0].Body.ErrorCode.ShouldBe("source_unreachable");
        }
        finally
        {
            await loop.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Loop_TextWithNul_PostsSourceNoTextWithoutText()
    {
        _api.OnLease = call => call == 1 ? Lease(Task_(1)) : Lease();
        var page = FakeSourceFetcher.Page() with { Text = "a\0b", TextChars = 3 };
        var loop = Loop(new FakeSourceFetcher((_, _) => ValueTask.FromResult<ErrorOr<FetchedSource>>(page)));

        await loop.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Wait.UntilAsync(() => _api.Posts.Count == 1, "a result");
            _api.Posts[0].Body.ErrorCode.ShouldBe("source_no_text");
            _api.Posts[0].Body.Text.ShouldBeNull();
        }
        finally
        {
            await loop.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Loop_DoneSubstitutedBySourceTooLarge_LogsAndCountsTheOutcomeActuallyPosted()
    {
        // The client replaces an over-cap done body by failed/source_too_large and reports it (D19): the loop must not log or
        // count the original done body.
        var counted = new List<(string Status, string ErrorCode)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == FetcherMetrics.MeterName && instrument.Name == "fetch_tasks_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string status = string.Empty, code = string.Empty;
            foreach (var tag in tags)
            {
                if (tag.Key == "status")
                {
                    status = (string)tag.Value!;
                }
                else if (tag.Key == "error_code")
                {
                    code = (string)tag.Value!;
                }
            }

            lock (counted)
            {
                counted.Add((status, code));
            }
        });
        listener.Start();
        var logger = new CapturingLogger<FetcherLoop>();
        _api.OnLease = call => call == 1 ? Lease(Task_(1)) : Lease();
        _api.PostedAs = sent => ResultBodies.Failed(sent.WorkerId, "source_too_large", sent.HttpStatus);
        var loop = Loop(new FakeSourceFetcher((_, _) => ValueTask.FromResult<ErrorOr<FetchedSource>>(FakeSourceFetcher.Page())), logger: logger);

        await loop.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Wait.UntilAsync(() => _api.Posts.Count == 1 && logger.Messages.Any(m => m.Contains("attempt 1 ", StringComparison.Ordinal)), "a finished task");
            _api.Posts[0].Body.Status.ShouldBe("done", "the loop sent the original body; only the client substitutes it");
            logger.Messages.ShouldContain(m => m.Contains("failed code source_too_large", StringComparison.Ordinal));
            logger.Messages.ShouldNotContain(m => m.Contains(" done code ", StringComparison.Ordinal));
            lock (counted)
            {
                counted.ShouldBe([("failed", "source_too_large")]);
            }
        }
        finally
        {
            await loop.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>QA additions (02.5.2-T3): guards of <see cref="ResultBodies"/> and the worker-id alphabet.</summary>
public sealed class ResultBodiesQaTests
{
    private static readonly FetcherOptions Options_ = new();

    private static FetchedSource WithText(string text, string? title = "Title") =>
        FakeSourceFetcher.Page() with { Text = text, TextChars = text.Length, Title = title };

    [Fact]
    public void Done_TextExactlyAtMax_IsDone()
    {
        var body = ResultBodies.Done("w", WithText(new string('a', Options_.MaxTextChars)), Options_);

        body.Status.ShouldBe("done");
        body.TextSha256!.Length.ShouldBe(64);
    }

    [Fact]
    public void Done_ReusesTheFetchersHash_WhenWellFormed()
    {
        const string abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

        var body = ResultBodies.Done("w", WithText("abc") with { Sha256 = abc }, Options_);

        body.Status.ShouldBe("done");
        body.TextSha256.ShouldBe(abc);
    }

    [Fact]
    public void Done_HashesTheText_WhenTheFetchersHashIsMalformed()
    {
        var body = ResultBodies.Done("w", WithText("abc") with { Sha256 = "NOT-A-HASH" }, Options_);

        body.TextSha256.ShouldBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    [Fact]
    public void Done_TextOneOverMax_IsSourceTooLarge()
    {
        var body = ResultBodies.Done("w", WithText(new string('a', Options_.MaxTextChars + 1)), Options_);

        body.Status.ShouldBe("failed");
        body.ErrorCode.ShouldBe("source_too_large");
        body.Text.ShouldBeNull();
    }

    [Theory]
    [InlineData(0xD800, true)]
    [InlineData(0xDC00, true)]
    [InlineData(0xD800, false)]
    [InlineData(0x0000, true)]
    [InlineData(0x007F, true)]
    [InlineData(0x0001, true)]
    public void Done_IllFormedText_IsSourceNoText(int code, bool trailing)
    {
        // Built at run time: xunit's theory serialisation would replace a lone surrogate with U+FFFD.
        var text = "abc" + (char)code + (trailing ? "def" : string.Empty);
        var body = ResultBodies.Done("w", WithText(text), Options_);

        body.Status.ShouldBe("failed");
        body.ErrorCode.ShouldBe("source_no_text");
        body.Text.ShouldBeNull();
    }

    [Fact]
    public void Done_TabNewlineCarriageReturnAndSurrogatePair_AreAllowed()
    {
        ResultBodies.Done("w", WithText("a\tb\nc\r\n\ud83d\ude00"), Options_).Status.ShouldBe("done");
    }

    [Fact]
    public void Done_IllFormedTitle_IsDroppedNotFailed()
    {
        var body = ResultBodies.Done("w", WithText("ok", "bad\ud800title"), Options_);

        body.Status.ShouldBe("done");
        body.Title.ShouldBeNull();
    }

    [Fact]
    public void Failed_UnknownCode_BecomesSourceUnreachable_AndBadHttpStatusIsOmitted()
    {
        var body = ResultBodies.Failed("w", "totally_unknown", 999);

        body.ErrorCode.ShouldBe("source_unreachable");
        body.HttpStatus.ShouldBeNull();
    }

    [Theory]
    [InlineData("my host name")]
    [InlineData("héllo-wörld")]
    [InlineData("a/b\\c?d#e")]
    [InlineData("")]
    [InlineData(null)]
    public void WorkerId_AlwaysMatchesTheApiAlphabet(string? configured)
    {
        var id = FetcherApiOptions.ResolveWorkerId(configured);

        System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9._:-]{1,64}$").ShouldBeTrue(id);
    }

    [Fact]
    public void WorkerId_VeryLongConfigured_IsTruncatedTo64()
    {
        FetcherApiOptions.ResolveWorkerId(new string('x', 500)).Length.ShouldBeLessThanOrEqualTo(64);
    }
}
