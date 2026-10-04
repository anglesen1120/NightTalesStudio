using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NightTales.Api.Endpoints.Internal;
using NightTales.Api.Internal.Fetcher;
using NightTales.Api.Security;
using NightTales.Infrastructure.DependencyInjection;

namespace NightTales.Api.DependencyInjection;

/// <summary>
/// The composition of the fetcher's internal lease API (02.5.2-T2, 02 D10): the options and their start-up check, the
/// token scheme, the lease and storage services, the second Kestrel endpoint and the isolated pipeline behind it.
/// </summary>
public static class FetcherInternalServiceCollectionExtensions
{
    /// <summary>
    /// Binds and validates <c>Fetcher:Internal</c> (failing the boot when it is enabled without
    /// <c>FETCHER_API_TOKEN</c>) and registers the <c>FetcherToken</c> scheme and everything the three routes use. The
    /// host must also have called <c>AddSources</c>, <c>AddStorage</c> and the telemetry registration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The host configuration.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddFetcherInternalApi(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<FetcherInternalOptions>()
            .Bind(configuration.GetSection(FetcherInternalOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<FetcherInternalOptions>, FetcherInternalOptionsValidator>());
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<FetcherInternalOptions>>().Value);

        services.TryAddSingleton<FetcherTokenVerifier>();
        services.TryAddSingleton<FetcherRateLimiter>();
        services.TryAddSingleton<FetcherResultRunner>();
        services.AddFetcherLeaseApi();

        // No default scheme: this only adds a named scheme the internal pipeline asks for explicitly, so the public
        // pipeline's JWT default is untouched.
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, FetcherTokenAuthenticationHandler>(
                FetcherTokenAuthenticationHandler.SchemeName,
                configureOptions: null);

        return services;
    }

    /// <summary>
    /// Declares the Kestrel endpoints when the internal listener is enabled. A code-configured endpoint makes Kestrel
    /// ignore <c>ASPNETCORE_URLS</c> and <c>ASPNETCORE_HTTP_PORTS</c>, so the public listener is declared here too, from
    /// <c>Kestrel:Endpoints</c> when that section exists and otherwise from the same <c>urls</c> / <c>HTTP_PORTS</c>
    /// values Kestrel would have used — minus the internal port, which only the internal pipeline may serve (11 CR-16).
    /// Disabled, it changes nothing.
    /// </summary>
    /// <param name="builder">The web application builder.</param>
    /// <returns>The same builder.</returns>
    public static WebApplicationBuilder ConfigureFetcherInternalListener(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Configuration.GetSection(FetcherInternalOptions.SectionName).Get<FetcherInternalOptions>();
        if (options is not { Enabled: true } || !IPAddress.TryParse(options.ListenAddress, out var address))
        {
            // Disabled needs nothing; an address that does not parse is reported by the options validator at start-up.
            return builder;
        }

        builder.WebHost.ConfigureKestrel((context, kestrel) =>
        {
            var configured = context.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any();
            if (!configured)
            {
                foreach (var listen in PublicListeners(context.Configuration, options.ListenPort))
                {
                    listen(kestrel);
                }
            }

            kestrel.Listen(address, options.ListenPort);
        });

        return builder;
    }

    /// <summary>
    /// Routes every request that arrived on the internal port to the isolated pipeline of
    /// <see cref="FetcherEndpoints"/> and nothing else, before any public middleware runs; the public pipeline never
    /// sees these routes. A no-op when the listener is disabled.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <returns>The same application.</returns>
    public static WebApplication UseFetcherInternalListener(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.Services.GetRequiredService<FetcherInternalOptions>();
        if (!options.Enabled)
        {
            return app;
        }

        var port = options.ListenPort;
        app.MapWhen(
            context => context.Connection.LocalPort == port,
            branch => branch.Run(FetcherEndpoints.HandleAsync));

        return app;
    }

    /// <summary>The listeners Kestrel would have bound from <c>urls</c> / <c>HTTP_PORTS</c>, without the internal port.</summary>
    private static List<Action<KestrelServerOptions>> PublicListeners(IConfiguration configuration, int internalPort)
    {
        var listeners = new List<Action<KestrelServerOptions>>();

        if (!string.IsNullOrWhiteSpace(configuration["HTTPS_PORTS"]))
        {
            throw new InvalidOperationException(
                "HTTPS_PORTS is not supported together with Fetcher:Internal:Enabled; declare TLS endpoints under Kestrel:Endpoints.");
        }

        var urls = configuration["urls"];
        if (!string.IsNullOrWhiteSpace(urls))
        {
            foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var uri = ParseUrl(url);
                if (uri.Port != internalPort)
                {
                    listeners.Add(Listen(uri.Host, uri.Port));
                }
            }

            return listeners;
        }

        var ports = configuration["HTTP_PORTS"];
        if (!string.IsNullOrWhiteSpace(ports))
        {
            foreach (var text in ports.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(text, out var port) || port is < 1 or > 65_535)
                {
                    throw new InvalidOperationException("HTTP_PORTS must be a ';'-separated list of ports.");
                }

                if (port != internalPort)
                {
                    listeners.Add(Listen("0.0.0.0", port));
                }
            }

            return listeners;
        }

        // Kestrel's own default when nothing is configured.
        listeners.Add(Listen("localhost", 5000));

        return listeners;
    }

    private static Uri ParseUrl(string url)
    {
        var normalised = url.Replace("://+", "://0.0.0.0", StringComparison.Ordinal)
            .Replace("://*", "://0.0.0.0", StringComparison.Ordinal);
        if (!Uri.TryCreate(normalised, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp
            || uri.Port < 1)
        {
            throw new InvalidOperationException(
                "With Fetcher:Internal:Enabled the public urls must be plain http://host:port addresses; declare TLS endpoints under Kestrel:Endpoints.");
        }

        return uri;
    }

    private static Action<KestrelServerOptions> Listen(string host, int port) => kestrel =>
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            kestrel.ListenLocalhost(port);
        }
        else if (IPAddress.TryParse(host.Trim('[', ']'), out var address))
        {
            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                kestrel.ListenAnyIP(port);
            }
            else
            {
                kestrel.Listen(address, port);
            }
        }
        else
        {
            throw new InvalidOperationException(
                "With Fetcher:Internal:Enabled the public urls must name localhost or an IP address.");
        }
    };
}
