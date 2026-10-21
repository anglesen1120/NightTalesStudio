using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace NightTales.Application.Features.SeriesBible.Events;

/// <summary>
/// The one place the <c>StoryApproved</c> proposal path recognises the series-level active-run index refusing a
/// write (02.6.3-T2 plan §7.1 step 7). A bible proposal is series-level (<c>episode_id IS NULL</c>), so an
/// approval that lands while another proposal of the same series is still <c>queued</c> or <c>running</c> is
/// refused by <c>ux_generation_runs_active_series</c> (<c>(series_id, kind) WHERE episode_id IS NULL AND status
/// IN ('queued','running')</c>). That refusal is not a failure of the delivery: the handler defers its own work
/// instead, so the proposals of one series run strictly one after another.
/// </summary>
/// <remarks>
/// The SQLSTATE and the constraint name are read off the provider's <see cref="DbException"/> through the BCL
/// only — this layer must not reference Npgsql (the shape of <c>FactSheetFlushConflict</c>, 02.5.4-T1 decision 6,
/// and of this feature's <c>BibleFlushConflict</c>). It is public because the unit tests of this feature exercise
/// it and this solution has no <c>InternalsVisibleTo</c>.
/// </remarks>
public static class BibleProposalRunConflict
{
    /// <summary>PostgreSQL's SQLSTATE for a unique-constraint violation (<c>unique_violation</c>).</summary>
    private const string UniqueViolation = "23505";

    /// <summary>The key Npgsql records the refused constraint under in <see cref="Exception.Data"/>.</summary>
    private const string ConstraintNameKey = "ConstraintName";

    /// <summary>
    /// The D2 partial unique index that allows one queued or running <em>series-level</em> run per series and kind
    /// (02.6.3-T2 / T17); <c>bible_proposal</c> is such a kind.
    /// </summary>
    public const string ActiveSeriesRunIndex = "ux_generation_runs_active_series";

    /// <summary>
    /// Whether the flush was refused by <see cref="ActiveSeriesRunIndex"/>. A refusal naming any other constraint
    /// is a defect this layer must not hide and is left to the caller's unit of work (it propagates).
    /// </summary>
    /// <param name="exception">The save's <see cref="DbUpdateException"/>.</param>
    /// <returns><c>true</c> when the series-level active-run index was the one refused.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    public static bool IsSeriesLevelRunInProgress(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException { SqlState: UniqueViolation } database
                && database.Data[ConstraintNameKey] is string constraint
                && string.Equals(constraint, ActiveSeriesRunIndex, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
