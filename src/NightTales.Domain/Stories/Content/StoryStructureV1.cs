namespace NightTales.Domain.Stories.Content;

/// <summary>
/// The <c>structure</c> block of <c>story.v1</c> (02 README D3, F08): the four free strings the writer
/// chose plus the narrative mode. <c>sub_genre</c>, <c>opening</c> and <c>twist_type</c> are mapped onto
/// the run's vocabulary by <see cref="StructureVocabMapper"/>; <c>setting</c> stays free text.
/// </summary>
/// <param name="SubGenre">The vocabulary term (or a plain short string) for the sub-genre.</param>
/// <param name="Setting">The free-text setting; ≤ 40 characters, no URL or markup.</param>
/// <param name="Opening">The vocabulary term (or a plain short string) for the opening device.</param>
/// <param name="TwistType">The vocabulary term (or a plain short string) for the twist.</param>
/// <param name="NarratorMode">One of <see cref="StoryEnums.NarratorModes"/>.</param>
public sealed record StoryStructureV1(
    string SubGenre,
    string Setting,
    string Opening,
    string TwistType,
    string NarratorMode);
