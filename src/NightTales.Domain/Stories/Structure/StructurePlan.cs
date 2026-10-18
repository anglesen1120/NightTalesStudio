#pragma warning disable CA1716 // `Structure` is the planned namespace of the variety guard (02.8.4-T1 plan); the .NET-only API is never consumed from Visual Basic.
namespace NightTales.Domain.Stories.Structure;

/// <summary>
/// What the variety guard asks the writer to do for one episode (02 README D6): the single value each
/// dimension must take and the values it must avoid. Values are vocabulary ids, never free text.
/// </summary>
/// <remarks>
/// <para>
/// 02.2.3-T2 declares the shape the prompt assembler serialises under <c>structure_plan</c>; 02.8.4-T1
/// owns the variety guard that fills it and may add members that do not change the wire shape.
/// </para>
/// <para>
/// <see cref="Empty"/> is the unconstrained plan: the assembler omits the whole <c>structure_plan</c> key when
/// the plan is <see langword="null"/>, so an empty plan and no plan are the same story for the writer, but a
/// caller that computed nothing can say so without a second null check.
/// </para>
/// </remarks>
/// <param name="Required">The values the writer must use.</param>
/// <param name="Forbidden">The values the writer must not use.</param>
public sealed record StructurePlan(StructureRequirements Required, StructureForbidden Forbidden)
{
    /// <summary>The unconstrained plan: nothing required, nothing forbidden.</summary>
    public static readonly StructurePlan Empty = new(StructureRequirements.None, StructureForbidden.None);

    /// <summary>Whether the plan constrains nothing, so it carries no instruction for the writer.</summary>
    public bool IsEmpty => Required == StructureRequirements.None && Forbidden.IsEmpty;
}

/// <summary>The single value each structure dimension must take; <see langword="null"/> means unconstrained.</summary>
/// <param name="SubGenre">The required sub-genre id.</param>
/// <param name="Setting">The required setting id.</param>
/// <param name="Opening">The required opening id.</param>
/// <param name="TwistType">The required twist-type id.</param>
public sealed record StructureRequirements(string? SubGenre, string? Setting, string? Opening, string? TwistType)
{
    /// <summary>No dimension is required.</summary>
    public static readonly StructureRequirements None = new(null, null, null, null);
}

/// <summary>The values each structure dimension must not take.</summary>
/// <param name="SubGenre">The forbidden sub-genre ids.</param>
/// <param name="Setting">The forbidden setting ids.</param>
/// <param name="Opening">The forbidden opening ids.</param>
/// <param name="TwistType">The forbidden twist-type ids.</param>
public sealed record StructureForbidden(
    IReadOnlyList<string> SubGenre,
    IReadOnlyList<string> Setting,
    IReadOnlyList<string> Opening,
    IReadOnlyList<string> TwistType)
{
    /// <summary>No dimension is forbidden.</summary>
    public static readonly StructureForbidden None = new([], [], [], []);

    /// <summary>Whether every dimension's forbidden list is empty.</summary>
    public bool IsEmpty =>
        SubGenre is not { Count: > 0 }
        && Setting is not { Count: > 0 }
        && Opening is not { Count: > 0 }
        && TwistType is not { Count: > 0 };
}
