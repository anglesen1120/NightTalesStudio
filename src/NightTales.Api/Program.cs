using NightTales.Api;
using NightTales.Api.Http;
using NightTales.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// 09.1.5-T1 (09 README D14): registers the OpenAPI document provider the build-time generator needs.
builder.Services.AddOpenApi();

builder.Services.AddNightTalesMediator();
builder.Services.AddPersistence(builder.Configuration);
builder.Services.AddProblemDetails(ProblemDetailsWriter.ConfigureProblemDetails);

var app = builder.Build();

app.UseExceptionHandler();

app.MapGet("/health/live", () => Results.Ok());

app.Run();

public partial class Program;
