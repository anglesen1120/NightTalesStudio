using NightTales.Api;
using NightTales.Api.Http;
using NightTales.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNightTalesMediator();
builder.Services.AddPersistence(builder.Configuration);
builder.Services.AddProblemDetails(ProblemDetailsWriter.ConfigureProblemDetails);

var app = builder.Build();

app.UseExceptionHandler();

app.MapGet("/health/live", () => Results.Ok());

app.Run();

public partial class Program;
