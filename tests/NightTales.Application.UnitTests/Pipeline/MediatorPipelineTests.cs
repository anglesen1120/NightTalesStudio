using ErrorOr;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Behaviors;
using NightTales.Application.UnitTests.Pipeline.Probes;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Pipeline;

public sealed class MediatorPipelineTests
{
    [Fact]
    public async Task Pipeline_RunsBehaviorsInDeclaredOrder()
    {
        using var host = MediatorTestHost.Create();
        using var scope = host.CreateScope();

        // The generated Mediator resolves IEnumerable<IPipelineBehavior<,>>, which preserves the
        // registration order the source generator took from MediatorPipeline.Behaviors.
        var expected = MediatorPipeline.Behaviors
            .Select(behavior => behavior.MakeGenericType(typeof(ProbeCommand), typeof(ErrorOr<Guid>)))
            .ToArray();
        var resolved = scope.ServiceProvider
            .GetServices<IPipelineBehavior<ProbeCommand, ErrorOr<Guid>>>()
            .Select(behavior => behavior.GetType())
            .ToArray();

        resolved.ShouldBe(expected);

        // And the chain really runs: validation (index 2) before the handler, logging (index 0) around both.
        var result = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send(new ProbeCommand("probe"), CancellationToken.None);

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe(ProbeCommandHandler.ResultId);
        host.Trace.Entries.ShouldBe(["validate", "handler", "log"]);
    }

    [Fact]
    public async Task Pipeline_RunsForQueryMessages()
    {
        using var host = MediatorTestHost.Create();
        using var scope = host.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send(new ProbeQuery("probe"), CancellationToken.None);

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe(ProbeQueryHandler.Result);
        host.Trace.Entries.ShouldBe(["query-handler", "log"]);
    }

    [Fact]
    public void Mediator_HandlersAreScoped()
    {
        using var host = MediatorTestHost.Create();
        using var first = host.CreateScope();
        using var second = host.CreateScope();

        var handler = first.ServiceProvider.GetRequiredService<ICommandHandler<ProbeCommand, ErrorOr<Guid>>>();
        var sameScope = first.ServiceProvider.GetRequiredService<ICommandHandler<ProbeCommand, ErrorOr<Guid>>>();
        var otherScope = second.ServiceProvider.GetRequiredService<ICommandHandler<ProbeCommand, ErrorOr<Guid>>>();

        handler.ShouldBeSameAs(sameScope);
        handler.ShouldNotBeSameAs(otherScope);

        first.ServiceProvider.GetRequiredService<ISender>()
            .ShouldNotBeSameAs(second.ServiceProvider.GetRequiredService<ISender>());
    }

    [Fact]
    public void HostPipeline_MatchesMediatorPipelineBehaviors()
    {
        // The generator only resolves an inline literal, so each host owns a copy of the list. These
        // assertions pin both copies to the single declared list (09 README D4).
        NightTales.Api.MediatorPipelineRegistration.AddNightTalesMediator(new ServiceCollection());
        NightTales.Api.MediatorPipelineRegistration.ConfiguredBehaviors.ShouldBe(MediatorPipeline.Behaviors);

        NightTales.Worker.MediatorPipelineRegistration.AddNightTalesMediator(new ServiceCollection());
        NightTales.Worker.MediatorPipelineRegistration.ConfiguredBehaviors.ShouldBe(MediatorPipeline.Behaviors);
    }
}
