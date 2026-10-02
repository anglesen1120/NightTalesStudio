namespace NightTales.Application.Abstractions.Prompts;

/// <summary>
/// One entry of <c>prompts/manifest.json</c> (02 README D5): the file's own version marker and the
/// sha256 of its raw bytes.
/// </summary>
/// <remarks>
/// <see cref="Version"/> is the marker in the file name (<c>*.v1.md</c> → <c>v1</c>), not a revision
/// counter, and it is reported for auditing only: the composite <c>prompt_version</c> of a run is
/// built from the file's key stem, so a manifest that stored a different version could not silently
/// change a stored version string. <see cref="Sha256"/> is lowercase hex over the bytes as they ship —
/// no line-ending normalisation, which is why <c>.gitattributes</c> pins the prompt tree to LF.
/// </remarks>
/// <param name="Version">The file's version marker, for example <c>v1</c>.</param>
/// <param name="Sha256">The lowercase hex sha256 of the file's raw bytes.</param>
public sealed record PromptFileInfo(string Version, string Sha256);
