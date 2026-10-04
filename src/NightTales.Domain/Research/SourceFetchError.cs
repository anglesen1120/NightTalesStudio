namespace NightTales.Domain.Research;

/// <summary>
/// A failed fetch as data (02.5.2-T1 Design): the D8 job code and, when the host answered, its HTTP status.
/// </summary>
/// <param name="Code">One of the D8 codes, for example <c>source_unreachable</c>.</param>
/// <param name="HttpStatus">The status the host answered with, when it answered.</param>
public sealed record SourceFetchError(string Code, int? HttpStatus = null);
