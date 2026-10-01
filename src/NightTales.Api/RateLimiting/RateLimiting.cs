using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Api.Http;

// `Microsoft.AspNetCore.HttpOverrides` also declares an `IPNetwork` (the obsolete predecessor of the
// framework's own type), so the trust lists name the BCL one explicitly.
using IPNetwork = System.Net.IPNetwork;

namespace NightTales.Api.RateLimiting;

/// <summary>
/// Registration and pipeline placement of the four rate-limit policies (09.3.5-T1, 09 README BD14/BD16,
/// W8, 09 W3 CR-7): the policies themselves, the trust boundary that decides what the limiter treats as
/// the caller's address, and the named outbound clients of the same change request.
/// </summary>
/// <remarks>
/// <para>
/// <c>AddRateLimiting</c> is the whole registration surface: policies, options and their start-up
/// validation, the forwarded-header trust, the port that resolves the caller's address, the rejection
/// callback and its counter. <c>UseForwardedHeaderTrust</c> and <c>UseRateLimiting</c> place the two
/// middlewares; they are separate because the trust boundary has to run before the hardening middleware
/// (which redirects to HTTPS based on the scheme the proxy declared) while the limiter has to run after
/// it, so a 429 still carries the CORS headers a browser needs to read it (10-web-ui L107–108 shows the
/// countdown from <c>Retry-After</c>).
/// </para>
/// <para>
/// The ASP.NET Core host inserts <c>UseAuthentication</c> ahead of the whole user pipeline, so the
/// limiter runs after it and a user-keyed partition sees the authenticated <c>sub</c> — that ordering is
/// the host's, not a call this file makes (09.3.5-T1 design, "the limiter must run after
/// authentication").
/// </para>
/// <para>
/// The partition store is the framework's own (<c>PartitionedRateLimiter</c>): it reclaims an idle
/// partition after ten seconds of disuse, verified against the .NET 10 source at implementation time, so
/// no size-limited store is wrapped around it and no <c>MaxPartitions</c>-style option is claimed
/// ([RULED — README §B10 WT48]).
/// </para>
/// </remarks>
public static class RateLimiting
{
    /// <summary>
    /// Registers the policies, their options and start-up validation, the forwarded-header trust, the
    /// address resolver, the rejection callback with its counter, and the two named outbound clients of
    /// 09 W3 CR-7.
    /// </summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configuration">The host configuration; the <c>RateLimiting</c> and <c>Http:ForwardedHeaders</c> sections are read from it.</param>
    public static IServiceCollection AddRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RateLimitingOptions>()
            .BindConfiguration(RateLimitingOptions.SectionName)
            .Validate(
                options => options.HasPositivePermitLimits(),
                RateLimitingOptions.NonPositivePermitLimitFailure)
            .Validate(
                options => options.HasWindowsAtLeastOneSecond(),
                RateLimitingOptions.TooShortWindowFailure)
            .Validate(
                options => options.RetryAfterCoversEveryWindow(),
                RateLimitingOptions.RetryAfterShorterThanWindowFailure)
            .Validate(
                options => options.HasValidSegmentsPerWindow(),
                RateLimitingOptions.InvalidSegmentsPerWindowFailure)
            .Validate(
                options => options.HasValidIpv6PrefixLength(),
                RateLimitingOptions.InvalidIpv6PrefixLengthFailure)
            .ValidateOnStart();

        services.TryAddSingleton<RateLimitMetrics>();
        services.TryAddSingleton<IClientIpResolver, ClientIpResolver>();
        services.TryAddSingleton(provider => new RateLimitRejection(
            provider.GetRequiredService<IOptions<RateLimitingOptions>>().Value,
            provider.GetRequiredService<RateLimitMetrics>(),
            provider.GetRequiredService<IClientIpResolver>(),
            provider.GetRequiredService<ILogger<RateLimitRejection>>()));

