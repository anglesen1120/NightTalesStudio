using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Features.Stories.Approval;
using NightTales.Domain.Common;
using NightTales.Infrastructure.DependencyInjection;
using NightTales.Infrastructure.Prompts;
using NightTales.Infrastructure.UnitTests.Prompts;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Stories;

/// <summary>
/// The <c>Story:TrueCrime</c> block (02.3.3-T1, 02 README T29) through the real registration: both keys
/// carry a default, so the block is optional and no deployment breaks by its absence, while a
/// present-but-invalid value — or an attestation version the shipped prompt catalog does not serve —
/// fails the start-up instead of the first approval.
/// </summary>
public sealed class StoryTrueCrimeRegistrationTests
{
    /// <summary>32 characters: the longest accepted marker, one below the limit.</summary>
    private const string MaxLengthVersion = "ta.v1ta.v1ta.v1ta.v1ta.v1ta.v1ta";

    /// <summary>33 characters: one above <see cref="StoryTrueCrimeOptionsValidator.MaxAttestationVersionLength"/>.</summary>
    private const string TooLongVersion = "ta.v1ta.v1ta.v1ta.v1ta.v1ta.v1ta.";

    /// <summary>The Vietnamese attestation clause the released manifest tracks.</summary>
    private const string AttestationVi = "shared/attestation.ta.v1.vi.md";

    /// <summary>The English attestation clause the released manifest tracks.</summary>
    private const string AttestationEn = "shared/attestation.ta.v1.en.md";

    /// <summary>
    /// The container of the host: the configuration of one case and the catalog the version cross-check
    /// reads, so the shipped versions and a relabelled release are both expressible.
    /// </summary>
    private static ServiceProvider Build(Dictionary<string, string?> settings, IPromptCatalog? catalog = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddSingleton(catalog ?? ShippedCatalog());
        services.AddStoryTrueCrime();

        return services.BuildServiceProvider();
    }

    /// <summary>The catalog of the released tree: both clauses carry the shipped <c>ta.v1</c>.</summary>
    private static PromptCatalog ShippedCatalog() => PromptsTree.Load(PromptsTree.Read());

    /// <summary>
    /// The released tree with the manifest's version markers relabelled and the file bytes untouched, so
    /// the check is exercised on a release whose declared version is not the one the configuration names.
    /// </summary>
    /// <param name="viVersion">The version the manifest declares for the Vietnamese clause.</param>
    /// <param name="enVersion">The version for the English clause, or <paramref name="viVersion"/>.</param>
    private static PromptCatalog RelabelledCatalog(string viVersion, string? enVersion = null)
    {
        var tree = PromptsTree.Read();
        Relabel(tree, AttestationVi, viVersion);
        Relabel(tree, AttestationEn, enVersion ?? viVersion);

        return PromptsTree.Load(tree);
    }

    /// <summary>
    /// Rewrites one manifest entry's <c>version</c> only. The file's digest covers its bytes, so this is
    /// not a rule 5 violation: nothing in the catalog itself notices the relabel.
    /// </summary>
    private static void Relabel(Dictionary<string, byte[]> tree, string key, string version)
    {
        var manifest = JsonNode.Parse(Encoding.UTF8.GetString(tree["manifest.json"]))!.AsObject();
        manifest["files"]![key]!["version"] = version;
        tree["manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
    }

    [Fact]
    public void StoryTrueCrimeOptions_MissingBlock_UsesTheDefaults()
    {
        using var provider = Build([]);

        var options = provider.GetRequiredService<StoryTrueCrimeOptions>();

        options.AttestationVersion.ShouldBe("ta.v1");
        options.MinCitedSources.ShouldBe(2);
    }

    [Fact]
    public void StoryTrueCrimeOptions_ValidBlock_BindsBothKeys()
    {
        using var provider = Build(
            new Dictionary<string, string?>
            {
                ["Story:TrueCrime:AttestationVersion"] = "ta.v2",
                ["Story:TrueCrime:MinCitedSources"] = "4",
            },
            RelabelledCatalog("ta.v2"));

        var options = provider.GetRequiredService<StoryTrueCrimeOptions>();

        options.AttestationVersion.ShouldBe("ta.v2");
        options.MinCitedSources.ShouldBe(4);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" ta.v1")]
    [InlineData("ta.v1 ")]
    [InlineData("ta\u0001v1")]
    [InlineData(TooLongVersion)]
    public void StoryTrueCrimeOptions_InvalidAttestationVersion_FailsStartupValidation(string version)
    {
        using var provider = Build(new Dictionary<string, string?> { ["Story:TrueCrime:AttestationVersion"] = version });

        var failure = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<StoryTrueCrimeOptions>());

