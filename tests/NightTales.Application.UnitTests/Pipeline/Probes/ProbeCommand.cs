using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Messaging;
using NightTales.Domain.Common;

namespace NightTales.Application.UnitTests.Pipeline.Probes;

/// <summary>
/// Probe command. <see cref="Secret"/> carries a value the pipeline must never log.
/// </summary>
/// <remarks>
/// It implements every pipeline marker — <see cref="IIdempotentCommand"/> and
/// <see cref="IRequireRole"/> — so the Mediator source generator closes all six behaviours of
/// <see cref="Behaviors.MediatorPipeline.Behaviors"/> for it and the ordering test can resolve the whole
/// chain. Both markers stay inert: <see cref="IdempotencyKey"/> is null, which the idempotency behaviour
/// passes through, and <see cref="RequiredRole"/> is the lowest role, which any authenticated caller
/// holds.
/// </remarks>
public sealed record ProbeCommand(
    string Name,
    string Secret = ProbeCommand.SecretValue,
    string? IdempotencyKey = null) : ICommand<ErrorOr<Guid>>, IIdempotentCommand, IRequireRole
{
    public const string SecretValue = "SECRET-123";

    public const string ForbiddenSecret = "forbidden";

    /// <inheritdoc />
    public PlatformRole RequiredRole => PlatformRole.User;
}
