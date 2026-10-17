using ErrorOr;
using NightTales.Application.Features.Research.Sources.Commands.AddSource;
using NightTales.Domain.Episodes;

namespace NightTales.Application.Features.Research.Sources;

/// <summary>
/// The one write path of a url source (02.5.1-T1 Design), shared by <c>POST /sources</c> and the candidate add
/// command (02.7.2-T2 R7, plan §4): the episode lock, the state gate, the URL parse and public-suffix check, the
/// blocklist, the episode cap, the rolling 24 h cap, the duplicate check and the inserts of the source, its fetch
/// task and its creation record.
/// </summary>
/// <remarks>
/// <para>
/// The service writes through the caller's tracked <c>AppDbContext</c>, inside the caller's unit of work: it never
/// opens or commits a transaction, never calls <c>SaveChanges</c> and never writes the audit row — the pipeline does
/// all three once, for the command that called it. That is why the candidate add command does not send
/// <c>AddSourceCommand</c> through the mediator (R7): a nested send would commit a second unit of work and a second
/// audit row.
/// </para>
/// <para>
/// The check order is the contract of 02.5.1-T1 and the acceptance checks assert the codes, so both callers see
/// 404 → state gate 409 → URL syntax 400 → blocklist 400 → episode cap 409 → 24 h cap 429 → duplicate 409 → insert.
/// </para>
/// </remarks>
public interface ISourceAdder
{
    /// <summary>
    /// The whole <c>POST /sources</c> path: the caller check, the locked episode load and the add itself.
    /// </summary>
    /// <param name="episodeId">The route value; the episode must belong to the caller.</param>
    /// <param name="url">The URL as pasted, or <see langword="null"/> for a note.</param>
    /// <param name="noteText">The user's note, or <see langword="null"/> for a URL.</param>
    /// <param name="cancellationToken">The request's token.</param>
    /// <returns>The created source, or the first failing check's error.</returns>
    ValueTask<ErrorOr<SourceDto>> AddAsync(
        Guid episodeId,
        string? url,
        string? noteText,
        CancellationToken cancellationToken);

    /// <summary>
    /// Takes the episode row lock, loads the episode through the tenant filter and applies the state gate — the
    /// part of <see cref="AddAsync"/> a caller needs before it may look at anything else of the episode.
    /// </summary>
    /// <remarks>
    /// Another user's id, an unknown id and a cancelled episode all answer <c>not_found</c>, never 403.
    /// </remarks>
    /// <param name="episodeId">The route value.</param>
    /// <param name="cancellationToken">The request's token.</param>
    /// <returns>The locked episode, or the load's or the gate's error.</returns>
    ValueTask<ErrorOr<Episode>> LockEditableEpisodeAsync(Guid episodeId, CancellationToken cancellationToken);

    /// <summary>
    /// The checks and inserts of <see cref="AddAsync"/> after its gate, for a caller that already holds the episode
    /// row lock and may need to validate something of its own between the gate and the insert (the candidate's
    /// status, in 02.7.2-T2).
    /// </summary>
    /// <remarks>
    /// The caller MUST hold the lock <see cref="LockEditableEpisodeAsync"/> took, in the same transaction, or the
    /// cap counts and the duplicate check read stale rows. The episode is that locked, tenant-filtered row.
    /// </remarks>
    /// <param name="userId">The caller, for the rolling 24 h cap's advisory lock and count.</param>
    /// <param name="episode">The episode returned by <see cref="LockEditableEpisodeAsync"/>.</param>
    /// <param name="url">The URL, or <see langword="null"/> for a note.</param>
    /// <param name="noteText">The note, or <see langword="null"/> for a URL.</param>
    /// <param name="cancellationToken">The request's token.</param>
    /// <returns>The created source, or the first failing check's error.</returns>
    ValueTask<ErrorOr<SourceDto>> AddLockedAsync(
        Guid userId,
        Episode episode,
        string? url,
        string? noteText,
        CancellationToken cancellationToken);
}