        failure.Message.ShouldContain("Story:TrueCrime:AttestationVersion");
    }

    [Fact]
    public void StoryTrueCrimeOptions_AttestationVersionAtTheLengthLimit_IsAccepted()
    {
        using var provider = Build(
            new Dictionary<string, string?>
            {
                ["Story:TrueCrime:AttestationVersion"] = MaxLengthVersion,
            },
            RelabelledCatalog(MaxLengthVersion));

        provider.GetRequiredService<StoryTrueCrimeOptions>().AttestationVersion.ShouldBe(MaxLengthVersion);
    }

    [Fact]
    public void StoryTrueCrimeOptions_AttestationVersionTheShippedCatalogDoesNotServe_FailsStartupValidation()
    {
        var catalog = ShippedCatalog();
        using var provider = Build(
            new Dictionary<string, string?> { ["Story:TrueCrime:AttestationVersion"] = "ta.v2" },
            catalog);

        var failure = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<StoryTrueCrimeOptions>());

        failure.Message.ShouldContain("Story:TrueCrime:AttestationVersion");
        var clause = catalog.GetAttestation(Language.Vi).Text;
        clause.ShouldNotBeNullOrWhiteSpace("the negative control needs the real clause text");
        failure.Message
            .Contains(clause, StringComparison.Ordinal)
            .ShouldBeFalse("the check names the key and the language, never a clause text");
    }

    [Fact]
    public void StoryTrueCrimeOptions_AttestationVersionTheCatalogShips_IsAccepted()
    {
        using var provider = Build(
            new Dictionary<string, string?> { ["Story:TrueCrime:AttestationVersion"] = "ta.v2" },
            RelabelledCatalog("ta.v2"));

        provider.GetRequiredService<StoryTrueCrimeOptions>().AttestationVersion.ShouldBe("ta.v2");
    }

    [Fact]
    public void StoryTrueCrimeOptions_CatalogVersionsDifferByLanguage_FailsStartupValidation()
    {
        // The release relabels one language only, so the configured version matches Vi and not En; the
        // check must fail for the language that does not match, not pass on the first one that does.
        using var provider = Build(
            new Dictionary<string, string?> { ["Story:TrueCrime:AttestationVersion"] = "ta.v1" },
            RelabelledCatalog(viVersion: "ta.v1", enVersion: "ta.v2"));

        var failure = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<StoryTrueCrimeOptions>());

        failure.Message.ShouldContain("'En'");
        failure.Message.ShouldNotContain("'Vi'");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("-1")]
    public void StoryTrueCrimeOptions_MinCitedSourcesOutOfRange_FailsStartupValidation(string floor)
    {
        using var provider = Build(new Dictionary<string, string?> { ["Story:TrueCrime:MinCitedSources"] = floor });

        var failure = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<StoryTrueCrimeOptions>());

        failure.Message.ShouldContain("Story:TrueCrime:MinCitedSources");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("10")]
    public void StoryTrueCrimeOptions_MinCitedSourcesAtTheBounds_IsAccepted(string floor)
    {
        using var provider = Build(new Dictionary<string, string?> { ["Story:TrueCrime:MinCitedSources"] = floor });

        provider.GetRequiredService<StoryTrueCrimeOptions>()
            .MinCitedSources.ShouldBe(int.Parse(floor, System.Globalization.CultureInfo.InvariantCulture));
    }
}
