using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using NightTales.Infrastructure.Persistence.Seeding;
using NightTales.Worker;
using NightTales.Worker.DependencyInjection;
using NightTales.Worker.Health;
using Serilog;

// The Worker host (09.2.6-T1 step 3, 09 W3 CR-42): Serilog, the one composition root
// (AddWorkerServices: persistence, Mediator, jobs, the recurring scan and the health checks), the
// Hangfire server, and a minimal Kestrel listener for /health/* bound to loopback. 09.9.3-T1
// replaces the health and logging surface.
var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture));

builder.Services.AddWorkerServices(builder.Configuration);
builder.Services.AddWorkerHangfireServer();

var healthPort = builder.Configuration.GetValue(
    $"{WorkerOptions.SectionName}:{nameof(WorkerOptions.HealthPort)}",
    WorkerOptions.DefaultHealthPort);

builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, healthPort));

var app = builder.Build();

// 09.2.5-T1: the same `nts-seed` command as the Api (plan §7), so the Worker image needs no extra
// project to seed. It exits before the Kestrel listener or the Hangfire server start.
if (args.Contains("--seed", StringComparer.Ordinal))
{
    Environment.ExitCode = SeedCommand.Run(app.Services);

    return;
}

app.MapWorkerHealth();

app.Run();
