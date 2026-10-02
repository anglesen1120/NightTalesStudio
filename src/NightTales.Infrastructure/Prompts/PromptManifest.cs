using NightTales.Application.Abstractions.Prompts;

namespace NightTales.Infrastructure.Prompts;

/// <summary>
/// The parsed, key-normalised <c>prompts/manifest.json</c> (02 README D5): the release marker and one
/// <see cref="PromptFileInfo"/> per tracked prompt file.
/// </summary>
/// <remarks>
/// <see cref="PromptManifestLoader"/> is the only producer. <see cref="Files"/> holds keys already
/// normalised to <c>/</c> and free of the keys a release never tracks (fixtures, tools,
/// <c>manifest.json</c>, <c>REFERENCES.md</c>, dotfiles) — those are rejected with a violation and are
/// absent here.
/// </remarks>
/// <param name="Release">The release marker, for example <c>2026.10.1</c>.</param>
/// <param name="Files">Every tracked file, keyed by its manifest key relative to <c>prompts/</c>.</param>
public sealed record PromptManifest(string Release, IReadOnlyDictionary<string, PromptFileInfo> Files)
{
    /// <summary>
    /// The manifest a failed load yields: no release and no files. The loader has already reported why,
    /// so the validator's later rules find nothing to add to that report.
    /// </summary>
    public static PromptManifest Empty { get; } =
        new(string.Empty, new Dictionary<string, PromptFileInfo>(StringComparer.Ordinal));
}
