using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace NightTales.Application.Features.SeriesBible;

/// <summary>
/// The one place a bible save recognises the provider refusing a write race (02.6.3-T1, plan §4 U2). The row
/// lock of the command is what normally serialises two saves of one series, so a refusal can still arrive
/// when a writer bypassed it: the statement then answers the ETag contract's 412 <c>bible_conflict</c> —
/// the same answer a stale <c>If-Match</c> gets, because the client must re-read the version either way —
/// instead of a raw 500. A refusal naming anything else is a defect this layer must not hide and is left to
/// the caller's unit of work (it propagates).
/// </summary>
/// <remarks>
/// The SQLSTATEs are read off the provider's <see cref="DbException"/> through the BCL only: this layer must
/// not reference Npgsql (the shape of <c>FactSheetFlushConflict</c>, 02.5.4-T1 decision 6). It is public
/// because the unit tests of this feature exercise it and this solution has no <c>InternalsVisibleTo</c>.
/// </remarks>
public static class BibleFlushConflict
{
    /// <summary>PostgreSQL's SQLSTATE for a failed serialisable transaction (<c>serialization_failure</c>).</summary>
    private const string SerializationFailure = "40001";

    /// <summary>PostgreSQL's SQLSTATE for a deadlock the server resolved (<c>deadlock_detected</c>).</summary>
    private const string DeadlockDetected = "40P01";

    /// <summary>
    /// Whether a failed save was refused by a write race rather than by a constraint or a bug: a
    /// serialisation failure or a deadlock anywhere in the exception chain.
    /// </summary>
    /// <param name="exception">The save's <see cref="DbUpdateException"/>; <see cref="DbUpdateConcurrencyException"/> is handled by its own name.</param>
    /// <returns><c>true</c> when the refusal is the concurrency contract's 412.</returns>
    public static bool IsSerialisationFailure(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException { SqlState: SerializationFailure or DeadlockDetected })
            {
                return true;
            }
        }

        return false;
    }
}
