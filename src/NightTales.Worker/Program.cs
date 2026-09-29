using NightTales.Infrastructure.Persistence;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddPersistence(builder.Configuration);

builder.Build().Run();
