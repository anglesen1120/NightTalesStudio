using System.Text;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Domain.Common;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Prompts;
using NightTales.Infrastructure.Prompts;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Prompts;

/// <summary>
/// The start-up gate of the prompt catalog (02.2.3-T1): rules 1-6 against the real <c>prompts/</c>
/// tree, and one case per way a release can be broken — a missing preset, a stale sha256, an unknown
/// placeholder, an untracked runtime file, a key that must never be in a release manifest.
/// </summary>
/// <remarks>
/// Every case runs over <see cref="PromptsTree.Read"/> with one mutation, so a failure names the rule
/// that broke rather than a difference between a fixture and the tree. The real embedded resources are
/// covered too, because they are what a host actually loads.
/// </remarks>
public sealed class PromptCatalogTests
{
    private const string GhostViPreset = "story/ghost.vi.v1.md";
    private const string GhostEnPreset = "story/ghost.en.v1.md";
    private const string SpiritualViPreset = "story/spiritual.vi.v1.md";
    private const string TrueCrimeEnPreset = "story/true_crime.en.v1.md";
    private const string StorySchema = "schemas/story.v1.json";
    private const string WriterSystemPrompt = "system/writer.v1.md";

    /// <summary>Acceptance check 7: the composite version's shape.</summary>
    private const string PromptVersionPattern =
        @"^rel=\d{4}\.\d{1,2}\.\d{1,2};sys=writer\.v1;preset=(ghost|spiritual|true_crime)\.(vi|en)\.v1;.*schema=story\.v1$";

    [Fact]
    public void PromptCatalog_AllSixPresetsPresent_LoadsAndBuildsPromptVersion()
    {
        var catalog = PromptsTree.Load(PromptsTree.Read());

        catalog.Release.ShouldMatch(@"^\d{4}\.\d{1,2}\.\d{1,2}$");

        foreach (var topic in Enum.GetValues<Topic>())
        {
            foreach (var language in Enum.GetValues<Language>())
            {
                catalog.GetPreset(topic, language).ShouldNotBeNullOrWhiteSpace();
            }
        }

        catalog.GetSystemPrompt().ShouldNotBeNullOrWhiteSpace();
        catalog.GetShared(PromptFile.EarRules, Language.Vi).ShouldNotBeNullOrWhiteSpace();
        catalog.GetShared(PromptFile.Overused, Language.En).ShouldNotBeNullOrWhiteSpace();
        catalog.GetShared(PromptFile.KeywordRules, Language.Vi).ShouldNotBeNullOrWhiteSpace();
        catalog.GetShared(PromptFile.StructureVocab, Language.Vi).ShouldContain("schema_version");
        Encoding.UTF8.GetString(catalog.GetSchema("story.v1.json").Span).ShouldContain("\"$schema\"");

        catalog.BuildPromptVersion(Parts())
            .ShouldMatch(PromptVersionPattern);
    }

    [Fact]
    public void PromptCatalog_MissingGhostViPreset_ThrowsAtStartup()
    {
        var tree = PromptsTree.Read();
        tree.Remove(GhostViPreset);

        var failure = PromptsTree.Failure(tree);

        failure.Message.ShouldContain("missing preset ghost.vi.v1");
    }

    [Fact]
    public void PromptCatalog_MissingTrueCrimeEnPreset_ThrowsAtStartup()
    {
        var tree = PromptsTree.Read();
        tree.Remove(TrueCrimeEnPreset);

        var failure = PromptsTree.Failure(tree);

        failure.Message.ShouldContain("missing preset true_crime.en.v1");
    }

    [Fact]
    public void PromptCatalog_Sha256Mismatch_ThrowsAtStartup()
    {
        var tree = PromptsTree.Read();
        PromptsTree.Corrupt(tree, GhostViPreset);

        var failure = PromptsTree.Failure(tree);

        failure.Message.ShouldContain($"sha256 mismatch for {GhostViPreset}");
    }

