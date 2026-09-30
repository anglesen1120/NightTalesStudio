using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Infrastructure.Jobs;

namespace NightTales.Api.DependencyInjection;

/// <summary>
/// The Api's job-scheduling registration (09 README D12/H4): the Api never enqueues a job, so the
/// port is wired to <see cref="ThrowingJobScheduler"/> and an accidental use fails fast.
/// </summary>
public static class ApiServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IJobScheduler"/> as the throwing implementation.</summary>
    public static IServiceCollection AddApiJobScheduler(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddSingleton<IJobScheduler, ThrowingJobScheduler>();
    }
}
