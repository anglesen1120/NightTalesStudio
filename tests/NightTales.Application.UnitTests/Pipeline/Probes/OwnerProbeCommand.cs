using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Messaging;
using NightTales.Domain.Common;

namespace NightTales.Application.UnitTests.Pipeline.Probes;

/// <summary>
/// Probe command that requires the owner role (09 README D4): a caller below it must be answered with
/// 404 <c>not_found</c>, never 403.
/// </summary>
public sealed record OwnerProbeCommand(string Name) : ICommand<ErrorOr<Guid>>, IRequireRole
{
    /// <inheritdoc />
    public PlatformRole RequiredRole => PlatformRole.Owner;
}

/// <summary>Probe handler for <see cref="OwnerProbeCommand"/>; records that it ran.</summary>
public sealed class OwnerProbeCommandHandler(PipelineTrace trace) : ICommandHandler<OwnerProbeCommand, ErrorOr<Guid>>
{
    public static readonly Guid ResultId = new("01900000-0000-7000-8000-000000000003");

    public ValueTask<ErrorOr<Guid>> Handle(OwnerProbeCommand command, CancellationToken cancellationToken)
    {
        trace.Record("owner-handler");
        return ValueTask.FromResult<ErrorOr<Guid>>(ResultId);
    }
}
