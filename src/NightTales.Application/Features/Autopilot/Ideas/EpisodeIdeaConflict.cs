using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace NightTales.Application.Features.Autopilot.Ideas;

/// <summary>
/// The one place the <c>MarkUsedAsync</c> write recognises the episode-uniqueness index refusing a write
/// (02.8.1-T1 plan §3.1, §4 U4). <c>episode_ideas</c> holds at most one idea per episode —
/// <c>UX_episode_ideas_episode</c>, <c>(episode_id) WHERE episode_id IS NOT NULL</c> — so marking a second idea
/// used for an episode the series already has an idea for is refused by the index, not by the entity.
/// </summary>
/// <remarks>
/// The SQLSTATE and the constraint name are read off the provider's <see cref="DbException"/> through the BCL
/// only — this layer must not reference Npgsql (the shape of <c>BibleProposalRunConflict</c> and
/// <c>FactSheetFlushConflict</c>). It is public because the unit tests of this feature exercise it and this
/// solution has no <c>InternalsVisibleTo</c>.
/// </remarks>
public static class EpisodeIdeaConflict
{
    /// <summary>PostgreSQL's SQLSTATE for a unique-constraint violation (<c>unique_violation</c>).</summary>
    private const string UniqueViolation = "23505";

    /// <summary>The key Npgsql records the refused constraint under in <see cref="Exception.Data"/>.</summary>
    private const string ConstraintNameKey = "ConstraintName";

    /// <summary>
    /// The partial unique index that allows one idea per episode (plan §3.1), as the EF configuration names it
    /// (<c>HasDatabaseName("UX_episode_ideas_episode")</c>) and as PostgreSQL therefore reports it.
    /// </summary>
    public const string EpisodeUniqueIndex = "UX_episode_ideas_episode";

    /// <summary>
    /// Whether the flush was refused by <see cref="EpisodeUniqueIndex"/>. A refusal naming any other constraint
    /// is a defect this layer must not hide and is left to the caller's unit of work (it propagates).
    /// </summary>
    /// <param name="exception">The save's <see cref="DbUpdateException"/>.</param>
    /// <returns><c>true</c> when the episode-uniqueness index was the one refused.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    public static bool IsEpisodeUniqueViolation(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException { SqlState: UniqueViolation } database
                && database.Data[ConstraintNameKey] is string constraint
                && string.Equals(constraint, EpisodeUniqueIndex, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
