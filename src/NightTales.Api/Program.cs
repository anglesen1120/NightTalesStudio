using NightTales.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPersistence(builder.Configuration);

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok());

app.Run();

public partial class Program;
