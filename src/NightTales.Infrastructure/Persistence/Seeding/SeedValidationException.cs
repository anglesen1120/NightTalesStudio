namespace NightTales.Infrastructure.Persistence.Seeding;

/// <summary>
/// A catalog or catalog file that failed validation before any write (09.2.5 plan §3.2, task
/// acceptance): the message is the error code itself, so a <c>nts-seed</c> run prints a key-named
/// reason and nothing was written. Two shapes are used: <c>seed_&lt;catalog&gt;_invalid:&lt;key&gt;:&lt;code&gt;</c>
/// for a bad row, and <c>seed_&lt;catalog&gt;_missing:&lt;path&gt;</c> for the file itself.
/// </summary>
/// <param name="code">The error code; it is also <see cref="Exception.Message"/>.</param>
public sealed class SeedValidationException(string code) : Exception(code)
{
    /// <summary>The stable error code, without secrets and without a stack fragment.</summary>
    public string Code { get; } = code;
}
