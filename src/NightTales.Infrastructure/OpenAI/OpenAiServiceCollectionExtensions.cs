// The Responses API of OpenAI 2.14.0 is marked experimental (OPENAI001); 02 README O1 pins this exact version.
#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Stories.Writing;
using OpenAI;
using OpenAI.Responses;
using Polly;
using Polly.Registry;
using Polly.Retry;
using Polly.Timeout;
using ModerationClient = OpenAI.Moderations.ModerationClient;

namespace NightTales.Infrastructure.OpenAI;

/// <summary>Composition of the OpenAI text adapter and the story writer (02.2.1-T2).</summary>
public static class OpenAiServiceCollectionExtensions
{
    /// <summary>The name of the <c>HttpClient</c> the SDK's transport sends through.</summary>
    public const string HttpClientName = "openai";

    /// <summary>The attempts per call: the first plus one retry (D7, F23).</summary>
    public const int MaxAttempts = 2;

    /// <summary>The longest <c>Retry-After</c> a retry waits for.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Registers <see cref="OpenAiOptions"/> (validated on start), the named <c>"openai"</c> HttpClient with
    /// <see cref="ClientRequestIdHandler"/>, one resilience pipeline per <see cref="TextStep"/>, the
    /// <see cref="ITextClient"/> singleton and the <see cref="IStoryWriter"/>. Needs the prompt catalog, usage
    /// metering, feature flags and <c>AddNightTalesApplication</c> in the same host.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddOpenAiTextClient(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        AddOpenAiTransport(services);

        foreach (var step in Enum.GetValues<TextStep>())
        {
            services.AddResiliencePipeline(OpenAiTextClient.PipelineKey(step), (builder, context) =>
            {
                var settings = context.ServiceProvider.GetRequiredService<IOptionsMonitor<OpenAiOptions>>().CurrentValue;
                ConfigurePipeline(builder, settings.RequestTimeout[step], settings.RetryBaseDelay);
            });
        }

