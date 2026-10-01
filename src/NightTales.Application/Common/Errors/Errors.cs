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

    /// <summary>
    /// The 400 of 09 README D7 for a check only the handler can make (another row it just read, a
    /// catalog lookup, a cursor it just decoded): <c>validation_failed</c> with the camelCase
    /// <paramref name="field"/> the client must fix, which is the shape the <c>ValidationBehavior</c>
    /// produces for a FluentValidation failure.
    /// </summary>
    /// <param name="field">The camelCase wire field, for example <c>voiceId</c> or <c>limit</c>.</param>
    /// <param name="description">The client-safe reason.</param>
    public static Error ValidationFailed(string field, string description) =>
        Error.Validation(
            code: ErrorCodes.ValidationFailed,
            description: description,
            metadata: new Dictionary<string, object> { [ErrorMetadata.Field] = field });
}
