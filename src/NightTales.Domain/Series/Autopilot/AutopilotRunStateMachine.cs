namespace NightTales.Domain.Series.Autopilot;

/// <summary>
/// The run state machine of 02.8.3 plan §3.5: which moves exist, which states are terminal, which ones are
/// producing, which ones write an <c>outcome</c>, and what that outcome is.
/// </summary>
/// <remarks>
/// <para>
/// The machine is the invariant the aggregate enforces: <see cref="AutopilotRun.AdvanceTo"/> asks
/// <see cref="CanTransition"/> before it writes a state, so a move the plan's transition table does not contain
/// throws instead of corrupting the column. "<c>any</c>" in the table means "any non-terminal state": a terminal
/// run never moves again, which is why no terminal state has an outgoing move.
/// </para>
/// <para>
/// Plan §3.5's two rules that hold for every row, encoded here: <see cref="IsTerminal"/> is the set that sets
/// <c>finished_at</c>, and <see cref="WritesOutcome"/> is the wider set of stopping states whose <c>outcome</c>
/// answers "why did it stop" (<see cref="OutcomeFor"/>).
/// </para>
/// </remarks>
public static class AutopilotRunStateMachine
{
    /// <summary>
    /// Whether the run can never move again (plan §3.5 rule 3): <c>published</c>, <c>published_private_locked</c>,
    /// <c>gate_failed</c>, <c>cancelled_by_owner</c>, <c>failed</c>. These are the states
    /// <see cref="AutopilotRun.FinishedAt"/> is written for.
    /// </summary>
    /// <param name="state">The state the run holds.</param>
    /// <returns><see langword="true"/> for the five terminal states.</returns>
    public static bool IsTerminal(AutopilotRunState state) => state switch
    {
        AutopilotRunState.Published
            or AutopilotRunState.PublishedPrivateLocked
            or AutopilotRunState.GateFailed
            or AutopilotRunState.CancelledByOwner
            or AutopilotRunState.Failed => true,
        _ => false,
    };

    /// <summary>
    /// Whether the run is still producing its episode (plan §3.5, the "at most one producing run per series"
    /// guard): <c>planned</c>, <c>idea_picked</c>, <c>researching</c>, <c>story_generating</c>, <c>story_gate</c>,
    /// <c>waiting_owner</c>, <c>rendering</c>, <c>video_gate</c>, <c>publication_created</c>.
    /// </summary>
    /// <remarks>
    /// The plan draws the range as "planned … publication_created", which is an interval of the diagram rather than
    /// of the enum, so the set is spelled out. Two non-terminal states are deliberately outside it:
    /// <see cref="AutopilotRunState.Paused"/> (a paused run is held, so it must not block the next slot) and
    /// <see cref="AutopilotRunState.AwaitingAuthorization"/> (a run blocked on a re-accept produced nothing and is
    /// held the same way). <see cref="AutopilotRunState.VetoWindow"/> and
    /// <see cref="AutopilotRunState.Scheduled"/> are outside it because production is finished and the publication
    /// belongs to 06 — plan §3.5 uses exactly that to feed the next slot.
    /// </remarks>
    /// <param name="state">The state the run holds.</param>
    /// <returns><see langword="true"/> for the nine producing states.</returns>
    public static bool IsProducing(AutopilotRunState state) => state switch
    {
        AutopilotRunState.Planned
            or AutopilotRunState.IdeaPicked
            or AutopilotRunState.Researching
            or AutopilotRunState.StoryGenerating
            or AutopilotRunState.StoryGate
            or AutopilotRunState.WaitingOwner
            or AutopilotRunState.Rendering
            or AutopilotRunState.VideoGate
            or AutopilotRunState.PublicationCreated => true,
        _ => false,
    };

