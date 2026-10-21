using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Hosting;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Domain.Common;

namespace NightTales.Infrastructure.Prompts;

/// <summary>
/// The <see cref="IRealWorldLexicon"/> over the D5 prompt tree (02.8.2-T2): the phrases of
/// <c>prompts/shared/real-world-framing.{vi|en}.v1.txt</c>, parsed once per language and served from
/// memory.
/// </summary>
/// <remarks>
/// <para>
/// The bytes arrive from the same <see cref="IPromptResourceSource"/> the catalog validated, so there is
/// one source of truth: the file is a tracked release file whose sha256 the start-up gate has already
/// checked, and this type ships no fallback copy of its own. A missing phrase list is never answered
/// with an empty list — an empty lexicon would pass every draft — so both a missing resource and a file
/// that carries no phrase throw.
/// </para>
/// <para>
/// It is also the start-up gate of the lexicon: <c>AddPromptCatalog</c> registers this singleton as an
/// <see cref="IHostedService"/> after <see cref="PromptCatalogStartupValidator"/>, so both languages
/// parse during the boot and a release that ships an empty lexicon aborts the start-up instead of the
/// first content gate (D5).
/// </para>
/// </remarks>
/// <param name="source">The prompt tree the release's bytes are read from.</param>
public sealed class RealWorldLexiconProvider(IPromptResourceSource source) : IRealWorldLexicon, IHostedService
{
    private readonly ConcurrentDictionary<Language, IReadOnlyList<string>> _byLanguage = new();

    /// <inheritdoc />
    public IReadOnlyList<string> PhrasesFor(Language language) =>
        _byLanguage.TryGetValue(language, out var cached) ? cached : _byLanguage.GetOrAdd(language, Parse);

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // D5: the lexicons are part of the release, so they parse with the catalog during start-up.
        foreach (var language in Enum.GetValues<Language>())
        {
            _ = PhrasesFor(language);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Parses one language: one phrase per line, trimmed, <c>#</c> comment lines and blank lines skipped,
    /// exact duplicates collapsed in the order the release lists them.
    /// </summary>
    /// <param name="language">The content language to parse.</param>
    /// <returns>The language's phrases; never empty.</returns>
    private IReadOnlyList<string> Parse(Language language)
    {
        var key = PromptKeys.RealWorldFraming(language);
        var text = Encoding.UTF8.GetString(source.Read(LogicalName(key)));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var phrases = new List<string>();

        foreach (var line in text.Split('\n'))
        {
            var phrase = line.Trim();
            if (phrase.Length == 0 || phrase[0] == '#')
            {
                continue;
            }

            if (seen.Add(phrase))
            {
                phrases.Add(phrase);
            }
        }

        return phrases.Count > 0
            ? phrases.AsReadOnly()
            : throw new InvalidOperationException(
                $"The prompt catalog's '{key}' carries no phrase, so the real-world framing detector would "
                + "match nothing: a release must ship at least one phrase per language (02.8.2-T2, D5).");
    }

    /// <summary>
    /// The logical name of a key's resource, resolved the way the catalog resolves it: the one embedded
    /// name whose dotted remainder is the key.
    /// </summary>
    /// <param name="key">The manifest key relative to <c>prompts/</c>.</param>
    /// <returns>The resource's logical name.</returns>
    private string LogicalName(string key)
    {
        var suffix = "." + PromptCatalogValidator.Dotted(key);
        string? match = null;

        foreach (var name in source.LogicalNames)
        {
            if (!name.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            if (match is not null)
            {
                throw new InvalidOperationException(
                    $"The prompt catalog has more than one embedded resource for '{key}': "
                    + $"'{match}' and '{name}'.");
            }

            match = name;
        }

        return match ?? throw new InvalidOperationException(
            $"The prompt catalog has no embedded resource for '{key}', so the release's real-world "
            + "framing lexicon cannot be read (02.8.2-T2, D5).");
    }
}
