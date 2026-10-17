using NightTales.Domain.Stories.Structure;

namespace NightTales.Domain.Autopilot.Ideas;

/// <summary>
/// The <c>structure</c> of one stored idea (02.8.1 plan §3.1: <c>{ sub_genre, setting, opening, twist_type,
/// narrator_mode, beats, beat_signature }</c>): the D3 <see cref="StoryStructureTags"/> of the idea, its
/// outline and the signature the variety guard keys on.
/// </summary>
/// <remarks>
/// It is the <c>jsonb</c> payload of <c>episode_ideas.structure</c>, written through <c>IdeaJson</c>, so
/// the tags and beats are stored once, in the shape 02.8.4-T1's guard and the writer's prompt read.
/// </remarks>
/// <param name="Tags">The D3 structure block: the four vocabulary tags and the narrator mode.</param>
/// <param name="Beats">The outline as beat names in narrative order.</param>
/// <param name="BeatSignature">The outline signature, <see cref="Signature"/> of <paramref name="Beats"/>.</param>
public sealed record IdeaStructure(
    StoryStructureTags Tags,
    IReadOnlyList<string> Beats,
    string BeatSignature)
{
    /// <summary>
    /// The outline signature of <paramref name="beats"/>: the beat names joined with <c>&gt;</c>, for example
    /// <c>setup&gt;clue&gt;twist</c>.
    /// </summary>
    /// <remarks>
    /// The signature is the outline's identity for the variety guard's template-repeat rule (02.8.4-T1,
    /// R3) and is stored next to the beats so the check never has to re-derive it from a row.
    /// </remarks>
    /// <param name="beats">The beat names in narrative order.</param>
    /// <returns>The signature; empty for an empty outline.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="beats"/> is <see langword="null"/>.</exception>
    public static string Signature(IReadOnlyList<string> beats)
    {
        ArgumentNullException.ThrowIfNull(beats);

        return string.Join('>', beats);
    }

    /// <summary>
    /// Builds the payload of one candidate: its D3 tags, its outline and the outline's signature.
    /// </summary>
    /// <remarks>
    /// The schema's structure block carries no <c>beats</c> property (they are the item's sibling), so the
    /// candidate's outline is the only beat sequence of the idea and the tags' own list is normalised to it.
    /// The stored document is flat (plan §3.1) and therefore carries one <c>beats</c> key, which is what
    /// makes the round trip of <see cref="IdeaJson"/> lossless.
    /// </remarks>
    /// <param name="candidate">The candidate a run persisted.</param>
    /// <returns>The structure payload of the row.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidate"/> is <see langword="null"/>.</exception>
    public static IdeaStructure From(IdeaCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var beats = candidate.Beats ?? [];
        var tags = candidate.Structure with { Beats = beats };

        return new IdeaStructure(tags, beats, Signature(beats));
    }
}