    /// <summary>
    /// Whether standing in <paramref name="state"/> is a stop that writes the run's <c>outcome</c> (plan §3.5
    /// rule 1): the five terminal states plus the three blocked states <c>waiting_owner</c>, <c>paused</c> and
    /// <c>awaiting_authorization</c>.
    /// </summary>
    /// <param name="state">The state the run holds or is entering.</param>
    /// <returns><see langword="true"/> for the eight stopping states.</returns>
    public static bool WritesOutcome(AutopilotRunState state) => state switch
    {
        AutopilotRunState.WaitingOwner
            or AutopilotRunState.Paused
            or AutopilotRunState.AwaitingAuthorization => true,
        _ => IsTerminal(state),
    };

    /// <summary>
    /// The <c>outcome</c> a stopping state writes (plan §3.5 rule 1): the run's answer to "why did it stop".
    /// </summary>
    /// <param name="state">The state the run holds or is entering.</param>
    /// <returns>
    /// The mapped outcome for the eight states <see cref="WritesOutcome"/> accepts, and <see langword="null"/> for
    /// every other state, because a run that continues leaves <c>outcome</c> empty.
    /// </returns>
    public static AutopilotRunOutcome? OutcomeFor(AutopilotRunState state) => state switch
    {
        AutopilotRunState.Published => AutopilotRunOutcome.Published,
        AutopilotRunState.PublishedPrivateLocked => AutopilotRunOutcome.PublishedPrivateLocked,
        AutopilotRunState.GateFailed => AutopilotRunOutcome.GateFailed,
        AutopilotRunState.CancelledByOwner => AutopilotRunOutcome.CancelledByOwner,
        AutopilotRunState.Failed => AutopilotRunOutcome.Failed,
        AutopilotRunState.WaitingOwner => AutopilotRunOutcome.WaitingOwner,
        AutopilotRunState.Paused => AutopilotRunOutcome.Paused,
        AutopilotRunState.AwaitingAuthorization => AutopilotRunOutcome.AwaitingAuthorization,
        _ => null,
    };

