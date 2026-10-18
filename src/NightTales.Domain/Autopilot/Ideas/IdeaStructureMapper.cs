using System.Text.Json;
using NightTales.Domain.Stories.Content;
using ContentLanguage = NightTales.Domain.Common.Language;

namespace NightTales.Domain.Autopilot.Ideas;

/// <summary>
/// Applies the D3 structure vocabulary mapping of 02.2.1-T1 to one idea candidate's tags (02.8.4-T1, the
/// hand-over IDEA-S52-1): a value the topic's own vocabulary carries is stored as it stands, anything else as
/// <c>other:&lt;text&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// The idea prompt asks the model for a value "from the vocabulary of the series' own topic" and tells it the
/// backend stores anything else as <c>other:&lt;text&gt;</c> (<c>prompts/ideas/generate.v1.md</c>); before this
/// mapping the planner stored whatever the model wrote, so <c>episode_ideas.structure</c> could hold a term no
/// vocabulary know and the variety guard would compare it as a value of its own. The mapping closes that gap
/// with the <b>same</b> mapper the story writer's path uses (<see cref="StructureVocabMapper"/>) and the same
/// vocabulary file (<c>prompts/shared/structure-vocab.v1.json</c>, read through the D5 prompt catalog), never a
/// second vocabulary.
/// </para>
/// <para>
/// <b>Scope.</b> Only the three vocabulary dimensions are mapped — <c>sub_genre</c>, <c>opening</c> and
/// <c>twist_type</c> — which is exactly the coverage of <see cref="StoryRules.StructureVocab"/>. The
/// <c>setting</c> tag stays the model's own free text, the narrator mode and the beats are untouched, and no
/// story text is ever read (the mapping sees one candidate's tags only, D19).
/// </para>
/// <para>
/// <b>Determinism and fail-open.</b> The mapping is a pure function of the candidate, the vocabulary and the
/// mapper's own rules. A value outside the vocabulary raises the mapper's advisory
/// <c>structure_vocab_unknown</c> and is stored as <c>other:&lt;text&gt;</c>; a value that is too long or
/// carries markup or a URL raises the mapper's blocking <c>structure_text_invalid</c>, which the caller uses to
/// drop the candidate rather than to fail the run. An unreadable vocabulary document yields an empty set
/// instead of an exception, so a damaged prompt asset degrades to "nothing is known" and never crashes a run.
/// </para>
/// </remarks>
public static class IdeaStructureMapper
{
    /// <summary>The vocabulary dimensions <see cref="StoryRules.StructureVocab"/> covers, as the JSON block spells them.</summary>
    private static readonly string[] MappedDimensions = ["sub_genre", "opening", "twist_type"];

