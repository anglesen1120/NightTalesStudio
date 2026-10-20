using NightTales.Domain.Common;

namespace NightTales.Application.Abstractions.Prompts;

/// <summary>
/// The real-world framing lexicon of the content gate (02.8.2-T2, 02 README D6/D5): the phrases that
/// make a draft claim to tell a true story about real people, read from
/// <c>prompts/shared/real-world-framing.{vi|en}.v1.txt</c>.
/// </summary>
/// <remarks>
/// <para>
/// The lexicon is <b>data</b>, not a template: no preset substitutes it, which is why it is not one of
/// the <c>PromptFile</c> shared blocks. Its two files are tracked by the release manifest and are in the
/// catalog's required list, so a release that drops one fails start-up by name (D5) instead of detecting
/// nothing.
/// </para>
/// <para>
/// A phrase is served exactly as the release ships it — trimmed, one per line, with the file's
/// <c>#</c> comment lines removed and exact duplicates collapsed — and the detector does the case- and
/// diacritic-insensitive matching (F31). The implementation is
/// <c>NightTales.Infrastructure.Prompts.RealWorldLexiconProvider</c>, a singleton that parses each
/// language once and answers from memory.
/// </para>
/// <para>
/// There is never an empty answer for a configured language: a release whose lexicon carries no phrase
/// is a broken release and throws, because an empty list would silently pass every draft.
/// </para>
/// </remarks>
public interface IRealWorldLexicon
{
    /// <summary>The framing phrases of one content language, in the order the release lists them.</summary>
    /// <param name="language">The content language of the episode's series.</param>
    /// <returns>The trimmed, comment-free, ordinal-distinct phrases; never empty.</returns>
    /// <exception cref="InvalidOperationException">
    /// The release ships no embedded lexicon for <paramref name="language"/>, or the file carries no phrase.
    /// </exception>
    IReadOnlyList<string> PhrasesFor(Language language);
}
