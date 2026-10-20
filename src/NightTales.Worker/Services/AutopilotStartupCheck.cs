using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Domain.Series.Autopilot;

namespace NightTales.Worker.Services;

/// <summary>
/// Fails the Worker boot on the two autopilot preconditions that would otherwise surface as a broken cadence
/// hours later (02.8.3 plan §3.2 Step 2, §3.5 Step 5), the same way
/// <c>BackgroundJobActivationCheck</c> fails it on an unresolvable job.
/// </summary>
/// <remarks>
/// <para>
/// <b>Time zones.</b> <see cref="AutopilotTimeZones.SelfCheck"/> resolves <c>UTC</c> and one DST zone. An image
/// built with <c>InvariantGlobalization=true</c> cannot, and every slot of every series would then throw
/// instead of being planned; the container check of the task file's Outputs is the build-time twin of this
/// check.
/// </para>
/// <para>
/// <b>One handler per state.</b> Resolving the whole handler set once proves every constructor graph and rejects
/// two handlers claiming the same <see cref="AutopilotRunState"/>, which
/// <see cref="AutopilotStepHandlers.Index"/> answers with an exception — a code error, so it belongs in the boot
/// and not in the first tick.
/// </para>
/// </remarks>
/// <param name="scopes">Creates the scope the handlers are resolved from.</param>
public sealed class AutopilotStartupCheck(IServiceScopeFactory scopes) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            AutopilotTimeZones.SelfCheck();
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                "The autopilot scheduler cannot resolve the system time zones (02.8.3 plan §3.2). The runtime is "
                + "missing the ICU/IANA tz database — build the image with InvariantGlobalization=false — so every "
                + "cadence would fail instead of being planned.",
                exception);
        }

        using var scope = scopes.CreateScope();

        _ = AutopilotStepHandlers.Index(scope.ServiceProvider.GetServices<IAutopilotStepHandler>());

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
