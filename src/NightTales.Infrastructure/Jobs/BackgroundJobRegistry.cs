using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Application;
using NightTales.Application.Abstractions.Jobs;

namespace NightTales.Infrastructure.Jobs;

/// <summary>
/// The <see cref="IBackgroundJob"/> types and <see cref="IRecurringJobRegistration"/> implementations
/// found when the composition root was built (09 README D12/D13).
/// </summary>
/// <remarks>
/// Hangfire resolves a job by its concrete type, so the scan registers every job class in DI
/// (<see cref="Scoped"/>) and this catalog feeds the start-time resolvability check
/// (<see cref="BackgroundJobActivationCheck"/>).
/// </remarks>
public sealed class BackgroundJobRegistry
{
    /// <summary>Creates the catalog and registers every type in <paramref name="services"/>.</summary>
    /// <param name="services">The service collection the job classes are added to.</param>
    /// <param name="assemblies">
    /// The assemblies to scan; the Application assembly is always included, because 09 README D12
    /// places job classes in <c>Application/Features/&lt;F&gt;/Jobs/</c>.
    /// </param>
    public static BackgroundJobRegistry AddTo(IServiceCollection services, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        var scanned = assemblies
            .Append(typeof(ApplicationAssemblyMarker).Assembly)
            .Distinct()
            .ToArray();

        var jobTypes = scanned
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => IsScannable(type) && typeof(IBackgroundJob).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        foreach (var jobType in jobTypes)
        {
            services.TryAddScoped(jobType);
        }

        var registrationTypes = scanned
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => IsScannable(type) && typeof(IRecurringJobRegistration).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        foreach (var registrationType in registrationTypes)
        {
            services.TryAddSingleton(typeof(IRecurringJobRegistration), registrationType);
        }

        return new BackgroundJobRegistry(jobTypes);
    }

    private BackgroundJobRegistry(IReadOnlyList<Type> jobTypes) => JobTypes = jobTypes;

    /// <summary>
    /// Whether <paramref name="type"/> can be a job class or a recurring registration: a top-level
    /// class (a nested type is the compiler's — a lambda closure or a state machine — or a test
    /// double the container cannot activate).
    /// </summary>
    private static bool IsScannable(Type type) =>
        type is { IsClass: true, IsAbstract: false }
        && !type.IsNested
        && !type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false);

    /// <summary>Every <see cref="IBackgroundJob"/> implementation of the scanned assemblies.</summary>
    public IReadOnlyList<Type> JobTypes { get; }
}
