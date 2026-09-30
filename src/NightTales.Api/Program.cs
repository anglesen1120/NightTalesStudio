using System.Text.Json;
using System.Text.Json.Serialization;
using NightTales.Api;
using NightTales.Api.DependencyInjection;
using NightTales.Api.Endpoints;
using NightTales.Api.Http;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Seeding;

var builder = WebApplication.CreateBuilder(args);

// 09.1.5-T1 (09 README D14): registers the OpenAPI document provider the build-time generator needs.
builder.Services.AddOpenApi();

builder.Services.AddNightTalesMediator();
builder.Services.AddPersistence(builder.Configuration);

// 09.2.3-T1 (09 README D10): the caller's claims are the tenant of every filtered query, and the
// scoped system context lets tests run outbox-style work in-process.
builder.Services.AddApiCurrentUser();

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

app.Run();

public partial class Program;
