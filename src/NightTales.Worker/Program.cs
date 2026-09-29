using NightTales.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddNightTalesMediator();

builder.Build().Run();
