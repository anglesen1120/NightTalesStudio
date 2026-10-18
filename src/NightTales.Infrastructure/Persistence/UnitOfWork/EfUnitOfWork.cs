using System.Data;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Infrastructure.Persistence.UnitOfWork;

/// <summary>
/// The <see cref="IUnitOfWork"/> of a command (09 README D5): one explicit <c>ReadCommitted</c>
/// transaction on the scope's <see cref="AppDbContext"/>, saved and committed once.
/// </summary>
/// <remarks>
/// <para>
/// Registered scoped, exactly like the context it wraps, so a handler's direct
/// <c>SaveChangesAsync</c> — which only the UnitOfWork and the seeders call — and the raw locks of
/// 02 (<c>pg_advisory_xact_lock</c>, <c>SELECT … FOR UPDATE</c>) run inside the same transaction.
/// </para>
/// <para>
/// A commit the database refuses is mapped to an error instead of an exception: a lost update becomes
/// <c>concurrency_conflict</c> (409); a transient failure — including the
/// <see cref="DbUpdateException"/> that wraps one, which is how <c>SaveChangesAsync</c> reports it —
/// and a constraint refusal of the command's <c>audit_logs</c> row both become
/// <c>service_unavailable</c> (503), because a change is never recorded without its audit row
/// (09.3.6-T1 failure handling). Every mapping rolls the transaction back first. A partial unique index a
/// command's own pre-check already covers — 02's active run, 09.6.1's render job, 06's one live connection per
/// channel — keeps the conflict status its failure table fixes instead of becoming a 500.
/// </para>
/// </remarks>
public sealed class EfUnitOfWork(AppDbContext database) : IUnitOfWork
{
    private const string ConcurrencyDescription =
        "The record changed while the command was running; retry with the current version.";

    private const string TransientDescription = "The database is temporarily unavailable; retry.";

    /// <summary>
    /// The stable description of a refused foreign key; it names the client's input, never the SQL or
    /// the constraint (09 README D17).
    /// </summary>
    private const string ForeignKeyDescription =
        "A record this request refers to no longer exists; reload and try again.";

    /// <summary>PostgreSQL's SQLSTATE for a foreign key violation.</summary>
    private const string ForeignKeyViolation = "23503";

    /// <summary>The table every command's audit row is written to (09 README BD15).</summary>
    private const string AuditTable = "audit_logs";

    /// <summary>PostgreSQL's SQLSTATE class for integrity-constraint violations (<c>23xxx</c>).</summary>
    private const string IntegrityConstraintClass = "23";

    /// <summary>
    /// The stable description of an audit row the database refused; it names the caller's situation,
    /// never the SQL or the constraint (09 README D17).
    /// </summary>
    private const string AuditRefusalDescription =
        "The change could not be recorded with its audit row, so nothing was saved; retry.";

    /// <summary>PostgreSQL's SQLSTATE for a unique-constraint violation.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>
    /// The D2 partial unique index that allows one queued or running run per episode
    /// (<c>episode_id IS NOT NULL AND status IN ('queued','running')</c>).
    /// </summary>
    private const string ActiveEpisodeRunIndex = "ux_generation_runs_active_episode";

    /// <summary>
    /// The partial unique index that allows one queued, submitted, running or ingesting render job per episode
    /// (09.6.1-T1, D5): the backstop of two requests that both passed the episode state check.
    /// </summary>
    private const string ActiveRenderJobIndex = "ux_render_jobs_active_episode";

    /// <summary>
    /// The partial unique index that allows one live connection per Google channel
    /// (<c>WHERE status &lt;&gt; 'revoked'</c>, 06 README D18): the authority for 06's "one owner per channel"
    /// rule, which is what a second account connecting the same channel loses to.
    /// </summary>
    private const string ChannelAccountIndex = "ux_channel_connections_account";

    /// <summary>
    /// The stable description of a refused live connection (06.1.2-T2): the same 409
    /// <c>channel_already_connected</c> the connect writer answers when its own locked read finds the other
    /// owner, so the race and the ordinary case read identically to the caller.
    /// </summary>
    private const string ChannelAlreadyConnectedDescription =
        "This account already has a live connection for that channel.";

    /// <summary>
    /// The stable description of a refused active run; the command already answers this situation with the
    /// 409 the caller is expecting, so the race must not surface as a 500 (02 README D2).
    /// </summary>
    private const string StoryInProgressDescription =
        "A story generation is already queued or running for this episode.";

