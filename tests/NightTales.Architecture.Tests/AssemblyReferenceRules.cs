using System.Reflection;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace NightTales.Architecture.Tests;

/// <summary>
/// Package/project allow-lists checked on <em>declared</em> references (the project.assets.json
/// dependency graph), not only emitted assembly references: a package that is referenced but unused
/// leaves no <c>AssemblyRef</c> behind, so <see cref="Assembly.GetReferencedAssemblies"/> alone would
/// pass it (09.1.2 plan A3, CRV-09W1-1).
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
        AssertDeclaredAllowed(
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
        AssertDeclaredAllowed(
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
            AssertDeclaredAllowed(
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
            AssertDeclaredAllowed(
                fetcher,
                AllowLists.FetcherAllowedReferences,
                AllowLists.FetcherAllowedPrefixes,
                nameof(Fetcher_ReferencesOnlyDomainAndCore));
        }
    }

    [Fact]
    public void RenderNode_ReferencesOnlyTheContractAndAspNetCore()
    {
        // 11 README D4: the contract is BCL-only; D3/D5: the server core is Contract + ErrorOr + ASP.NET Core and
        // never Domain, Application or Infrastructure, so a node binary can be loaded without the backend.
        var contract = ArchitectureModel.Require(ArchitectureModel.RenderNodeContract);
        AssertAllowed(
            contract,
            AllowLists.RenderNodeContractAllowedReferences,
            AllowLists.NoExtraPrefixes,
            nameof(RenderNode_ReferencesOnlyTheContractAndAspNetCore));
        AssertDeclaredAllowed(
            contract,
            AllowLists.RenderNodeContractAllowedReferences,
            AllowLists.NoExtraPrefixes,
            nameof(RenderNode_ReferencesOnlyTheContractAndAspNetCore));

        var server = ArchitectureModel.Require(ArchitectureModel.RenderNodeServer);
        AssertAllowed(
            server,
            AllowLists.RenderNodeServerAllowedReferences,
            AllowLists.RenderNodeServerAllowedPrefixes,
            nameof(RenderNode_ReferencesOnlyTheContractAndAspNetCore));
        AssertDeclaredAllowed(
            server,
            AllowLists.RenderNodeServerAllowedReferences,
            AllowLists.RenderNodeServerAllowedPrefixes,
            nameof(RenderNode_ReferencesOnlyTheContractAndAspNetCore));

        var adapter = ArchitectureModel.Require(ArchitectureModel.MptAdapter);
        AssertAllowed(
            adapter,
            AllowLists.MptAdapterAllowedReferences,
            AllowLists.MptAdapterAllowedPrefixes,
            nameof(RenderNode_ReferencesOnlyTheContractAndAspNetCore));
        AssertDeclaredAllowed(
            adapter,
            AllowLists.MptAdapterAllowedReferences,
            AllowLists.MptAdapterAllowedPrefixes,
            nameof(RenderNode_ReferencesOnlyTheContractAndAspNetCore));
    }

    [Fact]
    public void MockMpt_ReferencesOnlyTheServerAndTheContract()
    {
        // 11 README D3: the mock reuses the core and the contract and nothing else, so the fake node can never
        // drag the backend (Api, Worker, Infrastructure) into its image or a node bundle (D12).
        var mock = ArchitectureModel.Require(ArchitectureModel.MockMpt);
        AssertAllowed(
            mock,
            AllowLists.MockMptAllowedReferences,
            AllowLists.MockMptAllowedPrefixes,
            nameof(MockMpt_ReferencesOnlyTheServerAndTheContract));
        AssertDeclaredAllowed(
            mock,
            AllowLists.MockMptAllowedReferences,
            AllowLists.MockMptAllowedPrefixes,
            nameof(MockMpt_ReferencesOnlyTheServerAndTheContract));
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

    /// <summary>
    /// Maps every <em>direct</em> dependency declared in <c>project.assets.json</c> to the assemblies
    /// it actually supplies (compile entries), then applies the same allow-list. An unused reference
    /// is therefore caught; a reference that resolves to no assembly is ignored (analyzers, props).
    /// </summary>
    private static void AssertDeclaredAllowed(
        Assembly assembly,
        IReadOnlySet<string> allowedNames,
        IReadOnlyList<string> allowedPrefixes,
        string rule)
    {
        var offenders = FindDisallowedDeclaredReferences(assembly, allowedNames, allowedPrefixes);
        offenders.ShouldBeEmpty(
            $"{rule}: {assembly.GetName().Name} declares dependencies [{string.Join(", ", offenders)}]; "
            + $"allowed references are [{string.Join(", ", allowedNames)}] plus the framework baseline.");
    }

    private static List<string> FindDisallowedDeclaredReferences(
        Assembly assembly,
        IReadOnlySet<string> allowedNames,
        IReadOnlyList<string> allowedPrefixes)
    {
        var offenders = new List<string>();
        var assetsPath = FindAssetsJson(assembly);
        if (assetsPath is null)
        {
            // Never pass vacuously: without the dependency graph an unused forbidden reference
            // would be invisible (NTS-CRV-09W1-2).
            throw new InvalidOperationException(
                $"obj/project.assets.json not found for {assembly.GetName().Name}; "
                + "restore the project before running the architecture rules.");
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(assetsPath));
        var root = document.RootElement;

        // project.frameworks.<tfm>.dependencies holds the declared direct PackageReferences and
        // ProjectReferences; targets.<tfm> resolves each to its compile-time assemblies.
        var declared = new List<string>();
        foreach (var framework in root.GetProperty("project").GetProperty("frameworks").EnumerateObject())
        {
            if (framework.Value.TryGetProperty("dependencies", out var dependencies))
            {
                foreach (var dependency in dependencies.EnumerateObject())
                {
                    declared.Add(dependency.Name);
                }
            }
        }

        var resolved = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var target in root.GetProperty("targets").EnumerateObject())
        {
            foreach (var library in target.Value.EnumerateObject())
            {
                var key = library.Name;
                var id = key[..key.IndexOf('/')];
                if (!resolved.TryGetValue(id, out var assemblies))
                {
                    assemblies = [];
                    resolved[id] = assemblies;
                }

                if (library.Value.TryGetProperty("type", out var type) && type.GetString() == "project")
                {
                    assemblies.Add(id);
                }
                else if (library.Value.TryGetProperty("compile", out var compile))
                {
                    foreach (var entry in compile.EnumerateObject())
                    {
                        var path = entry.Name;
                        if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        {
                            assemblies.Add(Path.GetFileNameWithoutExtension(path));
                        }
                    }
                }
            }
        }

        foreach (var id in declared)
        {
            if (!resolved.TryGetValue(id, out var assemblies) || assemblies.Count == 0)
            {
                continue;
            }

            foreach (var name in assemblies.Distinct(StringComparer.Ordinal))
            {
                if (AllowLists.IsBaselineReference(name) || allowedNames.Contains(name)
                    || allowedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    continue;
                }

                offenders.Add($"{name} (via declared {id})");
            }
        }

        offenders.Sort(StringComparer.Ordinal);
        return offenders;
    }

    /// <summary>Locates <c>obj/project.assets.json</c> for a source project from the test output directory.</summary>
    private static string? FindAssetsJson(Assembly assembly)
    {
        var name = assembly.GetName().Name ?? throw new InvalidOperationException("unnamed assembly");
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            foreach (var root in new[] { "src", "tests" })
            {
                var candidate = Path.Combine(directory.FullName, root, name, "obj", "project.assets.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            // A tool project (11 README D3: tools/mock-mpt) lives outside src/tests; its directory name does not
            // match the assembly name, so the path is spelled out.
            if (string.Equals(name, "NightTales.MockMpt", StringComparison.Ordinal))
            {
                var tool = Path.Combine(
                    directory.FullName, "tools", "mock-mpt", "obj", "project.assets.json");
                if (File.Exists(tool))
                {
                    return tool;
                }
            }

            directory = directory.Parent;
        }

        return null;
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
