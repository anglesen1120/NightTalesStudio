using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Behaviors;
using NightTales.Application.UnitTests.Pipeline.Probes;

namespace NightTales.Application.UnitTests.Pipeline;

/// <summary>
/// Builds the real pipeline over the probe messages: the Mediator source generator's own
/// registrations (Scoped, as in both hosts) plus <see cref="MediatorPipeline.Behaviors"/>.
/// </summary>
public sealed class MediatorTestHost : IDisposable
{
    private readonly ServiceProvider _provider;

    private MediatorTestHost(ServiceProvider provider, PipelineTrace trace, CapturingLoggerProvider logs)
    {
        _provider = provider;
        Trace = trace;
        Logs = logs;
    }

    public PipelineTrace Trace { get; }

    public CapturingLoggerProvider Logs { get; }

    public static MediatorTestHost Create()
    {
        var trace = new PipelineTrace();
        var logs = new CapturingLoggerProvider(trace);
        var services = new ServiceCollection();

        services.AddSingleton(trace);
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(logs);
        });
        services.AddMediator(options =>
        {
            options.ServiceLifetime = ServiceLifetime.Scoped;
            options.Assemblies = [typeof(ProbeCommand)];

            // Inline literal on purpose: the Mediator source generator does not resolve a reference to
            // MediatorPipeline.Behaviors and would silently register no behaviours. The ordering test
            // asserts the resolved registrations equal MediatorPipeline.Behaviors.
            options.PipelineBehaviors =
            [
                typeof(LoggingBehavior<,>),
                typeof(AuthorizationBehavior<,>),
                typeof(ValidationBehavior<,>),
                typeof(UnitOfWorkBehavior<,>),
                typeof(IdempotencyBehavior<,>),
                typeof(AuditBehavior<,>),
            ];
        });
        services.AddValidatorsFromAssemblyContaining<ProbeCommandValidator>(ServiceLifetime.Scoped);

        return new MediatorTestHost(
            services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }),
            trace,
            logs);
    }

    public IServiceScope CreateScope() => _provider.CreateScope();

    public void Dispose() => _provider.Dispose();
}
