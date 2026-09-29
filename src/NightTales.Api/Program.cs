using NightTales.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNightTalesMediator();

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok());

app.Run();

public partial class Program;
