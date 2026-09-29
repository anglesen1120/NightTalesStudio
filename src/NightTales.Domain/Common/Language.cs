namespace NightTales.Domain.Common;

/// <summary>
/// Content language of a series, a voice or a story (09 README D16). Stored as snake_case text with
/// a <c>CHECK</c> constraint (09 README D8).
/// </summary>
public enum Language
{
    Vi,
    En,
}
