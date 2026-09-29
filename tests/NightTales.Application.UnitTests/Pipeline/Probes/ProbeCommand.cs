using ErrorOr;
using Mediator;

namespace NightTales.Application.UnitTests.Pipeline.Probes;

/// <summary>Probe command. <see cref="Secret"/> carries a value the pipeline must never log.</summary>
public sealed record ProbeCommand(string Name, string Secret = ProbeCommand.SecretValue) : ICommand<ErrorOr<Guid>>
{
    public const string SecretValue = "SECRET-123";

    public const string ForbiddenSecret = "forbidden";
}
