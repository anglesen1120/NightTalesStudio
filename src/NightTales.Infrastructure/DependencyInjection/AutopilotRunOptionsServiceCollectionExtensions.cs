using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NightTales.Application.Features.Autopilot.Orchestration;

namespace NightTales.Infrastructure.DependencyInjection;

/// <summary>The autopilot run policy (02.8.3-T2).</summary>
public static class AutopilotRunOptionsServiceCollectionExtensions
{
    /// <summary>
    /// Binds and validates <see cref="AutopilotRunOptions"/> from <c>Autopilot:Run</c> and registers the bound value
    /// as a plain singleton, because every Application service that reads it — the slot planner, the orchestrator and
    /// the autopilot page's next-slot preview — may not reference <c>Microsoft.Extensions.Options</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both hosts call it.</b> The Worker plans from this section and the Api renders the page's `nextSlots`
    /// preview, so one binding here keeps the two from disagreeing; before 02.8.3-T2 only the Worker bound it, and a
    /// preview computed from the in-code defaults could name slots the planner would never use.
    /// </para>
    /// <para>
    /// <b>No duplicate keys (06 CR-3).</b> <see cref="AutopilotRunOptions.ApplyPlatformValues"/> reads the lead time
    /// and the minimum veto window from the <c>YouTube:*</c> section, never from an <c>Autopilot:</c> copy of them,
    /// and every other key carries an in-code default, so none is a required start-up key. An impossible run policy
    /// fails the boot instead of the first tick.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddAutopilotRunOptions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<AutopilotRunOptions>()
            .BindConfiguration(AutopilotRunOptions.SectionName)
            .Configure<IConfiguration>((runOptions, configuration) =>
                AutopilotRunOptions.ApplyPlatformValues(runOptions, key => configuration[key]))
            .Validate(
                options => AutopilotRunOptions.Validate(options).Count == 0,
                $"'{AutopilotRunOptions.SectionName}' must hold a run policy whose bounds are in range (see AutopilotRunOptions.Validate).")
            .ValidateOnStart();
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<AutopilotRunOptions>>().Value);

        return services;
    }
}
