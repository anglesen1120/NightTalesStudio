namespace NightTales.Infrastructure.Persistence.Seeding;

/// <summary>
/// The outcome of one seeder run (09.2.5 plan §3.1): counts only. It never carries a value read from
/// the database, so it is safe to log and to assert on.
/// </summary>
/// <param name="Seeder">The catalog name; <see cref="ISeeder.Name"/>.</param>
/// <param name="Inserted">Rows the run added (absent natural key).</param>
/// <param name="Updated">Rows the run changed (present natural key, a catalog-owned field differed).</param>
/// <param name="Skipped">Rows that already matched, so nothing was written.</param>
/// <param name="Failed">
/// Whether the seeder threw and <see cref="SeedOptions.ContinueOnError"/> swallowed it, so the run
/// carried on. The counts are then all zero; <c>nts-seed</c> reads this to exit non-zero.
/// </param>
public sealed record SeedReport(string Seeder, int Inserted, int Updated, int Skipped, bool Failed = false);
