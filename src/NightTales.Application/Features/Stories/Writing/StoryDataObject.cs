using NightTales.Domain.Series.Bible;

namespace NightTales.Application.Features.Stories.Writing;

/// <summary>
/// The one JSON data object of a writer call (02 README D6). <c>task</c>, <c>length</c>,
/// <c>structure_plan</c> and <c>facts[].id</c> are control keys the model follows; <c>brief</c>,
/// <c>bible</c>, <c>facts[].statement</c> and <c>avoid_plots</c> are material that never changes a rule.
/// </summary>
/// <remarks>
/// Keys are snake_case on the wire: the writer system prompt and the presets name them
/// (<c>avoid_plots</c>, <c>structure_plan</c>, <c>length.units_per_segment</c>).
/// </remarks>
/// <param name="Task">Always <c>write_story</c>.</param>
/// <param name="Brief">The brief, truncated to the assembler's limit.</param>
/// <param name="Length">The resolved length contract.</param>
/// <param name="Bible">The bible, or <see langword="null"/> (omitted).</param>
/// <param name="Facts">The facts; empty when there is no approved sheet.</param>
/// <param name="AvoidPlots">The plots to avoid; empty when none.</param>
/// <param name="StructurePlan">The variety guard's plan, or <see langword="null"/> (omitted).</param>
/// <param name="CorrectiveNote">The retry note, or <see langword="null"/> (omitted) except on attempt 2.</param>
public sealed record StoryDataObject(
    string Task,
    string Brief,
    DataLength Length,
    BibleSnapshot? Bible,
    IReadOnlyList<DataFact> Facts,
    IReadOnlyList<string> AvoidPlots,
    DataStructurePlan? StructurePlan,
    DataCorrectiveNote? CorrectiveNote);

/// <summary>The <c>length</c> control object.</summary>
/// <param name="Tier">The tier, snake_case.</param>
/// <param name="Tokens">The token band, <c>[min, max]</c>.</param>
/// <param name="Segments">The segment band, <c>[min, max]</c>.</param>
/// <param name="UnitsPerSegment"><c>round(target_tokens ÷ min_segments)</c>.</param>
/// <param name="TargetTokens"><c>round((min_tokens + max_tokens) ÷ 2)</c>.</param>
public sealed record DataLength(string Tier, IReadOnlyList<int> Tokens, IReadOnlyList<int> Segments, int UnitsPerSegment, int TargetTokens);

/// <summary>One fact of the data object.</summary>
/// <param name="Id">The fact alias, a control key.</param>
/// <param name="Statement">The fact text, material.</param>
/// <param name="Sources">The source aliases.</param>
public sealed record DataFact(string Id, string Statement, IReadOnlyList<string> Sources);

/// <summary>The <c>structure_plan</c> control object.</summary>
/// <param name="Required">The required values; unset dimensions are omitted.</param>
/// <param name="Forbidden">The forbidden values; empty dimensions are omitted.</param>
public sealed record DataStructurePlan(DataStructureRequired Required, DataStructureForbidden Forbidden);

/// <summary>The required structure values.</summary>
/// <param name="SubGenre">The required sub-genre.</param>
/// <param name="Setting">The required setting.</param>
/// <param name="Opening">The required opening.</param>
/// <param name="TwistType">The required twist type.</param>
public sealed record DataStructureRequired(string? SubGenre, string? Setting, string? Opening, string? TwistType);

/// <summary>The forbidden structure values.</summary>
/// <param name="SubGenre">The forbidden sub-genres.</param>
/// <param name="Setting">The forbidden settings.</param>
/// <param name="Opening">The forbidden openings.</param>
/// <param name="TwistType">The forbidden twist types.</param>
public sealed record DataStructureForbidden(
    IReadOnlyList<string>? SubGenre,
    IReadOnlyList<string>? Setting,
    IReadOnlyList<string>? Opening,
    IReadOnlyList<string>? TwistType);

/// <summary>The retry's corrective note: rule ids only, never the invalid draft.</summary>
/// <param name="ViolatedRules">The validator rule ids of the first attempt.</param>
public sealed record DataCorrectiveNote(IReadOnlyList<string> ViolatedRules);
