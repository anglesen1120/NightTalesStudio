using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace NightTales.Application.Features.Research.Commands;

/// <summary>
/// The one place the fact-sheet edit and approve paths recognise the provider refusing a write race (02.5.4-T1,
/// decision 6). The two unique indexes of <c>fact_sheets</c> are what an edit, an approval and the generate job
/// contend on, so a refusal naming either answers the ETag contract's 412 <c>fact_sheet_conflict</c> instead of
/// becoming a raw 500; a refusal naming any other constraint is a defect this layer must not hide and is left to
/// the caller's unit of work (it propagates).
/// </summary>
internal static class FactSheetFlushConflict
{
    /// <summary>PostgreSQL's SQLSTATE for a unique-constraint violation (<c>unique_violation</c>).</summary>
    private const string UniqueViolation = "23505";

    /// <summary>The key Npgsql records the refused constraint under in <see cref="Exception.Data"/>.</summary>
    private const string ConstraintNameKey = "ConstraintName";

    /// <summary>
    /// The unique indexes of <c>fact_sheets</c> (02.5.3-T2 migration): <c>(episode_id, version)</c> and the
    /// partial <c>(episode_id) WHERE status = approved</c>.
    /// </summary>
    private static readonly string[] FactSheetIndexes =
    [
        "ux_fact_sheets_episode_version",
        "ux_fact_sheets_episode_approved",
    ];

    /// <summary>
    /// Whether the flush was refused by one of <see cref="FactSheetIndexes"/>. The Application layer may not
    /// reference Npgsql, so the provider's error is read through the BCL only: <see cref="DbException.SqlState"/>
    /// and the constraint name the provider records in <see cref="Exception.Data"/> (the keys <c>SqlState</c> and
    /// <c>ConstraintName</c>) — the same fields the Infrastructure layer reads off <c>PostgresException</c>.
    /// </summary>
    /// <param name="exception">The save's <see cref="DbUpdateException"/>.</param>
    /// <returns><c>true</c> when a fact-sheet unique index was the one refused.</returns>
    internal static bool IsFactSheetUniqueViolation(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException { SqlState: UniqueViolation } database
                && database.Data[ConstraintNameKey] is string constraint
                && Array.IndexOf(FactSheetIndexes, constraint) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}
