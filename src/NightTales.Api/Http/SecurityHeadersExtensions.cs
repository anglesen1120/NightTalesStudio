using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace NightTales.Api.Http;

/// <summary>
/// The Api's safe defaults of 09 README BD12 (09.9.4-T1): the hardening response headers, the
/// docs-page CSP, the CORS allow-list restricted to <c>App:PublicOrigin</c>, and the request-body cap.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is on in every environment, including Development — safe defaults are not a
/// production-only posture. Only the two HTTPS mechanisms are environment-gated: HSTS and HTTPS
/// redirection are off in Development, because the daily dev path is plain HTTP on
/// <c>http://localhost:4200</c>. Behind the 11 TLS terminator the app also has to trust the forwarded
/// protocol, which is BD14's <c>ForwardedHeadersOptions.KnownNetworks</c> (09.3.5-T1) and deliberately
/// not duplicated here; that middleware must run before this one for HTTPS redirection to see the
/// original scheme.
/// </para>
/// <para>
/// The headers are written twice on purpose: once before the handler runs, so a response that never
/// reaches the response-start callback still carries them, and once from <c>OnStarting</c>, because the
/// exception handler replaces the response it catches (<c>Response.Clear()</c> drops headers written
/// earlier) and that must not remove the hardening headers from an error response. The CSP is only ever
/// added by the second write: before the handler runs the response has no content type yet, and the CSP
/// belongs to the HTML docs surface alone.
/// </para>
/// </remarks>
public static class SecurityHeadersExtensions
{
    /// <summary>The CORS policy of the versioned API group: no credentials.</summary>
    public const string SpaCorsPolicy = "spa";

    /// <summary>The CORS policy of <c>/api/v1/auth/*</c>: the refresh cookie needs credentials (BD3).</summary>
    public const string AuthCorsPolicy = "spa-auth";

    private const string ContentTypeOptionsHeader = "X-Content-Type-Options";
    private const string ReferrerPolicyHeader = "Referrer-Policy";
    private const string FrameOptionsHeader = "X-Frame-Options";
    private const string ContentSecurityPolicyHeader = "Content-Security-Policy";

    /// <summary>The docs page's CSP (BD12): the Api documents itself and embeds nothing else.</summary>
    private const string DocsContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'; img-src 'self' data:";

    /// <summary>The request body cap of every endpoint (BD12), unless the endpoint asks for its own.</summary>
    private const long OneMebibyte = 1024 * 1024;

    private static readonly string[] AllowedMethods = ["GET", "POST", "PATCH", "DELETE"];

    // `Authorization` is on both policies because the bearer token travels in that header: without it the
    // preflight of every authenticated call would be refused [RULED — README §B10 WT42].
    private static readonly string[] AllowedRequestHeaders =
        ["Authorization", "Content-Type", "If-Match", "Idempotency-Key"];

    /// <summary>
    /// Registers the security options of BD12: the public origin (CORS and the cookie-auth
    /// <c>Origin</c> check), the request-body cap, the HSTS policy and the two CORS policies.
    /// </summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configuration">The host configuration (<c>App:*</c>, <c>Kestrel:Limits:*</c>).</param>
    public static IServiceCollection AddNightTalesSecurityHardening(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // 09 README BD3/BD16: `App:PublicOrigin` is the one browser origin of this deployment. It is
        // required, absolute, https, and carries no trailing slash, path, query or fragment; in
        // Development an http loopback origin is also accepted (10 CR-3), because that is the daily dev
        // path. It is validated on start, so a broken browser flow cannot ship silently.
        services.AddOptions<AppOptions>()
            .BindConfiguration(AppOptions.SectionName)
            .Validate<IHostEnvironment>(
                (options, environment) => AppOptions.TryNormalizeOrigin(
                    options.PublicOrigin,
                    environment.IsDevelopment(),
                    out _),
                AppOptions.InvalidOriginFailure)
            .ValidateOnStart();

        services.AddOptions<RequestLimitsOptions>()
            .BindConfiguration(RequestLimitsOptions.SectionName)
            .Validate(
                options => options.MaxRequestBodySize is > 0 and <= RequestLimitsOptions.MaxAllowedRequestBodySize,
                $"'{RequestLimitsOptions.SectionName}:{nameof(RequestLimitsOptions.MaxRequestBodySize)}' must be in "
                + $"(0, {RequestLimitsOptions.MaxAllowedRequestBodySize}] bytes (BD12).")
            .ValidateOnStart();

        // The cap is applied to the server, not only to the middleware below, so Kestrel refuses an
        // oversized body while it is still being read — including a chunked one, whose length is unknown
        // until the last byte. Both read the same options instance, so they cannot disagree.
        services.AddOptions<KestrelServerOptions>()
            .Configure<IOptions<RequestLimitsOptions>>(
                (kestrel, limits) => kestrel.Limits.MaxRequestBodySize = limits.Value.MaxRequestBodySize);

        // BD12's "HTTPS only + HSTS" with a one-year max age covering the subdomains. The header is only
        // ever sent over HTTPS and never for the framework's excluded hosts (localhost/127.0.0.1).
        services.Configure<HstsOptions>(options =>
        {
            options.MaxAge = TimeSpan.FromDays(365);
            options.IncludeSubDomains = true;
        });

        services.AddCors();
        services.AddOptions<CorsOptions>()
            .Configure<IOptions<AppOptions>, IHostEnvironment>((cors, app, environment) =>
                AddNightTalesPolicies(cors, app.Value.PublicOrigin, environment.IsDevelopment()));

        return services;
    }

