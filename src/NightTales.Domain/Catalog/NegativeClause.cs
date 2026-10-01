namespace NightTales.Domain.Catalog;

/// <summary>
/// The versioned platform negative clause (03 README D1): text every image prompt ends with, whatever
/// template a series chose. The clause lives outside the templates (F07), so changing it is one
/// version bump and one seed-file bump, never a rewrite of the seven catalog rows.
/// </summary>
/// <remarks>
/// A template must <b>not</b> contain this text (<see cref="StyleTemplateRules"/>); the builder appends
/// it exactly once. The relay (03.4.3-T1) rejects a prompt that does not end with
/// <see cref="Current"/>.
/// </remarks>
public static class NegativeClause
{
    /// <summary>The version of <see cref="V1"/>; a seed file must declare the same one.</summary>
    public const string Version = "v1";

    /// <summary>The v1 clause text.</summary>
    public const string V1 = "No gore, no blood, no text, no letters, no logos, no watermark, no real person likeness.";

    /// <summary>The clause the builder appends today.</summary>
    public static string Current => V1;
}
