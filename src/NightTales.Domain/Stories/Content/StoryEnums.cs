namespace NightTales.Domain.Stories.Content;

/// <summary>
/// The D3 enums of <c>story.v1</c> as string constants and valid sets (02 README D3). The strict schema
/// <c>prompts/schemas/story.v1.json</c> declares the same values, so a drift is a schema change, not a
/// validator tweak.
/// </summary>
public static class StoryEnums
{
    /// <summary>The paragraph beats of D3, in narrative order.</summary>
    public static readonly IReadOnlySet<string> Beats =
        new HashSet<string> { "setup", "build_up", "clue", "twist", "reveal", "aftermath" };

    /// <summary>The allowed <c>content_warnings</c> values.</summary>
    public static readonly IReadOnlySet<string> ContentWarnings =
        new HashSet<string> { "violence", "death", "crime", "supernatural", "self_harm_mention", "disturbing" };

    /// <summary>The allowed <c>narrator_mode</c> values.</summary>
    public static readonly IReadOnlySet<string> NarratorModes =
        new HashSet<string> { "first_person", "third_person" };
}
