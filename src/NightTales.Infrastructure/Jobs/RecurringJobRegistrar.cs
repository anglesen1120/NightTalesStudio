using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Jobs;

namespace NightTales.Infrastructure.Jobs;

/// <summary>
/// Keeps the stored recurring jobs equal to the code at every Worker start (09 README D12, 09 W3
/// CR-42): it registers every <see cref="IRecurringJobRegistration"/> from DI, then deletes
/// <b>every</b> stored recurring id no registration returned — there is no <c>manual:</c> prefix
/// exception and no opt-out, so an id whose registration was deleted is removed on the next start.
/// </summary>
/// <remarks>
/// Cron expressions are UTC (<see cref="Hangfire.RecurringJobOptions.TimeZone"/> is
/// <see cref="TimeZoneInfo.Utc"/>). A failure is logged and skipped — per registration, and again for
/// the removal pass — instead of failing the boot: a broken registration must not suppress the upserts
/// of the others or the stale sweep, and the Worker must stay up and answer <c>/health/ready</c> with
/// 503 while the database is down (09.9-D2). The next start retries the synchronisation. A duplicate
/// <see cref="Id"/> is a code error and does fail the boot.
/// </remarks>
/// <param name="services">The provider the registrations are resolved from.</param>
/// <param name="scheduler">The scheduler every registration upserts through.</param>
/// <param name="storage">The Hangfire storage whose recurring ids are reconciled.</param>
/// <param name="logger">Diagnostics for the removal pass and for storage failures.</param>
public sealed partial class RecurringJobRegistrar(
    IServiceProvider services,
    IJobScheduler scheduler,
    JobStorage storage,
    ILogger<RecurringJobRegistrar> logger) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var registrations = services.GetServices<IRecurringJobRegistration>().ToList();
        var registered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            if (!registered.Add(registration.Id))
            {
                throw new InvalidOperationException(
                    $"Two IRecurringJobRegistration implementations use the recurring job id "
                    + $"'{registration.Id}' (09 README D12); ids must be unique because they are the "
                    + "identity of the stored recurring job.");
            }
        }

        foreach (var registration in registrations)
        {
            try
            {
                registration.Register(scheduler);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogRegistrationFailed(exception, registration.Id);
            }
        }

        try
        {
            using var connection = storage.GetConnection();
            foreach (var stored in connection.GetRecurringJobs())
            {
                if (registered.Contains(stored.Id))
                {
                    continue;
                }

                LogRemovingStale(stored.Id);

                scheduler.RemoveRecurringIfExists(stored.Id);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogSynchronisationFailed(exception);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Warning,
        Message = "jobs.recurring.stale recurringJobId={RecurringJobId}")]
    private partial void LogRemovingStale(string recurringJobId);

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Error,
        Message = "jobs.recurring.sync-failed")]
    private partial void LogSynchronisationFailed(Exception exception);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Error,
        Message = "jobs.recurring.register-failed recurringJobId={RecurringJobId}")]
    private partial void LogRegistrationFailed(Exception exception, string recurringJobId);
}