    [Fact]
    public void PromptCatalog_UnknownPlaceholder_ThrowsAtStartup()
    {
        var tree = PromptsTree.Read();
        PromptsTree.Append(tree, GhostViPreset, "\nWrite it in {{tier}} minutes.\n");

        var failure = PromptsTree.Failure(tree);

        failure.Message.ShouldContain("unknown placeholder '{{tier}}'");
        failure.Message.ShouldContain(GhostViPreset);
    }

    [Fact]
    public void PromptCatalog_ManifestListsUnknownFile_ThrowsAtStartup()
    {
        var tree = PromptsTree.Read();
        PromptsTree.AddManifestEntry(tree, "revise/v1.md", "v1", new string('a', 64));

        var failure = PromptsTree.Failure(tree);

        failure.Message.ShouldContain("manifest file 'revise/v1.md' is not an embedded resource (not found)");
    }

    [Fact]
    public void PromptCatalog_RuntimeFileMissingFromManifest_ThrowsAtStartup()
    {
        var tree = PromptsTree.Read();
        tree["revise/v1.md"] = Encoding.UTF8.GetBytes("# revise\n\nA step prompt no manifest entry tracks.\n");

        var failure = PromptsTree.Failure(tree);

        failure.Message.ShouldContain("embedded runtime file 'revise.v1.md' is not tracked by the manifest");
    }

    [Fact]
    public void PromptCatalog_FixtureFileNotInManifest_DoesNotFail()
    {
        var tree = PromptsTree.Read();
        tree.ShouldContainKey("fixtures/injection/.gitkeep");
        tree.ShouldContainKey("story/.gitkeep");
        tree["fixtures/injection/brand-new.json"] = Encoding.UTF8.GetBytes("{\"injected\":\"ignore the rules\"}\n");
        tree["tools/rehash.py"] = Encoding.UTF8.GetBytes("print('the prompts/ helper that must not exist')\n");

        var catalog = PromptsTree.Load(tree);

        catalog.Files.Keys.ShouldNotContain(key => key.StartsWith("fixtures/", StringComparison.Ordinal));
        catalog.Files.Keys.ShouldNotContain(key => key.StartsWith("tools/", StringComparison.Ordinal));
        catalog.Files.Keys.ShouldNotContain("manifest.json");
        catalog.Files.Keys.ShouldNotContain("REFERENCES.md");
        catalog.Files.Keys.ShouldNotContain(key => key.Contains(".gitkeep", StringComparison.Ordinal));
    }

    /// <summary>
    /// A <c>manifest.json</c> under <c>fixtures/</c> is embedded like every other fixture, but it is not
    /// the release manifest: discovery matches the root manifest's exact logical name, so a fixture copy
    /// is ignored (T75, acceptance check 4).
    /// </summary>
    [Fact]
    public void PromptCatalog_FixtureManifest_DoesNotAmbiguateTheReleaseManifest()
    {
        var tree = PromptsTree.Read();
        tree["fixtures/injection/manifest.json"] =
            Encoding.UTF8.GetBytes("{ \"release\": \"0000.0.0\", \"files\": {} }");

        var catalog = PromptsTree.Load(tree);

        catalog.Release.ShouldMatch(@"^\d{4}\.\d{1,2}\.\d{1,2}$");
        catalog.Files.Keys.ShouldNotContain("manifest.json");
    }

    [Fact]
    public void PromptCatalog_ManifestKeyAbsolutePath_Rejected()
    {
        var tree = PromptsTree.Read();
        PromptsTree.AddManifestEntry(tree, "/etc/passwd", "v1", new string('a', 64));

        var failure = PromptsTree.Failure(tree);

        failure.Message.ShouldContain("invalid manifest key '/etc/passwd'");
        failure.Message.ShouldContain("relative to prompts/");
    }

    [Fact]
    public void PromptCatalog_ManifestKeyWithDotDot_Rejected()
    {
        var tree = PromptsTree.Read();
        PromptsTree.AddManifestEntry(tree, "../shared/keyword-rules.v1.md", "v1", new string('a', 64));

        var failure = PromptsTree.Failure(tree);

        failure.Message.ShouldContain("invalid manifest key '../shared/keyword-rules.v1.md'");
        failure.Message.ShouldContain("'..' segments are not allowed");
    }