    private const string FactSheetInProgressDescription =
        "A fact-sheet generation is already queued or running for this episode.";

    private const string FindSourcesInProgressDescription =
        "A source search is already queued or running for this episode.";

    private const string ActiveRunDescription = "A generation is already queued or running for this episode.";

    private IDbContextTransaction? _transaction;

    /// <inheritdoc />
    public async Task BeginAsync(CancellationToken cancellationToken)
    {
        if (_transaction is null)
        {
            _transaction = await database.Database
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<ErrorOr<Success>> CommitAsync(CancellationToken cancellationToken)
    {
        var transaction = _transaction
            ?? throw new InvalidOperationException($"'{nameof(CommitAsync)}' requires '{nameof(BeginAsync)}' first.");

        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _transaction = null;

            return Result.Success;
        }
        catch (DbUpdateConcurrencyException)
        {
            // The row changed between the load and the save (the xmin concurrency token of D8).
            await TryRollbackAsync(cancellationToken).ConfigureAwait(false);

            return Error.Conflict(code: ErrorCodes.ConcurrencyConflict, description: ConcurrencyDescription);
        }
        catch (DbUpdateException exception) when (IsForeignKeyViolation(exception))
        {
            // 09.3.1-T1 failure handling: a row the command referenced no longer exists (the user of a
            // `user_consents` row was deleted between steps) is the client's input being unusable, not a
            // server fault — 400 validation_failed instead of a raw 500 from the provider exception.
            await TryRollbackAsync(cancellationToken).ConfigureAwait(false);

            return Error.Validation(code: ErrorCodes.ValidationFailed, description: ForeignKeyDescription);
        }
        catch (DbUpdateException exception) when (IsAuditInsertRefusal(exception))
        {
            // 09.3.6-T1 failure handling: AuditBehavior adds the command's audit row inside this
            // transaction, so the provider refuses it here, at the single SaveChanges. The row is the
            // record of the change — the change is not allowed to commit without it — so the whole
            // command rolls back and the caller gets the 503 the failure table fixes, not a raw 500.
            await TryRollbackAsync(cancellationToken).ConfigureAwait(false);

            return Error.Failure(code: ErrorCodes.ServiceUnavailable, description: AuditRefusalDescription);
        }
        catch (DbUpdateException exception) when (IsActiveEpisodeRunViolation(exception))
        {
            await TryRollbackAsync(cancellationToken).ConfigureAwait(false);

            return ActiveRunConflict(exception);
        }
        catch (DbUpdateException exception) when (IsActiveRenderJobViolation(exception))
        {
            // 09.6.1-T1 failure handling: the second of two concurrent renders of one episode loses at the insert;
            // the reservation rolls back with the transaction, and the caller gets the concurrency 409, not a 500.
            await TryRollbackAsync(cancellationToken).ConfigureAwait(false);

            return Error.Conflict(code: ErrorCodes.ConcurrencyConflict, description: ConcurrencyDescription);
        }
        catch (DbUpdateException exception) when (IsChannelAlreadyConnectedViolation(exception))
        {
            // 06.1.2-T2 failure handling: two accounts connecting the same channel both read "no live row" and
            // both insert; the partial unique index refuses the second, so the loser gets the already-connected
            // 409 06.1.2-T1's callback maps to its fixed redirect — and never a raw 500 from the provider
            // exception. The winner's row is untouched by the rollback below.
            await TryRollbackAsync(cancellationToken).ConfigureAwait(false);

            return Error.Conflict(
                code: ErrorCodes.ChannelAlreadyConnected,
                description: ChannelAlreadyConnectedDescription);
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            await TryRollbackAsync(cancellationToken).ConfigureAwait(false);

            return Error.Failure(code: ErrorCodes.ServiceUnavailable, description: TransientDescription);
        }
    }

    /// <summary>
    /// Whether the save was refused by the D2 active-run index. The request already answers an active run with a
    /// 409 — this is the same situation, caught at the insert that lost the race, so it keeps that status instead
    /// of becoming a raw 500 (02.2.2-T2 implementation, 02.5.3-T2). The index is per <c>(episode, kind)</c>, so
    /// the mapping covers every kind whose pre-check lost the race, not only a fact-sheet run.
    /// </summary>
    private static bool IsActiveEpisodeRunViolation(Exception exception) =>
        ProviderError(exception) is { SqlState: UniqueViolation, ConstraintName: ActiveEpisodeRunIndex };

    private static bool IsActiveRenderJobViolation(Exception exception) =>
        ProviderError(exception) is { SqlState: UniqueViolation, ConstraintName: ActiveRenderJobIndex };

    private static bool IsChannelAlreadyConnectedViolation(Exception exception) =>
        ProviderError(exception) is { SqlState: UniqueViolation, ConstraintName: ChannelAccountIndex };

    /// <summary>
    /// The 409 of a refused active run, named for the kind of run the request was queueing: the index is
    /// per-episode-and-kind, so the refusal is per kind too. The kind is read from the entries the failed save
    /// reports and, when the provider reported none of them, from this unit of work's own context — the context
    /// that failed is the one that was inserting the run, so a tracked <c>Added</c> run names the kind. Only when
    /// no run is visible anywhere does the mapping state the kind-agnostic truth ("a generation is already
    /// queued or running for this episode", 409 <c>episode_invalid_state</c>): naming a kind it cannot see would
    /// be a guess.
    /// </summary>
    private Error ActiveRunConflict(DbUpdateException exception)
    {
        var kind = exception.Entries
            .Select(entry => entry.Entity)
            .OfType<GenerationRun>()
            .Select(run => (GenerationKind?)run.Kind)
            .FirstOrDefault()
            ?? database.ChangeTracker.Entries<GenerationRun>()
                .Where(entry => entry.State == EntityState.Added)
                .Select(entry => (GenerationKind?)entry.Entity.Kind)
                .FirstOrDefault();

        return kind switch
        {
            GenerationKind.Story => Error.Conflict(
                ErrorCodes.StoryGenerationInProgress,
                StoryInProgressDescription),
            GenerationKind.FactSheet => Error.Conflict(
                ErrorCodes.FactSheetGenerationInProgress,
                FactSheetInProgressDescription),
            GenerationKind.FindSources => Error.Conflict(
                ErrorCodes.FindSourcesInProgress,
                FindSourcesInProgressDescription),
            // Kind-agnostic on purpose (see the remarks): the index guarantees the episode has one active run,
            // so a conflict stating exactly that is always true, whatever kind it was.
            _ => Error.Conflict(ErrorCodes.EpisodeInvalidState, ActiveRunDescription),
        };
    }

    /// <inheritdoc />
    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        var transaction = _transaction;
        if (transaction is null)
        {
            return;
        }

        _transaction = null;
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the database reported a transient failure. <c>SaveChangesAsync</c> wraps the provider
    /// exception in a <see cref="DbUpdateException"/>, the transaction commit does not.
    /// </summary>
    private static bool IsTransient(Exception exception) =>
        exception is NpgsqlException { IsTransient: true }
        || exception.InnerException is NpgsqlException { IsTransient: true };

    /// <summary>
    /// Whether the save was refused by a foreign key (SQLSTATE 23503, <c>foreign_key_violation</c>).
    /// </summary>
    private static bool IsForeignKeyViolation(Exception exception) =>
        exception is DbUpdateException { InnerException: PostgresException { SqlState: ForeignKeyViolation } }
        || exception.InnerException is PostgresException { SqlState: ForeignKeyViolation };

    /// <summary>
    /// Whether the provider refused the command's <c>audit_logs</c> row. The test is the table the
    /// provider names in its own error, not the entries EF reports for the failed save, so a
    /// <c>CHECK</c> another table raises in the same batch keeps its existing behaviour instead of
    /// being reported as an audit-trail failure.
    /// </summary>
    private static bool IsAuditInsertRefusal(Exception exception) =>
        ProviderError(exception) is { TableName: AuditTable } error
        && error.SqlState.StartsWith(IntegrityConstraintClass, StringComparison.Ordinal);

    /// <summary>
    /// The provider's own error: <c>SaveChangesAsync</c> wraps it in a <see cref="DbUpdateException"/>.
    /// </summary>
    private static PostgresException? ProviderError(Exception exception) =>
        exception as PostgresException ?? exception.InnerException as PostgresException;

    /// <summary>
    /// Rolls back without letting a secondary failure replace the mapped error the caller is about to
    /// receive; the transient failure that caused it would break the rollback too.
    /// </summary>
    private async Task TryRollbackAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Deliberate: the mapped error is what the caller needs, and the context disposal rolls back
            // an abandoned transaction anyway.
        }
    }
}
