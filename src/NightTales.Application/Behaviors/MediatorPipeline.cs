namespace NightTales.Application.Behaviors;

/// <summary>
/// The single ordered behaviour list every host registers through
/// <c>AddMediator(o =&gt; o.PipelineBehaviors = MediatorPipeline.Behaviors)</c> (09 README D4/D5).
/// Order: Logging → Authorization → Validation → UnitOfWork → Idempotency → Audit → handler.
/// </summary>
public static class MediatorPipeline
{
    /// <summary>
    /// Open generic behaviours in execution order. The Mediator source generator resolves this field at
    /// compile time, closes each entry for every message it finds, and registers them in this order.
    /// </summary>
    public static readonly Type[] Behaviors =
    [
        typeof(LoggingBehavior<,>),
        typeof(AuthorizationBehavior<,>),
        typeof(ValidationBehavior<,>),
        typeof(UnitOfWorkBehavior<,>),
        typeof(IdempotencyBehavior<,>),
        typeof(AuditBehavior<,>),
    ];
}
