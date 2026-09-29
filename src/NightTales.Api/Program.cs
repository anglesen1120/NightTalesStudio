using NightTales.Api;
using NightTales.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder( args );

// 09.1.5-T1 (09 README D14): registers the OpenAPI document provider the build-time generator needs.
builder.Services.AddOpenApi();

builder.Services.AddNightTalesMediator();
builder.Services.AddPersistence(builder.Configuration);

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok());

app.Run();

public partial class Program;
