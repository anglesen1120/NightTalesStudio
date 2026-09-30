using ErrorOr;

namespace NightTales.Application.Common.Errors;

/// <summary>
/// The shared error factories, so a policy that appears in dozens of handlers is spelled once. The
/// HTTP status still comes from the code catalogue (<c>Api/Http/ErrorCatalogue.cs</c>), never from
/// <see cref="ErrorType"/> (09 README D7).
/// </summary>
public static class Errors
{
    /// <summary>
    /// The 404 of 09 README D10: a lookup through a tenant-filtered context found nothing, which means
    /// the row does not exist <em>or</em> belongs to another user. The caller must not be able to tell
    /// the two apart, so this is the answer for both — never 403 (id probing).
    /// </summary>
    public static Error NotFound() => Error.NotFound(code: ErrorCodes.NotFound);
}
