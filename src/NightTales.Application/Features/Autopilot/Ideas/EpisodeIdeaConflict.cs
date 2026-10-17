using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace NightTales.Application.Features.Autopilot.Ideas;

/// <summary>
/// The one place the <c>MarkUsedAsync</c> write recognises the table refusing it (02.8.1-T1 plan §3.1, §4 U4):
/// <c>episode_ideas</c> holds at most one idea per episode — <c>UX_episode_ideas_episode</c>,
/// <c>(episode_id) WHERE episode_id IS NOT NULL</c> — so marking a second idea used for an episode the series
/// already carries is refused by the index, not by the entity; and the same-owner key onto
/// <c>episodes (id, user_id)</c> refuses a link to an episode the idea's owner does not have, which answers
/// <c>not_found</c> instead of leaking the other tenant's row.
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

    /// <summary>PostgreSQL's SQLSTATE for a foreign-key violation (<c>foreign_key_violation</c>).</summary>
    private const string ForeignKeyViolation = "23503";

    /// <summary>The key Npgsql records the refused constraint under in <see cref="Exception.Data"/>.</summary>
    private const string ConstraintNameKey = "ConstraintName";

    /// <summary>
    /// The partial unique index that allows one idea per episode (plan §3.1), as the EF configuration names it
    /// (<c>HasDatabaseName("UX_episode_ideas_episode")</c>) and as PostgreSQL therefore reports it.
    /// </summary>
    public const string EpisodeUniqueIndex = "UX_episode_ideas_episode";

    /// <summary>
    /// The same-owner key onto <c>episodes (id, user_id)</c> (09 W3 CR-46): it refuses a link to an episode that
    /// does not exist for this owner, which is exactly the cross-tenant case the planner answers <c>not_found</c>
    /// for.
    /// </summary>
    public const string EpisodeForeignKey = "fk_episode_ideas_episodes_episode_id_user_id";

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

        return WasRefusedBy(exception, UniqueViolation, EpisodeUniqueIndex);
    }

    /// <summary>
    /// Whether the flush was refused by <see cref="EpisodeForeignKey"/>: the episode the caller asked to link does
    /// not exist for this idea's owner (another user's episode, or one that was deleted in between). A refusal
    /// naming any other constraint is a defect this layer must not hide and is left to the caller's unit of work
    /// (it propagates).
    /// </summary>
    /// <param name="exception">The save's <see cref="DbUpdateException"/>.</param>
    /// <returns><c>true</c> when the same-owner episode key was the one refused.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    public static bool IsEpisodeForeignKeyViolation(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return WasRefusedBy(exception, ForeignKeyViolation, EpisodeForeignKey);
    }

    /// <summary>
    /// Whether the provider refused the statement with <paramref name="sqlState"/> and named
    /// <paramref name="constraint"/>. The pair is read off the provider's <see cref="DbException"/> through the
    /// BCL only: this layer must not reference Npgsql.
    /// </summary>
    private static bool WasRefusedBy(DbUpdateException exception, string sqlState, string constraint)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException { SqlState: var state } database
                && string.Equals(state, sqlState, StringComparison.Ordinal)
                && database.Data[ConstraintNameKey] is string name
                && string.Equals(name, constraint, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
