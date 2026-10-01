using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Domain.Audit;

namespace NightTales.Application.UnitTests.Audit;

/// <summary>
/// The messages the <see cref="AuditBehaviorTests"/> send through the behaviour directly: one audited
/// command with a projection, one command that projects but declares no attribute (the attribute is what
/// turns the projection into a row).
/// </summary>
/// <remarks>
/// They live here rather than in the Application assembly on purpose: the architecture test of 09.3.6-T1
/// scans <c>NightTales.Application</c> for unmarked commands and must stay free to fail, so a probe that
/// breaks the rule may only exist in a test assembly.
/// </remarks>
/// <param name="EntityId">The entity the row points at.</param>
/// <param name="SubjectUserId">The account the projection claims, or <c>null</c> for "the caller".</param>
[Audited("probe.audited", Category.Episodes, Visibility.User)]
internal sealed record ProbeAuditedCommand(Guid EntityId, Guid? SubjectUserId)
    : ICommand<ErrorOr<Success>>, IAuditData
{
    /// <inheritdoc />
    public AuditData ToAuditData() => new(
        SubjectUserId,
        "probe",
        EntityId,
        new Dictionary<string, string>(StringComparer.Ordinal) { ["probe"] = "audited" });
}

/// <summary>A command that projects audit data but declares no attribute, so nothing is written.</summary>
/// <param name="EntityId">The entity the projection would have pointed at.</param>
internal sealed record ProbeUnmarkedCommand(Guid EntityId) : ICommand<ErrorOr<Success>>, IAuditData
{
    /// <inheritdoc />
    public AuditData ToAuditData() => new(
        null,
        "probe",
        EntityId,
        new Dictionary<string, string>(StringComparer.Ordinal) { ["probe"] = "unmarked" });
}

/// <summary>
/// The handler the Mediator source generator needs for <see cref="ProbeAuditedCommand"/>; the behaviour
/// tests never reach it (they call the behaviour with their own delegate).
/// </summary>
internal sealed class ProbeAuditedCommandHandler : ICommandHandler<ProbeAuditedCommand, ErrorOr<Success>>
{
    /// <inheritdoc />
    public ValueTask<ErrorOr<Success>> Handle(ProbeAuditedCommand command, CancellationToken cancellationToken)
        => ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
}

/// <summary>The handler of <see cref="ProbeUnmarkedCommand"/>, for the same reason.</summary>
internal sealed class ProbeUnmarkedCommandHandler : ICommandHandler<ProbeUnmarkedCommand, ErrorOr<Success>>
{
    /// <inheritdoc />
    public ValueTask<ErrorOr<Success>> Handle(ProbeUnmarkedCommand command, CancellationToken cancellationToken)
        => ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
}
