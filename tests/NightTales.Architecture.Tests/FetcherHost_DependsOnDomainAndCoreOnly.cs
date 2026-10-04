using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Domain.Research;
using NightTales.Fetcher;
using NightTales.Fetcher.Core.Research.Http;
using Shouldly;
using Xunit;

namespace NightTales.Architecture.Tests;

/// <summary>
/// 02.5.2-T3 [DECISION T40]: the fetcher container must carry no database, storage, OpenAI or Hangfire code (02 README
/// D10; 11 §5.1). <c>NightTales.Fetcher.Core</c> is checked on its emitted assembly references <em>and</em> on the
/// transitive closure of its declared dependencies (<c>project.assets.json</c>), so a package that pulls EF Core in
/// through a dependency of a dependency fails too.
/// </summary>
/// <remarks>
/// The host (<c>NightTales.Fetcher</c>) is held to the same rules, and its composition root is checked to register the
/// production address validator only (02 D20).
/// </remarks>
public sealed class FetcherDependencyRules
{
    private static readonly string[] ForbiddenPrefixes =
    [
        ArchitectureModel.Infrastructure,
        ArchitectureModel.Application,
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "OpenAI",
        "Hangfire",
        "AWSSDK",
        "Amazon.",
        "Minio",
    ];

    [Fact]
    public void FetcherCore_HasNoEfCoreOrOpenAiReference()
    {
        var core = ArchitectureModel.Require(ArchitectureModel.FetcherCore);

        var emitted = core.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToList();
        AssertClean(ArchitectureModel.FetcherCore, emitted, "references assembly", nameof(FetcherCore_HasNoEfCoreOrOpenAiReference));

        var closure = DeclaredClosure(core);
        AssertClean(ArchitectureModel.FetcherCore, closure, "depends (transitively) on", nameof(FetcherCore_HasNoEfCoreOrOpenAiReference));
    }

    [Fact]
    public void FetcherHost_DependsOnDomainAndCoreOnly()
    {
        var host = ArchitectureModel.Require(ArchitectureModel.Fetcher);
        var rule = nameof(FetcherHost_DependsOnDomainAndCoreOnly);

        var emitted = host.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToList();
        AssertClean(ArchitectureModel.Fetcher, emitted, "references assembly", rule);

        var closure = DeclaredClosure(host);
        AssertClean(ArchitectureModel.Fetcher, closure, "depends (transitively) on", rule);

        // The image is the plain .NET runtime: no ASP.NET Core framework or package, no listener (T43).
        var aspNet = emitted.Concat(closure)
            .Where(name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        aspNet.ShouldBeEmpty($"{rule}: {ArchitectureModel.Fetcher} depends on [{string.Join(", ", aspNet)}]; it opens no listening socket.");
    }

    [Fact]
    public void Fetcher_RegistersProductionAddressValidator()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Fetcher:Internal:BaseUrl"] = "http://127.0.0.1:8081",
                ["FETCHER_API_TOKEN"] = "architecture-test-token-0123456789abcdef",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFetcherHost(configuration);

        services.Count(descriptor => descriptor.ServiceType == typeof(IAddressValidator))
            .ShouldBe(1, "exactly one IAddressValidator registration (D20)");

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IAddressValidator>().ShouldBeOfType<GlobalUnicastAddressValidator>();

        var loopback = services
            .SelectMany(descriptor => new[] { descriptor.ServiceType, descriptor.ImplementationType, descriptor.ImplementationInstance?.GetType() })
            .Where(type => type is not null && type.Name.StartsWith("LoopbackAllowing", StringComparison.Ordinal))
            .ToList();
        loopback.ShouldBeEmpty("no loopback-allowing address validator may be registered in the production composition");
    }

    private static void AssertClean(string subject, IEnumerable<string> names, string verb, string rule)
    {
        var distinct = names.Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).ToList();

        var forbidden = distinct
            .Where(name => ForbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToList();
        forbidden.ShouldBeEmpty(
            $"{rule}: {subject} {verb} [{string.Join(", ", forbidden)}]; the fetcher container "
            + "must have no database, storage, OpenAI or Hangfire code (02 D10).");

        var backend = distinct
            .Where(name => name.StartsWith("NightTales.", StringComparison.Ordinal)
                && !string.Equals(name, ArchitectureModel.Domain, StringComparison.Ordinal)
                && !string.Equals(name, ArchitectureModel.FetcherCore, StringComparison.Ordinal)
                && !string.Equals(name, subject, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        backend.ShouldBeEmpty(
            $"{rule}: {subject} {verb} [{string.Join(", ", backend)}]; its only NightTales.* "
            + $"references are {ArchitectureModel.Domain} and {ArchitectureModel.FetcherCore}.");
    }

    /// <summary>
    /// Every package or project id reachable from the declared direct dependencies, plus the compile assemblies each
    /// supplies, read from <c>obj/project.assets.json</c>. Never passes vacuously: a missing graph throws.
    /// </summary>
    private static List<string> DeclaredClosure(Assembly assembly)
    {
        var assetsPath = FindAssetsJson(assembly)
            ?? throw new InvalidOperationException(
                $"obj/project.assets.json not found for {assembly.GetName().Name}; "
                + "restore the project before running the architecture rules.");

        using var document = JsonDocument.Parse(File.ReadAllBytes(assetsPath));
        var root = document.RootElement;

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var framework in root.GetProperty("project").GetProperty("frameworks").EnumerateObject())
        {
            if (!root.GetProperty("targets").TryGetProperty(framework.Name, out var target)
                || !framework.Value.TryGetProperty("dependencies", out var direct))
            {
                continue;
            }

            var libraries = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var library in target.EnumerateObject())
            {
                libraries[library.Name[..library.Name.IndexOf('/')]] = library.Value;
            }

            var pending = new Stack<string>(direct.EnumerateObject().Select(dependency => dependency.Name));
            while (pending.Count > 0)
            {
                var id = pending.Pop();
                if (!result.Add(id) || !libraries.TryGetValue(id, out var library))
                {
                    continue;
                }

                if (library.TryGetProperty("compile", out var compile))
                {
                    foreach (var entry in compile.EnumerateObject())
                    {
                        if (entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        {
                            result.Add(Path.GetFileNameWithoutExtension(entry.Name));
                        }
                    }
                }

                if (library.TryGetProperty("dependencies", out var dependencies))
                {
                    foreach (var dependency in dependencies.EnumerateObject())
                    {
                        pending.Push(dependency.Name);
                    }
                }
            }
        }

        return [.. result];
    }

    private static string? FindAssetsJson(Assembly assembly)
    {
        var name = assembly.GetName().Name ?? throw new InvalidOperationException("unnamed assembly");
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", name, "obj", "project.assets.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
