using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.Security;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Infrastructure.Jobs;
using NightTales.Infrastructure.Security;

namespace NightTales.Api.DependencyInjection;

/// <summary>
/// The Api's registrations outside the persistence layer: its actor (09 README D10) and its
/// job-scheduling port (09 README D12/H4).
/// </summary>
public static class ApiServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IJobScheduler"/> as the throwing implementation.</summary>
    public static IServiceCollection AddApiJobScheduler(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddSingleton<IJobScheduler, ThrowingJobScheduler>();
    }

    /// <summary>
    /// Registers the Api's actor (09 README D10): <see cref="HttpCurrentUser"/> reads the caller's
    /// <c>sub</c>/<c>role</c> claims and decorates a scoped <see cref="ScopedCurrentUser"/>, which
    /// doubles as the Api's <see cref="ISystemContext"/> for outbox-style work started in-process
    /// (tests only — production work runs in the Worker).
    /// </summary>
    /// <remarks>
    /// Runs after <c>AddPersistence</c>, whose anonymous fallback is removed first so that a single
    /// instance answers all three interfaces.
    /// </remarks>
    public static IServiceCollection AddApiCurrentUser(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpContextAccessor();

        services.RemoveAll<ICurrentUser>();
        services.RemoveAll<ISystemContext>();

        services.AddScoped<ScopedCurrentUser>();
        services.AddScoped<HttpCurrentUser>();
        services.AddScoped<ICurrentUser>(provider => provider.GetRequiredService<HttpCurrentUser>());
        services.AddScoped<ISystemContext>(provider => provider.GetRequiredService<HttpCurrentUser>());

        // 09.3.1-T1: the caller's IP/User-Agent for the security writes of the auth handlers. Outside a
        // request (the Worker never resolves it) both members read null.
        services.AddScoped<IRequestInfoAccessor, HttpRequestInfoAccessor>();

        return services;
    }
}
