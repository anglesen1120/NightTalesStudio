using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Idempotency;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Audit;
using NightTales.Application.Behaviors;
using NightTales.Application.UnitTests.Pipeline.Probes;
using NightTales.Domain.Common;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Time;

namespace NightTales.Application.UnitTests.Pipeline;

/// <summary>
/// Builds the real pipeline over the probe messages: the Mediator source generator's own
/// registrations (Scoped, as in both hosts) plus <see cref="MediatorPipeline.Behaviors"/>.
/// </summary>
/// <remarks>
/// The behaviours are the production ones, so the host supplies what they need without a database: a
/// settable <see cref="TestCurrentUser"/>, the <see cref="FakeClock"/>, a
/// <see cref="PassThroughUnitOfWork"/> and a <see cref="KeylessIdempotencyStore"/>. The probe messages
/// declare no idempotency key, so the store is never reached in a passing test.
/// </remarks>
public sealed class MediatorTestHost : IDisposable
{
    /// <summary>The id of the default authenticated caller; set <see cref="TestCurrentUser.Id"/> to null for anonymous.</summary>
    public static readonly Guid ActorId = new("01900000-0000-7000-8000-00000000000a");

    private readonly ServiceProvider _provider;

    private MediatorTestHost(
        ServiceProvider provider,
        PipelineTrace trace,
        CapturingLoggerProvider logs,
        TestCurrentUser currentUser)
    {
        _provider = provider;
        Trace = trace;
        Logs = logs;
        CurrentUser = currentUser;
    }

    public PipelineTrace Trace { get; }

    public CapturingLoggerProvider Logs { get; }

    /// <summary>The actor every behaviour of this host resolves, settable per test.</summary>
    public TestCurrentUser CurrentUser { get; }

    public static MediatorTestHost Create()
    {
        var trace = new PipelineTrace();
        var logs = new CapturingLoggerProvider(trace);
        var currentUser = new TestCurrentUser { Id = ActorId, Role = PlatformRole.User, Actor = ActorType.User };
        var services = new ServiceCollection();

        services.AddSingleton(trace);
        services.AddSingleton(currentUser);
        services.AddSingleton<ICurrentUser>(currentUser);
        services.AddSingleton<IClock>(new FakeClock());
        services.AddSingleton<IUnitOfWork, PassThroughUnitOfWork>();
        services.AddSingleton<IIdempotencyStore, KeylessIdempotencyStore>();

        // 09.3.6-T1: slot 6 resolves from the container as soon as a probe message runs the real chain, so
        // the pipeline needs the writer and the attribute cache. No probe of this assembly is [Audited], so
        // the fake throws instead of accepting a row.
        services.AddSingleton<IAuditWriter, UnusedAuditWriter>();
        services.AddSingleton<AuditMetadataCache>();
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
            logs,
            currentUser);
    }

    public IServiceScope CreateScope() => _provider.CreateScope();

    public void Dispose() => _provider.Dispose();
}
