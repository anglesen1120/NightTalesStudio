using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace NightTales.Application.Features.Publishing.Commands.CreatePublication;

/// <summary>
/// The one place the create's flush recognises the table refusing it (06.2.1-T1 task §Application step 8, 06
/// README D6/D18): <c>ux_publications_active_episode_platform</c> allows one live publication per
/// <c>(episode_id, platform)</c>, so a second create while the first is active is refused by the index rather
/// than by a read that a concurrent create could invalidate; and the same-owner key onto
/// <c>episodes (id, user_id)</c> refuses a row whose episode vanished between the read and the insert, which is
/// the 404 the tenant-filtered read would have answered.
/// </summary>
/// <remarks>
/// <para>
/// The SQLSTATE and the constraint name are read off the provider's <see cref="DbException"/> through the BCL
/// only — this layer must not reference Npgsql (the shape of <c>EpisodeIdeaConflict</c> and
/// <c>FactSheetFlushConflict</c>). A refusal naming any other constraint is a defect this layer must not hide:
/// it is left to the unit of work and propagates.
/// </para>
/// <para>
/// Every member is <c>internal</c>: the handler is the only caller, and the create tests exercise the two
/// predicates through it. The two names are the ones <c>PublicationConfiguration</c> and the generated migration
/// declare, so a rename there fails the create tests instead of silently turning a 409 into a 500.
/// </para>
/// </remarks>
internal static class CreatePublicationConflict
{
    /// <summary>PostgreSQL's SQLSTATE for a unique-constraint violation (<c>unique_violation</c>).</summary>
    private const string UniqueViolation = "23505";

    /// <summary>PostgreSQL's SQLSTATE for a foreign-key violation (<c>foreign_key_violation</c>).</summary>
    private const string ForeignKeyViolation = "23503";

    /// <summary>The key Npgsql records the refused constraint under in <see cref="Exception.Data"/>.</summary>
    private const string ConstraintNameKey = "ConstraintName";

    /// <summary>
    /// The partial unique index that makes "one live publication per episode and platform" a storage fact
    /// (06 README D6/D18), as the EF configuration names it and PostgreSQL therefore reports it.
    /// </summary>
    internal const string ActiveEpisodePlatformIndex = "ux_publications_active_episode_platform";

    /// <summary>
    /// The same-owner key onto <c>episodes (id, user_id)</c>: it refuses a publication for an episode that does
    /// not exist for this owner, which is the 404 the tenant-filtered read answers.
    /// </summary>
    internal const string EpisodeForeignKey = "fk_publications_episodes_episode_id_user_id";

    /// <summary>Whether the flush was refused by the one-live-publication index (the handler's 409).</summary>
    /// <param name="exception">The exception the flush raised.</param>
    /// <returns><c>true</c> when the partial unique index was the one refused.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    internal static bool IsActivePublicationViolation(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return WasRefusedBy(exception, UniqueViolation, ActiveEpisodePlatformIndex);
    }

    /// <summary>Whether the flush was refused by <see cref="EpisodeForeignKey"/> (the handler's 404).</summary>
    /// <param name="exception">The exception the flush raised.</param>
    /// <returns><c>true</c> when the same-owner episode key was the one refused.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    internal static bool IsEpisodeForeignKeyViolation(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return WasRefusedBy(exception, ForeignKeyViolation, EpisodeForeignKey);
    }

    /// <summary>
    /// Whether the provider refused a statement with <paramref name="sqlState"/> and named
    /// <paramref name="constraint"/>. The pair is read off the provider's <see cref="DbException"/> through the
    /// BCL only, and the walk covers a raw provider error as well as the <see cref="DbUpdateException"/> a
    /// <c>SaveChanges</c> wraps it in.
    /// </summary>
    private static bool WasRefusedBy(Exception exception, string sqlState, string constraint)
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
