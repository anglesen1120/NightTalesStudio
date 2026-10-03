namespace NightTales.Domain.Series.Bible;

/// <summary>
/// The writer's view of a series bible (02 README D6): a plain, id-free record serialised verbatim under
/// the data object's <c>bible</c> key.
/// </summary>
/// <remarks>
/// 02.2.3-T2 declares the record so the prompt assembler compiles; 02.6.3-T3 owns the budgeted builder
/// and may add members. It stays a plain record with no behaviour-only members, because it is serialised
/// as it stands.
/// </remarks>
/// <param name="Version">The bible's version.</param>
/// <param name="Narrator">The narrator, or <see langword="null"/> when none is defined.</param>
/// <param name="RecurringCharacters">The recurring characters.</param>
/// <param name="Places">The recurring places.</param>
/// <param name="Rules">The series' standing rules.</param>
/// <param name="EpisodeSummaries">The summaries of earlier approved episodes.</param>
/// <param name="Truncated"><see langword="true"/> when the size budget dropped anything.</param>
public sealed record BibleSnapshot(
    int Version,
    BibleNarratorSnapshot? Narrator,
    IReadOnlyList<BibleCharacterSnapshot> RecurringCharacters,
    IReadOnlyList<BiblePlaceSnapshot> Places,
    IReadOnlyList<string> Rules,
    IReadOnlyList<BibleEpisodeSummarySnapshot> EpisodeSummaries,
    bool Truncated);

/// <summary>The narrator's persona.</summary>
/// <param name="Name">The narrator's name, if any.</param>
/// <param name="Persona">The persona text.</param>
/// <param name="VoiceNotes">Free-form voice notes, if any.</param>
public sealed record BibleNarratorSnapshot(string? Name, string Persona, string? VoiceNotes);

/// <summary>A recurring character.</summary>
/// <param name="Name">The character's name.</param>
/// <param name="Description">The character's description.</param>
public sealed record BibleCharacterSnapshot(string Name, string Description);

/// <summary>A recurring place.</summary>
/// <param name="Name">The place's name.</param>
/// <param name="Description">The place's description.</param>
public sealed record BiblePlaceSnapshot(string Name, string Description);

/// <summary>The summary of one earlier approved episode.</summary>
/// <param name="Title">The episode's title.</param>
/// <param name="Summary">The summary text; <see langword="null"/> when the budget kept only the title.</param>
public sealed record BibleEpisodeSummarySnapshot(string Title, string? Summary);
