using Microsoft.Extensions.DependencyInjection;

namespace NightTales.Api.Http;

/// <summary>
/// The two named outbound <c>HttpClient</c>s of 09 W3 CR-7 — <c>pwnedpasswords</c> (consumed by
/// 09.3.1-T2) and <c>turnstile</c> (consumed by 09.3.3-T2) — registered by 09.3.5-T1 with the shared
/// <c>IHttpClientFactory</c> convention so their S-phase callers only have to ask for the name.
/// </summary>
/// <remarks>
/// <para>
/// Both calls leave the process for a third party, so both are bounded twice: an attempt is abandoned
/// after <see cref="AttemptTimeoutSeconds"/> seconds and the whole call after
/// <see cref="TotalTimeoutSeconds"/>, and a transport failure — a DNS failure, a reset connection, an
/// abandoned attempt — is retried exactly once. A 4xx is never retried: the answer is the provider's,
/// and repeating a request the provider refused is at best useless and, for a single-use Turnstile token,
/// actively harmful. The retried request is the same <see cref="HttpRequestMessage"/>, so a caller's
/// <c>idempotency_key</c> (09.3.3-T2) travels with the retry unchanged.
/// </para>
/// <para>
/// No key is set here: neither endpoint authenticates with a header (the Pwned Passwords range API is
/// anonymous; the Turnstile secret travels in the form body of the caller's request), so this file
/// configures only the transport bounds. A caller that needs its own header adds its own handler.
/// </para>
/// </remarks>
public static class OutboundHttpClients
{
    /// <summary>The name of the Pwned Passwords range client (09.3.1-T2).</summary>
    public const string PwnedPasswords = "pwnedpasswords";

    /// <summary>The name of the Cloudflare Turnstile <c>siteverify</c> client (09.3.3-T2).</summary>
    public const string Turnstile = "turnstile";

    /// <summary>The seconds one attempt may take before it counts as a transport failure.</summary>
    public const int AttemptTimeoutSeconds = 5;

    /// <summary>The seconds a whole call — every attempt included — may take.</summary>
    public const int TotalTimeoutSeconds = 8;

    /// <summary>How many transport failures are retried on top of the first attempt.</summary>
    public const int TransportRetries = 1;

    /// <summary>Registers both named clients with their attempt/total bounds and one transport retry.</summary>
    /// <param name="services">The service collection of the host.</param>
    public static IServiceCollection AddNightTalesOutboundClients(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        AddTransportRetry(services.AddHttpClient(PwnedPasswords, ConfigureClient));
        AddTransportRetry(services.AddHttpClient(Turnstile, ConfigureClient));

        return services;
    }

    private static void ConfigureClient(HttpClient client) =>
        client.Timeout = TimeSpan.FromSeconds(TotalTimeoutSeconds);

    /// <summary>
    /// Adds the one-transport-retry handler, with the per-attempt timeout inside it so every attempt gets
    /// its own budget.
    /// </summary>
    private static void AddTransportRetry(IHttpClientBuilder builder) =>
        builder
            .AddHttpMessageHandler(static () => new TransportRetryHandler(TransportRetries))
            .AddHttpMessageHandler(static () => new AttemptTimeoutHandler(TimeSpan.FromSeconds(AttemptTimeoutSeconds)));
}

/// <summary>
/// Abandons one attempt after a per-attempt budget (09 W3 CR-7): the budget is shorter than the client's
/// total timeout, so a call that hangs is retried once inside its total budget rather than being left to
/// the outer timeout alone.
/// </summary>
/// <param name="attemptTimeout">How long one attempt may take.</param>
internal sealed class AttemptTimeoutHandler(TimeSpan attemptTimeout) : DelegatingHandler
{
    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(attemptTimeout);

        try
        {
            return await base.SendAsync(request, attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller is still waiting: the attempt's own budget ran out, which is a transport
            // failure the retry handler above may retry.
            throw new TimeoutException($"The outbound attempt took longer than {attemptTimeout.TotalSeconds:F0} seconds.");
        }
    }
}

/// <summary>
/// Retries a transport failure a bounded number of times (09 W3 CR-7): the request itself is unchanged,
/// and a response — including a 4xx — is returned to the caller without a second attempt.
/// </summary>
/// <param name="retries">How many failures are retried after the first attempt.</param>
internal sealed class TransportRetryHandler(int retries) : DelegatingHandler
{
    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < retries && IsTransportFailure(exception, cancellationToken))
            {
                // Fall through to the next attempt: the caller asked for one retry, and a refusal (a
                // response, whatever its status) never reaches this catch at all.
            }
        }
    }

    private static bool IsTransportFailure(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && exception is HttpRequestException or IOException or TimeoutException or TaskCanceledException;
}
