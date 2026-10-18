using System.Reflection;
using Shouldly;
using Xunit;

namespace NightTales.Architecture.Tests;

/// <summary>
/// The two seams 06.2.3-T1 must not duplicate (CR-15/CR-16) and the egress posture of the upload path
/// (<c>InfrastructureOnly_GoogleApis</c>, plan §3): one options type and the frozen registration pair own the
/// <c>YouTube</c> section, and only Infrastructure names the Google SDK.
/// </summary>
/// <remarks>
/// Both rules read metadata only, so neither needs a loaded type model: the type and method counts come off the
/// loaded source assemblies, and the Google rule reads the <em>emitted</em> assembly references (the
/// <c>QuotaLedgerRules</c> posture). That also keeps the rule independent of which dependencies the architecture
/// test host happened to compile.
/// </remarks>
public sealed class PublishingUploadRules
{
    /// <summary>The one home of the section's options class (CR-15: 06.1.2-T1's file, never a second copy).</summary>
    private const string YouTubeOptionsTypeName = "NightTales.Application.Publishing.YouTubeOptions";

    private const string GoogleAssemblyPrefix = "Google.";

    /// <summary>
    /// The registration pair the upload path extends, sorted ordinally: 06.3.1-T1's quota ledger (through
    /// <c>AddPersistence</c>) and 06.1.2-T1's connect flow. A third <c>AddYouTube*</c> entry — a second options
    /// binding or a second registration extension — fails <see cref="NoSecondOptionsBindingOrRegistrationExtension_Exists"/>.
    /// </summary>
    private static readonly string[] FrozenRegistrationEntries =
    [
        "NightTales.Infrastructure.DependencyInjection.YouTubeServiceCollectionExtensions.AddYouTubePublishing(IServiceCollection)",
        "NightTales.Infrastructure.Publishing.PublishingServiceCollectionExtensions.AddYouTubePublishing(IServiceCollection, IConfiguration)",
    ];

    /// <summary>
    /// The assemblies that must never name a Google SDK type: the domain, the application layer and both hosts.
    /// Infrastructure is deliberately not one of them — it is the adapter that owns the SDK reference.
    /// </summary>
    private static readonly string[] GoogleFreeAssemblyNames =
    [
        "NightTales.Domain",
        "NightTales.Application",
        "NightTales.Api",
        "NightTales.Worker",
    ];

    [Fact]
    public void NoSecondOptionsBindingOrRegistrationExtension_Exists()
    {
        var types = SourceTypes().ToArray();

        // One options class, at the one path 06.1.2-T1 gave it: a second `YouTubeOptions` (the divergence CR-15
        // settled, where 06.2.4-T1 assumed `Features/Publishing/Options`) would let two hosts bind two shapes of the
        // same section and validate only one of them.
        var optionsTypes = types.Where(type => type.Name == "YouTubeOptions").ToArray();

        optionsTypes.Length.ShouldBe(1);
        optionsTypes[0].FullName.ShouldBe(YouTubeOptionsTypeName);

        // No second registration or binding entry: the frozen pair is the whole `AddYouTube*` surface of the tree.
        var registrations = types
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => method.Name.StartsWith("AddYouTube", StringComparison.Ordinal))
            .Select(Signature)
            .Order(StringComparer.Ordinal)
            .ToArray();

        registrations.ShouldBe(FrozenRegistrationEntries.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void AnyFeatureCode_NeverReferencesGoogleApis()
    {
        // 06.2.3-T1: every Google call the publishing feature makes goes through `IYouTubeClient`, so the SDK stays
        // behind the Infrastructure adapter and a host can be built and tested without it.
        // `Application_ReferencesOnlyAllowedPackages` already covers Application's declared packages and
        // `QuotaLedgerRules` covers the ledger's signatures; this rule adds the emitted-reference check for the
        // Domain and both hosts, which no rule covered before.
        var offenders = new List<string>();

        foreach (var assembly in ArchitectureModel.SourceAssemblies)
        {
            var name = assembly.GetName().Name;
            if (name is null || !GoogleFreeAssemblyNames.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            offenders.AddRange(
                assembly.GetReferencedAssemblies()
                    .Where(reference =>
                        reference.Name?.StartsWith(GoogleAssemblyPrefix, StringComparison.Ordinal) == true)
                    .Select(reference => $"{name} references {reference.Name}"));
        }

        offenders.ShouldBeEmpty(
            $"{nameof(AnyFeatureCode_NeverReferencesGoogleApis)}: {string.Join("; ", offenders)}");
    }

    private static IEnumerable<Type> SourceTypes() =>
        ArchitectureModel.SourceAssemblies.SelectMany(SafeGetTypes);

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type is not null).Cast<Type>();
        }
    }

    private static string Signature(MethodInfo method) =>
        $"{method.DeclaringType?.FullName}.{method.Name}("
        + string.Join(", ", method.GetParameters().Select(parameter => parameter.ParameterType.Name))
        + ")";
}
