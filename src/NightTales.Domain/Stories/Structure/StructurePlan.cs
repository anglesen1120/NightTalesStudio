#pragma warning disable CA1716 // `Structure` is the planned namespace of the variety guard (02.8.4-T1 plan); the .NET-only API is never consumed from Visual Basic.
namespace NightTales.Domain.Stories.Structure;

/// <summary>
/// What the variety guard asks the writer to do for one episode (02 README D6): the single value each
/// dimension must take and the values it must avoid. Values are vocabulary ids, never free text.
/// </summary>
/// <remarks>
/// 02.2.3-T2 declares the shape the prompt assembler serialises under <c>structure_plan</c>; 02.8.4-T1
/// owns the variety guard that fills it and may add members that do not change the wire shape.
/// </remarks>
/// <param name="Required">The values the writer must use.</param>
/// <param name="Forbidden">The values the writer must not use.</param>
public sealed record StructurePlan(StructureRequirements Required, StructureForbidden Forbidden);

/// <summary>The single value each structure dimension must take; <see langword="null"/> means unconstrained.</summary>
/// <param name="SubGenre">The required sub-genre id.</param>
/// <param name="Setting">The required setting id.</param>
/// <param name="Opening">The required opening id.</param>
/// <param name="TwistType">The required twist-type id.</param>
public sealed record StructureRequirements(string? SubGenre, string? Setting, string? Opening, string? TwistType);

/// <summary>The values each structure dimension must not take.</summary>
/// <param name="SubGenre">The forbidden sub-genre ids.</param>
/// <param name="Setting">The forbidden setting ids.</param>
/// <param name="Opening">The forbidden opening ids.</param>
/// <param name="TwistType">The forbidden twist-type ids.</param>
public sealed record StructureForbidden(
    IReadOnlyList<string> SubGenre,
    IReadOnlyList<string> Setting,
    IReadOnlyList<string> Opening,
    IReadOnlyList<string> TwistType);
