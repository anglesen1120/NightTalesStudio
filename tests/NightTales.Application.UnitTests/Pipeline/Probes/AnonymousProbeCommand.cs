using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Messaging;

namespace NightTales.Application.UnitTests.Pipeline.Probes;

/// <summary>
/// Probe command that opted out of the default-deny check with
/// <see cref="AllowAnonymousRequestAttribute"/> (09 README D4).
/// </summary>
[AllowAnonymousRequest]
public sealed record AnonymousProbeCommand(string Name) : ICommand<ErrorOr<Guid>>;

/// <summary>Probe handler for <see cref="AnonymousProbeCommand"/>; records that it ran.</summary>
public sealed class AnonymousProbeCommandHandler(PipelineTrace trace) : ICommandHandler<AnonymousProbeCommand, ErrorOr<Guid>>
{
    public static readonly Guid ResultId = new("01900000-0000-7000-8000-000000000002");

    public ValueTask<ErrorOr<Guid>> Handle(AnonymousProbeCommand command, CancellationToken cancellationToken)
    {
        trace.Record("anonymous-handler");
        return ValueTask.FromResult<ErrorOr<Guid>>(ResultId);
    }
}
