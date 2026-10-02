namespace NightTales.Application.Abstractions.Prompts;

/// <summary>
/// The prompt files one generation run used (02 README D5), in the manifest's own key form, so
/// <see cref="IPromptCatalog.BuildPromptVersion"/> can compose the run's <c>prompt_version</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every member is a manifest key relative to <c>prompts/</c> — <c>system/writer.v1.md</c>,
/// <c>story/ghost.vi.v1.md</c>, <c>schemas/story.v1.json</c>, <c>review/v1.md</c> — and the catalog
/// rejects a key the manifest does not track, so a stored version can never name a file that was not
/// part of the release.
/// </para>
/// <para>
/// The keys are what makes the composed value stable: each component is the key's file stem, so the
/// manifest's own <c>version</c> field can be corrected without rewriting a version already stored on
/// <c>story_versions</c>, <c>fact_sheets</c> and <c>generation_runs</c>.
/// </para>
/// </remarks>
/// <param name="SystemKey">The writer system prompt's key, normally <c>system/writer.v1.md</c>.</param>
/// <param name="PresetKey">The <c>topic × language</c> preset's key, for example <c>story/ghost.vi.v1.md</c>.</param>
/// <param name="SchemaKey">The output schema's key, for example <c>schemas/story.v1.json</c>.</param>
/// <param name="ReviewKey">The review prompt's key, or <see langword="null"/> when the run had no review step.</param>
/// <param name="RevisionKey">The revision prompt's key, or <see langword="null"/> when the run had no revision pass.</param>
public sealed record PromptVersionParts(
    string SystemKey,
    string PresetKey,
    string SchemaKey,
    string? ReviewKey = null,
    string? RevisionKey = null);
