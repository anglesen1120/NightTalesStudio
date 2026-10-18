using System.Text.Json;
using System.Text.Json.Serialization;
using Hangfire;
using Microsoft.AspNetCore.Http.Connections;
using NightTales.Api;
using NightTales.Api.DependencyInjection;
using NightTales.Api.Endpoints;
using NightTales.Api.Endpoints.Publishing;
using NightTales.Api.Endpoints.Research;
using NightTales.Api.Health;
using NightTales.Api.Http;
using NightTales.Api.Internal.Jobs;
using NightTales.Api.RateLimiting;
using NightTales.Api.Realtime;
using NightTales.Application.DependencyInjection;
using NightTales.Infrastructure.DependencyInjection;
using NightTales.Infrastructure.Email;
using NightTales.Infrastructure.OpenAI;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Flags;
using NightTales.Infrastructure.Persistence.Seeding;
using NightTales.Infrastructure.Persistence.Usage;
using NightTales.Infrastructure.Publishing;
using NightTales.Infrastructure.Rendering;
using NightTales.Infrastructure.Storage;
using NightTales.Infrastructure.Telemetry;
using Serilog;
using Serilog.Events;

// 09.9.3-T1: the bootstrap logger writes compact JSON from the first line on, so a start-up failure — a
// missing log salt, an invalid OTLP endpoint — is one JSON event and a non-zero exit, not a runtime trace.
Log.Logger = BootstrapLogging.Create();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // The local tooling already supplies OPENAI_API_KEY. Preserve an explicit canonical setting,
    // otherwise map that same credential into OpenAi:ApiKey; absent credentials still fail validation.
    if (string.IsNullOrWhiteSpace(builder.Configuration["OpenAi:ApiKey"])
        && !string.IsNullOrWhiteSpace(builder.Configuration["OPENAI_API_KEY"]))
    {
        builder.Configuration["OpenAi:ApiKey"] = builder.Configuration["OPENAI_API_KEY"];
    }

    // 09.9.3-T1 (09 README BD12): Serilog (compact JSON on stdout, OTLP sink when `Otlp:Enabled`), OpenTelemetry
    // traces/metrics and the custom metrics; the health checks behind /health/ready.
    builder.AddNtsTelemetry("nts-api");
    builder.Services.AddApiHealth();

    // 09.1.5-T1 (09 README D14): registers the OpenAPI document provider the build-time generator needs.
    // 09.9.5-T1 (09 README D7): the generated `ProblemDetails` component gains the two members its CLR
    // type cannot express (`code`, `traceId`), so the published document — and every client generated
    // from it (10.1.4) — carries the whole error contract of ProblemDetailsWriter.
    // 02.2.2-T2 (QA-2): the `Idempotency-Key` header is published as required with maxLength 128.
    builder.Services.AddOpenApi(options =>
    {
        options.AddSchemaTransformer<ProblemDetailsSchemaTransformer>();
        options.AddOperationTransformer<IdempotencyKeyOperationTransformer>();
    });

    builder.Services.AddNightTalesMediator();
    builder.Services.AddPersistence(builder.Configuration);

    // 09.9.2-T2 (09 README BD13, 09.9.2 plan §5.1): the feature-flag read service — the cached
    // `IFeatureFlags` port the `RequireFeature` route guard resolves, the writer's `NOTIFY` notifier, and this
    // instance's `LISTEN feature_flags_changed` invalidator. The read rate is one row per key per 30 s.
    builder.Services.AddFeatureFlags(builder.Configuration);

    // 02.2.3-T1 (02 README D5/F22): the Api writes stories too, so it loads and validates the same prompt
    // catalog at start-up; an invalid catalog aborts the boot instead of serving a draft built from a
    // prompt that was never released.
    builder.Services.AddPromptCatalog();

    // 02.2.4-T1 (02 README D14): price versions, the usage meter and the worst-case cost estimator.
    builder.Services.AddUsageMetering();

    // 02.3.2-T1 (D12): edited and approved drafts use the real moderation provider, outside generation metering.
    // The Api now requires the same existing OpenAi:ApiKey secret as the Worker; there is no allow-all fallback.
    builder.Services.AddOpenAiContentModerator();

    // 02.4.2-T1 (02 README D2): the FR-02-7 rolling-24 h limit that `GenerateStoryCommand` checks at step 5.
    builder.Services.AddGenerationLimit();

    // 02.3.4-T1 (02 README D21): the story quality checker and its required `Story:Quality` thresholds.
    builder.Services.AddStoryQuality();
    builder.Services.AddSimilarity();
    // 02.3.3-T1 (02 README T29, D8): the true-crime approval gate's attestation version and cited-source
    // floor. The gate runs here, and this host serves `currentStory.attestation`; every key has a default,
    // so the block stays optional (the Worker binds neither the gate nor the story read model).
    builder.Services.AddStoryTrueCrime();
    // 02.5.1-T1 (02 README D8/D10/D11): the story-source caps, the domain blocklist and the public-suffix list
    // behind `AddSourceCommand`; a bad cap or blocklist entry fails the boot.
    builder.Services.AddSources(builder.Configuration);
    // 02.7.2-T1 (02 README D11, F14): the reviewed research-site allow-list, loaded from the embedded file after
    // `AddSources` supplies the public-suffix list and blocklist it checks; a broken file fails the boot.
    builder.Services.AddResearchAllowList();

    // 09.4.1-T1 (09 §4 09.4.1): the series routes need the Application defaults — the 02 story-preset
    // port, whose wave-1 null catalog knows no key — and the post-commit ETag read of 09 README D15.
    builder.Services.AddNightTalesApplication();
    builder.Services.AddScoped<PostCommitETagReader>();

    // 09.7.2-T1: the presenter of GET /episodes/{id}/video-url. Scoped like the read model, the unit of
    // work and the audit writer it composes.
    builder.Services.AddScoped<EpisodeVideoUrlIssuer>();

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

    // 09.2.6-T3 (09 README BD9): the e-mail pipeline. The Api is the producer side — a handler queues through
    // IEmailSender and a caller that must know the outcome reads IEmailDeliveryStatus — while the Worker owns
    // SendEmailJob; both hosts register the same graph so a template or an option cannot drift between them.
    builder.Services.AddEmail(builder.Configuration);

    // 09.7.2-T1 (09 README BD10/BD12): the blob store, its client factory, the health cache and the
    // prober. The Api presigns the video URL; the Worker sweeps the same buckets.
    builder.Services.AddStorage(builder.Configuration);

    // 09.6.2-T1 (09 README BD6, 11 README D13): the node transport and the two render clients, so the Api
    // resolves IRenderClient/IRenderDownloadClient exactly as the Worker does. Node health polling stays a
    // Worker BackgroundService; what the Api composes here is the pinned-TLS transport and the two clients.
    builder.Services.AddRenderNodes(builder.Configuration);
    builder.Services.AddRenderClients(builder.Configuration);

    // 06.1.2-T1 (06 README D11, 06.1 connect plan §Infrastructure): the YouTube OAuth client resolution —
    // the active client's secret read through the secret provider — and the Google authorization-code client
    // the connect and callback handlers resolve, both pinned to the named HttpClient of `YouTube:Http`.
    // The options bind with their validator, so an empty `ActiveClientId`, a `Clients` entry without a
    // `SecretRef` or a scope outside the two URIs fails the boot instead of the first connect.
    builder.Services.AddYouTubePublishing(builder.Configuration);

    // 09.3.2-T1 step 6: the bearer scheme over the same key.
    builder.Services.AddNightTalesJwtBearer();

    // 02.5.2-T2 (02 D10): the fetcher's internal lease API — the `FetcherToken` scheme, the lease/heartbeat/result
    // services, the extracted-text store and the `Fetcher:Internal` options. Enabled without FETCHER_API_TOKEN the
    // host refuses to start, so the listener can never run unauthenticated.
    builder.Services.AddFetcherInternalApi(builder.Configuration);
    builder.ConfigureFetcherInternalListener();

    // 09.10.4-T1 (09 README §B10 WT34): the fallback policy that rejects BD4's enrolment-only token
    // everywhere, the MfaEnrolment policy of the two enrolment routes, and the result handler that answers
    // 404 not_found for a refused request (D10).
    builder.Services.AddNightTalesMfaAuthorization();

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

    // 09.2.6-T2 (09 README D1/D10, 09.2.6 plan §5): the Hangfire dashboard of `/internal/jobs`. The Api
    // reads the Worker's job storage as a client only — no Hangfire server, no recurring reconciliation —
    // and the surface is reachable with the `__Host-nts-jobs` cookie alone, minted by
    // `POST /internal/jobs/login` for an owner or an admin whose re-authentication is inside 09.10.4-T1's
    // window. An owner may act on jobs; an admin only reads; every other caller gets a 404.
    builder.Services.AddJobsDashboard(builder.Configuration);

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

    // 02.5.2-T2 (02 D10, plan §5.2): every request that arrived on the internal port (`Fetcher:Internal:ListenPort`) is
    // served by the fetcher's own pipeline — token scheme, bounded body, per-worker limit, three routes — and never
    // reaches the public middleware below; the public port never maps these routes. A no-op while the listener is
    // disabled.
    app.UseFetcherInternalListener();

    app.UseExceptionHandler();

    // 09.9.3-T1: one structured line per request — the route template (never the raw path or its query), the
    // method, status and duration in milliseconds, the episode id of an episode route and the trace/span ids,
    // so a log line joins its trace. The caller hash is added by the enricher.
    app.UseSerilogRequestLogging(options =>
    {
        options.Logger = app.Services.GetRequiredService<Serilog.ILogger>();
        options.MessageTemplate = "HTTP {requestMethod} {route} responded {statusCode} in {durationMs} ms";
        options.GetMessageTemplateProperties = (http, _, elapsedMs, statusCode) =>
        [
            new LogEventProperty("requestMethod", new ScalarValue(http.Request.Method)),
        new LogEventProperty(
            "route", new ScalarValue((http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched")),
        new LogEventProperty("statusCode", new ScalarValue(statusCode)),
        new LogEventProperty("durationMs", new ScalarValue(elapsedMs)),
        ];
        options.EnrichDiagnosticContext = (diagnostics, http) =>
        {
            if (NightTales.Api.Http.EpisodeIdRouteParameter.ValueOf(http) is { } episodeId)
            {
                diagnostics.Set("episodeId", episodeId);
            }

            var activity = System.Diagnostics.Activity.Current;
            if (activity is not null)
            {
                diagnostics.Set("traceId", activity.TraceId.ToString());
                diagnostics.Set("spanId", activity.SpanId.ToString());
            }
        };
    });

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

    // 09.2.6-T2 (fix): authentication and authorization are called here explicitly, right after the block
    // that installs the CORS middleware. The host otherwise inserts both *ahead of the whole user pipeline*
    // (`WebApplicationBuilder.ConfigureApplication` wires `UseRouting`, `UseAuthentication` and
    // `UseAuthorization` before the source pipeline), which leaves `UseAuthorization` in front of `UseCors`:
    // the CORS preflight (`OPTIONS`) of a route that carries `RequireAuthorization` is then challenged with
    // the platform's 401 before the CORS middleware can answer it, so the browser never sees the `204` with
    // `Access-Control-Allow-Origin`/`Access-Control-Allow-Credentials` a cross-origin caller needs. Only two
    // routes carried both today — `POST /internal/jobs/login` (`spa-auth`) and `POST /api/v1/hubs/ticket`
    // (`spa`, 09.8.1-T2) — which is why the auth group, whose routes are `AllowAnonymous`, never showed it.
    // Calling the two methods here is also what makes the host skip its own insertion: each sets the
    // `__AuthenticationMiddlewareSet`/`__AuthorizationMiddlewareSet` property the host checks. The order is
    // then the documented one: routing, CORS, authentication, authorization.
    app.UseAuthentication();
    app.UseAuthorization();

    // 09.3.5-T1 (09 README BD14/BD16): the limiter. It runs after the authentication and authorization
    // middleware above and after the CORS middleware, so a user-keyed policy sees the caller's `sub` and a
    // 429 still carries the headers a browser needs to read it (10-web-ui L107–108). A rejected request never
    // reaches an endpoint, so it opens no transaction and writes no row.
    app.UseRateLimiting();

    // 09.2.6-T2 (09.2.6 plan §5, README D1/D10): the audit middleware first, then the dashboard's own
    // branch, so the rows can name the operator the filter authenticated and the POSTs are checked against a
    // foreign `Origin` before Hangfire's dispatcher sees them.
    //
    // The branch is entered only for a request routing did not already claim. `UseHangfireDashboard` mounts
    // Hangfire over the whole `/internal/jobs` subtree with a `Map`, which owns every path under it and never
    // falls through, so a request the dashboard declines ends at the branch's own end — where the host
    // answers 500 ("the request reached the end of the pipeline without executing the endpoint") whenever
    // routing had selected a route of that subtree instead. The login route below is such a route: an
    // endpoint, not a dashboard page. Skipping the branch for a selected endpoint lets it run where every
    // other route does; a dashboard page matches no route, so it still reaches Hangfire.
    app.UseJobsDashboardAudit();
    app.MapWhen(
        context => context.GetEndpoint() is null
            && context.Request.Path.StartsWithSegments(
                JobsDashboardAuthentication.DashboardPath,
                StringComparison.OrdinalIgnoreCase),
        jobs => jobs.UseHangfireDashboard(JobsDashboardAuthentication.DashboardPath));

    // 09.3.5-T1 (09 README BD16): no rate policy, by design — the limiter gives an endpoint without
    // `RequireRateLimiting` a pass-through partition, so a probe fleet that shares one address can never
    // exhaust a shared bucket, and the generated OpenAPI document (09.1.5-T1 writes it at build, no route is
    // mapped for it) is not limited either. The probes stay cheap: storage and nodes read in-memory caches, and
    // the database probe is shared for a few seconds behind one in-flight query (09.9.3-T1), so a probe fleet
    // cannot open one connection per call.
    app.MapHealthEndpoints();

    // 09.3.1-T1 (09 README D14): the auth route group; in P it carries POST /api/v1/auth/register, which
    // answers 403 registration_closed until 09.3.1-T3 swaps the registration flow.
    app.MapAuthEndpoints();

    // 09.2.6-T2 (09.2.6 plan §5): the dashboard's login, the only route of that surface that is an
    // endpoint — the dashboard pages themselves come from the Hangfire branch wired above, which steps aside
    // for the requests routing has already claimed for this route.
    app.MapJobsDashboardEndpoints();

    // 09.10.4-T1 (09 README D14, BD4): the /me route group's MFA routes — setup, verify, recovery codes and
    // disable. The enrolment-only token reaches the first two and nothing else (WT34).
    app.MapMeEndpoints();

    // 09.4.1-T1 (09 README D14, 09 §4 09.4.1): the series route group.
    app.MapSeriesEndpoints();

    // 09.5.1-T1 (09 README D14, 09 §4 09.5.1): the episode routes — create under a series, list, read and
    // cancel.
    app.MapEpisodeEndpoints();

    // 02.2.2-T2 (02 README D9, 02.2.2 plan §5.1): POST /api/v1/episodes/{id}/story/generate.
    app.MapStoryEndpoints();
    // 02.5.1-T1 (02 README D8, 02.5.1 plan §5.1): the episode's story-source routes.
    app.MapSourceEndpoints();
    // 02.5.3-T2 (02 README D1/D2/D6, 02.5.3 plan §5):
    // POST /api/v1/episodes/{id}/fact-sheet/generate.
    app.MapFactSheetEndpoints();

    // 02.7.2-T2 (02 README D1/D2/D19, 02.7 plan §5): the episode's source-finder routes — request a search
    // (POST …/sources/find, flag `source_finder`), list the candidates, add one, dismiss one.
    app.MapSourceFinderEndpoints();

    // 02.6.3-T1 (02 README D9, 02.6.3 plan §4 U1/U2): GET/PUT /api/v1/series/{id}/bible, the bible.v1 document
    // behind a required If-Match ("sb-<version>"). Accept/dismiss of a pending proposal is 02.6.3-T2.
    app.MapSeriesBibleEndpoints();

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
    // 09.7.2-T1 (09 README BD10, 09.7.2 plan §5): the presigned read URL of an episode's final video. A GET
    // has no Mediator pipeline, so the issuer (Api/Http, outside Endpoints because of the D8 architecture
    // rule) opens its own transaction around the audit row.
    app.MapEpisodeVideoEndpoints();

    // 09.6.1-T1 (09 README BD5): POST /api/v1/episodes/{id}/render queues one render job for one approved
    // story. The route requires If-Match and Idempotency-Key and carries the paid policy.
    // 09.6.4-T1 (09 README BD8): DELETE /api/v1/render-jobs/{id} cancels one of them — a queued job at once, a
    // submitted/running one by asking the adapter through the Worker's outbox handler. No If-Match, no
    // Idempotency-Key, no rate policy: the route frees money and is idempotent by the row's own status.
    app.MapRenderingEndpoints();

    // 06.3.1-T1 (06 README D9): GET /api/v1/publishing/youtube/eta reports the platform's remaining daily
    // YouTube upload units. The route is gated by the youtube_publish feature flag and needs a session.
    app.MapYouTubeEtaEndpoints();

    // 06.1.2-T1 (06 README D3, 06.1 connect plan §5.1): the channel routes — POST /api/v1/channels/{platform}/connect
    // mints the state/PKCE pair and answers Google's authorization URL, GET /api/v1/channels/{platform}/callback
    // claims that state anonymously and answers one of two fixed relative paths.
    app.MapPublishingEndpoints();

    app.Run();
}
catch (Exception exception) when (exception is not HostAbortedException)
{
    // One JSON event on stdout, flushed in the finally below; the rethrow keeps the runtime's abort (a
    // non-zero exit) and lets an in-process host factory see the original exception.
    BootstrapLogging.ReportFailure(exception);

    throw;
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program;
