using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace NightTales.Infrastructure.Jobs;

/// <summary>
/// Fails the Worker boot when a job class found by the scan cannot be resolved from DI (09.2.6-T1
/// failure handling). Hangfire activates a job through the DI scope, so a missing registration — or a
/// constructor dependency nobody registered — would otherwise surface as a failed job hours later,
/// once per enqueue.
/// </summary>
/// <remarks>
/// The check runs before the Hangfire server (it is registered first) and resolves every type once,
/// which also validates their constructor graphs.
/// </remarks>
/// <param name="services">Creates the scope the job classes are resolved from.</param>
/// <param name="registry">The job types found when the composition root was built.</param>
public sealed class BackgroundJobActivationCheck(
    IServiceScopeFactory services,
    BackgroundJobRegistry registry) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();

        foreach (var jobType in registry.JobTypes)
        {
            try
            {
                _ = scope.ServiceProvider.GetRequiredService(jobType);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Background job '{jobType.FullName}' is not resolvable from the service provider "
                    + "(09 README D12). Register it, or register every service its constructor needs; "
                    + "Hangfire activates jobs through the DI scope.",
                    exception);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
