using NightTales.Api;
using NightTales.Api.DependencyInjection;
using NightTales.Api.Http;
using NightTales.Infrastructure.Persistence;

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

app.UseExceptionHandler();

app.MapGet("/health/live", () => Results.Ok());

app.Run();

public partial class Program;
