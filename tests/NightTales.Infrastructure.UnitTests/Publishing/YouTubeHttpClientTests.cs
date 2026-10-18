using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Application.Common.Errors;
using NightTales.Application.Publishing;
using NightTales.Infrastructure.Publishing;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Publishing;

/// <summary>
/// The two transports the 06.1.2-T1 registration composes: the named OAuth client carries the configured
/// <c>YouTube:Http:TimeoutSeconds</c> and a bounded response body, and the Google Data API client the SDK builds
/// inside <see cref="GoogleYouTubeClient"/> applies the same bound — a compromised or misconfigured endpoint must
/// not be able to make a callback buffer an arbitrary body inside that timeout (security/performance review F6b/P3).
/// </summary>
/// <remarks>
/// The SDK path runs against a handler that answers one canned JSON body: no listener, no live Google. The
/// over-the-bound case is one mebibyte of JSON, far past <see cref="GoogleYouTubeClient.MaxResponseContentBytes"/>,
/// so it proves the bound is in force on the SDK's own client (§Tests of the task).
/// </remarks>
public sealed class YouTubeHttpClientTests
{
    private const string ClientId = "nts-youtube-dev";
    private const string SecretRef = "nts/dev/api/YOUTUBE_CLIENT_SECRET";
    private const string RedirectUri = "http://127.0.0.1:5000/api/v1/channels/youtube/callback";
    private const string ChannelId = "canary-channel-one";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    public void The_named_oauth_client_carries_the_configured_timeout_and_a_bounded_body(int timeoutSeconds)
    {
        using var provider = Services(timeoutSeconds);

        var client = provider
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(GoogleYouTubeClient.OAuthHttpClientName);

        client.Timeout.ShouldBe(TimeSpan.FromSeconds(timeoutSeconds));
        client.MaxResponseContentBufferSize.ShouldBe(GoogleYouTubeClient.MaxResponseContentBytes);
    }

    [Fact]
    public void The_upload_client_carries_no_shared_timeout_a_bounded_body_and_no_request_logging()
    {
        using var provider = Services(15);

        var client = provider
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(GoogleYouTubeClient.UploadHttpClientName);

        // Each upload call arms its own linked deadline from `YouTube:Upload:*` (06.2.3-T1 protocol decision 3), so a
        // shared timeout would spend one call's budget on another's and must stay off.
        client.Timeout.ShouldBe(Timeout.InfiniteTimeSpan);
        client.MaxResponseContentBufferSize.ShouldBe(GoogleYouTubeClient.MaxResponseContentBytes);

        // The session URI is a capability and it is the request URI: the framework's own request logging for this
        // client is off, so no line can carry it.
        var rules = provider.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.Rules;
        rules.ShouldContain(rule =>
            rule.CategoryName == $"System.Net.Http.HttpClient.{GoogleYouTubeClient.UploadHttpClientName}"
            && rule.LogLevel == LogLevel.None);
    }

    [Fact]
    public async Task A_channel_payload_below_the_bound_is_read_through_the_sdk_client()
    {
        var title = new string('t', 1024);
        using var handlers = HandlerProvider(ChannelBody(title));

        var channel = await Client(handlers.GetRequiredService<IHttpMessageHandlerFactory>())
            .GetMyChannelAsync("canary-access-token", Ct);

        channel.IsError.ShouldBeFalse();
        channel.Value.Id.ShouldBe(ChannelId);
        channel.Value.Title.ShouldBe(title);
    }

    [Fact]
    public async Task A_channel_payload_over_the_bound_fails_instead_of_being_buffered()
    {
        using var handlers = HandlerProvider(ChannelBody(new string('t', 1024 * 1024)));

        var channel = await Client(handlers.GetRequiredService<IHttpMessageHandlerFactory>())
            .GetMyChannelAsync("canary-access-token", Ct);

        // The bound is what makes this fail: without it the SDK would buffer the body and answer the channel, which
        // the case above shows this handler and payload shape otherwise produce. Only the failure matters here —
        // which transport error the SDK surfaces is its business, not this test's.
        channel.IsError.ShouldBeTrue();
    }

    private static GoogleYouTubeClient Client(IHttpMessageHandlerFactory handlers) =>
        new(
            new UnusedHttpClientFactory(),
            handlers,
            Options.Create(new YouTubeOptions
            {
                ApiBaseUrl = "https://www.googleapis.com",
                Http = new YouTubeHttpOptions { TimeoutSeconds = 15 },
            }),
            new FakeClock(),
            NullLogger<GoogleYouTubeClient>.Instance);

    /// <summary>The one canned body every SDK request of a test receives.</summary>
    private static ServiceProvider HandlerProvider(string body)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient(GoogleYouTubeClient.ApiHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StaticBodyHandler(body));

        return services.BuildServiceProvider();
    }

    private static string ChannelBody(string title) =>
        $$"""
        {
          "kind": "youtube#channelListResponse",
          "items": [
            { "kind": "youtube#channel", "id": "{{ChannelId}}", "snippet": { "title": "{{title}}" } }
          ]
        }
        """;

    /// <summary>
    /// The composition of the client check: the host environment the validator adapter reads, the section the
    /// registration binds, and the registration itself.
    /// </summary>
    private static ServiceProvider Services(int timeoutSeconds)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.ActiveClientId)}"] = ClientId,
                [$"{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.Clients)}:{ClientId}:{nameof(YouTubeClientOptions.SecretRef)}"] = SecretRef,
                [$"{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.RedirectUri)}"] = RedirectUri,
                [$"{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.Http)}:{nameof(YouTubeHttpOptions.TimeoutSeconds)}"] =
                    timeoutSeconds.ToString(CultureInfo.InvariantCulture),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment("Production"));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddYouTubePublishing(configuration);

        return services.BuildServiceProvider();
    }

    /// <summary>Answers every request with one JSON body.</summary>
    private sealed class StaticBodyHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>Fails the test if the channel read ever creates the named OAuth client (it must not).</summary>
    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("the channel read must not create the named OAuth client.");
    }

    /// <summary>A hand-written host environment: the validator adapter reads Development only.</summary>
    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "NightTales.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
