using System.Reflection;
using Shouldly;
using Xunit;

namespace NightTales.Architecture.Tests;

/// <summary>
/// Package/project allow-lists checked on assembly references, not type dependencies: a package that
/// is referenced but unused leaves no type dependency behind (09.1.2 plan A3).
/// </summary>
public sealed class AssemblyReferenceRules
{
    [Fact]
    public void Domain_ReferencesOnlyErrorOr()
    {
        var assembly = ArchitectureModel.Require(ArchitectureModel.Domain);
        AssertAllowed(
            assembly,
            AllowLists.DomainAllowedReferences,
            AllowLists.NoExtraPrefixes,
            nameof(Domain_ReferencesOnlyErrorOr));
    }

    [Fact]
    public void Application_ReferencesOnlyAllowedPackages()
    {
        var assembly = ArchitectureModel.Require(ArchitectureModel.Application);
        AssertAllowed(
            assembly,
            AllowLists.ApplicationAllowedReferences,
            AllowLists.NoExtraPrefixes,
            nameof(Application_ReferencesOnlyAllowedPackages));
    }

    [Fact]
    public void Fetcher_ReferencesOnlyDomainAndCore()
    {
        var core = ArchitectureModel.Find(ArchitectureModel.FetcherCore);
        if (core is not null)
        {
            AssertAllowed(
                core,
                AllowLists.FetcherCoreAllowedReferences,
                AllowLists.NoExtraPrefixes,
                nameof(Fetcher_ReferencesOnlyDomainAndCore));
        }

        var fetcher = ArchitectureModel.Find(ArchitectureModel.Fetcher);
        if (fetcher is not null)
        {
            AssertAllowed(
                fetcher,
                AllowLists.FetcherAllowedReferences,
                AllowLists.FetcherAllowedPrefixes,
                nameof(Fetcher_ReferencesOnlyDomainAndCore));
        }
    }

    private static void AssertAllowed(
        Assembly assembly,
        IReadOnlySet<string> allowedNames,
        IReadOnlyList<string> allowedPrefixes,
        string rule)
    {
        var offenders = FindDisallowedReferences(assembly, allowedNames, allowedPrefixes);
        offenders.ShouldBeEmpty(
            $"{rule}: {assembly.GetName().Name} references [{string.Join(", ", offenders)}]; "
            + $"allowed references are [{string.Join(", ", allowedNames)}] plus the framework baseline.");
    }

    private static List<string> FindDisallowedReferences(
        Assembly assembly,
        IReadOnlySet<string> allowedNames,
        IReadOnlyList<string> allowedPrefixes)
    {
        var offenders = new List<string>();
        foreach (var reference in assembly.GetReferencedAssemblies())
        {
            var name = reference.Name;
            if (string.IsNullOrEmpty(name) || AllowLists.IsBaselineReference(name) || allowedNames.Contains(name))
            {
                continue;
            }

            if (allowedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }

            offenders.Add(name);
        }

        offenders.Sort(StringComparer.Ordinal);
        return offenders;
    }
}
