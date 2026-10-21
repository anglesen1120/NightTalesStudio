// The Responses API of OpenAI 2.14.0 is marked experimental (OPENAI001); the adapter under test uses it.
#pragma warning disable OPENAI001

using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Flags;
using NightTales.Application.Abstractions.Text;
using NightTales.Infrastructure.OpenAI;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.OpenAI;

/// <summary>
/// 02.2.1-T2 QA additions: negative and boundary cases on top of <see cref="OpenAiTextClientTests"/> —
/// more 4xx statuses, malformed Retry-After, transport failures, an empty output, the exception-message
/// log boundary, a caller cancel mid Retry-After delay, and the D7 composition contract under the
/// Worker's own <c>ValidateScopes = true</c>.
/// </summary>
public sealed class OpenAiTextClientQaTests
{
    private const string OkJson = """{"title":"T","count":3}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnprocessableContent)]
    public async Task OpenAiTextClient_Other4xxStatuses_ReturnProviderErrorAndAreNotRetried(HttpStatusCode status)
    {
        using var host = new OpenAiTestHost();
        host.Handler.Reply(status, OpenAiTestHost.Error("some_error"));

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        result.ShouldBeOfType<TextCallProviderError<Probe>>().Status.ShouldBe((int)status);
        host.Handler.Requests.Count.ShouldBe(1, "a non-429 4xx is never retried (F23)");
    }

    [Fact]
    public async Task OpenAiTextClient_TransportFailureAfterRetry_ReturnsProviderUnavailable()
    {
        using var host = new OpenAiTestHost();
        host.Handler
            .Then(_ => throw new HttpRequestException("connection refused"))
            .Then(_ => throw new HttpRequestException("connection refused"));

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        result.ShouldBeOfType<TextCallUnavailable<Probe>>();
        host.Handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task OpenAiTextClient_MalformedRetryAfter_FallsBackToComputedBackoff()
    {
        using var host = new OpenAiTestHost();
        host.Handler
            .Reply(HttpStatusCode.TooManyRequests, OpenAiTestHost.Error("rate_limit_exceeded"), ("Retry-After", "not-a-number"))
            .Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(OkJson));

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        result.ShouldBeOfType<TextCallOk<Probe>>();
        host.Handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task OpenAiTextClient_EmptyOutputText_ReturnsInvalidOutput()
    {
        using var host = new OpenAiTestHost();
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(string.Empty));

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        result.ShouldBeOfType<TextCallInvalidOutput<Probe>>()
            .RuleIds.ShouldBe(["schema_invalid"]);
    }

    [Fact]
    public async Task OpenAiTextClient_UnexpectedException_MessageNeverReachesTheLog()
    {
        using var host = new OpenAiTestHost();
        host.Handler.Then(_ => throw new FormatException("echoed SYSTEM-PROMPT-SECRET and unit-test-api-key-placeholder"));

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        result.ShouldBeOfType<TextCallProviderError<Probe>>().Status.ShouldBe(0);

        var all = string.Join("\n", host.Logs.Messages);
        all.ShouldContain("FormatException");
        all.ShouldNotContain("SYSTEM-PROMPT-SECRET");
        all.ShouldNotContain("unit-test-api-key-placeholder");
        all.ShouldNotContain("echoed");
    }

    [Fact]
    public async Task OpenAiTextClient_RetryDelayRespectsCallerCancellation()
    {
        // A Retry-After beyond the cap must not hold the call when the caller cancels mid-delay.
        using var host = new OpenAiTestHost();
        host.Handler.Reply(HttpStatusCode.TooManyRequests, OpenAiTestHost.Error("rate_limit_exceeded"), ("Retry-After", "600"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(150);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), cts.Token));
        watch.Stop();

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30),
            "the caller's token cancels the capped Retry-After wait instead of sitting out 30 s");
        host.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task OpenAiTextClient_FundsCodeOnTheSecondAttempt_ReturnsProviderFundsExhausted()
    {
        using var host = new OpenAiTestHost();
        host.Handler
            .Reply(HttpStatusCode.TooManyRequests, OpenAiTestHost.Error("rate_limit_exceeded"))
            .Reply(HttpStatusCode.TooManyRequests, OpenAiTestHost.Error("credit_balance_exhausted"));

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        result.ShouldBeOfType<TextCallFundsExhausted<Probe>>();
        host.Handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task OpenAiTextClient_SafetyIdentifierIsNotTheRawUserId()
    {
        var userId = Guid.Parse("0197a000-0000-7000-8000-00000000beef");
        using var host = new OpenAiTestHost();
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(OkJson));

        await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(userId: userId), Ct);

        var identifier = host.Handler.Requests.Single().Json.GetProperty("safety_identifier").GetString();
        identifier.ShouldNotBe(userId.ToString(), "the raw user id must never reach the provider (D7)");
        identifier!.Length.ShouldBe(64, "a sha256 hex digest");
        identifier.ShouldSatisfyAllConditions(i => i.All(c => char.IsAsciiHexDigitLower(c)).ShouldBeTrue());
    }

    [Fact]
    public void OpenAiComposition_ITextClientResolves_WhenFlagsAreScoped()
    {
        // QA-1: the Worker registers IFeatureFlags as scoped and resolves with ValidateScopes = true;
        // the singleton client resolves it per call through a fresh scope (UsageMeter precedent).
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment());
        services.AddScoped<IFeatureFlags>(_ => new FakeFlags());   // the Worker's lifetime for IFeatureFlags
        services.AddOpenAiTextClient();
        services.Configure<OpenAiOptions>(options => options.ApiKey = "unit-test-api-key-placeholder");

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        provider.GetService<ITextClient>().ShouldNotBeNull(
            "ITextClient must resolve in the Worker's provider (IFeatureFlags is scoped there)");
    }
}
