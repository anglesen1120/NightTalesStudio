using ErrorOr;
using Mediator;

namespace NightTales.Application.UnitTests.Pipeline.Probes;

/// <summary>Probe query handler; records that it ran and returns a fixed value.</summary>
public sealed class ProbeQueryHandler(PipelineTrace trace) : IQueryHandler<ProbeQuery, ErrorOr<int>>
{
    public const int Result = 42;

    public ValueTask<ErrorOr<int>> Handle(ProbeQuery query, CancellationToken cancellationToken)
    {
        trace.Record("query-handler");
        return ValueTask.FromResult<ErrorOr<int>>(Result);
    }
}
