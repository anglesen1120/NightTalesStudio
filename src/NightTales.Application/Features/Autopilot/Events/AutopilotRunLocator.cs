using NightTales.Application.Abstractions;
using NightTales.Domain.Series.Autopilot;

namespace NightTales.Application.Features.Autopilot.Events;

/// <summary>
/// The <see cref="ISystemWorkQuery"/> implementation of <see cref="IAutopilotRunLocator"/>: one id-only read of
/// <c>autopilot_runs</c> across users, filtered by the episode and by "not finished yet".
/// </summary>
/// <remarks>
/// <para>
/// <b>Ids only.</b> The port returns <c>(Id, UserId)</c> pairs and never a row; every actual read of the run then
/// happens in the owner's own scope, so the tenant filter of 09 README D10 stays on for it.
/// </para>
/// <para>
/// <b>The terminal set is spelled out</b> because a predicate must be translatable: <c>AutopilotRunStateMachine</c>
/// answers in C#, not in SQL, and a hand-maintained list that drifted from it would advance finished runs. The
/// array is built from exactly the five states <c>IsTerminal</c> names, and the architecture is guarded by the
/// domain's own test of that set.
/// </para>
/// </remarks>
/// <param name="work">The allow-listed cross-user work query.</param>
public sealed class AutopilotRunLocator(ISystemWorkQuery work) : IAutopilotRunLocator
{
    /// <summary>The five terminal states of plan §3.5, the same set as <c>AutopilotRunStateMachine.IsTerminal</c>.</summary>
    private static readonly AutopilotRunState[] TerminalStates =
    [
        AutopilotRunState.Published,
        AutopilotRunState.PublishedPrivateLocked,
        AutopilotRunState.GateFailed,
        AutopilotRunState.CancelledByOwner,
        AutopilotRunState.Failed,
    ];

    /// <inheritdoc />
    public async Task<(Guid RunId, Guid UserId)?> FindActiveByEpisodeAsync(Guid episodeId, CancellationToken ct)
    {
        await foreach (var pair in work
            .PendingAsync<AutopilotRun>(
                run => run.EpisodeId == episodeId && !TerminalStates.Contains(run.State),
                batch: 1,
                ct)
            .ConfigureAwait(false))
        {
            return pair;
        }

        return null;
    }
}