    /// <summary>
    /// Acceptance check 5: a key that repeats the <c>prompts/</c> root is rejected as invalid — the
    /// manifest's keys are relative to <c>prompts/</c> (ruling #4/T27) — and not merely reported as a
    /// file that is missing.
    /// </summary>
    [Fact]
    public void PromptCatalog_ManifestKeyRepeatingPromptsRoot_Rejected()
    {
        var tree = PromptsTree.Read();
        PromptsTree.AddManifestEntry(tree, "prompts/system/writer.v1.md", "v1", new string('a', 64));

        var failure = PromptsTree.Failure(tree);

        failure.Message.ShouldContain("invalid manifest key 'prompts/system/writer.v1.md'");
        failure.Message.ShouldContain("must not repeat the 'prompts/' root");
    }

    [Fact]
    public void PromptCatalog_ReportsAllViolationsNotOnlyFirst()
    {
        var tree = PromptsTree.Read();
        tree.Remove(GhostViPreset);
        PromptsTree.Append(tree, SpiritualViPreset, "\nWrite it in {{tier}} minutes.\n");
        PromptsTree.Corrupt(tree, GhostEnPreset);
        PromptsTree.AddManifestEntry(tree, "prompts/system/writer.v1.md", "v1", new string('a', 64));

        var failure = PromptsTree.Failure(tree);

        failure.Violations.Count.ShouldBeGreaterThanOrEqualTo(5);
        failure.Message.ShouldContain("missing preset ghost.vi.v1");
        failure.Message.ShouldContain($"manifest file '{GhostViPreset}' is not an embedded resource");
        failure.Message.ShouldContain("unknown placeholder '{{tier}}'");
        failure.Message.ShouldContain($"sha256 mismatch for {GhostEnPreset}");
        failure.Message.ShouldContain("invalid manifest key 'prompts/system/writer.v1.md'");
    }

    [Fact]
    public void PromptCatalog_InvalidReleaseFormat_ThrowsAtStartup()
    {
        var tree = PromptsTree.Read();
        PromptsTree.SetRelease(tree, "2026-10-01");

        var failure = PromptsTree.Failure(tree);

        failure.Message.ShouldContain("release '2026-10-01' must match yyyy.M.d");
    }

    [Fact]
    public void PromptCatalog_SchemaNotJson_ThrowsAtStartup()
    {
        var tree = PromptsTree.Read();
        PromptsTree.Write(tree, StorySchema, "{ not json ");

        var failure = PromptsTree.Failure(tree);

        failure.Message.ShouldContain($"{StorySchema} is not valid JSON");
    }

    [Fact]
    public void PromptCatalog_CompositePromptVersion_ContainsReleaseSysPresetSchema()
    {
        var tree = PromptsTree.Read();
        PromptsTree.AddStepPrompts(tree);
        var catalog = PromptsTree.Load(tree);

        var version = catalog.BuildPromptVersion(
            new PromptVersionParts(WriterSystemPrompt, GhostViPreset, StorySchema, "review/v1.md", "revise/v1.md"));

        version.ShouldBe($"rel={catalog.Release};sys=writer.v1;preset=ghost.vi.v1;review=v1;revise=v1;schema=story.v1");
        version.ShouldMatch(PromptVersionPattern);
    }

    [Fact]
    public void PromptCatalog_CompositePromptVersion_OmitsReviewWhenTheRunHasNoReviewStep()
    {
        var catalog = PromptsTree.Load(PromptsTree.Read());

        var version = catalog.BuildPromptVersion(Parts());

        version.ShouldBe($"rel={catalog.Release};sys=writer.v1;preset=ghost.vi.v1;schema=story.v1");
        version.ShouldNotContain("review=");
        version.ShouldNotContain("revise=");
    }

