using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Infrastructure.Prompts;
using Shouldly;

namespace NightTales.Infrastructure.UnitTests.Prompts;

/// <summary>
/// A dictionary-backed <see cref="IPromptResourceSource"/>: the catalog's seam, so every rule of the
/// start-up gate can be exercised on a tree the test owns — a missing preset, one flipped byte, an
/// extra placeholder — with no file system involved at all.
/// </summary>
internal sealed class FakePromptResourceSource : IPromptResourceSource
{
    private readonly Dictionary<string, byte[]> _resources;

    /// <summary>Wraps <paramref name="resources"/>, keyed by logical name.</summary>
    /// <param name="resources">The resource bytes; the source does not copy them.</param>
    internal FakePromptResourceSource(Dictionary<string, byte[]> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);

        _resources = resources;
        LogicalNames = [.. resources.Keys.Order(StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> LogicalNames { get; }

    /// <inheritdoc />
    public byte[] Read(string logicalName) =>
        _resources.TryGetValue(logicalName, out var bytes)
            ? bytes
            : throw new InvalidOperationException($"The fake tree has no resource named '{logicalName}'.");
}

/// <summary>
/// The real <c>prompts/</c> tree read from disk, plus the mutations the start-up gate's cases need.
/// </summary>
/// <remarks>
/// The tree is read from the working copy rather than from the embedded resources, so a case can take
/// the released tree and break exactly one thing — delete a preset, flip a byte, add a placeholder —
/// and assert the one violation that follows. <see cref="LinkBasePrefix"/> mirrors what
/// <c>LinkBase="Prompts"</c> produces, because the loader addresses the release manifest by its exact
/// logical name; <c>PromptCatalog_EmbeddedLogicalNames_UseThePromptsLinkBasePrefix</c> pins the real one.
/// </remarks>
internal static class PromptsTree
{
    /// <summary>The logical-name prefix the embedded <c>prompts/**</c> resources carry.</summary>
    internal const string LinkBasePrefix = "NightTales.Infrastructure.Prompts.";

    private const string Manifest = "manifest.json";

    /// <summary>
    /// The files under <c>prompts/</c> that <c>NightTales.Infrastructure.csproj</c> deliberately removes from its
    /// prompt-tree embed, so this fake tree leaves them out too: they are not catalog prompts. 02.8.3-T2's two
    /// authorization texts live in the tree as their source of truth but their ten <c>{{…}}</c> tokens are outside
    /// the closed <see cref="NightTales.Application.Abstractions.Prompts.PromptPlaceholders"/> set rule 6 enforces;
    /// <c>NightTales.Application.csproj</c> embeds them (linked from the same files) and
    /// <c>AuthorizationTextRenderer</c> reads them there.
    /// </summary>
    private static readonly string[] NotCatalogPrompts =
    [
        "shared/autopilot-authorization.vi.v1.md",
        "shared/autopilot-authorization.en.v1.md",
    ];

    /// <summary>The repository's <c>prompts/</c> directory.</summary>
    internal static string Root { get; } = FindRoot();

    /// <summary>Every file of the tree, keyed by its prompts-relative path with <c>/</c>.</summary>
    /// <returns>The tree, including the files a release never tracks (fixtures, dotfiles, the manifest).</returns>
    internal static Dictionary<string, byte[]> Read()
    {
        var tree = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            var key = Path.GetRelativePath(Root, path).Replace('\\', '/');
            if (NotCatalogPrompts.Contains(key, StringComparer.Ordinal))
            {
                continue;
            }

            tree[key] = File.ReadAllBytes(path);
        }

        return tree;
    }

    /// <summary>A resource source over <paramref name="tree"/> with the given logical-name prefix.</summary>
    /// <param name="tree">The prompts-relative files.</param>
    /// <param name="prefix">The logical-name prefix to build the names with.</param>
    /// <returns>The source the catalog is constructed from.</returns>
    internal static FakePromptResourceSource Source(
        IDictionary<string, byte[]> tree,
        string prefix = LinkBasePrefix)
    {
        ArgumentNullException.ThrowIfNull(tree);

        var resources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (path, bytes) in tree)
        {
            resources[prefix + path.Replace('/', '.')] = bytes;
        }

        return new FakePromptResourceSource(resources);
    }

    /// <summary>Loads the catalog of <paramref name="tree"/>.</summary>
    /// <param name="tree">The prompts-relative files.</param>
    /// <param name="prefix">The logical-name prefix to build the names with.</param>
    /// <returns>The loaded catalog.</returns>
    internal static PromptCatalog Load(IDictionary<string, byte[]> tree, string prefix = LinkBasePrefix) =>
        new(Source(tree, prefix));

    /// <summary>The failure of loading <paramref name="tree"/>.</summary>
    /// <param name="tree">The prompts-relative files.</param>
    /// <param name="prefix">The logical-name prefix to build the names with.</param>
    /// <returns>The exception, for the caller's assertions.</returns>
    internal static PromptCatalogException Failure(
        IDictionary<string, byte[]> tree,
        string prefix = LinkBasePrefix) =>
        Should.Throw<PromptCatalogException>(() =>
        {
            _ = Load(tree, prefix);
        });

