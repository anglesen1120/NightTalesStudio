using Shouldly;
using Xunit;

namespace NightTales.Architecture.Tests;

/// <summary>
/// F22 (02 README D5): the prompt catalog is loaded and validated once at start-up and then answers
/// from memory, so neither the catalog nor the manifest loader may touch the file system — not per
/// request, and not per lookup.
/// </summary>
/// <remarks>
/// <para>
/// The rule is checked on the IL, not on the source text: every call site of the two types — including
/// the sites of the lambdas they contain — is scanned for a member declared under <c>System.IO</c>.
/// <c>newobj</c> and <c>ldtoken</c> are part of the scan, so <c>new FileStream(...)</c> and
/// <c>typeof(FileInfo)</c> are caught as well as <c>File.ReadAllBytes(...)</c> and
/// <c>Directory.GetFiles(...)</c>.
/// </para>
/// <para>
/// <c>EmbeddedPromptResourceSource</c> is deliberately not in the rule: it is the one type that reads,
/// it reads embedded resources rather than a directory, and the catalog reaches it only through the
/// <c>IPromptResourceSource</c> seam, once, from its constructor.
/// </para>
/// </remarks>
public sealed class PromptCatalogRules
{
    private const string PromptCatalog = "NightTales.Infrastructure.Prompts.PromptCatalog";
    private const string PromptManifestLoader = "NightTales.Infrastructure.Prompts.PromptManifestLoader";
    private const string SystemIo = "System.IO";

    [Fact]
    public void PromptCatalog_NoDiskReadAtRequestTime()
    {
        string[] types = [PromptCatalog, PromptManifestLoader];
        var violations = new List<string>();

        foreach (var type in types)
        {
            var sites = CallSiteRules.FindCallsInType(type);

            // A renamed type would leave the scan empty and the rule vacuous.
            sites.ShouldNotBeEmpty($"{type} was not found in the source assemblies, so the rule proved nothing.");

            foreach (var site in sites)
            {
                if (site.DeclaringType == SystemIo
                    || site.DeclaringType.StartsWith(SystemIo + ".", StringComparison.Ordinal))
                {
                    violations.Add($"{type}.{site.Method} calls {site.DeclaringType}.{site.CalledMember}");
                }
            }
        }

        violations.ShouldBeEmpty(
            $"{nameof(PromptCatalog_NoDiskReadAtRequestTime)}: the prompt catalog must answer from memory "
            + $"(02 README F22); found {string.Join("; ", violations)}");
    }
}
