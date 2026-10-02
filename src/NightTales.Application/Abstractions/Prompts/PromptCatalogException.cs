namespace NightTales.Application.Abstractions.Prompts;

/// <summary>
/// The prompt catalog failed to load or to answer (02 README D5, F22): <see cref="Violations"/> carries
/// <b>every</b> problem of one load, never just the first.
/// </summary>
/// <remarks>
/// <para>
/// <c>PromptCatalog</c> loads and validates in its constructor and throws this type, so a host that
/// registers the catalog cannot start with a missing preset, a sha256 that no longer matches the bytes
/// or a template that uses a placeholder outside the closed list; the start-up gate turns the exception
/// into a non-zero exit that names every violation at once — a start-up that reports one of five
/// problems wastes a deploy cycle.
/// </para>
/// <para>
/// The same type is thrown by a lookup of a key the manifest does not track (a step prompt of a task
/// that has not shipped yet, a schema name that does not exist). That is a programming error, not user
/// input, and it never reaches a caller as an <c>ErrorOr</c> failure.
/// </para>
/// </remarks>
public sealed class PromptCatalogException : Exception
{
    /// <summary>The violations of one load, each naming the file and the expectation it broke.</summary>
    public IReadOnlyList<string> Violations { get; }

    /// <summary>Creates the exception from one load's violations.</summary>
    /// <param name="violations">Every violation of the load; the message joins them in order.</param>
    public PromptCatalogException(IReadOnlyList<string> violations)
        : base(Describe(violations))
    {
        ArgumentNullException.ThrowIfNull(violations);

        Violations = [.. violations];
    }

    /// <summary>
    /// The message of a failed load: the count and then every violation, so a log line and the
    /// exception's own text say the same thing.
    /// </summary>
    private static string Describe(IReadOnlyList<string>? violations) =>
        violations is null
            ? "The prompt catalog is invalid."
            : $"The prompt catalog is invalid ({violations.Count} violation(s)): {string.Join("; ", violations)}";
}