    /// <summary>Adds the hardening pipeline: HTTPS policy outside Development, headers, CORS, body cap.</summary>
    /// <param name="app">The host's application builder.</param>
    public static WebApplication UseNightTalesSecurityHardening(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!app.Environment.IsDevelopment())
        {
            // HSTS first: it is a response header, so anything later may still add to the response.
            app.UseHsts();
            app.UseHttpsRedirection();
        }

        app.Use(HardeningHeaders);

        // CORS runs before the body cap so a refused request still carries its CORS headers: the limiter
        // short-circuits with 413 (below), and a browser on `App:PublicOrigin` has to be able to read that
        // answer — if the cap ran first the CORS middleware would never execute for the refused request and
        // the response would have no `Access-Control-Allow-Origin`. Routing has already run (WebApplication
        // inserts it at the head of the pipeline), so the endpoint's own `RequireCors` metadata is known
        // here: the versioned API group carries `spa`, `/api/v1/auth/*` carries `spa-auth` (credentials are
        // only allowed there).
        app.UseCors();

        // The factory runs when the pipeline is built — that is, after start-up validation has accepted the
        // options — so an invalid cap fails the host with its fixed message and never on a request. The cap
        // stays before the endpoints, so an oversized body is still refused before the handler runs.
        app.Use(next =>
        {
            var defaultLimit = app.Services.GetRequiredService<IOptions<RequestLimitsOptions>>().Value.MaxRequestBodySize;

            return LimitRequestBody(next, defaultLimit);
        });

        return app;
    }

    private static async Task HardeningHeaders(HttpContext context, RequestDelegate next)
    {
        ApplyHardeningHeaders(context.Response);
        context.Response.OnStarting(() =>
        {
            ApplyHardeningHeaders(context.Response);

            return Task.CompletedTask;
        });

        await next(context).ConfigureAwait(false);
    }

    private static void ApplyHardeningHeaders(HttpResponse response)
    {
        response.Headers[ContentTypeOptionsHeader] = "nosniff";
        response.Headers[ReferrerPolicyHeader] = "no-referrer";
        response.Headers[FrameOptionsHeader] = "DENY";

        // The Api's only HTML surface is the documentation page (OpenAPI/Swagger), so an HTML answer is
        // the docs page: it gets the CSP, an ordinary JSON answer never does (BD12).
        if (response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
        {
            response.Headers[ContentSecurityPolicyHeader] = DocsContentSecurityPolicy;
        }
    }

    private static RequestDelegate LimitRequestBody(RequestDelegate next, long defaultLimit) => async context =>
    {
        // The endpoint's own metadata wins: an endpoint that accepts a larger body (a render command with a
        // large script) declares it with `RequestSizeLimitAttribute`, and the server-level cap reads the
        // same metadata, so the two agree.
        var limit = context.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize
            ?? defaultLimit;

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = limit;
        }

        if (context.Request.ContentLength is { } declared && declared > limit)
        {
            // Refused before the handler and before any body byte is read: no ProblemDetails (the SPA maps
            // 413 generically, 10 D9), no allocation proportional to the refused body.
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;

            return;
        }

        await next(context).ConfigureAwait(false);
    };

    private static void AddNightTalesPolicies(CorsOptions cors, string? publicOrigin, bool isDevelopment)
    {
        // A host with an absent or invalid origin fails start-up (the validation above); this only has to
        // be safe in the moments before that, so an origin no browser can present is used and the policy
        // can still be built.
        var origin = AppOptions.TryNormalizeOrigin(publicOrigin, isDevelopment, out var normalized) ? normalized : null;

        cors.AddPolicy(SpaCorsPolicy, policy => Configure(policy, origin, withCredentials: false));
        cors.AddPolicy(AuthCorsPolicy, policy => Configure(policy, origin, withCredentials: true));
    }

    private static void Configure(CorsPolicyBuilder policy, string? origin, bool withCredentials)
    {
        if (origin is null)
        {
            policy.SetIsOriginAllowed(_ => false);
        }
        else
        {
            policy.WithOrigins(origin);
        }

        policy.WithMethods(AllowedMethods).WithHeaders(AllowedRequestHeaders);

        if (withCredentials)
        {
            // Only `/api/v1/auth/*`: the refresh cookie must travel on the refresh and logout calls (BD3).
            policy.AllowCredentials();
        }
    }
}
