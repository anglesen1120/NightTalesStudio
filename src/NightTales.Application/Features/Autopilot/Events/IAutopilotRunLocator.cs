namespace NightTales.Application.Features.Autopilot.Events;

/// <summary>
/// Resolves the autopilot run an outbox event belongs to, whoever owns it (02.8.3 plan §7.2): the events of a
/// step carry the episode, and the run's owner is the tenant every follow-up command must run as.
/// </summary>
/// <remarks>
/// <para>
/// The lookup is the allow-listed cross-user read of 09 README D10 (<c>ISystemWorkQuery</c>): it yields the
/// <c>(id, user_id)</c> pair only, and the caller then opens the owner's scope. It is needed because the render
/// events (<c>render_succeeded.v1</c>, <c>render_failed.v1</c>) carry no user id at all, and a handler must not
/// infer an owner from an episode read it is not yet allowed to make.
/// </para>
/// <para>
/// Only a <b>non-terminal</b> run is a match: an episode of a user (never planned by the autopilot) has no run, and
/// a run that already finished must not be advanced by a late redelivery. Both answer
/// <see langword="null"/>, which every handler treats as a silent no-op.
/// </para>
/// </remarks>
public interface IAutopilotRunLocator
{
    /// <summary>Finds the live run of <paramref name="episodeId"/>.</summary>
    /// <param name="episodeId">The episode the event names.</param>
    /// <param name="ct">Cancels the lookup.</param>
    /// <returns>The run and its owner, or <see langword="null"/> when the episode has no live run.</returns>
    Task<(Guid RunId, Guid UserId)?> FindActiveByEpisodeAsync(Guid episodeId, CancellationToken ct);
}
