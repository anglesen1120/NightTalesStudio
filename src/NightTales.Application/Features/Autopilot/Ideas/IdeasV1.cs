using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Stories.Structure;

namespace NightTales.Application.Features.Autopilot.Ideas;

/// <summary>
/// The <c>ideas.v1</c> model answer (02.8.1-T1 plan §3.2, 02 README D5): the batch of episode ideas the prompt
/// asked for. The strict schema is <c>prompts/schemas/ideas.v1.json</c> and the deserialiser is the adapter's
/// snake_case options (<c>OpenAiTextClient</c>), the same convention as <c>FactsV1</c> and
/// <c>BibleProposalV1</c>: the member names are the wire names.
/// </summary>
/// <remarks>
/// <para>
/// The schema requires every property of every member and forbids extras, so nothing here is nullable: a JSON
/// <c>null</c>, a missing member or an unknown one is a deserialisation failure the client maps to
/// <c>InvalidOutput</c> (its output options respect nullable annotations and required constructor parameters),
/// which is the retry-then-<c>ideas_invalid_output</c> path rather than a partially filled candidate.
/// </para>
/// <para>
/// The DTO carries no id, no score and no similarity: those are the run's, not the model's, and a model that
/// answered one anyway would be rejected by the strict schema's <c>additionalProperties: false</c>.
/// </para>
/// </remarks>
/// <param name="Ideas">The proposed ideas, in the model's order; the strict schema's <c>minItems</c> is 1.</param>
public sealed record IdeasV1(IReadOnlyList<IdeaV1> Ideas)
{
    /// <summary>
    /// Maps the answer to the Domain candidates <c>IdeaValidator</c> consumes: the model's untrusted ideas,
    /// exactly as they arrived, in the model's order, with nothing dropped and no field repaired here.
    /// Everything an idea is not allowed to do is decided downstream, so this mapping never decides content.
    /// </summary>
    /// <returns>One candidate per idea, in the model's order.</returns>
    /// <exception cref="InvalidOperationException">The answer deserialised with a null <c>ideas</c> array, which the client's options refuse before this call.</exception>
    public IReadOnlyList<IdeaCandidate> ToCandidates() => [.. Ideas.Select(idea => idea.ToCandidate())];
}

/// <summary>
/// One raw idea of an <c>ideas.v1</c> answer (02.8.1-T1 plan §3.2): its four texts, its D3 structure block and its
/// beat outline, exactly as the model wrote them.
/// </summary>
/// <remarks>
/// The five structure values are the model's own short strings, not vocabulary ids: the D3 vocabulary mapping
/// (<c>other:&lt;text&gt;</c>) belongs to the story writer's path and is not applied here — the idea prompt asks
/// for vocabulary values and 02.8.4-T1's variety guard reads them as they stand.
/// </remarks>
/// <param name="Title">The episode title.</param>
/// <param name="Logline">The one-line premise.</param>
/// <param name="Hook">The opening hook.</param>
/// <param name="Setting">The setting in words; the setting <em>tag</em> is in <paramref name="Structure"/>.</param>
/// <param name="Structure">The D3 structure block of the idea.</param>
/// <param name="Beats">The outline as beat names in narrative order (the schema's <c>minItems</c> 3, <c>maxItems</c> 8).</param>
public sealed record IdeaV1(
    string Title,
    string Logline,
    string Hook,
    string Setting,
    IdeaStructureV1 Structure,
    IReadOnlyList<string> Beats)
{
    /// <summary>Maps this idea to the Domain candidate of the same name and order.</summary>
    /// <returns>The candidate; its tags carry the same outline as its <c>Beats</c>.</returns>
    public IdeaCandidate ToCandidate() =>
        new(Title, Logline, Hook, Setting, Structure.ToTags(Beats), Beats);
}

/// <summary>
/// The <c>structure</c> block of one <c>ideas.v1</c> answer (02 README D3): the four vocabulary dimensions and the
/// narrator mode, one value each.
/// </summary>
/// <remarks>
/// D3's block is document-level, so the model answers one value per dimension and no arrays. The beats are not
/// part of this block on the wire — they are the idea's own <c>beats</c> array — and
/// <see cref="ToTags"/> puts them back together into the Domain record.
/// </remarks>
/// <param name="SubGenre">The sub-genre value.</param>
/// <param name="Setting">The setting value.</param>
/// <param name="Opening">The opening value.</param>
/// <param name="TwistType">The twist-type value.</param>
/// <param name="NarratorMode">The narrator mode; the schema's enum is <c>first_person</c> or <c>third_person</c>.</param>
public sealed record IdeaStructureV1(
    string SubGenre,
    string Setting,
    string Opening,
    string TwistType,
    string NarratorMode)
{
    /// <summary>Builds the Domain structure tags of one idea, with the outline that travels beside this block.</summary>
    /// <param name="beats">The idea's beat names in narrative order.</param>
    /// <returns>The D3 structure block of the candidate.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="beats"/> is <see langword="null"/>.</exception>
    public StoryStructureTags ToTags(IReadOnlyList<string> beats)
    {
        ArgumentNullException.ThrowIfNull(beats);

        return new StoryStructureTags(SubGenre, Setting, Opening, TwistType, NarratorMode, beats);
    }
}
