using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Publishing;
using NightTales.Infrastructure.Security;

namespace NightTales.Infrastructure.Publishing;

/// <summary>
/// The composition root of the YouTube connect flow (06.1.2-T1): the <c>YouTube</c> section, the OAuth client
/// resolver, the secret provider, the Google client and the two named transports they use.
/// </summary>
/// <remarks>
/// It registers no <c>IConnectChannelConnection</c>: that port is implemented by the connection store of
/// 06.1.2-T2, and the callback handler resolves it late so a host that has no implementation still boots.
/// </remarks>
public static class PublishingServiceCollectionExtensions
{
    /// <summary>
    /// Binds and validates <see cref="YouTubeOptions"/> once, then registers <see cref="ISecretProvider"/>
    /// (configuration-backed), <see cref="IOAuthClientResolver"/> (the configured client map),
    /// <see cref="IYouTubeClient"/> (Google) and the named clients <c>youtube-oauth</c> and <c>youtube-api</c>.
    /// </summary>
    /// <remarks>
    /// The host must already register <see cref="NightTales.Domain.Common.IClock"/> and the HTTP client factory
    /// (<c>AddHttpClient</c>), as <c>AddPersistence</c> does. The registration of the section is additive: a host
    /// that ran 06.3.1-T1's <c>AddYouTubePublishing</c> through <c>AddPersistence</c> keeps that single binding
    /// and its single start-up validation, and only the validators of the two tasks are both registered.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The host configuration that carries the <c>YouTube</c> section.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddYouTubePublishing(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // 06.3.1-T1 binds and validate-on-starts `YouTube` from `AddPersistence`, and the Api runs both that and
        // this method. Binding must not happen twice: the guard keeps a second configure action out (two binds
        // would read the same section into the same instance anyway). Start-up validation is added
        // unconditionally: `ValidateOnStart` records one entry per (options type, name) in
        // `StartupValidatorOptions`, so the second call for this type is the same entry, while skipping it
        // whenever some *other* options type of the host already registered an `IStartupValidator` would leave
        // this section unvalidated.
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IConfigureOptions<YouTubeOptions>)))
        {
            services.AddOptions<YouTubeOptions>()
                .BindConfiguration(YouTubeOptions.SectionName);
        }

        services.AddOptions<YouTubeOptions>().ValidateOnStart();

        // A different implementation type from 06.3.1-T1's quota validator, so both stay registered and a single
        // start-up validation runs them both.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<YouTubeOptions>, YouTubeOptionsValidation>());

        services.TryAddSingleton<ISecretProvider, ConfigurationSecretProvider>();
        services.TryAddSingleton<IOAuthClientResolver, ConfigOAuthClientResolver>();
        services.TryAddSingleton<IYouTubeClient, GoogleYouTubeClient>();

        // Both read the request scope: the claim runs on the scope's connection inside its unit of work, and the
        // family read goes through the scoped session store (09.3.2-T1).
        services.TryAddScoped<IOAuthStateStore, EfOAuthStateStore>();
        services.TryAddScoped<ISessionFamilyReader, SessionFamilyReader>();

        // The form posts: the timeout is the configured one, the transport is shared with the API client below, and
        // the buffered body is bounded. A token answer is a few hundred bytes, so a compromised or misconfigured
        // token endpoint must not be able to make the callback buffer an arbitrary body inside that timeout.
        services.AddHttpClient(GoogleYouTubeClient.OAuthHttpClientName, (provider, client) =>
            {
                var http = provider.GetRequiredService<IOptions<YouTubeOptions>>().Value.Http;

                client.Timeout = TimeSpan.FromSeconds(http.TimeoutSeconds);
                client.MaxResponseContentBufferSize = GoogleYouTubeClient.MaxResponseContentBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(CreateTransport);

        // The Google Data API client's handler chain. Its own timeout is unused — the SDK's
        // ConfigurableHttpClient carries `YouTube:Http:TimeoutSeconds` (see GoogleYouTubeClient).
        services.AddHttpClient(GoogleYouTubeClient.ApiHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(CreateTransport);

        return services;
    }

    /// <summary>
    /// The primary handler of both named clients: no automatic redirect — an OAuth answer or a Data API payload
    /// must never be replayed to another host, and the token endpoints answer in place — a pooled connection
    /// lifetime, and transparent gzip, which the Google client asks for.
    /// </summary>
    private static HttpMessageHandler CreateTransport() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    };
}

/// <summary>
/// Fails the start-up on an invalid <c>YouTube</c> section (06.1.2-T1), with every problem listed: the configured
/// client, its scopes and the scheme of every endpoint decide whether a connect can work at all.
/// </summary>
/// <remarks>
/// The value rules are Application's (<see cref="YouTubeOptionsValidator"/>); this adapter carries the one rule that
/// needs the host — outside Development every endpoint must be <c>https</c> — exactly as <c>BudgetOptionsValidation</c>
/// does for <c>Budget:Enabled</c>. <c>RedirectUri</c> is deliberately not scheme-checked: it must equal the URI
/// registered on that environment's Google client (06.1.1-T1) and the committed development value is a loopback
/// <c>http</c> one, while the four endpoints below are Google's own addresses (https in every environment). The message
/// names the key and the rule only: an endpoint is configuration, so no value of it reaches a failure text
/// (06 README D17).
/// </remarks>
internal sealed class YouTubeOptionsValidation(IHostEnvironment environment) : IValidateOptions<YouTubeOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, YouTubeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = YouTubeOptionsValidator.Validate(options).ToList();

        if (!environment.IsDevelopment())
        {
            problems.AddRange(
                Endpoints(options)
                    .Where(endpoint => !endpoint.Value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    .Select(endpoint =>
                        $"'{YouTubeOptions.SectionName}:{endpoint.Key}' must be an https URL outside Development."));
        }

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }

    private static IReadOnlyList<KeyValuePair<string, string>> Endpoints(YouTubeOptions options) =>
    [
        new(nameof(YouTubeOptions.AuthorizationEndpoint), options.AuthorizationEndpoint),
        new(nameof(YouTubeOptions.TokenEndpoint), options.TokenEndpoint),
        new(nameof(YouTubeOptions.RevokeEndpoint), options.RevokeEndpoint),
        new(nameof(YouTubeOptions.ApiBaseUrl), options.ApiBaseUrl),
    ];
}
