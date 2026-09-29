using NightTales.Infrastructure.Persistence;
using NightTales.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddNightTalesMediator();
builder.Services.AddPersistence(builder.Configuration);

builder.Build().Run();
