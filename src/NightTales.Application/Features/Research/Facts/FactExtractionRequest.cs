using NightTales.Application.Abstractions.Text;
using NightTales.Domain.Common;

namespace NightTales.Application.Features.Research.Facts;

/// <summary>
/// One fact-extraction call (02 README D21, 02.5.3-T1): the run's context, the run's language, the
/// alias-assigned sources and the call's output bound.
/// </summary>
/// <param name="Run">The run the call belongs to.</param>
/// <param name="Language">The content language of the run; the language the facts are written in.</param>
/// <param name="Sources">The sources the model may read, already alias-assigned (<c>s1…s10</c>) and truncated by <c>FactInputBudget</c>.</param>
/// <param name="MaxOutputTokens">The call's <c>max_output_tokens</c> from the price version (README T56); the caller supplies it — there is no <c>Facts:MaxOutputTokens</c> key.</param>
public sealed record FactExtractionRequest(
    RunContext Run,
    Language Language,
    IReadOnlyList<FactSourceInput> Sources,
    int MaxOutputTokens);

/// <summary>
/// One source of a fact-extraction call as the model sees it: the alias, the title and the text — nothing
/// else. <see cref="SourceId"/> and <see cref="IsUserNote"/> are backend-only and are never serialised into
/// the data object (02 README D6, 02.5.3-T1).
/// </summary>
/// <param name="Alias">The source alias, <c>s1…s10</c>.</param>
/// <param name="SourceId">The <c>story_sources.id</c> the alias maps to; the backend keeps it, the model never sees it.</param>
/// <param name="Title">The source title.</param>
/// <param name="Text">The source text: <c>extracted_text</c> for a url source, <c>note_text</c> for a user note.</param>
/// <param name="IsUserNote">Whether the source is the user's own note (README CR-4); never truncated and never in the similarity corpus (F18).</param>
public sealed record FactSourceInput(
    string Alias,
    Guid SourceId,
    string Title,
    string Text,
    bool IsUserNote);
