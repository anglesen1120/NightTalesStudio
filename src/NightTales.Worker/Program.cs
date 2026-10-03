using Microsoft.Extensions.Hosting;
using NightTales.Infrastructure.Persistence.Seeding;
using NightTales.Infrastructure.Telemetry;
using NightTales.Worker;
using NightTales.Worker.Cli;
using NightTales.Worker.Health;

// The Worker host (09.2.6-T1 step 3, 09 W3 CR-42): its composition lives in
// NightTales.Worker.WorkerApplication.Build, which both this entry point and the composition test use
// (11.1.1-T1). Here only the argument modes stay: `flags`, `nts-seed`, the `nts-worker nodes` verbs and
// the health surface.
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

// 09.9.3-T1: compact JSON from the first line on, so a start-up failure — a missing log salt, an invalid
// OTLP endpoint, a node without its key — is one JSON event and a non-zero exit, not a runtime trace.
Serilog.Log.Logger = BootstrapLogging.Create();

try
{
    var app = WorkerApplication.Build(args);

    // 09.2.5-T1: the same `nts-seed` command as the Api (plan §7), so the Worker image needs no extra
    // project to seed. It exits before the Kestrel listener or the Hangfire server start.
    if (args.Contains("--seed", StringComparer.Ordinal))
    {
        Environment.ExitCode = SeedCommand.Run(app.Services);

        return;
    }

    // 11.3.3-T1 (11 README D13, plan §5): `nts-worker nodes list|set-state` runs the Worker binary in
    // argument mode, with no web listener and no Hangfire server.
    if (args.Length > 0 && string.Equals(args[0], NodesCommand.Verb, StringComparison.Ordinal))
    {
        Environment.ExitCode = NodesCommand.RunAsync(args, app.Services, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        return;
    }

    // 11.3.3-T1 (11 README D13/plan §7): the registry must match `RenderNodes:Nodes` before the Worker
    // announces itself healthy. A node without its adapter key exits 2, naming the variable and never a value.
    var registrationCode = WorkerNodeRegistration.Run(app.Services);
    if (registrationCode != 0)
    {
        Environment.ExitCode = registrationCode;

        return;
    }

    app.MapWorkerHealth();

    app.Run();
}
catch (Exception exception) when (exception is not HostAbortedException)
{
    // One JSON event on stdout, flushed in the finally below; the rethrow keeps the runtime's abort (a
    // non-zero exit) and lets an in-process host factory see the original exception.
    BootstrapLogging.ReportFailure(exception);

    throw;
}
finally
{
    Serilog.Log.CloseAndFlush();
}
