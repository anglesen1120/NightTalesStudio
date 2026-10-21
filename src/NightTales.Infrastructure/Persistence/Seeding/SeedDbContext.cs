using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;

namespace NightTales.Infrastructure.Persistence.Seeding;

/// <summary>
/// The two helpers every seeder needs from the tracked context (09.2.5 plan §3.2): a transactional
/// unique-violation test and a tracker reset for the concurrent-insert retry.
/// </summary>
internal static class SeedDbContext
{
    /// <summary>True when <paramref name="exception"/> wraps a PostgreSQL unique violation (SQLSTATE 23505).</summary>
    internal static bool IsUniqueViolation(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };
    }

    /// <summary>
    /// Detaches every tracked entity after a failed <c>SaveChanges</c>, so the upsert can re-read the
    /// rows the other host committed and turn the losing inserts into updates.
    /// </summary>
    /// <remarks>
    /// The port is the scoped <c>AppDbContext</c> (09 README D8), the only context that tracks and saves;
    /// the cast is how the seeder reaches <c>ChangeTracker</c> without widening <see cref="IAppDbContext"/>
    /// for a single caller.
    /// </remarks>
    internal static void ResetTracking(IAppDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (db is not DbContext context)
        {
            throw new InvalidOperationException("Seeding requires the tracked AppDbContext (09 README D8).");
        }

        context.ChangeTracker.Clear();
    }
}
