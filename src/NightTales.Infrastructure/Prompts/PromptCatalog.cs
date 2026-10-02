using System.Text;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Domain.Common;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Prompts;

namespace NightTales.Infrastructure.Prompts;

/// <summary>
/// The embedded prompt catalog of the 02 story writer (02 README D5, F22): it loads
/// <c>prompts/manifest.json</c> and the tree it tracks in its constructor, validates all six rules at
/// once, and keeps every text and schema in memory so a generation call never reads a file.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton (<c>AddPromptCatalog</c>) and started through
/// <see cref="PromptCatalogStartupValidator"/>, so an invalid catalog aborts the boot with one
/// <see cref="PromptCatalogException"/> naming every violation, instead of a draft built from a prompt
/// that was never released. There is no file system here and no per-request I/O: the bytes arrive from
/// the <see cref="IPromptResourceSource"/> seam, which the start-up gate reads exactly once.
/// </para>
/// <para>
/// A lookup of a key the manifest does not track — the step prompt of a task that has not shipped yet,
/// a schema name that does not exist — throws <see cref="PromptCatalogException"/>. That is a
/// programming error, never user input.
/// </para>
/// </remarks>
public sealed class PromptCatalog : IPromptCatalog
{
    private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReadOnlyMemory<byte>> _schemas = new(StringComparer.Ordinal);

    /// <summary>Loads and validates the catalog of <paramref name="source"/>.</summary>
    /// <param name="source">The embedded resource tree the catalog is built from.</param>
    /// <exception cref="PromptCatalogException">The catalog violates at least one of the six rules.</exception>
    public PromptCatalog(IPromptResourceSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var validator = new PromptCatalogValidator(source);
        validator.Validate();

        if (validator.Violations.Count > 0)
        {
            throw new PromptCatalogException(validator.Violations);
        }

        Release = validator.Manifest.Release;
        Files = validator.Manifest.Files;

        foreach (var (key, bytes) in validator.Bytes)
        {
            if (PromptKeys.IsSchema(key))
            {
                _schemas[key] = bytes;
            }
            else
            {
                _texts[key] = Encoding.UTF8.GetString(bytes);
            }
        }
    }

    /// <inheritdoc />
    public string Release { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, PromptFileInfo> Files { get; }

    /// <inheritdoc />
    public string GetSystemPrompt() => Text(PromptKeys.SystemPrompt);

    /// <inheritdoc />
    public string GetPreset(Topic topic, Language language) => Text(PromptKeys.Preset(topic, language));

    /// <inheritdoc />
    public string GetShared(PromptFile file, Language language) => Text(PromptKeys.Shared(file, language));

    /// <inheritdoc />
    public string GetStepPrompt(PromptStep promptStep) => Text(PromptKeys.Step(promptStep));

    /// <inheritdoc />
    public ReadOnlyMemory<byte> GetSchema(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var key = PromptKeys.Schema(name);

        return _schemas.TryGetValue(key, out var schema)
            ? schema
            : throw NotTracked(key);
    }

    /// <inheritdoc />
    public string BuildPromptVersion(PromptVersionParts parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var segments = new List<string>(5)
        {
            $"rel={Release}",
            Component("sys", parts.SystemKey),
            Component("preset", parts.PresetKey),
        };

        if (parts.ReviewKey is not null)
        {
            segments.Add(Component("review", parts.ReviewKey));
        }

        if (parts.RevisionKey is not null)
        {
            segments.Add(Component("revise", parts.RevisionKey));
        }

        segments.Add(Component("schema", parts.SchemaKey));

        return string.Join(';', segments);
    }

    /// <summary>One component of the composite version: the name, then the key's file stem.</summary>
    private string Component(string name, string key)
    {
        if (!Files.ContainsKey(key))
        {
            throw new PromptCatalogException(
                new List<string> { $"prompt version key '{key}' is not tracked by the manifest" });
        }

        return $"{name}={Stem(key)}";
    }

    /// <summary>The text of a tracked file, or the violation a lookup of an untracked key is.</summary>
    private string Text(string key) =>
        _texts.TryGetValue(key, out var text) ? text : throw NotTracked(key);

    /// <summary>
    /// A key's file stem: the file name without its directory or extension, so
    /// <c>system/writer.v1.md</c> is <c>writer.v1</c> and <c>review/v1.md</c> is <c>v1</c>.
    /// </summary>
    private static string Stem(string key)
    {
        var name = key[(key.LastIndexOf('/') + 1)..];
        var extension = name.LastIndexOf('.');

        return extension < 0 ? name : name[..extension];
    }

    private static PromptCatalogException NotTracked(string key) =>
        new(new List<string> { $"the manifest does not track '{key}'" });
}
