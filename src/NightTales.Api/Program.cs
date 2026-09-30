using NightTales.Api;
using NightTales.Api.DependencyInjection;
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

app.Run();

public partial class Program;
