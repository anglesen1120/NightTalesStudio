using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NightTales.Infrastructure.Realtime;
using NightTales.Infrastructure.Telemetry;
using NightTales.Worker.Cli;
using NightTales.Worker.DependencyInjection;
using NightTales.Worker.Health;
using Serilog;

namespace NightTales.Worker;

/// <summary>
/// The Worker's host composition (09.2.6-T1 step 3, 09 W3 CR-42, 11.1.1-T1): Serilog, the one service
/// composition root (<c>AddWorkerServices</c>), the Hangfire server, the realtime producer and the
/// minimal Kestrel listener for <c>/health/*</c>.
/// </summary>
/// <remarks>
/// <c>Program.cs</c> calls <see cref="Build"/>, and the composition test calls it too, so a
/// Development boot — where the host validates the DI graph — is exercised by a test instead of only
/// by <c>tools/dev/up.sh</c> (11.1.1-T1).
/// </remarks>
public static class WorkerApplication
{
    /// <summary>Builds the Worker host without starting it. 09.9.3-T1 replaces the health surface.</summary>
    /// <param name="args">The process arguments (host configuration, or <c>--seed</c>).</param>
    public static WebApplication Build(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var builder = WebApplication.CreateBuilder(HostArguments(args));

        // 09.9.2-T2 (09 README BD16, 09.9.2 plan §6): a `flags` invocation owns the process console — a
        // listing on stdout and one typed failure on stderr. Its logging is therefore decided here, from the
        // original args and before the host exists, because the normal Worker's console sink would print what
        // the CLI contract forbids: Serilog's template appends {Exception}, and the EF Core/Npgsql categories
        // log a failed command together with the server and the database. A verb host gets no sink at all.
        // 09.9.3-T1: the normal Worker host gets the shared observability wiring (compact JSON on stdout,
        // OTLP when enabled, traces and metrics) under the service name `nts-worker`.
        if (FlagsCommand.IsFlagsVerb(args))
        {
            builder.Host.UseSerilog((_, _, _) => { });
        }
        else
        {
            builder.AddNtsTelemetry("nts-worker");
        }

        // 11.1.1-T1: keep scope validation — the check that catches a captive scoped dependency — but
        // turn off build-time graph validation. The Mediator source generator registers every
        // Application handler in this host, including the Api-only auth handlers (LoginHandler,
        // LogoutHandler, RefreshHandler, RegistrationGateHandler). Those need IAuthSessionService and
        // IRequestInfoAccessor, which the Worker neither dispatches nor may compose: 09.3.2-T1 keeps the
        // web signing key out of the Worker (SecurityServiceCollectionExtensions' remark), and a
        // request-scoped accessor has no meaning here. Validating the whole graph would therefore fail
        // every Development boot over services this host never resolves, so the Api-only registrations
        // stay in the Api. The Worker's own graph — every job class the scan found — is validated
        // instead by BackgroundJobActivationCheck (09.2.6-T1), which resolves each one at start.
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = false;
        });

        builder.Services.AddWorkerServices(builder.Configuration);
        builder.Services.AddWorkerHangfireServer();

        // 09.8.1-T1 (09 README D11/R6): the Worker's outbox handlers are producers, so the Worker needs
        // the notifier too. It is the producer half only — the LISTEN loop and the hub belong to the Api,
        // which is the only host with sockets.
        builder.Services.AddRealtime(builder.Configuration);

        var healthPort = builder.Configuration.GetValue(
            $"{WorkerOptions.SectionName}:{nameof(WorkerOptions.HealthPort)}",
            WorkerOptions.DefaultHealthPort);
        var healthAddress = builder.Configuration.GetValue(
            $"{WorkerOptions.SectionName}:{nameof(WorkerOptions.HealthAddress)}",
            WorkerOptions.DefaultHealthAddress);

        // The bind address defaults to loopback (the native dev stack); the optional Compose service
        // sets it to 0.0.0.0 so the published 127.0.0.1 host port reaches the listener (11.1.1-T1).
        var healthEndpoint = WorkerOptions.ResolveHealthAddress(healthAddress);
        builder.WebHost.ConfigureKestrel(options => options.Listen(healthEndpoint, healthPort));

        return builder.Build();
    }

    /// <summary>Preserves host settings, removing only a leading flags command's positional arguments.</summary>
    /// <param name="args">The process arguments.</param>
    public static string[] HostArguments(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (!FlagsCommand.IsFlagsVerb(args))
        {
            return args;
        }

        var prefixLength = args.Length > 1 ? args[1] switch
        {
            "list" => 2,
            "set" => Math.Min(4, args.Length),
            _ => 1,
        } : 1;

        return args[prefixLength..];
    }
}