        // F23: the SDK's own retry is off (ClientRetryPolicy with 0 retries); the pipeline above is the only retry layer.
        services.TryAddSingleton(provider =>
        {
            var settings = provider.GetRequiredService<IOptionsMonitor<OpenAiOptions>>().CurrentValue;
            var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);

            return new ResponsesClient(
                new ApiKeyCredential(settings.ApiKey),
                new ResponsesClientOptions
                {
                    Endpoint = new Uri(settings.BaseUrl.TrimEnd('/') + "/"),
                    Transport = new HttpClientPipelineTransport(http),
                    RetryPolicy = new ClientRetryPolicy(0),
                    NetworkTimeout = Timeout.InfiniteTimeSpan,
                });
        });
        services.TryAddSingleton<ITextClient, OpenAiTextClient>();
        services.TryAddSingleton<IStoryWriter, OpenAiStoryWriter>();

        AddFactsExtractor(services);
        AddContentModerator(services);

        return services;
    }

    /// <summary>
    /// 02.5.3-T1 (02 README D21): <see cref="FactsOptions"/> bound from <c>Facts</c> and validated on start,
    /// and the <see cref="IFactExtractor"/> over the same <see cref="ITextClient"/> and the prompt catalog.
    /// </summary>
    /// <param name="services">The service collection.</param>
    private static void AddFactsExtractor(IServiceCollection services)
    {
        services.AddOptions<FactsOptions>()
            .BindConfiguration(FactsOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<FactsOptions>, FactsOptionsValidator>());

        services.TryAddSingleton<IFactExtractor, OpenAiFactExtractor>();
    }

    /// <summary>
    /// Registers the real moderation adapter without the text-generation services. The Api uses the same
    /// <c>OpenAi:ApiKey</c>, transport and validated options as the Worker for edit and approval checks.
    /// </summary>
    /// <param name="services">The service collection; persistence supplies the moderation-event writer.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddOpenAiContentModerator(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        AddOpenAiTransport(services);
        AddContentModerator(services);

        return services;
    }

    private static void AddOpenAiTransport(IServiceCollection services)
    {
        services.AddOptions<OpenAiOptions>()
            .BindConfiguration(OpenAiOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<OpenAiOptions>, OpenAiOptionsValidator>());

        services.TryAddTransient<ClientRequestIdHandler>();
        services.AddHttpClient(HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            // No redirects: neither prompts nor moderation input may be replayed to another host.
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2), AllowAutoRedirect = false })
            .AddHttpMessageHandler<ClientRequestIdHandler>();
    }

    /// <summary>
    /// 02.4.1-T1 (02 README D12): <see cref="ModerationOptions"/> bound from <c>OpenAi:Moderation</c>, the
    /// <c>openai-moderation</c> pipeline (the text calls' retry shape with the moderation timeout), the moderation
    /// client over the same <c>"openai"</c> HttpClient and base URL as the text client (10 CR-16: the e2e stub also
    /// answers <c>/moderations</c>) and <see cref="IContentModerator"/>. Persistence supplies the event writer.
    /// </summary>
    private static void AddContentModerator(IServiceCollection services)
    {
        services.AddOptions<ModerationOptions>()
            .BindConfiguration(ModerationOptions.SectionName)
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Model),
                $"'{ModerationOptions.SectionName}:{nameof(ModerationOptions.Model)}' is required.")
            .Validate(
                options => options.MaxCharsPerChunk >= ModerationOptions.MinCharsPerChunk,
                $"'{ModerationOptions.SectionName}:{nameof(ModerationOptions.MaxCharsPerChunk)}' must be at least {ModerationOptions.MinCharsPerChunk}.")
            .Validate(
                options => options.TimeoutSeconds > 0,
                $"'{ModerationOptions.SectionName}:{nameof(ModerationOptions.TimeoutSeconds)}' must be positive.")
            .ValidateOnStart();

        services.AddResiliencePipeline(OpenAiContentModerator.PipelineKey, (builder, context) =>
        {
            var provider = context.ServiceProvider;
            var moderation = provider.GetRequiredService<IOptions<ModerationOptions>>().Value;
            var retryBaseDelay = provider.GetRequiredService<IOptionsMonitor<OpenAiOptions>>().CurrentValue.RetryBaseDelay;
            ConfigurePipeline(builder, TimeSpan.FromSeconds(moderation.TimeoutSeconds), retryBaseDelay);
        });

        // The SDK's own retry is off here too (F23); the pipeline above is the only retry layer.
        services.TryAddSingleton(provider =>
        {
            var settings = provider.GetRequiredService<IOptionsMonitor<OpenAiOptions>>().CurrentValue;
            var moderation = provider.GetRequiredService<IOptions<ModerationOptions>>().Value;
            var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);

            return new ModerationClient(
                moderation.Model,
                new ApiKeyCredential(settings.ApiKey),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri(settings.BaseUrl.TrimEnd('/') + "/"),
                    Transport = new HttpClientPipelineTransport(http),
                    RetryPolicy = new ClientRetryPolicy(0),
                    NetworkTimeout = Timeout.InfiniteTimeSpan,
                });
        });
        services.TryAddSingleton<IContentModerator>(provider => new OpenAiContentModerator(
            provider.GetRequiredService<ModerationClient>(),
            provider.GetRequiredService<IUsageMeter>(),
            provider.GetRequiredService<IOptions<ModerationOptions>>().Value,
            provider.GetRequiredService<ResiliencePipelineProvider<string>>(),
            provider.GetRequiredService<ILogger<OpenAiContentModerator>>()));
    }

    /// <summary>
    /// One attempt is bounded by <paramref name="attemptTimeout"/>; a 429 (without a funds code), a 5xx, a transport
    /// failure or a timeout is retried once with jittered backoff that honours <c>Retry-After</c> (D7, F23).
    /// </summary>
    internal static void ConfigurePipeline(ResiliencePipelineBuilder builder, TimeSpan attemptTimeout, TimeSpan baseDelay) =>
        builder
            // D19: Polly's telemetry logger prints the failed outcome's exception message, and a ClientResultException
            // message carries the provider's response body, which can echo request text. Metrics stay on; logs go.
            .ConfigureTelemetry(NullLoggerFactory.Instance)
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = MaxAttempts - 1,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = baseDelay,
                ShouldHandle = new PredicateBuilder()
                    .Handle<ClientResultException>(ex => ex.Status is 0 or >= 500 || (ex.Status == 429 && !OpenAiTextClient.IsFundsExhausted(ex)))
                    .Handle<TimeoutRejectedException>()
                    .Handle<HttpRequestException>(),
                // A Retry-After wins over the computed backoff (capped); no header returns null, which falls back to it.
                DelayGenerator = args =>
                {
                    var retryAfter = args.Outcome.Exception is ClientResultException ex ? OpenAiTextClient.RetryAfter(ex) : null;

                    return new ValueTask<TimeSpan?>(retryAfter is { } wait ? (wait > MaxRetryAfter ? MaxRetryAfter : wait) : null);
                },
            })
            .AddTimeout(attemptTimeout);
}
