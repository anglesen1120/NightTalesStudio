using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Connections;
using NightTales.Api;
using NightTales.Api.DependencyInjection;
using NightTales.Api.Endpoints;
using NightTales.Api.Http;
using NightTales.Api.RateLimiting;
using NightTales.Api.Realtime;
using NightTales.Application.DependencyInjection;
using NightTales.Infrastructure.DependencyInjection;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Seeding;
using NightTales.Infrastructure.Storage;

var builder = WebApplication.CreateBuilder(args);

// 09.1.5-T1 (09 README D14): registers the OpenAPI document provider the build-time generator needs.
builder.Services.AddOpenApi();

builder.Services.AddNightTalesMediator();
builder.Services.AddPersistence(builder.Configuration);

// 09.4.1-T1 (09 §4 09.4.1): the series routes need the Application defaults — the 02 story-preset
// port, whose wave-1 null catalog knows no key — and the post-commit ETag read of 09 README D15.
builder.Services.AddNightTalesApplication();
builder.Services.AddScoped<PostCommitETagReader>();

// 09.2.3-T1 (09 README D10): the caller's claims are the tenant of every filtered query, and the
// scoped system context lets tests run outbox-style work in-process.
builder.Services.AddApiCurrentUser();

// 09.4.2-T1 (09 README D14, plan §3/§5): the two catalog routes need the public bucket base URL (the
// Worker registers the same builder) and their own cache max-age.
builder.Services.AddPublicAssetUrl();
builder.Services.AddOptions<CatalogOptions>()
    .BindConfiguration(CatalogOptions.SectionName)
    .Validate(
        options => options.PublicCacheSeconds > 0,
        $"'{CatalogOptions.SectionName}:{nameof(CatalogOptions.PublicCacheSeconds)}' must be greater than zero.")
    .ValidateOnStart();

// 09.3.2-T1 (09 README BD1/BD3): the ES256 issuer, the refresh-token store and the credential flows,
// plus their start-up validation — a host that cannot sign a correct token must not serve traffic. The
// Api registers them (not AddPersistence): the Worker has no web signing key and must not need one.
builder.Services.AddNightTalesAuth(builder.Configuration);

// 09.3.2-T1 step 6: the bearer scheme over the same key.
builder.Services.AddNightTalesJwtBearer();

// 09.9.4-T1 (09 README BD12/WF15): the Data Protection key ring persisted in PostgreSQL and encrypted
// with the DP_KEK certificate, the purpose-scoped token protector over it, and the platform's safe
// defaults — the hardening headers, the CORS allow-list reduced to `App:PublicOrigin`, HSTS and the
// 1 MiB request-body cap. Both fail start-up on a missing KEK or an invalid public origin.
builder.Services.AddNightTalesDataProtection(builder.Configuration);
builder.Services.AddNightTalesSecurityHardening(builder.Configuration);

// 09.3.5-T1 (09 README BD14/BD16, W8): the four rate-limit policies (`auth`, `paid`, `hub`, `anon`),
// their forwarded-header trust (the address the limiter partitions on is one a configured proxy
// declared) and the two named outbound clients of 09 W3 CR-7. The limits are configuration with the
// BD14 defaults, and the policy names are the ones the endpoint owners pass to `RequireRateLimiting`.
builder.Services.AddRateLimiting(builder.Configuration);

builder.Services.AddProblemDetails(ProblemDetailsWriter.ConfigureProblemDetails);

// 09 README D14: enums travel snake_case over the wire. 09.3.1-T1's register body is the first route
// that needs it (`consents[].document` ∈ `tos | privacy`, 10-web-ui SC30), so the converter is
// registered on the minimal-API JSON options. Integer enum values stay accepted for reading; nothing
// serializes an enum as a number.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)));

// 09 README D12/H4: the Api only writes the outbox, so an accidental direct enqueue fails fast.
builder.Services.AddApiJobScheduler();

