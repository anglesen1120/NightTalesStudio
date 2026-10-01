using System.Text.Json;
using System.Text.Json.Serialization;
using NightTales.Api;
using NightTales.Api.DependencyInjection;
using NightTales.Api.Endpoints;
using NightTales.Api.Http;
using NightTales.Application.DependencyInjection;
using NightTales.Infrastructure.DependencyInjection;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Seeding;

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

// 09.3.2-T1 (09 README BD1/BD3): the ES256 issuer, the refresh-token store and the credential flows,
// plus their start-up validation — a host that cannot sign a correct token must not serve traffic. The
// Api registers them (not AddPersistence): the Worker has no web signing key and must not need one.
builder.Services.AddNightTalesAuth(builder.Configuration);

// 09.3.2-T1 step 6: the bearer scheme over the same key, and `App:PublicOrigin`.
builder.Services.AddNightTalesJwtBearer();

builder.Services.AddProblemDetails(ProblemDetailsWriter.ConfigureProblemDetails);

// 09 README D14: enums travel snake_case over the wire. 09.3.1-T1's register body is the first route
// that needs it (`consents[].document` ∈ `tos | privacy`, 10-web-ui SC30), so the converter is
// registered on the minimal-API JSON options. Integer enum values stay accepted for reading; nothing
// serializes an enum as a number.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)));

// 09 README D12/H4: the Api only writes the outbox, so an accidental direct enqueue fails fast.
builder.Services.AddApiJobScheduler();

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

app.MapGet("/health/live", () => Results.Ok());

// 09.3.1-T1 (09 README D14): the auth route group; in P it carries POST /api/v1/auth/register, which
// answers 403 registration_closed until 09.3.1-T3 swaps the registration flow.
app.MapAuthEndpoints();

// 09.4.1-T1 (09 README D14, 09 §4 09.4.1): the series route group.
app.MapSeriesEndpoints();

app.Run();

public partial class Program;
