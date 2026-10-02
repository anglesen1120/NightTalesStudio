using NightTales.Infrastructure.Persistence.Seeding;
using NightTales.Worker;
using NightTales.Worker.Cli;
using NightTales.Worker.Health;

// The Worker host (09.2.6-T1 step 3, 09 W3 CR-42): its composition lives in
// NightTales.Worker.WorkerApplication.Build, which both this entry point and the composition test use
// (11.1.1-T1). Here only the `flags` and `nts-seed` commands and the health surface stay.
//
// 09.9.2-T2 (09 README BD13/BD16, 09.9.2 plan §6): the `flags` verb is answered before the normal host
// exists. Its verb host owns the console and returns before any route is mapped or Kestrel starts, so
// `nts-worker flags list` binds no port — the Worker's listener stays the health surface only. A verb host
// the process cannot create is answered the way every other verb failure is: one typed line on stderr and
// exit 2, never the runtime's stack trace when the entry point builds the host itself.
if (FlagsCommand.IsFlagsVerb(args))
{
    Environment.ExitCode = FlagsCommand.RunHost(args);

    return;
}

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
