using System.Reflection;

namespace NightTales.Infrastructure.Prompts;

/// <summary>
/// The one place the prompt catalog reads bytes from (02 README D5, F22): production reads the
/// assembly's embedded <c>prompts/**</c> resources, and a test hands in a dictionary-backed source, so
/// the catalog's rules can be exercised without a file system.
/// </summary>
/// <remarks>
/// Public, like <c>ErrorCatalogue</c>, because this solution has no <c>InternalsVisibleTo</c>: the unit
/// tests of the loader and the catalog implement this interface. Nothing else should: a second
/// production implementation would mean a second prompt tree.
/// </remarks>
public interface IPromptResourceSource
{
    /// <summary>
    /// Every resource's logical name, in ordinal order so that a violation message is deterministic.
    /// </summary>
    IReadOnlyCollection<string> LogicalNames { get; }

    /// <summary>Reads one resource's raw bytes.</summary>
    /// <param name="logicalName">A name from <see cref="LogicalNames"/>.</param>
    /// <returns>The bytes exactly as embedded — no line-ending normalisation, because the manifest hashes them.</returns>
    byte[] Read(string logicalName);
}

/// <summary>
/// The production <see cref="IPromptResourceSource"/>: the embedded resources of
/// <c>NightTales.Infrastructure</c>, where <c>prompts/**</c> is embedded with
/// <c>LinkBase="Prompts"</c> (02.2.3-T1, 02 D5).
/// </summary>
/// <remarks>
/// The embedded logical name of <c>prompts/story/ghost.vi.v1.md</c> is
/// <c>NightTales.Infrastructure.Prompts.story.ghost.vi.v1.md</c> — the LinkBase, the path with
/// <c>/</c> replaced by <c>.</c>, and no extension stripping. The catalog resolves a manifest key by
/// matching that dotted suffix, so a change to the prefix is a one-line fix here and never a re-hash;
/// <c>PromptCatalog_EmbeddedLogicalNames_UseThePromptsLinkBasePrefix</c> pins it.
/// </remarks>
public sealed class EmbeddedPromptResourceSource : IPromptResourceSource
{
    private readonly Assembly _assembly;
    private readonly string[] _logicalNames;

    /// <summary>Reads the resources of the <c>NightTales.Infrastructure</c> assembly itself.</summary>
    public EmbeddedPromptResourceSource()
        : this(typeof(EmbeddedPromptResourceSource).Assembly)
    {
    }

    /// <summary>Reads the resources of <paramref name="assembly"/>; the seam the tests use with a stub assembly.</summary>
    /// <param name="assembly">The assembly whose embedded resources are the prompt tree.</param>
    public EmbeddedPromptResourceSource(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        _assembly = assembly;
        _logicalNames = [.. assembly.GetManifestResourceNames().Order(StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> LogicalNames => _logicalNames;

    /// <inheritdoc />
    public byte[] Read(string logicalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);

        using var stream = _assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(
                $"The assembly '{_assembly.GetName().Name}' has no embedded resource named '{logicalName}'.");

        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);

        return bytes;
    }
}
