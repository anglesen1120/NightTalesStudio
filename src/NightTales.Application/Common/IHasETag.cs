namespace NightTales.Application.Common;

/// <summary>
/// A value that carries the strong ETag of the row it was read from, so the endpoint helpers set the
/// <c>ETag</c> header without every handler repeating the format (09 README D15, 09.1.4-T1).
/// </summary>
/// <remarks>
/// The ETag is opaque to clients and always includes the quotes, for example <c>"ep-7"</c>.
/// </remarks>
public interface IHasETag
{
    /// <summary>The quoted, strong ETag of this value, for example <c>"ep-7"</c>.</summary>
    string ETag { get; }
}