    /// <summary>Replaces a file's text and re-hashes it, so only the rule under test can fire.</summary>
    /// <param name="tree">The prompts-relative files.</param>
    /// <param name="key">The file's key.</param>
    /// <param name="text">The new body.</param>
    internal static void Write(IDictionary<string, byte[]> tree, string key, string text)
    {
        tree[key] = Encoding.UTF8.GetBytes(text);
        Rehash(tree, key);
    }

    /// <summary>Appends text to a file and re-hashes it.</summary>
    /// <param name="tree">The prompts-relative files.</param>
    /// <param name="key">The file's key.</param>
    /// <param name="text">The text to append.</param>
    internal static void Append(IDictionary<string, byte[]> tree, string key, string text)
    {
        tree[key] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(tree[key]) + text);
        Rehash(tree, key);
    }

    /// <summary>Flips a file's bytes without re-hashing it, which is what a stale manifest entry looks like.</summary>
    /// <param name="tree">The prompts-relative files.</param>
    /// <param name="key">The file's key.</param>
    internal static void Corrupt(IDictionary<string, byte[]> tree, string key) =>
        tree[key] = [.. tree[key], (byte)'!'];

    /// <summary>Sets the manifest's <c>release</c>.</summary>
    /// <param name="tree">The prompts-relative files.</param>
    /// <param name="release">The new marker.</param>
    internal static void SetRelease(IDictionary<string, byte[]> tree, string release)
    {
        var manifest = ManifestOf(tree);
        manifest["release"] = release;
        Save(tree, manifest);
    }

    /// <summary>Adds a manifest entry for a key, with or without a matching file.</summary>
    /// <param name="tree">The prompts-relative files.</param>
    /// <param name="key">The key to add.</param>
    /// <param name="version">Its version marker.</param>
    /// <param name="sha256">Its recorded digest.</param>
    internal static void AddManifestEntry(
        IDictionary<string, byte[]> tree,
        string key,
        string version,
        string sha256)
    {
        var manifest = ManifestOf(tree);
        manifest["files"]!.AsObject()[key] = new JsonObject { ["version"] = version, ["sha256"] = sha256 };
        Save(tree, manifest);
    }

    /// <summary>Adds <c>review/v1.md</c> and <c>revise/v1.md</c> as tracked files (a run with both steps).</summary>
    /// <param name="tree">The prompts-relative files.</param>
    internal static void AddStepPrompts(IDictionary<string, byte[]> tree)
    {
        string[] keys = ["review/v1.md", "revise/v1.md"];

        foreach (var key in keys)
        {
            tree[key] = Encoding.UTF8.GetBytes($"# {key}\n\nStep prompt body.\n");
            AddManifestEntry(tree, key, "v1", Convert.ToHexStringLower(SHA256.HashData(tree[key])));
        }
    }

    /// <summary>Removes one entry of <c>files</c>, so the file is still embedded but no longer tracked.</summary>
    /// <param name="tree">The prompts-relative files.</param>
    /// <param name="key">The entry to remove.</param>
    internal static void RemoveManifestEntry(IDictionary<string, byte[]> tree, string key)
    {
        var manifest = ManifestOf(tree);
        manifest["files"]!.AsObject().Remove(key);
        Save(tree, manifest);
    }

    /// <summary>Replaces the whole manifest text, for a shape the typed helpers cannot express.</summary>
    /// <param name="tree">The prompts-relative files.</param>
    /// <param name="json">The new manifest bytes as text.</param>
    internal static void WriteManifest(IDictionary<string, byte[]> tree, string json) =>
        tree[Manifest] = Encoding.UTF8.GetBytes(json);

    /// <summary>Drops a member of one manifest entry, for example its <c>sha256</c>.</summary>
    /// <param name="tree">The prompts-relative files.</param>
    /// <param name="key">The tracked file.</param>
    /// <param name="member">The entry's member to remove.</param>
    internal static void RemoveManifestMember(IDictionary<string, byte[]> tree, string key, string member)
    {
        var manifest = ManifestOf(tree);
        manifest["files"]![key]!.AsObject().Remove(member);
        Save(tree, manifest);
    }

    /// <summary>Drops a root member of the manifest itself, for example <c>release</c>.</summary>
    /// <param name="tree">The prompts-relative files.</param>
    /// <param name="member">The root member to remove.</param>
    internal static void RemoveManifestRootMember(IDictionary<string, byte[]> tree, string member)
    {
        var manifest = ManifestOf(tree);
        manifest.Remove(member);
        Save(tree, manifest);
    }

    private static void Rehash(IDictionary<string, byte[]> tree, string key)
    {
        var manifest = ManifestOf(tree);
        manifest["files"]![key]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(tree[key]));
        Save(tree, manifest);
    }

    private static JsonObject ManifestOf(IDictionary<string, byte[]> tree) =>
        JsonNode.Parse(Encoding.UTF8.GetString(tree[Manifest]))!.AsObject();

    private static void Save(IDictionary<string, byte[]> tree, JsonObject manifest) =>
        tree[Manifest] = Encoding.UTF8.GetBytes(manifest.ToJsonString());

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "prompts", Manifest);
            if (File.Exists(candidate))
            {
                return Path.Combine(directory.FullName, "prompts");
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"No directory above '{AppContext.BaseDirectory}' contains prompts/{Manifest}; "
            + "the prompt catalog tests need the repository working copy.");
    }
}