    /// <summary>
    /// The structure terms one topic and content language own, read out of the shared vocabulary document
    /// (<c>prompts/shared/structure-vocab.v1.json</c>): the <c>sub_genre</c>, <c>opening</c> and
    /// <c>twist_type</c> lists of that topic in that language, and nothing else.
    /// </summary>
    /// <remarks>
    /// The block holds one entry per topic and one list per dimension and language; <c>setting</c> is a free
    /// string and is therefore not part of the set (<see cref="StoryRules.StructureVocab"/>'s own contract). An
    /// unknown topic, an absent language and an unreadable document all answer the empty set: the mapping then
    /// stores every value as <c>other:&lt;text&gt;</c>, which is the fail-open reading the prompt states.
    /// </remarks>
    /// <param name="vocabularyJson">The shared block's text, as the prompt catalog carries it.</param>
    /// <param name="topic">The series' narrative topic, snake_case (<c>ghost</c>).</param>
    /// <param name="language">The series' content language.</param>
    /// <returns>The topic's vocabulary terms in that language; empty when nothing is known.</returns>
    public static IReadOnlySet<string> Vocabulary(string? vocabularyJson, string? topic, ContentLanguage language)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(vocabularyJson) || string.IsNullOrWhiteSpace(topic))
        {
            return terms;
        }

        try
        {
            using var document = JsonDocument.Parse(vocabularyJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("topics", out var topics)
                || topics.ValueKind != JsonValueKind.Object
                || !topics.TryGetProperty(topic, out var entry)
                || entry.ValueKind != JsonValueKind.Object)
            {
                return terms;
            }

            foreach (var dimension in MappedDimensions)
            {
                AddTerms(entry, dimension, language, terms);
            }

            return terms;
        }
        catch (JsonException)
        {
            // A damaged vocabulary asset is not a reason to fail a run: nothing is known, so every value is
            // mapped to other:<text> exactly as an unknown topic would be.
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Maps one candidate's three vocabulary-checked tags, leaving the setting tag, the narrator mode and the
    /// beats exactly as the model wrote them.
    /// </summary>
    /// <param name="candidate">The candidate <see cref="IdeaValidator"/> accepted.</param>
    /// <param name="rules">The rules carrying the topic's vocabulary (<see cref="Vocabulary"/>).</param>
    /// <returns>The mapped candidate and the issues the mapping raised.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidate"/> or <paramref name="rules"/> is <see langword="null"/>.</exception>
    public static IdeaStructureMapping Map(IdeaCandidate candidate, StoryRules rules)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(rules);

        var issues = new List<ValidationIssue>();
        var subGenre = MapTag(candidate.Structure.SubGenre, rules, issues);
        var opening = MapTag(candidate.Structure.Opening, rules, issues);
        var twistType = MapTag(candidate.Structure.TwistType, rules, issues);

        var tags = candidate.Structure with
        {
            SubGenre = subGenre,
            Opening = opening,
            TwistType = twistType,
        };

        return new IdeaStructureMapping(candidate with { Structure = tags }, issues);
    }

    /// <summary>Maps one tag through the shared mapper, collecting its issues.</summary>
    /// <param name="value">The model's value.</param>
    /// <param name="rules">The rules carrying the vocabulary.</param>
    /// <param name="issues">The issue list of this candidate's mapping.</param>
    /// <returns>The value to store.</returns>
    private static string MapTag(string? value, StoryRules rules, List<ValidationIssue> issues)
    {
        // A blank value is passed through as it stands: the shared mapper never meets one on the writer's path
        // (the draft validator refuses a blank structure value) and would answer `other:` for it, which the
        // variety guard would then read as a value of its own instead of "this dimension says nothing".
        if (string.IsNullOrWhiteSpace(value))
        {
            return value ?? string.Empty;
        }

        var mapped = StructureVocabMapper.MapTerm(value, rules);
        issues.AddRange(mapped.Issues);

        return mapped.Value;
    }

    /// <summary>Adds one dimension's terms of one language to the set.</summary>
    /// <param name="entry">The topic's own block of the vocabulary document.</param>
    /// <param name="dimension">The dimension's key (<c>sub_genre</c>).</param>
    /// <param name="language">The content language.</param>
    /// <param name="terms">The set being built.</param>
    private static void AddTerms(JsonElement entry, string dimension, ContentLanguage language, HashSet<string> terms)
    {
        if (!entry.TryGetProperty(dimension, out var lists)
            || lists.ValueKind != JsonValueKind.Object
            || !lists.TryGetProperty(WireName(language), out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var term = value.GetString();
            if (!string.IsNullOrWhiteSpace(term))
            {
                terms.Add(term.Trim());
            }
        }
    }

    /// <summary>The vocabulary document's own spelling of a content language (<c>vi</c>, <c>en</c>).</summary>
    /// <param name="language">The content language.</param>
    /// <returns>The language's key in the shared block.</returns>
    private static string WireName(ContentLanguage language) => language == ContentLanguage.Vi ? "vi" : "en";
}

/// <summary>
/// The outcome of mapping one candidate's tags (02.8.4-T1, IDEA-S52-1): the candidate to persist and the issues
/// the shared mapper raised for it.
/// </summary>
/// <remarks>
/// The advisory <c>structure_vocab_unknown</c> of an unmapped value is the normal state of a model value the
/// vocabulary does not carry, and the value is still stored (as <c>other:&lt;text&gt;</c>). Only a blocking
/// <c>structure_text_invalid</c> — a value over <see cref="StoryRules.StructureFreeStringMaxChars"/> characters
/// or carrying markup or a URL — drops the candidate, and it is a safety net: <see cref="IdeaValidator"/>
/// enforces the same limits before this mapping runs.
/// </remarks>
/// <param name="Candidate">The candidate with its mapped tags.</param>
/// <param name="Issues">The issues the mapping raised; empty when every value was already vocabulary-shaped.</param>
public sealed record IdeaStructureMapping(IdeaCandidate Candidate, IReadOnlyList<ValidationIssue> Issues)
{
    /// <summary>Whether a blocking issue makes the candidate unstorable.</summary>
    public bool IsBlocked => Issues.Any(issue => issue.Severity == IssueSeverity.Blocking);
}
