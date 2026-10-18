#pragma warning disable CA1716 // `Structure` is the planned namespace of the variety guard (02.8.4-T1 plan); the .NET-only API is never consumed from Visual Basic.
namespace NightTales.Domain.Stories.Structure;

/// <summary>
/// The variety guard's one-shot reading of the series' structure history (02.8.4-T1, R1–R5): the per-dimension
/// tag counts of the window, the window itself, and the steering values the idea prompt receives.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WindowSize"/> is the number of history entries actually inside the window
/// (<c>min(history.Count, WindowK)</c>), so the R2 share is <c>(count + 1) / (WindowSize + 1)</c>: four of ten
/// openings is 5/11 = 45 % and therefore over a 40 % cap, while three of ten is 4/11 = 36 % and passes.
/// </para>
/// <para>
/// The counts, <see cref="Recent"/> and <see cref="LastFive"/> cover entries that carry a structure; on a cold
/// start (R5, fewer than two such entries) the lists and the steering values are empty, so
/// <see cref="VarietyGuard.Evaluate"/> can neither reject nor penalise — a new series must never be blocked.
/// </para>
/// <para>
/// The dictionaries are ordinal and case-sensitive, because the values are the stored D3 tag strings compared
/// exactly as they were written.
/// </para>
/// </remarks>
/// <param name="WindowSize">The number of history entries the window holds; the denominator base of R2.</param>
/// <param name="SubGenreCounts">How often each <c>sub_genre</c> value occurs in the window.</param>
/// <param name="SettingCounts">How often each <c>setting</c> value occurs in the window.</param>
/// <param name="OpeningCounts">How often each <c>opening</c> value occurs in the window.</param>
/// <param name="TwistCounts">How often each <c>twist_type</c> value occurs in the window.</param>
/// <param name="Recent">The window entries, most recent first; empty on a cold start.</param>
/// <param name="LastFive">The first <c>TemplateWindowK</c> entries, most recent first; empty on a cold start.</param>
/// <param name="SuggestedRequired">The R4 rotation value per dimension; <c>StructureRequirements.None</c> when there is none.</param>
/// <param name="SuggestedForbidden">The R2 over-cap values per dimension; <c>StructureForbidden.None</c> when there are none.</param>
public sealed record VarietyBatchAssessment(
    int WindowSize,
    IReadOnlyDictionary<string, int> SubGenreCounts,
    IReadOnlyDictionary<string, int> SettingCounts,
    IReadOnlyDictionary<string, int> OpeningCounts,
    IReadOnlyDictionary<string, int> TwistCounts,
    IReadOnlyList<EpisodeStructureRef> Recent,
    IReadOnlyList<EpisodeStructureRef> LastFive,
    StructureRequirements SuggestedRequired,
    StructureForbidden SuggestedForbidden);
