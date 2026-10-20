using NightTales.Domain.Common;
using NightTales.Infrastructure.Prompts;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Prompts;

/// <summary>
/// <see cref="RealWorldLexiconProvider"/> over the D5 prompt tree (02.8.2-T2): each language serves its
/// own framing phrases, a <c>#</c> comment and a blank line never reach the detector, an exact
/// duplicate is collapsed, and a release whose lexicon carries no phrase fails instead of detecting
/// nothing.
/// </summary>
/// <remarks>
/// The parse cases run over the real <c>prompts/</c> tree with one rewritten lexicon, so the bytes under
/// test are the released ones; the embedded case is the resource a host actually reads.
/// </remarks>
public sealed class RealWorldLexiconProviderTests
{
    private const string LexiconVi = "shared/real-world-framing.vi.v1.txt";
    private const string LexiconEn = "shared/real-world-framing.en.v1.txt";

    [Fact]
    public void RealWorldLexiconProvider_ViAndEn_ServeTheCanonicalFramingPhrases()
    {
        var provider = new RealWorldLexiconProvider(PromptsTree.Source(PromptsTree.Read()));

        var vi = provider.PhrasesFor(Language.Vi);
        var en = provider.PhrasesFor(Language.En);

        vi.ShouldNotBeEmpty();
        en.ShouldNotBeEmpty();

        // F31: the two canonical signals the detector must be able to match, one per language.
        vi.ShouldContain("dựa trên sự kiện có thật");
        en.ShouldContain("based on real events");

        vi.ShouldNotContain("true story");
        en.ShouldNotContain("dựa trên sự kiện có thật");

        foreach (var phrases in new[] { vi, en })
        {
            phrases.ShouldAllBe(phrase => phrase == phrase.Trim());
            phrases.ShouldAllBe(phrase => phrase.Length > 0 && !phrase.StartsWith('#'));
            phrases.Distinct(StringComparer.Ordinal).Count().ShouldBe(phrases.Count);
        }
    }

    [Fact]
    public void RealWorldLexiconProvider_SkipsCommentsAndBlankLines_AndCollapsesDuplicates()
    {
        var tree = PromptsTree.Read();
        PromptsTree.Write(
            tree,
            LexiconEn,
            "# Real-person / real-event framing signals — test fixture.\n"
            + "\n"
            + "  based on real events  \n"
            + "based on real events\n"
            + "\t\n"
            + "a true story\n"
            + "# a second comment\n");

        var phrases = new RealWorldLexiconProvider(PromptsTree.Source(tree)).PhrasesFor(Language.En);

        phrases.ShouldBe(["based on real events", "a true story"]);
    }

    [Fact]
    public void RealWorldLexiconProvider_ParsesEachLanguageOnce()
    {
        var provider = new RealWorldLexiconProvider(PromptsTree.Source(PromptsTree.Read()));

        provider.PhrasesFor(Language.Vi).ShouldBeSameAs(provider.PhrasesFor(Language.Vi));
        provider.PhrasesFor(Language.En).ShouldBeSameAs(provider.PhrasesFor(Language.En));
    }

    [Fact]
    public void RealWorldLexiconProvider_LexiconWithoutPhrases_ThrowsInsteadOfServingNothing()
    {
        var tree = PromptsTree.Read();
        PromptsTree.Write(tree, LexiconVi, "# Dấu hiệu chuyện thật / người thật — an emptied lexicon.\n\n   \n");

        var provider = new RealWorldLexiconProvider(PromptsTree.Source(tree));

        Should.Throw<InvalidOperationException>(() => provider.PhrasesFor(Language.Vi))
            .Message.ShouldContain(LexiconVi);
    }

    [Fact]
    public void RealWorldLexiconProvider_ResourceMissing_ThrowsNamingTheKey()
    {
        var tree = PromptsTree.Read();
        tree.Remove(LexiconEn);

        var provider = new RealWorldLexiconProvider(PromptsTree.Source(tree));

        Should.Throw<InvalidOperationException>(() => provider.PhrasesFor(Language.En))
            .Message.ShouldContain(LexiconEn);
    }

    /// <summary>
    /// The start-up gate of the lexicon (D5): <c>AddPromptCatalog</c> registers this singleton as an
    /// <c>IHostedService</c>, so <c>StartAsync</c> is what parses both languages during the boot — a release
    /// that ships an empty lexicon must abort there, not at the first content gate, and the cached lists are
    /// the ones later gates read.
    /// </summary>
    [Fact]
    public async Task RealWorldLexiconProvider_StartAsync_ParsesBothLanguagesAndServesThemFromCache()
    {
        var provider = new RealWorldLexiconProvider(PromptsTree.Source(PromptsTree.Read()));

        await provider.StartAsync(TestContext.Current.CancellationToken);

        var vi = provider.PhrasesFor(Language.Vi);
        var en = provider.PhrasesFor(Language.En);

        vi.ShouldNotBeEmpty();
        en.ShouldNotBeEmpty();
        vi.ShouldBeSameAs(provider.PhrasesFor(Language.Vi));
        en.ShouldBeSameAs(provider.PhrasesFor(Language.En));
    }

    /// <summary>
    /// Acceptance: an empty lexicon throws at start-up. Without this case, dropping the hosted-service
    /// registration or <c>StartAsync</c> would leave every other test green while the release only failed at
    /// the first gate.
    /// </summary>
    [Fact]
    public async Task RealWorldLexiconProvider_StartAsync_EmptyLexicon_AbortsStartUp()
    {
        var tree = PromptsTree.Read();
        PromptsTree.Write(tree, LexiconEn, "# Real-person / real-event framing signals — an emptied lexicon.\n\n");

        var provider = new RealWorldLexiconProvider(PromptsTree.Source(tree));

        var failure = await Should.ThrowAsync<InvalidOperationException>(
            () => provider.StartAsync(TestContext.Current.CancellationToken));

        failure.Message.ShouldContain(LexiconEn);
    }

    /// <summary>
    /// The real embedded tree: <c>LinkBase="Prompts"</c> and the <c>.vi</c>/<c>.en</c> markers before the
    /// extension must not move the file into a satellite assembly, so a host reads both lexicons.
    /// </summary>
    [Fact]
    public void RealWorldLexiconProvider_EmbeddedResources_ServeBothLanguages()
    {
        var provider = new RealWorldLexiconProvider(new EmbeddedPromptResourceSource());

        provider.PhrasesFor(Language.Vi).ShouldContain("dựa trên sự kiện có thật");
        provider.PhrasesFor(Language.En).ShouldContain("based on real events");
    }
}