// 09.8.1-T1 (09 README BD11): the episode hub and its `HubTicket` scheme, the per-instance connection
// registry and the one LISTEN loop of this Api instance. The production IHubTicketValidator arrives with
// 09.8.1-T2, so until then a connect attempt fails fast instead of opening an anonymous socket.
builder.Services.AddNightTalesRealtime(builder.Configuration);

var app = builder.Build();

// 09.2.5-T1: the explicit `nts-seed` command (plan §7). It runs the seeders of the same graph in one
// scope and exits without starting the web server; `Seed:ContinueOnError` stays false, so a failure is
// a non-zero exit. The Development auto-seed is the hosted service AddPersistence registered.
if (args.Contains("--seed", StringComparer.Ordinal))
{
    Environment.ExitCode = SeedCommand.Run(app.Services);

    return;
}

app.UseExceptionHandler();

// 09.3.5-T1 (09 README BD14): the trust boundary first — the forwarded-headers middleware rewrites the
// connection's address and scheme from `X-Forwarded-For`/`X-Forwarded-Proto`, and only for a peer inside
// a configured network, so the address every later decision uses is one the load balancer vouched for.
// It must precede the hardening middleware, whose HTTPS redirection reads the declared scheme.
app.UseForwardedHeaderTrust();

// 09.9.4-T1 (BD12): HSTS and HTTPS redirection outside Development, the hardening headers (also on an
// error response), the CORS policies and the request-body cap. It runs before authorization, so an
// oversized body is refused before the caller is authenticated; the CORS middleware runs before the cap,
// so the 413 refusal still carries its CORS headers.
app.UseNightTalesSecurityHardening();

// 09.3.5-T1 (09 README BD14/BD16): the limiter. It runs after the host's own authentication middleware
// (the ASP.NET Core host inserts it ahead of the whole user pipeline) and after the CORS middleware, so a
// user-keyed policy sees the caller's `sub` and a 429 still carries the headers a browser needs to read
// it (10-web-ui L107–108). A rejected request never reaches an endpoint, so it opens no transaction and
// writes no row.
app.UseRateLimiting();

// 09.3.5-T1 (09 README BD16): no rate policy, by design — the limiter gives an endpoint without
// `RequireRateLimiting` a pass-through partition, so a probe fleet that shares one address can never
// exhaust a shared bucket, and the generated OpenAPI document (09.1.5-T1 writes it at build, no route is
// mapped for it) is not limited either. Both are cheap, cache-only reads.
app.MapGet("/health/live", () => Results.Ok());

// 09.3.1-T1 (09 README D14): the auth route group; in P it carries POST /api/v1/auth/register, which
// answers 403 registration_closed until 09.3.1-T3 swaps the registration flow.
app.MapAuthEndpoints();

// 09.4.1-T1 (09 README D14, 09 §4 09.4.1): the series route group.
app.MapSeriesEndpoints();

// 09.5.1-T1 (09 README D14, 09 §4 09.5.1): the episode routes — create under a series, list, read and
// cancel.
app.MapEpisodeEndpoints();

// 09.4.2-T1 (09 README D14, 09 §4 09.4.2): the catalog routes — the voices and the style templates the
// picker and the style grid read.
app.MapCatalogEndpoints();

// 09.8.1-T2 (09 README D14, BD11): the ticket exchange. The client POSTs its access token here, gets a
// 60-second single-use ticket and connects with `?ticket=…`, so the JWT never travels in a URL.
app.MapHubEndpoints();

// 09.8.1-T1 (09 README BD11, 09-backend-api L214): the hub of 09.8.1 — WebSockets only, so the client
// skips negotiation and the JWT never travels in the URL (the single-use ticket does, and it dies at
// connect).
app.MapHub<EpisodeHub>(EpisodeHub.Path, options => options.Transports = HttpTransportType.WebSockets);

app.Run();

public partial class Program;
