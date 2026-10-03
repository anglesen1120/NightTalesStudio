// The Responses API of OpenAI 2.14.0 is marked experimental (OPENAI001); the adapter under test uses it.
#pragma warning disable OPENAI001

using System.Net;
using System.Net.Sockets;
using System.Text;
using ErrorOr;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories.Writing;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Length;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.OpenAI;
using NightTales.Infrastructure.Persistence.Usage;
using NightTales.Infrastructure.Prompts;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.OpenAI;

/// <summary>
/// 02.2.1-T2 review fixes (CR-1..3, SEC-1..4): the metering of the writer over the real adapter, assembler, embedded
/// prompt catalog and the real <see cref="StoryCostEstimator"/>, plus the adapter's hardening.
/// </summary>
public sealed class OpenAiMeteringReviewFixTests
{
    private const string OkJson = """{"title":"T","count":3}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Writer_MissingUsage_WithTheRealCatalogComposition_WritesAnEstimatedRowAndReturnsTheOutcome()
    {
        // CR-1: the embedded catalog ships no review/revision prompt, so the whole-pipeline bound fails with
        // prompt_not_shipped; the writer bound measures the writer prompt only.
        using var host = new OpenAiTestHost();
        var catalog = new PromptCatalog(new EmbeddedPromptResourceSource());
        var meter = new RecordingMeter();
        var writer = Writer(host, catalog, meter, new StoryCostEstimator(new LunaPriceCatalog(), catalog, new ConfigurationBuilder().Build(), new FakeClock()));
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(StoryJson, usage: string.Empty));

        (await new StoryCostEstimator(new LunaPriceCatalog(), catalog, new ConfigurationBuilder().Build(), new FakeClock())
            .WorstCaseAsync(LengthTier.Standard, Language.Vi, Ct)).FirstError.Code.ShouldBe(ErrorCodes.PromptNotShipped);

        var result = await writer.WriteAsync(Request(), Ct);

        result.ShouldBeOfType<TextCallOk<StoryContentV1>>();
        meter.Items.Single().ShouldSatisfyAllConditions(
            i => i.Estimated.ShouldBeTrue(),
            i => i.InputTokens.ShouldBeNull(),
            i => i.CostUsd.ShouldNotBeNull().ShouldBeGreaterThan(0m));
    }

    [Fact]
    public async Task Writer_FailedResponseBodyWithUsage_IsMeteredFromTheReportedUsage()
    {
        // CR-2: status "failed" with a usage object maps to ProviderUnavailable and keeps the usage.
        using var host = new OpenAiTestHost();
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(OkJson, status: "failed"));

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        var unavailable = result.ShouldBeOfType<TextCallUnavailable<Probe>>();
        unavailable.Usage.ShouldNotBeNull().InputTokens.ShouldBe(100);
        unavailable.MayBeBilled.ShouldBeTrue();

        var catalog = new PromptCatalog(new EmbeddedPromptResourceSource());
        var meter = new RecordingMeter();
        using var writerHost = new OpenAiTestHost();
        writerHost.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(StoryJson, status: "failed"));
        var outcome = await Writer(writerHost, catalog, meter, new LunaEstimator()).WriteAsync(Request(), Ct);

        outcome.ShouldBeOfType<TextCallUnavailable<StoryContentV1>>();
        meter.Items.Single().ShouldSatisfyAllConditions(i => i.Estimated.ShouldBeFalse(), i => i.InputTokens.ShouldBe(100), i => i.OutputTokens.ShouldBe(50));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task Writer_DefinitiveRejection_IsNotBilledAndNotMetered(HttpStatusCode status)
    {
        using var host = new OpenAiTestHost();
        var meter = new RecordingMeter();
        host.Handler.Reply(status, OpenAiTestHost.Error("rejected"));

        var result = await Writer(host, new PromptCatalog(new EmbeddedPromptResourceSource()), meter, new LunaEstimator()).WriteAsync(Request(), Ct);

        var error = result.ShouldBeOfType<TextCallProviderError<StoryContentV1>>();
        error.Status.ShouldBe((int)status);
        error.MayBeBilled.ShouldBeFalse();
        meter.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Writer_FundsExhaustedFlag_SendsNothingAndMetersNothing()
    {
        using var host = new OpenAiTestHost();
        host.Flags.FundsExhausted = true;
        var meter = new RecordingMeter();

        var result = await Writer(host, new PromptCatalog(new EmbeddedPromptResourceSource()), meter, new LunaEstimator()).WriteAsync(Request(), Ct);

        result.ShouldBeOfType<TextCallFundsExhausted<StoryContentV1>>().MayBeBilled.ShouldBeFalse();
        host.Handler.Requests.ShouldBeEmpty();
        meter.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Writer_FundsExhausted429_IsNotBilledAndNotMetered()
    {
        using var host = new OpenAiTestHost();
        var meter = new RecordingMeter();
        host.Handler.Reply(HttpStatusCode.TooManyRequests, OpenAiTestHost.Error("credit_balance_exhausted"));

        var result = await Writer(host, new PromptCatalog(new EmbeddedPromptResourceSource()), meter, new LunaEstimator()).WriteAsync(Request(), Ct);

        result.ShouldBeOfType<TextCallFundsExhausted<StoryContentV1>>().MayBeBilled.ShouldBeFalse();
        meter.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Writer_ServerErrorAfterRetry_IsMeteredAtTheBound()
    {
        using var host = new OpenAiTestHost();
        var meter = new RecordingMeter();
        host.Handler
            .Reply(HttpStatusCode.InternalServerError, OpenAiTestHost.Error("server_error"))
            .Reply(HttpStatusCode.BadGateway, OpenAiTestHost.Error("server_error"));

        var result = await Writer(host, new PromptCatalog(new EmbeddedPromptResourceSource()), meter, new LunaEstimator()).WriteAsync(Request(), Ct);

        result.ShouldBeOfType<TextCallUnavailable<StoryContentV1>>().MayBeBilled.ShouldBeTrue();
        meter.Items.Single().ShouldSatisfyAllConditions(i => i.Estimated.ShouldBeTrue(), i => i.CostUsd.ShouldBe(LunaEstimator.Bound));
    }

    [Fact]
    public async Task Writer_Plain429AfterRetry_IsMeteredAtTheBound()
    {
        using var host = new OpenAiTestHost();
        var meter = new RecordingMeter();
        host.Handler
            .Reply(HttpStatusCode.TooManyRequests, OpenAiTestHost.Error("rate_limit_exceeded"))
            .Reply(HttpStatusCode.TooManyRequests, OpenAiTestHost.Error("rate_limit_exceeded"));

        var result = await Writer(host, new PromptCatalog(new EmbeddedPromptResourceSource()), meter, new LunaEstimator()).WriteAsync(Request(), Ct);

        result.ShouldBeOfType<TextCallUnavailable<StoryContentV1>>().MayBeBilled.ShouldBeTrue();
        meter.Items.Single().Estimated.ShouldBeTrue();
    }

    [Fact]
    public async Task Writer_TimeoutAfterRetry_IsMeteredAtTheBound()
    {
        using var host = new OpenAiTestHost(o => o.RequestTimeout[TextStep.Writer] = TimeSpan.FromMilliseconds(150));
        var meter = new RecordingMeter();
        for (var i = 0; i < 2; i++)
        {
            host.Handler.Then(async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);

                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        }

        var result = await Writer(host, new PromptCatalog(new EmbeddedPromptResourceSource()), meter, new LunaEstimator()).WriteAsync(Request(), Ct);

        result.ShouldBeOfType<TextCallUnavailable<StoryContentV1>>().MayBeBilled.ShouldBeTrue();
        meter.Items.Single().Estimated.ShouldBeTrue();
    }

    [Fact]
    public async Task Writer_CallerCancellationAfterTheRequestWasSent_RecordsAnEstimatedRowAndRethrows()
    {
        using var host = new OpenAiTestHost();
        var meter = new RecordingMeter();
        host.Handler.Then(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(150);

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await Writer(host, new PromptCatalog(new EmbeddedPromptResourceSource()), meter, new LunaEstimator()).WriteAsync(Request(), cts.Token));

        meter.Items.Single().ShouldSatisfyAllConditions(
            i => i.Estimated.ShouldBeTrue(),
            i => i.CostUsd.ShouldBe(LunaEstimator.Bound),
            i => i.Model.ShouldBe(OpenAiTestHost.Model));
    }

    [Theory]
    [InlineData("Infinity")]
    [InlineData("NaN")]
    [InlineData("-5")]
    public async Task OpenAiTextClient_NonFiniteOrNegativeRetryAfter_FallsBackToBackoffAndRetries(string retryAfter)
    {
        // SEC-3: used to throw OverflowException out of the delay generator and surface as ProviderError(0).
        using var host = new OpenAiTestHost();
        host.Handler
            .Reply(HttpStatusCode.TooManyRequests, OpenAiTestHost.Error("rate_limit_exceeded"), ("Retry-After", retryAfter))
            .Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(OkJson));

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        result.ShouldBeOfType<TextCallOk<Probe>>();
        host.Handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task OpenAiTextClient_HugeRetryAfter_IsClampedNotAnError()
    {
        // 1e30 seconds does not fit a TimeSpan: it is clamped to the 30 s cap, so the call is still waiting
        // for its retry (cancelled by the caller here) instead of having failed with ProviderError(0).
        using var host = new OpenAiTestHost();
        host.Handler
            .Reply(HttpStatusCode.TooManyRequests, OpenAiTestHost.Error("rate_limit_exceeded"), ("Retry-After", "1e30"))
            .Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(OkJson));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(750);

        await Should.ThrowAsync<OperationCanceledException>(async () => await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), cts.Token));

        host.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task OpenAiTextClient_Redirect_IsNotFollowedAndMapsToAnUnbilledProviderError()
    {
        // SEC-4: a 307 must not replay the prompt body at the Location target.
        using var server = new RedirectServer();
        using var host = new OpenAiTestHost(o => o.BaseUrl = server.BaseUrl, scriptedTransport: false, development: true);

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        var error = result.ShouldBeOfType<TextCallProviderError<Probe>>();
        error.Status.ShouldBe(307);
        error.MayBeBilled.ShouldBeFalse();
        server.Requests.ShouldBe(1, "the redirect target is never called");
    }

    private static OpenAiStoryWriter Writer(OpenAiTestHost host, PromptCatalog catalog, RecordingMeter meter, IStoryCostEstimator estimator) =>
        new(new PromptAssembler(catalog, NullLogger<PromptAssembler>.Instance), host.Client, catalog, meter, estimator);

    private static StoryWriteRequest Request() =>
        new(
            new RunContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new ReservationId(Guid.NewGuid()), GenerationTrigger.User, 1, "1"),
            new SeriesSnapshot(Guid.NewGuid(), Topic.Ghost, Language.Vi),
            new EpisodeSnapshot(Guid.NewGuid(), "A haunted house at night", LengthTier.Standard, null),
            new LengthTarget(1300, 2080, 20, 32, 6000, 2500, 6000),
            null,
            null,
            [],
            null);

    private const string StoryJson =
        """
        {"title":"T","hook":{"text":"h","keywords":["door"],"fact_refs":[]},
         "paragraphs":[{"text":"p","keywords":["hall"],"fact_refs":[],"beat":"setup"}],
         "ending":{"text":"e","keywords":["night"],"fact_refs":[]},
         "description":"d","tags":["ghost"],"content_warnings":[],
         "structure":{"sub_genre":"haunted_house","setting":"house","opening":"cold","twist_type":"reveal","narrator_mode":"first"}}
        """;

    private sealed class RecordingMeter : IUsageMeter
    {
        internal List<UsageItem> Items { get; } = [];

        public Task RecordAsync(UsageItem item, CancellationToken ct)
        {
            Items.Add(item);

            return Task.CompletedTask;
        }
    }

    private sealed class LunaEstimator : IStoryCostEstimator
    {
        internal const decimal Bound = 0.75m;

        public Task<ErrorOr<UsageBound>> WorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            throw new InvalidOperationException("The writer must not use the whole-pipeline bound.");

        public Task<ErrorOr<UsageBound>> WriterWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(new UsageBound(Bound, 1, 1));
    }

    private sealed class LunaPriceCatalog : IPriceCatalog
    {
        private static readonly PriceRates Luna = new(0.10m, 0.01m, 0.125m, 0.50m);

        public Task<ErrorOr<PriceVersion>> GetActiveAsync(DateTimeOffset queuedAt, CancellationToken ct) => Task.FromResult<ErrorOr<PriceVersion>>(Build());

        public Task<PriceVersion> GetAsync(int version, CancellationToken ct) => Task.FromResult(Build());

        private static PriceVersion Build()
        {
            var rates = new PriceVersionRates(
                new Dictionary<string, PriceRates> { ["gpt-6-luna"] = Luna },
                new Dictionary<string, ImagePriceRate>(),
                null,
                TextCostCalculator.DefaultSurchargeThresholdTokens).ToJson();

            return PriceVersion.Create(1, FakeClock.DefaultStart, rates, PriceVersionJson.WriteLimits(PriceLimits.D4Defaults), "fake", FakeClock.DefaultStart);
        }
    }

    /// <summary>A loopback server that answers every request with a 307 to another path of itself and counts the requests.</summary>
    private sealed class RedirectServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private int _requests;

        internal RedirectServer()
        {
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        internal int Requests => Volatile.Read(ref _requests);

        internal string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1";

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                    _ = Task.Run(() => ServeAsync(client));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Disposed: the test is over.
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new byte[16_384];
                    var seen = new StringBuilder();
                    while (!seen.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var read = await stream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                        if (read == 0)
                        {
                            return;
                        }

                        seen.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    Interlocked.Increment(ref _requests);
                    var reply = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/other\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(reply, _stop.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    // The client went away.
                }
            }
        }
    }
}
