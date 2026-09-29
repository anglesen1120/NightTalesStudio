using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application;
using NightTales.Application.Behaviors;

namespace NightTales.Worker;

/// <summary>
/// Composition root for the Mediator pipeline in the Worker host (09 README D4/F03).
/// </summary>
public static class MediatorPipelineRegistration
{
    /// <summary>
    /// The list handed to <c>AddMediator</c>. The Mediator source generator only accepts an inline
    /// literal (a field, spread or variable reference compiles but silently registers no behaviours),
    /// so <see cref="AddNightTalesMediator"/> repeats these six types and this list must stay identical
    /// to that literal; the unit test asserts it equals <see cref="MediatorPipeline.Behaviors"/>.
    /// </summary>
    public static IReadOnlyList<Type> ConfiguredBehaviors { get; } =
    [
        typeof(LoggingBehavior<,>),
        typeof(AuthorizationBehavior<,>),
        typeof(ValidationBehavior<,>),
        typeof(UnitOfWorkBehavior<,>),
        typeof(IdempotencyBehavior<,>),
        typeof(AuditBehavior<,>),
    ];

    /// <summary>
    /// Registers the Scoped Mediator (F03) over the Application assembly plus the FluentValidation
    /// validators, in the declared pipeline order.
    /// </summary>
    public static IServiceCollection AddNightTalesMediator(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddMediator(options =>
        {
            options.ServiceLifetime = ServiceLifetime.Scoped;
            options.Assemblies = [typeof(ApplicationAssemblyMarker)];

            // The Mediator source generator parses this assignment at compile time: an inline literal
            // is required. A field, spread or local-variable reference compiles but silently registers
            // no behaviours at all, so MediatorPipeline.Behaviors must not be used here — the unit test
            // asserts this literal equals it.
            options.PipelineBehaviors =
            [
                typeof(LoggingBehavior<,>),
                typeof(AuthorizationBehavior<,>),
                typeof(ValidationBehavior<,>),
                typeof(UnitOfWorkBehavior<,>),
                typeof(IdempotencyBehavior<,>),
                typeof(AuditBehavior<,>),
            ];

            // Test assemblies generate their own Mediator for their probe messages; keeping the host's
            // generated types internal avoids the CS0436/CS0121 clash between two public
            // MediatorDependencyInjectionExtensions in the same namespaces.
            options.GenerateTypesAsInternal = true;
        });

        services.AddValidatorsFromAssemblyContaining<ApplicationAssemblyMarker>(ServiceLifetime.Scoped);

        return services;
    }
}
