using System.Text;
using System.Text.Json.Nodes;
using NightTales.Application.Features.Stories.Approval;
using NightTales.Domain.Common;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Prompts;

/// <summary>
/// <see cref="AttestationTextProvider"/> over the real embedded prompt catalog (02.3.3-T1, 02 README
/// T29): each language serves its own wording of <c>ta.v1</c>, and the version it reports is the one the
/// release manifest declares for the file — not the marker in the file name.
/// </summary>
public sealed class AttestationTextProviderTests
{
    private const string AttestationVi = "shared/attestation.ta.v1.vi.md";
    private const string AttestationEn = "shared/attestation.ta.v1.en.md";

    [Fact]
    public async Task AttestationTextProvider_ViAndEn_DifferAndMatchManifestVersion()
    {
        var catalog = PromptsTree.Load(PromptsTree.Read());
        var provider = new AttestationTextProvider(catalog);

        var (viVersion, viText) = await provider.GetAsync(Language.Vi, TestContext.Current.CancellationToken);
        var (enVersion, enText) = await provider.GetAsync(Language.En, TestContext.Current.CancellationToken);

        viVersion.ShouldBe(catalog.Files[AttestationVi].Version);
        enVersion.ShouldBe(catalog.Files[AttestationEn].Version);
        viVersion.ShouldBe("ta.v1");
        enVersion.ShouldBe("ta.v1");

        viText.ShouldNotBeNullOrWhiteSpace();
        enText.ShouldNotBeNullOrWhiteSpace();
        viText.ShouldNotBe(enText);

        // The three clauses of 02.3.3 in both languages: sources, private persons, victims.
        viText.ShouldContain("nguồn");
        viText.ShouldContain("người riêng tư");
        viText.ShouldContain("Nạn nhân");
        enText.ShouldContain("sources");
        enText.ShouldContain("private person");
        enText.ShouldContain("Victims");
    }

    /// <summary>
    /// The declared version is what a stored tick is compared against, so the provider reports the
    /// manifest's value: a release that relabels the file but keeps its bytes is not a rule 5 violation —
    /// that digest covers the file's bytes only — so nothing in the catalog itself notices the change.
    /// What makes the relabel safe is the start-up check that compares this value with
    /// <c>Story:TrueCrime:AttestationVersion</c> for every language: a manifest that declares
    /// <c>ta.v2</c> stops matching every stored <c>ta.v1</c> tick, and a host whose configuration was not
    /// moved with it never starts.
    /// </summary>
    [Fact]
    public async Task AttestationTextProvider_ReportsTheManifestVersion_NotTheFileNameMarker()
    {
        var tree = PromptsTree.Read();
        var manifest = JsonNode.Parse(Encoding.UTF8.GetString(tree["manifest.json"]))!.AsObject();
        manifest["files"]![AttestationVi]!["version"] = "ta.v2";
        tree["manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());

        var provider = new AttestationTextProvider(PromptsTree.Load(tree));

        var (version, text) = await provider.GetAsync(Language.Vi, TestContext.Current.CancellationToken);

        version.ShouldBe("ta.v2");
        text.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task AttestationTextProvider_CancelledToken_ThrowsBeforeAnyLookup()
    {
        var provider = new AttestationTextProvider(PromptsTree.Load(PromptsTree.Read()));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            async () => await provider.GetAsync(Language.Vi, cancelled.Token));
    }
}