        services.AddSingleton<IConfigureOptions<RateLimiterOptions>, RateLimiterOptionsSetup>();
        services.AddRateLimiter(static _ => { });

        AddForwardedHeaderTrust(services, configuration);
        services.AddNightTalesOutboundClients();

        return services;
    }

    /// <summary>
    /// Places the forwarded-headers middleware (BD14): it rewrites the connection's address and scheme
    /// from <c>X-Forwarded-For</c>/<c>X-Forwarded-Proto</c>, and only for a peer inside a configured
    /// network or proxy, so the address the limiter partitions on is one the load balancer vouched for.
    /// </summary>
    /// <remarks>
    /// <c>X-Forwarded-Host</c> is never trusted (host-header injection), which is why the options enable
    /// exactly two headers. The middleware must run before the hardening middleware, whose HTTPS
    /// redirection reads the scheme the proxy declared.
    /// </remarks>
    /// <param name="app">The host's application builder.</param>
    public static IApplicationBuilder UseForwardedHeaderTrust(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseForwardedHeaders();
    }

    /// <summary>
    /// Places the limiter middleware (BD14). It runs after the hardening middleware so a 429 carries the
    /// CORS headers of the endpoint's policy, and before every endpoint, so a rejected request reaches no
    /// handler, opens no transaction and writes no row (09.3.5-T1 failure handling).
    /// </summary>
    /// <param name="app">The host's application builder.</param>
    public static IApplicationBuilder UseRateLimiting(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseRateLimiter();
    }

    /// <summary>
    /// Binds the forwarded-header trust of <c>Http:ForwardedHeaders</c> and validates the two lists at
    /// start-up. An empty list is the deliberate fail-closed configuration in Staging and Production (no
    /// declared proxy ⇒ the headers are ignored and the peer's own address is used); in Development the
    /// loopback peer is trusted so a local proxy can be simulated.
    /// </summary>
    /// <remarks>
    /// The fail-closed case cannot be left to the empty lists alone: ASP.NET Core's
    /// <c>ForwardedHeadersMiddleware</c> treats both lists empty as "check no sender", i.e. trust every
    /// peer's headers, which is the opposite of the intent. The final configuration step therefore turns
    /// the headers off outright when nothing ended up trusted (09 README BD14/D17, SEC-0935-01).
    /// </remarks>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configuration">The host configuration.</param>
    private static void AddForwardedHeaderTrust(IServiceCollection services, IConfiguration configuration)
    {
        var networkEntries = ReadEntries(configuration, ForwardedHeaderTrust.KnownNetworksKey);
        var proxyEntries = ReadEntries(configuration, ForwardedHeaderTrust.KnownProxiesKey);

        // Parsed once, at start-up: a typo must fail the boot, not silently leave the proxy untrusted
        // (an unparsable entry is reported by the validators below and its valid siblings are still
        // applied, so the failure message can name the list rather than one position in it).
        var networks = new List<IPNetwork>();
        var networksParsed = ForwardedHeaderTrust.TryParseNetworks(networkEntries, networks);
        var proxies = new List<IPAddress>();
        var proxiesParsed = ForwardedHeaderTrust.TryParseProxies(proxyEntries, proxies);

        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IHostEnvironment>(static (options, environment) =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                options.ForwardLimit = 1;

                // Both framework defaults trust the loopback address; clearing them is what makes an
                // empty configuration fail closed instead of quietly trusting a local process.
                options.KnownProxies.Clear();
                options.KnownIPNetworks.Clear();

                if (environment.IsDevelopment())
                {
                    // The development default of the design: a local proxy on the loopback interface is
                    // the only peer whose headers a developer's host may believe.
                    options.KnownIPNetworks.Add(ForwardedHeaderTrust.LoopbackV4);
                    options.KnownIPNetworks.Add(ForwardedHeaderTrust.LoopbackV6);
                }
            })
            .Configure(options =>
            {
                foreach (var network in networks)
                {
                    options.KnownIPNetworks.Add(network);
                }

                foreach (var proxy in proxies)
                {
                    options.KnownProxies.Add(proxy);
                }
            })
            .Configure(static options =>
            {
                // Runs last, after every list is populated: with no trusted proxy or network there is no
                // sender whose declaration may be believed, so the headers are turned off rather than
                // left to the framework's both-empty fast path, which trusts every sender.
                if (options.KnownProxies.Count == 0 && options.KnownIPNetworks.Count == 0)
                {
                    options.ForwardedHeaders = ForwardedHeaders.None;
                }
            })
            .Validate(_ => networksParsed, ForwardedHeaderTrust.InvalidNetworkEntryFailure)
            .Validate(_ => proxiesParsed, ForwardedHeaderTrust.InvalidProxyEntryFailure)
            .ValidateOnStart();
    }

    /// <summary>Reads one comma-separated configuration value as a list of trimmed, non-empty entries.</summary>
    private static string[] ReadEntries(IConfiguration configuration, string key)
    {
        var value = configuration.GetSection(ForwardedHeaderTrust.SectionName)[key];

        return string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

/// <summary>
/// The forwarded-header trust of the Api (09 README BD14): the two lists of
/// <c>Http:ForwardedHeaders</c> that say which peers may declare a client's address, parsed and
/// validated once at start-up.
/// </summary>
/// <remarks>
/// The section is deliberately not bound to an options type of its own: the framework's
/// <see cref="ForwardedHeadersOptions"/> is what the middleware reads, and the two lists are its two
/// trust inputs. A list holds one comma-separated value, so a deployment's secret or configuration
/// store needs no array support to set it.
/// </remarks>
internal static class ForwardedHeaderTrust
{
    /// <summary>The configuration section of the trust boundary.</summary>
    internal const string SectionName = "Http:ForwardedHeaders";

    /// <summary>The key of the comma-separated CIDR list whose peers may declare <c>X-Forwarded-For</c>.</summary>
    internal const string KnownNetworksKey = "KnownNetworks";

    /// <summary>The key of the comma-separated address list of trusted proxies, without a prefix length.</summary>
    internal const string KnownProxiesKey = "KnownProxies";

    /// <summary>The failure an unparsable entry of the network list produces at start-up.</summary>
    internal const string InvalidNetworkEntryFailure =
        "'Http:ForwardedHeaders:KnownNetworks' must be a comma-separated list of CIDR blocks, "
        + "for example '10.0.0.0/8,::1/128'.";

    /// <summary>The failure an unparsable entry of the proxy list produces at start-up.</summary>
    internal const string InvalidProxyEntryFailure =
        "'Http:ForwardedHeaders:KnownProxies' must be a comma-separated list of IP addresses, "
        + "for example '127.0.0.1,10.0.0.4'.";

    /// <summary>The development default: the whole IPv4 loopback block.</summary>
    internal static readonly IPNetwork LoopbackV4 = new(IPAddress.Loopback, 32);

    /// <summary>The development default: the whole IPv6 loopback block.</summary>
    internal static readonly IPNetwork LoopbackV6 = new(IPAddress.IPv6Loopback, 128);

    /// <summary>
    /// Parses <paramref name="entries"/> as CIDR blocks into <paramref name="target"/>, adding every
    /// entry it can parse and reporting whether they all parsed.
    /// </summary>
    internal static bool TryParseNetworks(IReadOnlyList<string> entries, List<IPNetwork> target)
    {
        var parsed = true;
        foreach (var entry in entries)
        {
            if (IPNetwork.TryParse(entry, out var network))
            {
                target.Add(network);
            }
            else
            {
                parsed = false;
            }
        }

        return parsed;
    }

    /// <summary>
    /// Parses <paramref name="entries"/> as addresses into <paramref name="target"/>, adding every entry
    /// it can parse and reporting whether they all parsed.
    /// </summary>
    internal static bool TryParseProxies(IReadOnlyList<string> entries, List<IPAddress> target)
    {
        var parsed = true;
        foreach (var entry in entries)
        {
            if (IPAddress.TryParse(entry, out var address))
            {
                target.Add(address);
            }
            else
            {
                parsed = false;
            }
        }

        return parsed;
    }
}

/// <summary>
/// Builds the limiter's four policies from <see cref="RateLimitingOptions"/> when the host first resolves
/// them (09.3.5-T1 design).
/// </summary>
/// <remarks>
/// <para>
/// One partitioner per policy: the partition key comes from <see cref="RateLimitPartitionKeys"/>, so the
/// key a request is limited by and the key a rejection logs are produced by the same code. The limiter
/// itself is the framework's fixed- or sliding-window one; <c>QueueLimit</c> is zero, so a request over
/// the limit is refused at once instead of waiting for a permit it does not have.
/// </para>
/// <para>
/// When <see cref="RateLimitingOptions.Enabled"/> is false every policy is registered as a pass-through
/// partition. Registering nothing would be worse than useless: the middleware throws for a policy name a
/// route carries but the host never registered, so an incident switch would turn every limited route into
/// a 500.
/// </para>
/// </remarks>
/// <param name="rateLimiting">The validated limits.</param>
/// <param name="ipResolver">The resolver of the caller's address.</param>
/// <param name="rejection">The callback every rejection runs.</param>
internal sealed class RateLimiterOptionsSetup(
    IOptions<RateLimitingOptions> rateLimiting,
    IClientIpResolver ipResolver,
    RateLimitRejection rejection) : IConfigureOptions<RateLimiterOptions>
{
    private readonly RateLimitingOptions _rateLimiting = rateLimiting.Value;

    /// <inheritdoc />
    public void Configure(RateLimiterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The framework's default is 503; a limited request is refused with the documented 429 (BD14).
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = rejection.WriteAsync;

        foreach (var policy in RateLimitPolicies.All)
        {
            options.AddPolicy(policy, PartitionerFor(policy));
        }
    }

    /// <summary>
    /// One partitioner per policy, built once at setup: the request path only evaluates the key and hands
    /// the cached limiter-options factory to the framework (PERF-P2). The factory still creates a fresh,
    /// mutable options instance for every new partition, which is what keeps two partitions independent.
    /// </summary>
    /// <param name="policy">A name of <see cref="RateLimitPolicies.All"/>.</param>
    private Func<HttpContext, RateLimitPartition<string>> PartitionerFor(string policy)
    {
        if (!_rateLimiting.Enabled)
        {
            return context => RateLimitPartition.GetNoLimiter(PartitionKey(policy, context));
        }

        var policyOptions = _rateLimiting.For(policy);

        if (RateLimitPolicies.IsSliding(policy))
        {
            Func<string, SlidingWindowRateLimiterOptions> slidingFactory = _ => policyOptions.ToSlidingWindowOptions();

            return context => RateLimitPartition.GetSlidingWindowLimiter(PartitionKey(policy, context), slidingFactory);
        }

        Func<string, FixedWindowRateLimiterOptions> fixedFactory = _ => policyOptions.ToFixedWindowOptions();

        return context => RateLimitPartition.GetFixedWindowLimiter(PartitionKey(policy, context), fixedFactory);
    }

    /// <summary>
    /// The key the limiter partitions on, parked on the request so the rejection callback logs the very
    /// key that limited it instead of hashing the address a second time (PERF-P1).
    /// </summary>
    private string PartitionKey(string policy, HttpContext context)
    {
        var partitionKey = RateLimitPartitionKeys.For(
            context,
            ipResolver,
            policy,
            _rateLimiting.Ipv6PrefixLength);

        context.Items[RateLimitPartitionKeys.PartitionKeyItemKey] = partitionKey;

        return partitionKey;
    }
}
