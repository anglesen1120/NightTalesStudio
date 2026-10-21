using System.Security.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Rendering;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace NightTales.Infrastructure.Rendering;

/// <summary>
/// The two render-node pipelines of 09.6.2-T1 (09 README BD6, WF08/W9) and the composition that pairs them
/// with the two clients, the bound and validated <c>Render</c> section and the node transport of 11.3.3-T1.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not <c>AddStandardResilienceHandler</c> (W9): the standard handler's total timeout, retry
/// predicate and breaker are not the shape BD6 needs — the DELETE must never retry, a 429 must never be
/// retried, and the breaker must not open at P volume by accident.
/// </para>
/// <para>
/// One pipeline is per <em>client</em>, not per node: a node with a bad TLS pin, a closed port or a hung
/// adapter is rejected by <see cref="IRenderNodeConnectionFactory"/> per node, and the breaker is what takes
/// the node out of the retry loop. A per-node breaker would mean one breaker and one sampler per node row in
/// every Worker process — the pipelines are per process, so a node's failures share the breaker with the other
/// nodes until 11.3.3-T2's selector demonstrably needs otherwise (09.6.2-T1 task §Notes, open finding in the
/// slice's report).
/// </para>
/// </remarks>
public static class RenderClientResilience
{
    /// <summary>
    /// The first backoff of a retried call (200 ms, exponential with jitter): a node that refuses a
    /// connection is asked again quickly, and the jitter spreads the attempts of concurrent jobs.
    /// </summary>
    internal static readonly TimeSpan RetryBaseDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The one 4xx status that is a transient node fault rather than a caller fault: 408 is the adapter's own
    /// request timeout (05 D4), the 5xx class covering the rest.
    /// </summary>
    private const int RequestTimeoutStatus = 408;

    /// <summary>Registers the <c>Render</c> section, both pipelines, the named download client and the clients.</summary>
    /// <remarks>
    /// The <c>RenderNodes</c> section <see cref="MptRenderClient"/> reads its health-poll interval from must be
    /// registered by the same composition root, and <c>AddRenderNodes</c> is what binds and validates it; this
    /// method registers the options as well, so a root that composes the render clients alone still resolves
    /// them with the section's defaults instead of failing to build the client at all.
    /// </remarks>
    /// <param name="services">The composition root's service collection.</param>
    /// <param name="configuration">The host configuration: the <c>Render</c> section.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddRenderClients(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RenderNodesOptions>();

        services.AddOptions<RenderClientOptions>()
            .BindConfiguration(RenderClientOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<RenderClientOptions>, RenderClientOptionsValidator>());

        // Both clients take their timeouts, their retry budget and their breaker shape from the same bound
        // section, so a host change is one key and never a code change (BD6, W9).
        services.AddResiliencePipeline<string, AdapterCallResult>(MptRenderClient.RetryingClientName, (builder, context) =>
        {
            builder.TimeProvider = context.ServiceProvider.GetRequiredService<TimeProvider>();
            ConfigureRetryingPipeline(
                builder,
                context.ServiceProvider.GetRequiredService<IOptionsMonitor<RenderClientOptions>>().CurrentValue);
        });

        services.AddResiliencePipeline<string, AdapterCallResult>(MptRenderClient.NoRetryClientName, (builder, context) =>
        {
            builder.TimeProvider = context.ServiceProvider.GetRequiredService<TimeProvider>();
            ConfigureNoRetryPipeline(
                builder,
                context.ServiceProvider.GetRequiredService<IOptionsMonitor<RenderClientOptions>>().CurrentValue);
        });

        // The download client never resolves this named client - only IRenderNodeConnectionFactory can build a
        // client that carries the node's base URL, its key and its pinned certificate (F13) - but the name
        // resolves to the shape RenderDownloadClient documents: no timeout of its own and no redirects, so a
        // host or a test that asks for it gets exactly what the class promises.
        services.AddHttpClient(RenderDownloadClient.HttpClientName, RenderDownloadClient.ConfigureHttpClient)
            .ConfigurePrimaryHttpMessageHandler(CreateDownloadHandler);

        // Every host that resolves a client has a clock; the render clients' timers and the pipelines' timeouts
        // run on it, so a test drives them with a manual provider (09 README D3).
        services.TryAddSingleton(TimeProvider.System);

        // Both clients are stateless over injected state (the connection factory, the pipelines, the options),
        // so one instance per host serves every job.
        services.TryAddSingleton<IRenderClient, MptRenderClient>();
        services.TryAddSingleton<IRenderDownloadClient, RenderDownloadClient>();