    /// <summary>
    /// The state a stopping outcome stands for — the inverse of <see cref="OutcomeFor"/> over all eight stopping
    /// states, which is what <see cref="AutopilotRun.Finish"/> uses to pick the terminal state of an outcome.
    /// </summary>
    /// <param name="outcome">The outcome the caller wants to write.</param>
    /// <returns>The state that writes <paramref name="outcome"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="outcome"/> is not a defined member.</exception>
    public static AutopilotRunState StateFor(AutopilotRunOutcome outcome) => outcome switch
    {
        AutopilotRunOutcome.Published => AutopilotRunState.Published,
        AutopilotRunOutcome.PublishedPrivateLocked => AutopilotRunState.PublishedPrivateLocked,
        AutopilotRunOutcome.WaitingOwner => AutopilotRunState.WaitingOwner,
        AutopilotRunOutcome.GateFailed => AutopilotRunState.GateFailed,
        AutopilotRunOutcome.CancelledByOwner => AutopilotRunState.CancelledByOwner,
        AutopilotRunOutcome.AwaitingAuthorization => AutopilotRunState.AwaitingAuthorization,
        AutopilotRunOutcome.Paused => AutopilotRunState.Paused,
        AutopilotRunOutcome.Failed => AutopilotRunState.Failed,
        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome),
            outcome,
            "An autopilot run outcome has no state (02.8.3 plan §3.5)."),
    };

    /// <summary>
    /// Whether the plan §3.5 transition table contains the move <paramref name="from"/> → <paramref name="to"/>.
    /// Every row of that table is encoded here; nothing outside it is true.
    /// </summary>
    /// <remarks>
    /// The rows, quoted from the plan, in the order they are checked below: the two "any" rows (<c>any
    /// non-terminal → paused</c>, <c>any → failed</c>), the resume row (<c>paused → the state its step implies</c>,
    /// which admits every non-terminal state because a pause can start in any of them), and then each explicit
    /// row: <c>planned ⇄ awaiting_authorization</c>, <c>planned → idea_picked</c>,
    /// <c>idea_picked → story_generating | researching | waiting_owner</c>, <c>researching → waiting_owner</c>,
    /// <c>waiting_owner → story_generating | rendering | publication_created</c>,
    /// <c>story_generating → story_gate | story_generating</c> (the one regenerate),
    /// <c>story_gate → rendering | story_generating | gate_failed | waiting_owner</c>,
    /// <c>rendering → video_gate</c>, <c>video_gate → publication_created | gate_failed | waiting_owner</c>,
    /// <c>publication_created → veto_window</c>, <c>veto_window → scheduled | cancelled_by_owner</c> and
    /// <c>scheduled → published | published_private_locked</c>.
    /// </remarks>
    /// <param name="from">The state the run holds.</param>
    /// <param name="to">The state it would enter.</param>
    /// <returns><see langword="true"/> when the table contains the move.</returns>
    public static bool CanTransition(AutopilotRunState from, AutopilotRunState to)
    {
        if (IsTerminal(from))
        {
            // A terminal run never moves again (plan §3.5 rule 3), which is also why "any → failed" reads
            // "any non-terminal → failed": `published` cannot become `failed`.
            return false;
        }

        if (to == AutopilotRunState.Paused)
        {
            // `any non-terminal → paused`: a §3.6 trigger holds the row wherever it stands.
            return from != AutopilotRunState.Paused;
        }

        if (to == AutopilotRunState.Failed)
        {
            // `any → failed`: an unrecoverable error, a moderated story, a render failure, a missed slot, an
            // expired owner wait. `paused → failed` is included, so a held row can give up.
            return true;
        }

        if (from == AutopilotRunState.Paused)
        {
            // `paused → <the state the aggregate derives from its step>`: every non-terminal state is reachable,
            // and AutopilotRun.AdvanceTo pins the derived one, so this row cannot be used to jump the ladder.
            return !IsTerminal(to) && to != AutopilotRunState.Paused;
        }

        return (from, to) switch
        {
            (AutopilotRunState.Planned, AutopilotRunState.AwaitingAuthorization) => true,
            (AutopilotRunState.AwaitingAuthorization, AutopilotRunState.Planned) => true,
            (AutopilotRunState.Planned, AutopilotRunState.IdeaPicked) => true,
            (AutopilotRunState.IdeaPicked, AutopilotRunState.StoryGenerating) => true,
            (AutopilotRunState.IdeaPicked, AutopilotRunState.Researching) => true,
            (AutopilotRunState.IdeaPicked, AutopilotRunState.WaitingOwner) => true,
            (AutopilotRunState.Researching, AutopilotRunState.WaitingOwner) => true,
            (AutopilotRunState.WaitingOwner, AutopilotRunState.StoryGenerating) => true,
            (AutopilotRunState.WaitingOwner, AutopilotRunState.Rendering) => true,
            (AutopilotRunState.WaitingOwner, AutopilotRunState.PublicationCreated) => true,
            (AutopilotRunState.StoryGenerating, AutopilotRunState.StoryGate) => true,
            (AutopilotRunState.StoryGenerating, AutopilotRunState.StoryGenerating) => true,
            (AutopilotRunState.StoryGate, AutopilotRunState.Rendering) => true,
            (AutopilotRunState.StoryGate, AutopilotRunState.StoryGenerating) => true,
            (AutopilotRunState.StoryGate, AutopilotRunState.GateFailed) => true,
            (AutopilotRunState.StoryGate, AutopilotRunState.WaitingOwner) => true,
            (AutopilotRunState.Rendering, AutopilotRunState.VideoGate) => true,
            (AutopilotRunState.VideoGate, AutopilotRunState.PublicationCreated) => true,
            (AutopilotRunState.VideoGate, AutopilotRunState.GateFailed) => true,
            (AutopilotRunState.VideoGate, AutopilotRunState.WaitingOwner) => true,
            (AutopilotRunState.PublicationCreated, AutopilotRunState.VetoWindow) => true,
            (AutopilotRunState.VetoWindow, AutopilotRunState.Scheduled) => true,
            (AutopilotRunState.VetoWindow, AutopilotRunState.CancelledByOwner) => true,
            (AutopilotRunState.Scheduled, AutopilotRunState.Published) => true,
            (AutopilotRunState.Scheduled, AutopilotRunState.PublishedPrivateLocked) => true,
            _ => false,
        };
    }
}
