using NightTales.Infrastructure.Persistence.Seeding;
using NightTales.Worker;
using NightTales.Worker.Health;

// The Worker host (09.2.6-T1 step 3, 09 W3 CR-42): its composition lives in
// NightTales.Worker.WorkerApplication.Build, which both this entry point and the composition test use
// (11.1.1-T1). Here only the `nts-seed` command and the health surface stay.
var app = WorkerApplication.Build(args);

// 09.2.5-T1: the same `nts-seed` command as the Api (plan §7), so the Worker image needs no extra
// project to seed. It exits before the Kestrel listener or the Hangfire server start.
if (args.Contains("--seed", StringComparer.Ordinal))
{
    Environment.ExitCode = SeedCommand.Run(app.Services);

    return;
}

app.MapWorkerHealth();

app.Run();
