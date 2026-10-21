using NightTales.Domain.Common;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Prompts;

namespace NightTales.Application.Abstractions.Prompts;

/// <summary>
/// The embedded prompt catalog of the 02 story writer (02 README D5): the release's manifest, the
/// preset and shared prompt texts, the output schemas, and the composite <c>prompt_version</c> that
/// records which prompt set produced a draft.
/// </summary>
/// <remarks>
/// <para>
/// One singleton per host, loaded and validated at start-up (F22). Every member answers from memory:
/// there is no file system and no per-request I/O, and a missing or changed prompt fails the boot
/// instead of surfacing as a wrong draft later. The implementation is
/// <c>NightTales.Infrastructure.Prompts.PromptCatalog</c>.
/// </para>
/// <para>
/// A lookup of a key the manifest does not track throws
/// <see cref="PromptCatalogException"/> — a step prompt of a task that has not shipped yet is a
/// programming error, never user input.
/// </para>
/// </remarks>
public interface IPromptCatalog
{
    /// <summary>The release the manifest declares, for example <c>2026.10.1</c>.</summary>
    string Release { get; }

    /// <summary>
    /// Every tracked prompt file of the release, keyed by its manifest key (relative to
    /// <c>prompts/</c>, with <c>/</c> on every OS).
    /// </summary>
    IReadOnlyDictionary<string, PromptFileInfo> Files { get; }

    /// <summary>The shared writer system prompt, <c>system/writer.v1.md</c>.</summary>
    /// <returns>The prompt text as it ships, with no placeholder substituted.</returns>
    string GetSystemPrompt();

    /// <summary>The preset of one topic and content language, for example <c>story/ghost.vi.v1.md</c>.</summary>
    /// <param name="topic">The narrative topic of the run.</param>
    /// <param name="language">The content language of the run.</param>
    /// <returns>The preset text, with its shared-block placeholders still in place.</returns>
    string GetPreset(Topic topic, Language language);

    /// <summary>One shared rule block, in the language's spelling where it varies by language.</summary>
    /// <param name="file">The shared block to read.</param>
    /// <param name="language">The content language; ignored by the two blocks that do not vary.</param>
    /// <returns>The block's text, ready to be substituted into a template.</returns>
    string GetShared(PromptFile file, Language language);

    /// <summary>The prompt of one generation step.</summary>
    /// <param name="promptStep">The step whose prompt is needed.</param>
    /// <returns>The step prompt's text.</returns>
    string GetStepPrompt(PromptStep promptStep);

    /// <summary>
    /// The true-crime attestation clause text of one content language (02.3.3-T1, 02 README T29),
    /// <c>shared/attestation.ta.v1.{vi|en}.md</c>, with the version marker the manifest declares for it.
    /// </summary>
    /// <param name="language">The content language of the episode's series.</param>
    /// <returns>The clause text and the manifest version, for example <c>ta.v1</c>.</returns>
    /// <exception cref="PromptCatalogException">The manifest does not track the file.</exception>
    AttestationText GetAttestation(Language language);

    /// <summary>The strict output schema of one file under <c>prompts/schemas/</c>.</summary>
    /// <param name="name">The schema's file name, for example <c>story.v1.json</c>.</param>
    /// <returns>The schema's raw bytes; the OpenAI adapter wraps them in the SDK's schema type.</returns>
    ReadOnlyMemory<byte> GetSchema(string name);

    /// <summary>
    /// The composite <c>prompt_version</c> of one run, for example
    /// <c>rel=2026.10.1;sys=writer.v1;preset=ghost.vi.v1;review=v1;revise=v1;schema=story.v1</c>.
    /// </summary>
    /// <param name="parts">The files the run used; the review component is omitted when it has no review step.</param>
    /// <returns>The version string stored on <c>story_versions</c>, <c>fact_sheets</c> and <c>generation_runs</c>.</returns>
    /// <exception cref="PromptCatalogException">One of the parts' keys is not tracked by the manifest.</exception>
    string BuildPromptVersion(PromptVersionParts parts);
}