    [Fact]
    public void PromptCatalog_CompositePromptVersion_UntrackedKey_Throws()
    {
        var catalog = PromptsTree.Load(PromptsTree.Read());

        var failure = Should.Throw<PromptCatalogException>(
            () => catalog.BuildPromptVersion(new PromptVersionParts(WriterSystemPrompt, GhostViPreset, "revise/v1.md")));

        failure.Message.ShouldContain("prompt version key 'revise/v1.md' is not tracked by the manifest");
    }

    /// <summary>
    /// A step prompt that has not shipped yet is a lookup miss, not a start-up failure: the catalog
    /// loads (rule 3 does not require the step prompts) and only the lookup that asks for one fails.
    /// </summary>
    [Fact]
    public void PromptCatalog_StepPromptNotShippedYet_LoadsAndFailsOnlyOnLookup()
    {
        var catalog = PromptsTree.Load(PromptsTree.Read());

        var failure = Should.Throw<PromptCatalogException>(() => catalog.GetStepPrompt(PromptStep.Revision));

        failure.Message.ShouldContain("the manifest does not track 'revise/v1.md'");
        catalog.GetStepPrompt(PromptStep.Writer).ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The real embedded tree, not the working copy: this is what a host loads, and it pins the logical
    /// names <c>LinkBase="Prompts"</c> produces ([INFERENCE] confirmed by this case).
    /// </summary>
    [Fact]
    public void PromptCatalog_EmbeddedLogicalNames_UseThePromptsLinkBasePrefix()
    {
        var source = new EmbeddedPromptResourceSource();

        source.LogicalNames.ShouldContain("NightTales.Infrastructure.Prompts.manifest.json");
        source.LogicalNames.ShouldContain("NightTales.Infrastructure.Prompts.story.ghost.vi.v1.md");

        var catalog = new PromptCatalog(source);

        catalog.Release.ShouldMatch(@"^\d{4}\.\d{1,2}\.\d{1,2}$");
        catalog.GetPreset(Topic.Ghost, Language.Vi).ShouldNotBeNullOrWhiteSpace();
        catalog.Files.ShouldContainKey(WriterSystemPrompt);
    }

    /// <summary>Rule 1: a tree with no manifest at all fails naming the missing file, not a NullReference.</summary>
    [Fact]
    public void PromptCatalog_ManifestMissing_ThrowsAtStartup()
    {
        var tree = PromptsTree.Read();
        tree.Remove("manifest.json");

        var failure = PromptsTree.Failure(tree);

        failure.Message.ShouldContain("prompts/manifest.json is not an embedded resource (not found)");
        failure.Message.ShouldContain("missing preset ghost.vi.v1");
    }

    /// <summary>
    /// A nested <c>manifest.json</c> never stands in for the release manifest: with the root one gone the
    /// load reports "not found" instead of parsing the fixture as the release contract (02 README T75).
    /// </summary>
    [Fact]
    public void PromptCatalog_RootManifestMissing_WithFixtureManifestPresent_ReportsNotFound()
    {
        var tree = PromptsTree.Read();
        tree.Remove("manifest.json");
        tree["fixtures/injection/manifest.json"] =
            Encoding.UTF8.GetBytes("{ \"release\": \"0000.0.0\", \"files\": {} }");

        var failure = PromptsTree.Failure(tree);

        failure.Message.ShouldContain("prompts/manifest.json is not an embedded resource (not found)");
    }

    /// <summary>Rule 1: a manifest that is not JSON, or has no release, is a named violation.</summary>
    [Fact]
    public void PromptCatalog_ManifestNotJsonOrNoRelease_ThrowsAtStartup()
    {
        var broken = PromptsTree.Read();
        PromptsTree.WriteManifest(broken, "{ not json ");
        PromptsTree.Failure(broken).Message.ShouldContain("prompts/manifest.json is not valid JSON");

        var noRelease = PromptsTree.Read();
        PromptsTree.RemoveManifestRootMember(noRelease, "release");
        PromptsTree.Failure(noRelease).Message.ShouldContain("prompts/manifest.json has no string 'release'");
    }

    /// <summary>Ruling #4: two keys that normalise to the same path are a duplicate, not a silent overwrite.</summary>
    [Fact]
    public void PromptCatalog_DuplicateKeyAfterBackslashNormalisation_ThrowsAtStartup()
    {
        var tree = PromptsTree.Read();
        // JSON property names are case-sensitive and literal: "story\\ghost.en.v1.md" coexists with
        // "story/ghost.en.v1.md" in the document and collides only after \→/ normalisation.
        PromptsTree.AddManifestEntry(tree, "story\\ghost.en.v1.md", "v2", new string('b', 64));

        var failure = PromptsTree.Failure(tree);

        failure.Message.ShouldContain("duplicate manifest key 'story/ghost.en.v1.md' after normalisation");
    }

    /// <summary>Rule 5: a tracked entry without a sha256 is a violation of its own, not a mismatch.</summary>
    [Fact]
    public void PromptCatalog_ManifestEntryWithoutSha256_ThrowsAtStartup()
    {
        var tree = PromptsTree.Read();
        PromptsTree.RemoveManifestMember(tree, GhostViPreset, "sha256");

        var failure = PromptsTree.Failure(tree);

        failure.Message.ShouldContain($"manifest entry '{GhostViPreset}' has no sha256");
    }

    /// <summary>
    /// A missing sha256 does not stop the same load from checking the file's content: the bytes are read
    /// before the digest is examined, so the missing digest and the bad placeholder are reported together
    /// rather than one per run.
    /// </summary>
    [Fact]
    public void PromptCatalog_EntryWithoutSha256_StillReportsContentViolations()
    {
        var tree = PromptsTree.Read();
        PromptsTree.Append(tree, GhostViPreset, "\nWrite it in {{tier}} minutes.\n");
        PromptsTree.RemoveManifestMember(tree, GhostViPreset, "sha256");

        var failure = PromptsTree.Failure(tree);

        failure.Message.ShouldContain($"manifest entry '{GhostViPreset}' has no sha256");
        failure.Message.ShouldContain("unknown placeholder '{{tier}}' in " + GhostViPreset);
    }

    /// <summary>An entry that is a string rather than an object is reported, not deserialised into a crash.</summary>
    [Fact]
    public void PromptCatalog_ManifestEntryNotObject_ThrowsAtStartup()
    {
        var tree = PromptsTree.Read();
        var manifest = System.Text.Json.Nodes.JsonNode
            .Parse(Encoding.UTF8.GetString(tree["manifest.json"]))!.AsObject();
        manifest["files"]![GhostViPreset] = "v1";
        tree["manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());

        var failure = PromptsTree.Failure(tree);

        failure.Message.ShouldContain($"manifest entry '{GhostViPreset}' must be an object");
    }

    /// <summary>Rule 4b's exclusion is path-based: a root that merely shares a prefix with an excluded root is tracked.</summary>
    [Fact]
    public void PromptCatalog_UntrackedFileUnderNonExcludedRoot_ThrowsAtStartup()
    {
        var tree = PromptsTree.Read();
        tree["toolshed/v1.md"] = Encoding.UTF8.GetBytes("# a runtime file under a root that is not tools/\n");
        tree["story/.hidden/v1.md"] = Encoding.UTF8.GetBytes("# a dotfile: excluded, must NOT raise a violation\n");

        var failure = PromptsTree.Failure(tree);

        failure.Message.ShouldContain("embedded runtime file 'toolshed.v1.md' is not tracked by the manifest");
        failure.Message.ShouldNotContain(".hidden");
    }

    /// <summary>A manifest key with an empty path segment is rejected as invalid, not "not found".</summary>
    [Fact]
    public void PromptCatalog_ManifestKeyWithEmptySegment_Rejected()
    {
        var tree = PromptsTree.Read();
        PromptsTree.AddManifestEntry(tree, "story//ghost.vi.v1.md", "v1", new string('a', 64));

        var failure = PromptsTree.Failure(tree);

        failure.Message.ShouldContain("invalid manifest key 'story//ghost.vi.v1.md'");
        failure.Message.ShouldContain("empty path segment");
    }

    /// <summary>Rule 6 fires on every tracked file, not only presets: a placeholder in the writer system prompt fails.</summary>
    [Fact]
    public void PromptCatalog_UnknownPlaceholderInSystemPrompt_ThrowsAtStartup()
    {
        var tree = PromptsTree.Read();
        PromptsTree.Append(tree, WriterSystemPrompt, "\nSpeak in {{languageName}}.\n");

        var failure = PromptsTree.Failure(tree);

        // Case matters: language_name is closed-list, languageName is not.
        failure.Message.ShouldContain("unknown placeholder '{{languageName}}' in system/writer.v1.md");
    }

    /// <summary>A truncated placeholder at the end of a tracked file is reported rather than ignored,
    /// even when the token names a closed placeholder: the literal must not reach the model.</summary>
    [Fact]
    public void PromptCatalog_UnterminatedPlaceholder_ThrowsAtStartup()
    {
        var tree = PromptsTree.Read();
        PromptsTree.Append(tree, GhostViPreset, "\nWrite it in {{ear_rules");

        var failure = PromptsTree.Failure(tree);

        failure.Message.ShouldContain("unterminated placeholder '{{ear_rules' in " + GhostViPreset);
    }

    /// <summary>An empty placeholder name is not in the closed list.</summary>
    [Fact]
    public void PromptCatalog_EmptyPlaceholder_ThrowsAtStartup()
    {
        var tree = PromptsTree.Read();
        PromptsTree.Append(tree, GhostViPreset, "\nThe {{}} value.\n");

        var failure = PromptsTree.Failure(tree);

        failure.Message.ShouldContain("unknown placeholder '{{}}'");
    }

    /// <summary>Rule 6: a schema file that parses but is not a JSON object is a violation.</summary>
    [Fact]
    public void PromptCatalog_SchemaNotJsonObject_ThrowsAtStartup()
    {
        var tree = PromptsTree.Read();
        PromptsTree.Write(tree, StorySchema, "[1,2]");

        var failure = PromptsTree.Failure(tree);

        failure.Message.ShouldContain($"{StorySchema} must be a JSON object");
    }

    /// <summary>Rule 3: a shared block a preset substitutes is required — removing its entry fails start-up.</summary>
    [Fact]
    public void PromptCatalog_SharedFileMissing_ThrowsAtStartup()
    {
        var tree = PromptsTree.Read();
        PromptsTree.RemoveManifestEntry(tree, "shared/ear-rules.en.v1.md");

        var failure = PromptsTree.Failure(tree);

        failure.Message.ShouldContain("missing required file 'shared/ear-rules.en.v1.md'");
        failure.Message.ShouldContain(
            "embedded runtime file 'shared.ear-rules.en.v1.md' is not tracked by the manifest");
    }

    /// <summary>GetSchema refuses a blank or untracked name; lookups never reach for the file system.</summary>
    [Fact]
    public void PromptCatalog_GetSchema_RejectsUnknownAndBlankNames()
    {
        var catalog = PromptsTree.Load(PromptsTree.Read());

        Should.Throw<PromptCatalogException>(() => catalog.GetSchema("nope.v1.json"))
            .Message.ShouldContain("the manifest does not track 'schemas/nope.v1.json'");
        Should.Throw<ArgumentException>(() => catalog.GetSchema(" "));
    }

    /// <summary>An out-of-range PromptFile is a programming error, rejected before any key lookup.</summary>
    [Fact]
    public void PromptCatalog_GetShared_UnknownPromptFile_Throws()
    {
        var catalog = PromptsTree.Load(PromptsTree.Read());

        Should.Throw<ArgumentOutOfRangeException>(() => catalog.GetShared((PromptFile)99, Language.Vi));
        Should.Throw<ArgumentOutOfRangeException>(() => catalog.GetStepPrompt((PromptStep)99));
    }


    private static PromptVersionParts Parts() => new(WriterSystemPrompt, GhostViPreset, StorySchema);
}
