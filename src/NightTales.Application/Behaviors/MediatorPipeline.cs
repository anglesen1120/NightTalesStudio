namespace NightTales.Application.Behaviors;

/// <summary>
/// The canonical ordered behaviour list (09 README D4/D5):
/// Logging → Authorization → Validation → UnitOfWork → Idempotency → Audit → handler.
/// Hosts must NOT pass this field to <c>AddMediator(o =&gt; o.PipelineBehaviors = …)</c>: Mediator 3's
/// source generator silently registers zero behaviours for a non-literal value. Copy the inline
/// literal in <c>MediatorPipelineRegistration</c> of each host instead; the pipeline test pins that
/// literal to this list.
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
