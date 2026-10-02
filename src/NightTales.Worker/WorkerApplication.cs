using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NightTales.Infrastructure.Realtime;
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

        var builder = WebApplication.CreateBuilder(args);

        builder.Host.UseSerilog((context, services, configuration) => configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture));

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

        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, healthPort));

        return builder.Build();
    }
}
