namespace NightTales.Infrastructure.Persistence;

/// <summary>
/// Persistence configuration bound from the <c>Persistence</c> section (09 README D8, §3.3).
/// </summary>
public sealed class PersistenceOptions
{
    public const string SectionName = "Persistence";

    public const string ConnectionStringName = "Postgres";

    public const int DefaultCommandTimeoutSeconds = 30;

    /// <summary>Applied to every Npgsql command; defaults to <see cref="DefaultCommandTimeoutSeconds"/>.</summary>
    public int CommandTimeoutSeconds { get; set; } = DefaultCommandTimeoutSeconds;
}
