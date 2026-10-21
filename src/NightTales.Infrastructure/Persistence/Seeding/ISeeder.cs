namespace NightTales.Infrastructure.Persistence.Seeding;

/// <summary>
/// One catalog seeder (09.2.5 plan §3.1): a component that upserts the rows of a single
/// <c>seed/</c> catalog in the tracked <c>AppDbContext</c> scope. <see cref="Order"/> fixes the run
/// sequence, so a voice (or its template) exists before a row that references it is inserted.
/// </summary>
public interface ISeeder
{
    /// <summary>The catalog name; it is the <see cref="SeedReport.Seeder"/> value and the log field.</summary>
    string Name { get; }

    /// <summary>The position in the run sequence; lower runs first.</summary>
    int Order { get; }

    /// <summary>Upserts the catalog and returns the counts of one run.</summary>
    ValueTask<SeedReport> SeedAsync(CancellationToken ct);
}