        return services;
    }

    /// <summary>
    /// The retrying pipeline (BD6): 30 s total outermost, then the retry budget, then the breaker, then the
    /// 10 s attempt innermost. A DELETE never runs here — it is routed to
    /// <see cref="MptRenderClient.NoRetryClientName"/> (WT55).
    /// </summary>
    /// <param name="builder">The builder the host's pipeline factory owns.</param>
    /// <param name="options">The bound <c>Render</c> section.</param>
    internal static void ConfigureRetryingPipeline(
        ResiliencePipelineBuilder<AdapterCallResult> builder,
        RenderClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        ConfigureTelemetry(builder);

        var client = options.Client;

        // 30 s total (WF08): every attempt and every backoff of one logical call is inside it, so a render
        // dispatch can never occupy the Worker longer than this.
        builder.AddTimeout(TimeSpan.FromSeconds(client.TotalSeconds));

        builder.AddRetry(new RetryStrategyOptions<AdapterCallResult>
        {
            MaxRetryAttempts = client.MaxRetries,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = RetryBaseDelay,
            ShouldHandle = args => ValueTask.FromResult(IsRetryable(args.Outcome)),
        });

        builder.AddCircuitBreaker(BreakerOptions(options));

        // 10 s per attempt innermost: it bounds one node call, so the retry above it starts a fresh attempt.
        builder.AddTimeout(TimeSpan.FromSeconds(client.AttemptSeconds));
    }

    /// <summary>
    /// The DELETE pipeline: the same total timeout, the same attempt timeout and the same breaker, and no
    /// retry strategy at all. Routing the call is the only reliable guard — a transport failure carries no
    /// request to inspect, so no predicate could tell a DELETE from a POST (task §Design).
    /// </summary>
    /// <param name="builder">The builder the host's pipeline factory owns.</param>
    /// <param name="options">The bound <c>Render</c> section.</param>
    internal static void ConfigureNoRetryPipeline(
        ResiliencePipelineBuilder<AdapterCallResult> builder,
        RenderClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        ConfigureTelemetry(builder);

        var client = options.Client;

        builder.AddTimeout(TimeSpan.FromSeconds(client.TotalSeconds));
        builder.AddCircuitBreaker(BreakerOptions(options));
        builder.AddTimeout(TimeSpan.FromSeconds(client.AttemptSeconds));
    }

    /// <summary>
    /// Whether one outcome may be retried, and whether it opens the breaker with the retryable verdict it also
    /// hands the breaker: a non-cancellation fault that is not a TLS failure, or a 408/5xx answer. A 429 is
    /// deliberately neither — <c>adapter_capacity_full</c> is a <see cref="RenderClientFailureKind.Deferred"/>
    /// carrying the adapter's <c>Retry-After</c>, which would outlive the 30 s total timeout anyway (BD6, 11 T6).
    /// </summary>
    /// <param name="outcome">What one attempt produced.</param>
    /// <returns><c>true</c> when the call may be repeated and counts against the breaker.</returns>
    internal static bool IsRetryable(Outcome<AdapterCallResult> outcome)
    {
        // An open breaker is not a fault to repeat: it is the pipeline refusing to try at all, and a retry
        // would be rejected again without a single request. The caller sees the same transient node error,
        // so the retry budget is spent on the node's actual answers instead (BD6).
        if (outcome.Exception is BrokenCircuitException)
        {
            return false;
        }

        // The caller's cancellation is never a retryable fault; it leaves this class as an
        // OperationCanceledException (task §Design).
        if (outcome.Exception is OperationCanceledException)
        {
            return false;
        }

        // A refused pinned handshake is the node's configuration, not its availability: every attempt would be
        // refused identically, so the retry budget is not spent on it and the breaker does not count it. The
        // caller gets the terminal, alerting tls failure instead (task §Failure handling, D13/S3).
        if (IsTlsFailure(outcome.Exception))
        {
            return false;
        }

        if (outcome.Exception is not null)
        {
            return true;
        }

        return outcome.Result is { } result && result.Status is RequestTimeoutStatus or >= 500;
    }

    /// <summary>
    /// Whether an exception is the pinned TLS handshake failing rather than the network: the certificate was
    /// refused (a pin mismatch, an expired certificate) or the handshake's authentication failed. The check
    /// walks the whole inner-exception chain, because the platform wraps the handshake's
    /// <see cref="AuthenticationException"/> differently per transport.
    /// </summary>
    /// <param name="exception">The outcome's exception, or <c>null</c>.</param>
    /// <returns><c>true</c> when the chain carries an <see cref="AuthenticationException"/>.</returns>
    internal static bool IsTlsFailure(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Silences Polly's telemetry logger while keeping its metrics: that logger prints the failed outcome's
    /// exception message, and an <see cref="HttpRequestException"/> message carries the request URI — the
    /// node's base URL must never reach a log (BD16/S4; the same reason as D19 for the OpenAI pipelines).
    /// </summary>
    /// <param name="builder">The builder being configured.</param>
    private static void ConfigureTelemetry(ResiliencePipelineBuilder<AdapterCallResult> builder) =>
        builder.ConfigureTelemetry(NullLoggerFactory.Instance);

    /// <summary>The breaker of both pipelines (W9): 5 calls minimum, a 1.0 ratio, a 60 s window, a 30 s break.</summary>
    /// <param name="options">The bound <c>Render</c> section.</param>
    /// <returns>The breaker options to add.</returns>
    private static CircuitBreakerStrategyOptions<AdapterCallResult> BreakerOptions(RenderClientOptions options)
    {
        var breaker = options.Client.CircuitBreaker;

        return new CircuitBreakerStrategyOptions<AdapterCallResult>
        {
            FailureRatio = breaker.FailureRatio,
            MinimumThroughput = breaker.MinimumThroughput,
            SamplingDuration = TimeSpan.FromSeconds(breaker.SamplingDurationSeconds),
            BreakDuration = TimeSpan.FromSeconds(breaker.BreakDurationSeconds),
            ShouldHandle = args => ValueTask.FromResult(IsRetryable(args.Outcome)),
        };
    }

    /// <summary>The download client's primary handler, built by the client that owns its settings.</summary>
    /// <returns>The handler every "render-node-download" client uses.</returns>
    private static HttpMessageHandler CreateDownloadHandler()
    {
        var handler = new SocketsHttpHandler();
        RenderDownloadClient.ConfigurePrimaryHandler(handler);
        return handler;
    }
}
