using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NightTales.Application.Features.Autopilot.Gates;

namespace NightTales.Infrastructure.DependencyInjection;

/// <summary>The autopilot gate options (02.8.2-T1).</summary>
public static class AutopilotGatesOptionsServiceCollectionExtensions
{
    /// <summary>
    /// Binds and validates <see cref="AutopilotGateOptions"/> from <c>Autopilot:Gates</c> (02.8.2 plan §3.3).
    /// Every key carries an in-code default — including <c>DryRun = true</c>, so a deployment that says nothing
    /// about the gates never auto-approves anything — and a value that could not decide a verdict fails the
    /// start-up instead of the first evaluation. The plain validated value is registered <em>scoped</em> from the
    /// monitor's current value, so the Application services take it as a constructor dependency (they may not
    /// reference <c>IOptionsMonitor</c> itself) and a threshold change is still picked up without a restart.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddAutopilotGatesOptions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<AutopilotGateOptions>()
            .BindConfiguration(AutopilotGateOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AutopilotGateOptions>, AutopilotGateOptionsValidation>());
        services.TryAddScoped(provider =>
            provider.GetRequiredService<IOptionsMonitor<AutopilotGateOptions>>().CurrentValue);

        return services;
    }
}

/// <summary>Fails the start-up on an invalid <c>Autopilot:Gates</c> block, with every problem listed.</summary>
internal sealed class AutopilotGateOptionsValidation : IValidateOptions<AutopilotGateOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AutopilotGateOptions options)
    {
        var problems = AutopilotGateOptionsValidator.Validate(options);

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
