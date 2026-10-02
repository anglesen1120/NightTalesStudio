namespace NightTales.Domain.Stories.Prompts;

/// <summary>
/// The eight prompt steps of a generation run (02 README D21, <c>TextStep</c>): the seven step prompts
/// of the D5 catalog plus the writer, which is the shared system prompt.
/// </summary>
/// <remarks>
/// The member set mirrors <c>TextStep</c> one for one, so a <c>TextCallRequest</c> and a catalog
/// lookup name the same step. The enum lives in Domain and not in Application because the catalog is
/// addressed by it and 02.2.1-T2's Application <c>TextStep</c> must stay a separate type: the wire
/// step of a call and the file a step is built from are different concepts that happen to agree today.
/// </remarks>
public enum PromptStep
{
    /// <summary>The shared writer system prompt, <c>system/writer.v1.md</c>.</summary>
    Writer,

    /// <summary>The self-review prompt, <c>review/v1.md</c> (02.6.1-T1).</summary>
    Review,

    /// <summary>The single revision pass, <c>revise/v1.md</c> (02.6.2-T1).</summary>
    Revision,

    /// <summary>The fact-extraction prompt, <c>facts/v1.md</c> (02.5.3-T1).</summary>
    Facts,

    /// <summary>The series-bible proposal prompt, <c>bible/propose.v1.md</c> (02.6.3-T2).</summary>
    Bible,

    /// <summary>The idea-generation prompt, <c>ideas/generate.v1.md</c> (02.8.1-T1).</summary>
    Ideas,

    /// <summary>The idea-novelty scoring prompt, <c>ideas/novelty.v1.md</c> (02.8.1-T2).</summary>
    Novelty,

    /// <summary>The source-finder prompt, <c>finder/v1.md</c> (02.7.2-T3).</summary>
    Finder,
}
