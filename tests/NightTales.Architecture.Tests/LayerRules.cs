using ArchUnitNET.xUnitV3;
using Xunit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace NightTales.Architecture.Tests;

/// <summary>
/// Layer rules (09.1.2 L63-64, README D12). Both rules pass on an empty type set
/// (<c>WithoutRequiringPositiveResults</c>), because the concepts arrive with later tasks.
/// </summary>
public sealed class LayerRules
{
    private const string PersistenceNamespacePattern = @"NightTales\.Infrastructure\.Persistence.*";
    private const string HangfireNamespacePattern = @"Hangfire.*";
    private const string MediatorGeneratedNamespacePattern = @"Mediator.*";

    [Fact]
    public void ApiAndWorker_UseInfrastructureOnlyFromCompositionRoots()
    {
        var api = ArchitectureModel.Require(ArchitectureModel.Api);
        var worker = ArchitectureModel.Require(ArchitectureModel.Worker);

        var rule = Types()
            .That().ResideInAssembly(api, [worker])
            .And().DoNotHaveNameMatching("Program|.*ServiceCollectionExtensions")
            .And().DoNotResideInNamespaceMatching(MediatorGeneratedNamespacePattern)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(PersistenceNamespacePattern)
            .WithoutRequiringPositiveResults();

        ArchRuleAssert.CheckRule(ArchitectureModel.Architecture, rule);
    }

    [Fact]
    public void Hangfire_ConfinedToJobsAndHosts()
    {
        var worker = ArchitectureModel.Require(ArchitectureModel.Worker);
        var api = ArchitectureModel.Require(ArchitectureModel.Api);

        var rule = Types()
            .That().DoNotResideInNamespaceMatching(@"NightTales\.Infrastructure\.Jobs.*")
            .And().DoNotResideInAssembly(worker, [api])
            .And().DoNotResideInNamespaceMatching(MediatorGeneratedNamespacePattern)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(HangfireNamespacePattern)
            .WithoutRequiringPositiveResults();

        ArchRuleAssert.CheckRule(ArchitectureModel.Architecture, rule);
    }
}
