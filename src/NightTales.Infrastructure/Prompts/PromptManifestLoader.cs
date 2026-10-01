using System.Text.Json;
using NightTales.Application.Abstractions.Prompts;

namespace NightTales.Infrastructure.Prompts;

/// <summary>
/// Reads <c>prompts/manifest.json</c> out of an <see cref="IPromptResourceSource"/> and normalises it
/// (02 README D5, ruling #4): the key set is a release contract, so a key that is absolute, carries
/// <c>..</c>, repeats the <c>prompts/</c> root, or names something a release never tracks is
/// <b>invalid</b> — reported as such, never merely "not found".
/// </summary>
/// <remarks>
/// <para>
/// The loader reports instead of throwing: it fills the caller's violation list and returns
/// <see cref="PromptManifest.Empty"/> when there is nothing to hand on, so one load reports every
/// problem at once (02.2.3-T1 rule 1).
/// </para>
/// <para>
/// Nothing here touches the file system (F22): the manifest arrives as bytes from the resource source,
/// and the JSON is read with <see cref="JsonDocument"/> so a missing or mistyped member is a named
/// violation instead of a deserialisation exception.
/// </para>
/// </remarks>
internal static class PromptManifestLoader
{
    /// <summary>The manifest's own name under <c>prompts/</c>; never a key of <see cref="PromptManifest.Files"/>.</summary>
    internal const string ManifestFileName = "manifest.json";

    /// <summary>
    /// The exact logical name of the release manifest. <c>NightTales.Infrastructure.csproj</c> embeds the
    /// tree with <c>LinkBase="Prompts"</c>, so a resource's logical name is the assembly's root namespace,
    /// then <c>Prompts</c>, then the prompts-relative path with <c>/</c> replaced by <c>.</c>; the root
    /// manifest is therefore <c>NightTales.Infrastructure.Prompts.manifest.json</c>.
    /// </summary>
    /// <remarks>
    /// The whole name is matched on purpose: a nested <c>manifest.json</c> — a fixture under
    /// <c>fixtures/</c>, a helper copy under <c>tools/</c> — carries a longer logical name, and a suffix
    /// match or a shortest-name pick would let one stand in for the release manifest, or make discovery
    /// ambiguous, when the root manifest is missing (02 README T75).
    /// </remarks>
    internal static string RootLogicalName { get; } =
        (typeof(PromptManifestLoader).Assembly.GetName().Name ?? string.Empty) + ".Prompts." + ManifestFileName;

    /// <summary>The reference document of the prompt tree, excluded from the release (ruling #4).</summary>
    internal const string ReferencesFileName = "REFERENCES.md";

    /// <summary>The roots a release never tracks (02 README T27/T75).</summary>
    private static readonly string[] ExcludedRoots = ["fixtures", "tools"];

    /// <summary>Reads, parses and normalises the manifest of <paramref name="source"/>.</summary>
    /// <param name="source">The resource source whose tree carries the manifest.</param>
    /// <param name="violations">The load's violation list, appended to.</param>
    /// <returns>The normalised manifest, or <see cref="PromptManifest.Empty"/> when it cannot be read.</returns>
    internal static PromptManifest Load(IPromptResourceSource source, ICollection<string> violations)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(violations);

        if (!source.LogicalNames.Contains(RootLogicalName))
        {
            violations.Add("prompts/manifest.json is not an embedded resource (not found)");

            return PromptManifest.Empty;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(source.Read(RootLogicalName));
        }
        catch (JsonException exception)
        {
            violations.Add($"prompts/manifest.json is not valid JSON: {exception.Message}");

            return PromptManifest.Empty;
        }

        using (document)
        {
            return Read(document.RootElement, violations);
        }
    }

    /// <summary>
    /// Whether <paramref name="value"/> is a release marker: four digits, a dot, one or two digits, a
    /// dot, one or two digits (rule 1's <c>^\d{4}\.\d{1,2}\.\d{1,2}$</c>).
    /// </summary>
    /// <param name="value">The manifest's <c>release</c> member, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> for a well-formed marker.</returns>
    internal static bool IsRelease(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var parts = value.Split('.');

        return parts.Length == 3
            && parts[0].Length == 4
            && AllDigits(parts[0])
            && parts[1].Length is 1 or 2
            && AllDigits(parts[1])
            && parts[2].Length is 1 or 2
            && AllDigits(parts[2]);
    }

    /// <summary>
    /// Why a normalised key may not be tracked, or <see langword="null"/> when it may.
    /// </summary>
    /// <param name="key">The key after <c>\</c> was replaced by <c>/</c>.</param>
    /// <returns>A reason for the violation message, or <see langword="null"/>.</returns>
    internal static string? Rejection(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (key.Length == 0)
        {
            return "the key is empty";
        }

        if (key.StartsWith('/'))
        {
            return "keys are relative to prompts/, not rooted";
        }

        if (key.Length > 1 && key[1] == ':')
        {
            return "keys are relative to prompts/, not a drive path";
        }

        var segments = key.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                return "the key has an empty path segment";
            }

            if (segment == "..")
            {
                return "'..' segments are not allowed";
            }

            if (segment.StartsWith('.'))
            {
                return "dotfiles are never tracked";
            }
        }

        var root = segments[0];
        if (root == "prompts")
        {
            return "keys are relative to prompts/, so they must not repeat the 'prompts/' root";
        }

        if (ExcludedRoots.Contains(root, StringComparer.Ordinal))
        {
            return $"'{root}/' is excluded from the release manifest";
        }

        if (key is ManifestFileName or ReferencesFileName)
        {
            return $"'{key}' is excluded from the release manifest";
        }

        return null;
    }

    private static PromptManifest Read(JsonElement root, ICollection<string> violations)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            violations.Add("prompts/manifest.json must be a JSON object");

            return PromptManifest.Empty;
        }

        var release = ReadString(root, "release");
        if (release is null)
        {
            violations.Add("prompts/manifest.json has no string 'release'");
        }
        else if (!IsRelease(release))
        {
            violations.Add($"prompts/manifest.json release '{release}' must match yyyy.M.d");
        }

        var files = new Dictionary<string, PromptFileInfo>(StringComparer.Ordinal);

        if (!root.TryGetProperty("files", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            violations.Add("prompts/manifest.json has no 'files' object");

            return new PromptManifest(release ?? string.Empty, files);
        }

        foreach (var entry in element.EnumerateObject())
        {
            var key = entry.Name.Replace('\\', '/');
            var rejection = Rejection(key);
            if (rejection is not null)
            {
                violations.Add($"invalid manifest key '{entry.Name}': {rejection}");

                continue;
            }

            var info = ReadFileInfo(entry.Value, key, violations);
            if (!files.TryAdd(key, info))
            {
                violations.Add($"duplicate manifest key '{key}' after normalisation");
            }
        }

        return new PromptManifest(release ?? string.Empty, files);
    }

    private static PromptFileInfo ReadFileInfo(JsonElement element, string key, ICollection<string> violations)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            violations.Add($"manifest entry '{key}' must be an object");

            return new PromptFileInfo(string.Empty, string.Empty);
        }

        var version = ReadString(element, "version");
        if (string.IsNullOrEmpty(version))
        {
            violations.Add($"manifest entry '{key}' has no 'version'");
        }

        // A blank sha256 is reported by the digest rule, which also knows the file the entry names.
        return new PromptFileInfo(version ?? string.Empty, ReadString(element, "sha256") ?? string.Empty);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool AllDigits(string value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
