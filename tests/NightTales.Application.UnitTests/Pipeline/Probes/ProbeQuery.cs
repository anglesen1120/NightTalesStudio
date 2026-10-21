using ErrorOr;
using Mediator;

namespace NightTales.Application.UnitTests.Pipeline.Probes;

/// <summary>Probe query, to prove the pipeline runs for query messages too.</summary>
public sealed record ProbeQuery(string Name) : IQuery<ErrorOr<int>>;
