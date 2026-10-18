using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Features.Publishing.Channels;
using NightTales.Application.Features.Publishing.Channels.Commands.ConnectChannelConnection;
using NightTales.Application.Publishing;
using NightTales.Infrastructure.Security;

namespace NightTales.Infrastructure.Publishing;

/// <summary>
/// The composition root of the YouTube connect flow (06.1.2-T1) and of the channel-connection store
/// (06.1.2-T2): the <c>YouTube</c> section, the OAuth client resolver, the secret provider, the Google client
/// and the two named transports they use, plus the store, the row lock and the refresh-under-lock token path.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AddChannelConnections"/> carries the 06.1.2-T2 registrations, and
/// <see cref="AddYouTubePublishing"/> calls it, so a host that composes the connect flow gets the connect's
/// writer too: <c>IConnectChannelConnection</c> is no longer a port the host may be missing — the callback
/// handler still resolves it late, which is what keeps the port optional for a harness.
/// </para>
/// <para>
/// It binds no options and adds no named client of its own, so the Worker — which needs the store and the token
/// path for its publishing jobs but composes no OAuth client map — can call
/// <see cref="AddChannelConnections"/> alone.
/// </para>
/// </remarks>
public static class PublishingServiceCollectionExtensions
{
    /// <summary>
    /// Binds and validates <see cref="YouTubeOptions"/> once, then registers <see cref="ISecretProvider"/>
    /// (configuration-backed), <see cref="IOAuthClientResolver"/> (the configured client map),
    /// <see cref="IYouTubeClient"/> (Google) and the named clients <c>youtube-oauth</c>, <c>youtube-api</c> and
    /// <c>youtube-upload</c>.
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

        // 06.1.2-T2: the connection store the callback writes through, the row lock of `channel_connections`
        // and the one refresh-under-lock token path. It needs no section and no named client of its own, so the
        // Worker composes it on its own (see the method's remarks).
        services.AddChannelConnections();

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

        // The resumable session's three calls (06.2.3-T1): the same transport, but no shared timeout — each call
        // arms its own linked deadline from `YouTube:Upload:*`, so one slow chunk cannot spend another's budget —
        // and the same bounded body. The session URI is a capability and it is the request URI, so the framework's
        // own request logging for this client is switched off: a line must never carry it.
        services.AddHttpClient(GoogleYouTubeClient.UploadHttpClientName, (_, client) =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.MaxResponseContentBufferSize = GoogleYouTubeClient.MaxResponseContentBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(CreateTransport);

        services.AddLogging(logging => logging.AddFilter(
            $"System.Net.Http.HttpClient.{GoogleYouTubeClient.UploadHttpClientName}",
            LogLevel.None));

        return services;
    }

    /// <summary>
    /// Registers the channel-connection store of 06.1.2-T2 — the write port the callback resolves
    /// (<see cref="IConnectChannelConnection"/>), the row lock of <c>channel_connections</c> and the
    /// refresh-under-lock token path — all scoped to the request or job scope whose <c>AppDbContext</c> and
    /// transaction they join.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It binds no section and validates no options: the <c>YouTube</c> section, the secret provider and the
    /// Google client belong to <see cref="AddYouTubePublishing"/>, which calls this method. A host that needs
    /// only the store — the Worker's publishing jobs (06.2.3-T2's upload, 06.4.2-T1's token health) — calls this
    /// one on its own, because <c>YouTube:Clients</c> is an Api-only concern the Worker does not carry.
    /// </para>
    /// <para>
    /// <c>IConnectChannelConnection</c> is registered here rather than by 06.1.2-T1, whose callback resolves the
    /// port late (<c>IServiceProvider.GetService</c>): this registration is what makes that call live on a host
    /// that composes the connect flow. The handler is also registered by the Mediator's generated registrations
    /// for its own command, which is a different service type, so neither registration doubles the other.
    /// </para>
    /// <para>
    /// The graph resolves through the scope's <c>AppDbContext</c>, <c>IClock</c> and <c>ITokenProtector</c>
    /// (composed by <c>AddPersistence</c> and <c>AddNightTalesDataProtection</c>); <c>ITokenService</c> also
    /// needs <see cref="IYouTubeClient"/> and <see cref="IOAuthClientResolver"/>, which only
    /// <see cref="AddYouTubePublishing"/> composes. Nothing resolves the store at start-up, so a host that
    /// composes this method first stays bootable: it fails at the first resolution of <c>ITokenService</c>
    /// instead of at its start, which is the step the upload wiring of 06.2.3 adds.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddChannelConnections(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Scoped, like the context they wrap: the callback's writer joins unit #2 (06 CR-22) and the job-side
        // token path joins the transaction its own caller opens or opens one itself.
        services.TryAddScoped<IChannelConnectionLocker, ChannelConnectionLocker>();
        services.TryAddScoped<ITokenService, TokenService>();
        services.TryAddScoped<IConnectChannelConnection, ConnectChannelConnectionHandler>();

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
        new(nameof(YouTubeOptions.UploadBaseUrl), options.UploadBaseUrl),
    ];
}
