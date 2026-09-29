namespace NightTales.Application.Common.Errors;

/// <summary>
/// Snake_case error codes (09 README D7). The HTTP status is resolved from the code catalogue
/// (<c>Api/Http/ErrorCatalogue.cs</c>), never from <c>ErrorType</c>.
/// </summary>
public static class ErrorCodes
{
    /// <summary>A FluentValidation failure; mapped to 400 with errors grouped by camelCase field.</summary>
    public const string ValidationFailed = "validation_failed";
}
