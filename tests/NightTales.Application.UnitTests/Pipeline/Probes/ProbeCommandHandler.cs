using ErrorOr;
using Mediator;

namespace NightTales.Application.UnitTests.Pipeline.Probes;

/// <summary>Probe command handler; records that it ran and returns a fixed id.</summary>
public sealed class ProbeCommandHandler(PipelineTrace trace) : ICommandHandler<ProbeCommand, ErrorOr<Guid>>
{
    public static readonly Guid ResultId = new("01900000-0000-7000-8000-000000000001");

    public ValueTask<ErrorOr<Guid>> Handle(ProbeCommand command, CancellationToken cancellationToken)
    {
        trace.Record("handler");
        return ValueTask.FromResult<ErrorOr<Guid>>(ResultId);
    }
}
