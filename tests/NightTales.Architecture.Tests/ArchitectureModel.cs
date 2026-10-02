using System.Reflection;
using ArchUnitNET.Loader;
using ArchModel = ArchUnitNET.Domain.Architecture;
using Assembly = System.Reflection.Assembly;

namespace NightTales.Architecture.Tests;

/// <summary>
/// Loads every <c>src</c> assembly once and builds the ArchUnitNET model the layer rules run against.
/// Rules are written against namespaces and markers, so a marker that does not exist yet yields an
/// empty set and the rule passes (09.1.2 plan §1).
/// </summary>
internal static class ArchitectureModel
{
    internal const string Domain = "NightTales.Domain";
    internal const string Application = "NightTales.Application";
    internal const string Infrastructure = "NightTales.Infrastructure";
    internal const string RenderNodeContract = "NightTales.RenderNode.Contract";
    internal const string RenderNodeServer = "NightTales.RenderNode.Server";
    internal const string Api = "NightTales.Api";
    internal const string Worker = "NightTales.Worker";
    internal const string FetcherCore = "NightTales.Fetcher.Core";
    internal const string Fetcher = "NightTales.Fetcher";

    private static readonly string[] SourceAssemblyNames =
    [
        Domain,
        Application,
        Infrastructure,
        RenderNodeContract,
        RenderNodeServer,
        Api,
        Worker,
        FetcherCore,
        Fetcher,
    ];

    /// <summary>Every source assembly present in the test output, in declaration order.</summary>
    internal static readonly List<Assembly> SourceAssemblies = LoadSourceAssemblies();

    /// <summary>The ArchUnitNET model over <see cref="SourceAssemblies"/>.</summary>
    internal static readonly ArchModel Architecture =
        new ArchLoader().LoadAssemblies([.. SourceAssemblies]).Build();

    internal static Assembly? Find(string assemblyName) =>
        SourceAssemblies.FirstOrDefault(
            assembly => string.Equals(assembly.GetName().Name, assemblyName, StringComparison.Ordinal));

    internal static Assembly Require(string assemblyName) =>
        Find(assemblyName)
        ?? throw new InvalidOperationException(
            $"Source assembly '{assemblyName}' is missing from the test output; the architecture rules cannot run.");

    private static List<Assembly> LoadSourceAssemblies()
    {
        var directory = AppContext.BaseDirectory;
        var assemblies = new List<Assembly>(SourceAssemblyNames.Length);
        foreach (var name in SourceAssemblyNames)
        {
            var path = Path.Combine(directory, name + ".dll");
            if (File.Exists(path))
            {
                assemblies.Add(Assembly.LoadFrom(path));
            }
        }

        return assemblies;
    }
}
