using ErrorOr;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Events;

namespace NightTales.Domain.Series.Autopilot;

/// <summary>
/// One autopilot run of a series (02.8.3 plan §3.3 <c>autopilot_runs</c>): the slot's row and the state machine
/// that walks it from <see cref="AutopilotRunState.Planned"/> to a terminal outcome without an owner, calling the
/// same commands a user would call.
/// </summary>
/// <remarks>
/// <para>
/// Every state change goes through a method of this aggregate — <see cref="AdvanceTo"/>, <see cref="Pause"/>,
/// <see cref="Resume"/>, <see cref="Fail"/> and <see cref="Finish"/> — and each of them asks
/// <see cref="AutopilotRunStateMachine.CanTransition"/> first, so a move the plan's transition table does not
/// contain throws instead of corrupting the row. No property has a public setter.
/// </para>
/// <para>
/// Two columns answer two different questions (plan §3.5 rule 1): <see cref="Outcome"/> is written whenever the run
/// <em>stops</em> — terminal, or blocked on the owner, paused, waiting for an authorization — and cleared when it
/// continues; <see cref="FinishedAt"/> is written only in a terminal state. Consumers that want "done" filter
/// <c>finished_at</c>, consumers that want "why did it stop" read <c>outcome</c>.
/// </para>
/// <para>
/// The aggregate raises no event of its own: the two events this feature uses already exist
/// (<see cref="AutopilotOwnerActionRequired"/> and the outbox rows of the steps' own commands), and plan §3.5
/// delivers every transition through the orchestrator's compare-and-set instead.
/// </para>
/// </remarks>
public sealed class AutopilotRun : AggregateRoot, IUserOwned, IAuditable
{
    /// <summary>Story generations one run may pay for: the first one plus the single regenerate (plan §3.5).</summary>
    public const int MaxStoryAttempts = 2;

    /// <summary>Renders one run may request (plan §3.5: a render failure ends the run, it never retries).</summary>
    public const int MaxRenderAttempts = 1;

    private AutopilotRun()
    {
    }

    /// <summary>The code of the one validation error this aggregate can answer for <c>gate_results</c>.</summary>
    public const string InvalidGateResultsCode = "autopilot_run_invalid_gate_results";

    /// <summary>
    /// The reason code a missed slot passes to <see cref="Fail"/> (plan §3.4, §3.6 row 13): the run ends
    /// <see cref="AutopilotRunState.Failed"/> and records <see cref="AutopilotPauseReason.SlotMissed"/> on its own
    /// row, which is what the pause streak reads.
    /// </summary>
    public const string SlotMissedReason = "slot_missed";

    public Guid UserId { get; private set; }

    public Guid SeriesId { get; private set; }

    /// <summary>The intended publish instant of the slot (plan §3.2); <c>UNIQUE (series_id, slot_at)</c>.</summary>
    public DateTimeOffset SlotAt { get; private set; }

    /// <summary>
    /// The computed earliest safe start of plan §3.4 (<c>min(story_start_at, slot_at − buffer_days)</c>): what the
    /// tick compares <c>now</c> against before it moves <see cref="AutopilotRunState.Planned"/> on.
    /// </summary>
    public DateTimeOffset StartAt { get; private set; }

    /// <summary>
    /// The instant the row was planned. Distinct from <see cref="StartAt"/> on purpose (decision 4): a run created
    /// for a slot whose buffer already elapsed starts in the same tick.
    /// </summary>
    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>The live position of the run; only this aggregate's methods write it.</summary>
    public AutopilotRunState State { get; private set; }

    /// <summary>
    /// The last completed idempotency step, one of <see cref="AutopilotRunStep"/>'s constants, or
    /// <see langword="null"/> before the first one. The orchestrator's local guard: a step runs only while this is
    /// strictly behind it (<see cref="AutopilotRunStep.IsBehind"/>).
    /// </summary>
    public string? Step { get; private set; }

    /// <summary>
    /// Why the run stopped (plan §3.5 rule 1): non-null exactly while the run stands in a stopping state, cleared
    /// when it continues.
    /// </summary>
    public AutopilotRunOutcome? Outcome { get; private set; }

    /// <summary>
    /// The plan §3.6 trigger that held the run, while it stands in <see cref="AutopilotRunState.Paused"/>.
    /// </summary>
    public AutopilotPauseReason? PauseReason { get; private set; }

    /// <summary>Placeholder FK to <c>episode_ideas</c>, whose constraint 02.8.1 adds.</summary>
    public Guid? IdeaId { get; private set; }

    public Guid? EpisodeId { get; private set; }

    /// <summary>The publication this run created, once it reaches <see cref="AutopilotRunState.PublicationCreated"/>.</summary>
    public Guid? PublicationId { get; private set; }

    /// <summary>
    /// The authorization the run relied on, snapshotted from the series when the row was planned (plan §3.7): a
    /// later re-accept does not rewrite the row's history.
    /// </summary>
    public Guid? AuthorizationId { get; private set; }

    /// <summary>Story generations consumed so far, 0…<see cref="MaxStoryAttempts"/>.</summary>
    public int Attempt { get; private set; }

    /// <summary>Renders consumed so far, 0…<see cref="MaxRenderAttempts"/>; also the evidence that a render was dispatched.</summary>
    public int RenderAttempts { get; private set; }

    /// <summary>
    /// Why the owner is needed, while the run stands in <see cref="AutopilotRunState.WaitingOwner"/>: set by
    /// <see cref="SetWaitingFor"/> before the transition and cleared automatically when the run leaves that state
    /// (a pause keeps it, because the ask is still pending).
    /// </summary>
    public WaitingFor? WaitingFor { get; private set; }

    /// <summary>
    /// When the state last changed (plan §3.3): the observability column and the stale-run sweeper's input, written
    /// in the same call as the state itself.
    /// </summary>
    public DateTimeOffset LastTransitionAt { get; private set; }

    /// <summary>Non-null only in a terminal state (plan §3.5 rule 1).</summary>
    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>The <c>gate_results</c> JSON document (<c>gates.v1</c>, 02.8.2 owns its shape).</summary>
    public string? GateResults { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Plans the run of one slot: a new aggregate in <see cref="AutopilotRunState.Planned"/> that starts no earlier
    /// than <paramref name="startAt"/> and stops at <paramref name="slotAt"/> (plan §3.2–§3.4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The id is a UUIDv7 (<c>Guid.CreateVersion7()</c>), like every aggregate factory of the repo, and
    /// <see cref="StartedAt"/>, <see cref="LastTransitionAt"/>, <see cref="CreatedAt"/> and
    /// <see cref="UpdatedAt"/> all begin at <paramref name="now"/>: planning <em>is</em> the first transition.
    /// </para>
    /// <para>
    /// Nothing is validated against other rows here — the planner owns the unique slot insert, the daily cap and the
    /// authorization lookup, because they read other tables (plan §3.4, §7.1). This factory only refuses a row that
    /// would be unaddressable (an empty owner/series or a default instant).
    /// </para>
    /// </remarks>
    /// <param name="userId">The owner; the tenant filter of 09 README D10 compares it on every read.</param>
    /// <param name="seriesId">The series whose cadence produced the slot.</param>
    /// <param name="slotAt">The intended publish instant of the slot (plan §3.2), UTC.</param>
    /// <param name="startAt">The computed earliest safe start (plan §3.4); it may already be in the past.</param>
    /// <param name="authorizationId">The series' current authorization at planning time, or <see langword="null"/> when it has none.</param>
    /// <param name="now">The planner's clock reading; the domain never reads the clock itself.</param>
    /// <returns>The new aggregate, tracked by the caller's context.</returns>
    /// <exception cref="ArgumentException">The owner or series is <see cref="Guid.Empty"/>, or an instant is default.</exception>
    public static AutopilotRun Plan(
        Guid userId,
        Guid seriesId,
        DateTimeOffset slotAt,
        DateTimeOffset startAt,
        Guid? authorizationId,
        DateTimeOffset now)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("An autopilot run needs its owner.", nameof(userId));
        }

        if (seriesId == Guid.Empty)
        {
            throw new ArgumentException("An autopilot run needs its series.", nameof(seriesId));
        }

        if (slotAt == default)
        {
            throw new ArgumentException("An autopilot run needs its slot instant (plan §3.2).", nameof(slotAt));
        }

        if (startAt == default)
        {
            throw new ArgumentException("An autopilot run needs a start instant (plan §3.4).", nameof(startAt));
        }

        return new AutopilotRun
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            SeriesId = seriesId,
            SlotAt = slotAt,
            StartAt = startAt,
            State = AutopilotRunState.Planned,
            AuthorizationId = authorizationId,
            StartedAt = now,
            LastTransitionAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Moves the run to <paramref name="to"/>, which must be a row of the plan §3.5 transition table, and writes the
    /// three columns every transition owns: <see cref="Step"/> (when one completed), <see cref="Outcome"/> (only
    /// while the target is a stopping state, cleared otherwise), <see cref="FinishedAt"/> (only for a terminal
    /// target) and <see cref="LastTransitionAt"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The guards of the table are the caller's, because they read other rows (the series, the connection, the
    /// budget, the slot's timing); what this method owns is the machine rule, the two <c>waiting_for</c> edges, and
    /// the resume edge. A paused run only resumes to the state its step implies
    /// (<see cref="Resume"/>), so a direct <c>paused → x</c> call with any other non-terminal <c>x</c> throws;
    /// a terminal target is the <c>any non-terminal → failed</c> row and is allowed, because a held run can still
    /// give up. The <paramref name="step"/> argument is the last completed step, so <c>render</c> is refused while
    /// entering <see cref="AutopilotRunState.Rendering"/>: the render step belongs to the success edge, and writing it
    /// on dispatch makes a pause during the render resume into <see cref="AutopilotRunState.VideoGate"/> with nothing
    /// rendered.
    /// </para>
    /// <para>
    /// Entering <see cref="AutopilotRunState.WaitingOwner"/> requires <see cref="SetWaitingFor"/> to have run first,
    /// because a run waiting for its owner must name why (plan §3.3). Every target other than
    /// <see cref="AutopilotRunState.WaitingOwner"/> and <see cref="AutopilotRunState.Paused"/> clears the column,
    /// wherever the run came from, so a hold that ends — by a failure, by a publication, by a generation — never
    /// leaves a stale ask behind.
    /// <paramref name="authorizationId"/> snapshots a re-accepted authorization on the row that relied on it.
    /// </para>
    /// </remarks>
    /// <param name="to">The state the transition table's row leads to.</param>
    /// <param name="step">The step that just completed, one of <see cref="AutopilotRunStep"/>'s constants; <see langword="null"/> keeps the current one.</param>
    /// <param name="now">The caller's clock reading; the domain never reads the clock itself.</param>
    /// <param name="authorizationId">The authorization this run relied on, when the transition re-checks it.</param>
    /// <exception cref="InvalidOperationException">
    /// The table has no such move; the target is <see cref="AutopilotRunState.WaitingOwner"/> and no
    /// <c>waiting_for</c> is set; or the run is paused and <paramref name="to"/> is not the state its step implies.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="step"/> is not a step of the ladder.</exception>
    public void AdvanceTo(AutopilotRunState to, string? step, DateTimeOffset now, Guid? authorizationId = null)
    {
        var from = State;

        if (!AutopilotRunStateMachine.CanTransition(from, to))
        {
            throw new InvalidOperationException(
                $"An autopilot run cannot move from {from} to {to}; the plan §3.5 transition table has no such row.");
        }

        if (from == AutopilotRunState.Paused && !AutopilotRunStateMachine.IsTerminal(to))
        {
            var previous = StateBeforePause();

            if (to != previous)
            {
                throw new InvalidOperationException(
                    $"A paused autopilot run only resumes to the state its step implies ({previous}), not to "
                    + $"{to} (plan §3.5 row 'paused → previous state').");
            }
        }

        if (to == AutopilotRunState.WaitingOwner && WaitingFor is null)
        {
            throw new InvalidOperationException(
                "Entering waiting_owner needs SetWaitingFor first, so the run names why the owner is needed "
                + "(plan §3.3 column waiting_for).");
        }

        if (to == AutopilotRunState.Rendering && step == AutopilotRunStep.Render)
        {
            // `step` is the last *completed* step (plan §3.3), so dispatch is not completion: a run entering
            // `rendering` still carries the gate's step, and the render step is written by the success edge
            // (`rendering → video_gate`). Writing it here would make a pause during the render resume into
            // `video_gate` with no finished video, because the resume derivation only has the column to read.
            throw new InvalidOperationException(
                "A run entering rendering keeps its last completed step: the render step is recorded when the "
                + "render succeeds, not when it is dispatched (02.8.3 plan §3.3, §3.5).");
        }

        if (step is not null)
        {
            if (!AutopilotRunStep.IsKnown(step))
            {
                throw new ArgumentException(
                    $"'{step}' is not a step of the autopilot ladder (02.8.3 plan §3.5); use AutopilotRunStep.",
                    nameof(step));
            }

            Step = step;
        }

        if (authorizationId is not null)
        {
            AuthorizationId = authorizationId;
        }

        State = to;

        // The ask is cleared by where the run goes, not by where it came from: `paused` keeps it (the owner is
        // still needed, and Resume derives the state from it), and so does a resume back into `waiting_owner`.
        // A hold that ends any other way — a failure, a publication, a story generation — ends the ask.
        if (WaitingFor is not null && to != AutopilotRunState.WaitingOwner && to != AutopilotRunState.Paused)
        {
            WaitingFor = null;
        }

        if (from == AutopilotRunState.Paused && to != AutopilotRunState.Paused)
        {
            PauseReason = null;
        }

        Outcome = AutopilotRunStateMachine.OutcomeFor(to);

        if (AutopilotRunStateMachine.IsTerminal(to))
        {
            FinishedAt = now;
        }

        LastTransitionAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Names why the owner is needed (plan §3.3 <c>waiting_for</c>): call it before the transition into
    /// <see cref="AutopilotRunState.WaitingOwner"/>. Leaving that state clears the column — a pause keeps it, because
    /// the owner's ask is still pending — so <see langword="null"/> only makes sense while the run does not wait.
    /// </summary>
    /// <remarks>
    /// Re-labelling the wait while it stands (plan §3.5 row <c>waiting_owner → waiting_owner</c>, <c>sources</c> →
    /// <c>fact_sheet</c>, S71-16) is this call alone: it is not a transition, so <see cref="LastTransitionAt"/> keeps
    /// the instant the run entered <see cref="AutopilotRunState.WaitingOwner"/> and the 48 h expiry counts from there.
    /// </remarks>
    /// <param name="waitingFor">The missing input, one of <see cref="WaitingFor"/>'s members.</param>
    public void SetWaitingFor(WaitingFor? waitingFor) => WaitingFor = waitingFor;

    /// <summary>
    /// Attaches the episode the slot's idea produced (row <c>planned → idea_picked</c> creates it from
    /// <c>IdeaPick.Picked</c>).
    /// </summary>
    /// <param name="episodeId">The episode of this run.</param>
    /// <exception cref="ArgumentException"><paramref name="episodeId"/> is <see cref="Guid.Empty"/>.</exception>
    /// <exception cref="InvalidOperationException">The run already has a different episode.</exception>
    public void AttachEpisode(Guid episodeId)
    {
        if (episodeId == Guid.Empty)
        {
            throw new ArgumentException("An autopilot run's episode must be a real id.", nameof(episodeId));
        }

        if (EpisodeId is { } existing && existing != episodeId)
        {
            throw new InvalidOperationException(
                $"Autopilot run {Id} already has episode {existing}; a run produces one episode (plan §3.5).");
        }

        EpisodeId = episodeId;
    }

    /// <summary>Attaches the idea the run picked (the <c>idea</c> step's outcome).</summary>
    /// <param name="ideaId">The picked idea.</param>
    /// <exception cref="ArgumentException"><paramref name="ideaId"/> is <see cref="Guid.Empty"/>.</exception>
    /// <exception cref="InvalidOperationException">The run already has a different idea.</exception>
    public void AttachIdea(Guid ideaId)
    {
        if (ideaId == Guid.Empty)
        {
            throw new ArgumentException("An autopilot run's idea must be a real id.", nameof(ideaId));
        }

        if (IdeaId is { } existing && existing != ideaId)
        {
            throw new InvalidOperationException(
                $"Autopilot run {Id} already has idea {existing}; a run picks one idea (plan §3.5).");
        }

        IdeaId = ideaId;
    }

    /// <summary>
    /// Attaches the publication the run created (rows <c>video_gate → publication_created</c> and
    /// <c>waiting_owner → publication_created</c>).
    /// </summary>
    /// <param name="publicationId">The publication of this run.</param>
    /// <exception cref="ArgumentException"><paramref name="publicationId"/> is <see cref="Guid.Empty"/>.</exception>
    /// <exception cref="InvalidOperationException">The run already has a different publication.</exception>
    public void AttachPublication(Guid publicationId)
    {
        if (publicationId == Guid.Empty)
        {
            throw new ArgumentException("An autopilot run's publication must be a real id.", nameof(publicationId));
        }

        if (PublicationId is { } existing && existing != publicationId)
        {
            throw new InvalidOperationException(
                $"Autopilot run {Id} already has publication {existing}; a run creates one publication (plan §3.5).");
        }

        PublicationId = publicationId;
    }

    /// <summary>
    /// Spends one of the run's <see cref="MaxStoryAttempts"/> story generations: 0 → 1 for the first generation,
    /// 1 → 2 for the one regenerate of plan §3.5's row <c>story_gate → story_generating</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Both attempts are already spent (a third generation is impossible).</exception>
    public void ConsumeStoryAttempt()
    {
        if (Attempt >= MaxStoryAttempts)
        {
            throw new InvalidOperationException(
                $"Autopilot run {Id} already spent its {MaxStoryAttempts} story generations; the plan §3.5 machine "
                + "regenerates once and then ends gate_failed.");
        }

        Attempt++;
    }

    /// <summary>
    /// Spends the run's single render (plan §3.5: <c>render_attempts ≤ 1</c>).
    /// </summary>
    /// <remarks>
    /// Call it when the render is dispatched: <see cref="RenderAttempts"/> being 1 is the only persisted evidence
    /// that a render is in flight, which is what lets <see cref="Resume"/> restore
    /// <see cref="AutopilotRunState.Rendering"/> instead of replaying the story gate.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The render was already consumed.</exception>
    public void ConsumeRenderAttempt()
    {
        if (RenderAttempts >= MaxRenderAttempts)
        {
            throw new InvalidOperationException(
                $"Autopilot run {Id} already used its render; a render failure ends the run instead of retrying "
                + "(plan §3.5).");
        }

        RenderAttempts++;
    }

    /// <summary>
    /// Holds the run for the owner (row <c>any non-terminal → paused</c> of plan §3.5): the state becomes
    /// <see cref="AutopilotRunState.Paused"/>, the outcome <c>paused</c>, and <paramref name="reason"/> is recorded
    /// for the alert and the run log.
    /// </summary>
    /// <remarks>
    /// A second trigger on an already paused run replaces the reason and touches nothing else, so two triggers that
    /// fire in the same tick cannot fail the row. The move is refused on a terminal run, because only the owner
    /// resumes a pause and a finished run has nothing left to resume.
    /// </remarks>
    /// <param name="reason">The plan §3.6 trigger.</param>
    /// <param name="now">The caller's clock reading.</param>
    /// <exception cref="InvalidOperationException">The run is terminal.</exception>
    public void Pause(AutopilotPauseReason reason, DateTimeOffset now)
    {
        if (State == AutopilotRunState.Paused)
        {
            PauseReason = reason;
            LastTransitionAt = now;
            UpdatedAt = now;
            return;
        }

        AdvanceTo(AutopilotRunState.Paused, step: null, now);
        PauseReason = reason;
    }

    /// <summary>
    /// Resumes a paused run (row <c>paused → previous state</c>): the state its step and attached ids imply, with
    /// the outcome cleared and the reason forgotten.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Plan §3.3 has no column for "the state before the pause" (decision 13), so the state is derived from the
    /// evidence the row already carries. The rules, first match wins:
    /// </para>
    /// <list type="number">
    /// <item><description>step <c>go_live</c> or <c>consent</c> → <see cref="AutopilotRunState.Scheduled"/>, because consent was written;</description></item>
    /// <item><description>a publication attached → <see cref="AutopilotRunState.PublicationCreated"/> (also for step <c>publication</c>);</description></item>
    /// <item><description>
    /// a <c>waiting_for</c> set → <see cref="AutopilotRunState.WaitingOwner"/>, the only state that carries one
    /// (a pause keeps it, so a run paused while it waited for the owner resumes still waiting);
    /// </description></item>
    /// <item><description>step <c>video_gate</c> → <see cref="AutopilotRunState.VideoGate"/> (its pass creates the publication);</description></item>
    /// <item><description>step <c>render</c> → <see cref="AutopilotRunState.VideoGate"/>, because the render came back;</description></item>
    /// <item><description>
    /// step <c>story</c>, <c>story2</c> or <c>story_gate</c> → <see cref="AutopilotRunState.Rendering"/> when
    /// <see cref="RenderAttempts"/> is 1, else <see cref="AutopilotRunState.StoryGate"/>. The gate is preferred over
    /// the generation because the gate is free and idempotent while a generation is paid and guarded by its key. The
    /// one exception is step <c>story_gate</c> with both story attempts spent: that is the gate's one regeneration
    /// still pending, so the run resumes into <see cref="AutopilotRunState.StoryGenerating"/> and the generation step
    /// dispatches <c>…:story2</c> (or adopts it when it was already dispatched);
    /// </description></item>
    /// <item><description>step <c>find_sources</c> or <c>fact_sheet</c> → <see cref="AutopilotRunState.Researching"/>;</description></item>
    /// <item><description>
    /// step <c>sources_required</c> → <see cref="AutopilotRunState.StoryGenerating"/>, because the row carries it
    /// only after the owner answered the F33 ask (a run still waiting for those sources is caught by the
    /// <c>waiting_for</c> rule above);
    /// </description></item>
    /// <item><description>step <c>idea</c> → <see cref="AutopilotRunState.StoryGenerating"/> when an episode is attached, else <see cref="AutopilotRunState.IdeaPicked"/>;</description></item>
    /// <item><description>
    /// no step, or step <c>authorization</c> → <see cref="AutopilotRunState.Planned"/>. A run paused while
    /// <see cref="AutopilotRunState.AwaitingAuthorization"/> lands here too: the next tick re-checks the
    /// authorization and returns it to that state when it is still missing, which is why no column is needed for it.
    /// </description></item>
    /// </list>
    /// <para>
    /// Anything else is a step outside <see cref="AutopilotRunStep"/> and throws instead of guessing (decision 13).
    /// </para>
    /// </remarks>
    /// <param name="now">The caller's clock reading.</param>
    /// <exception cref="InvalidOperationException">The run is not paused, or its step is not a step of the ladder.</exception>
    public void Resume(DateTimeOffset now)
    {
        if (State != AutopilotRunState.Paused)
        {
            throw new InvalidOperationException(
                $"Autopilot run {Id} is {State}, so there is no pause to resume (plan §3.5).");
        }

        AdvanceTo(StateBeforePause(), step: null, now);
    }

    /// <summary>
    /// Ends the run as <see cref="AutopilotRunState.Failed"/> (row <c>any → failed</c> of plan §3.5): a moderated
    /// story, a render failure, an expired owner wait, a missed slot or an unrecoverable error.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="reason"/> is <b>not</b> stored: plan §3.3 has no column for it (decision 4), so it travels to
    /// the structured log and the metric the caller writes. The one exception is
    /// <see cref="SlotMissedReason"/>, which also records <see cref="AutopilotPauseReason.SlotMissed"/> on the row
    /// because plan §3.4/§3.6 row 13 counts those misses.
    /// </para>
    /// <para>
    /// Failing an already failed run is a no-op, so a redelivered failure handler answers the same way instead of
    /// throwing; failing a run that ended another way is refused by the machine.
    /// </para>
    /// </remarks>
    /// <param name="reason">The error code for the log, e.g. <c>story_moderated</c>, <c>render_failed</c> or <see cref="SlotMissedReason"/>.</param>
    /// <param name="now">The caller's clock reading.</param>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is blank.</exception>
    /// <exception cref="InvalidOperationException">The run already ended in another terminal state.</exception>
    public void Fail(string reason, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (State == AutopilotRunState.Failed)
        {
            return;
        }

        AdvanceTo(AutopilotRunState.Failed, step: null, now);

        if (string.Equals(reason, SlotMissedReason, StringComparison.Ordinal))
        {
            PauseReason = AutopilotPauseReason.SlotMissed;
        }
    }

    /// <summary>
    /// Ends the run with a terminal outcome, which is the machine's terminal row for it: <c>scheduled → published</c>,
    /// <c>scheduled → published_private_locked</c>, <c>story_gate|video_gate → gate_failed</c> or
    /// <c>veto_window → cancelled_by_owner</c> (and <c>any → failed</c>, which <see cref="Fail"/> covers with its
    /// reason).
    /// </summary>
    /// <param name="outcome">One of the five terminal outcomes.</param>
    /// <param name="now">The caller's clock reading.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="outcome"/> is not terminal: <see cref="AutopilotRunOutcome.WaitingOwner"/>,
    /// <see cref="AutopilotRunOutcome.Paused"/> and <see cref="AutopilotRunOutcome.AwaitingAuthorization"/> are stops
    /// the run leaves again, so they never set <c>finished_at</c>.
    /// </exception>
    /// <exception cref="InvalidOperationException">The machine has no move from the current state to that outcome's state.</exception>
    public void Finish(AutopilotRunOutcome outcome, DateTimeOffset now)
    {
        var target = AutopilotRunStateMachine.StateFor(outcome);

        if (!AutopilotRunStateMachine.IsTerminal(target))
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "Only a terminal outcome finishes an autopilot run; waiting_owner, paused and "
                + "awaiting_authorization are stops the run leaves again (plan §3.5 rule 1).");
        }

        AdvanceTo(target, step: null, now);
    }

    /// <summary>
    /// Stores the merged <c>gates.v1</c> document of one gate verdict (02.8.2-T1, plan §3.4). The caller
    /// (<c>GateResultWriter</c>) reads the stored document, merges the half it owns and hands the whole document
    /// back: the aggregate keeps the column, the Application keeps the document's shape — the same split
    /// <c>RenderJob.RecordQualityReport</c> makes, because the Domain may not reference the Application (D1).
    /// </summary>
    /// <remarks>
    /// The merge happens before this call, on purpose: the story and the video verdict arrive at different moments
    /// of the same run and neither may erase the other, so the read-modify-write belongs beside the document's
    /// shape rather than on this aggregate. Nothing here validates the shape beyond "there is a document".
    /// </remarks>
    /// <param name="gateResultsJson">The merged <c>gates.v1</c> document, already serialised.</param>
    /// <returns>Success, or the validation error of a blank document.</returns>
    public ErrorOr<Success> RecordGateResults(string gateResultsJson)
    {
        if (string.IsNullOrWhiteSpace(gateResultsJson))
        {
            return Error.Validation(
                InvalidGateResultsCode,
                "A gate verdict carries its gates.v1 document (02.8.2 plan §3.4).");
        }

        GateResults = gateResultsJson;

        return Result.Success;
    }

    /// <summary>
    /// Raises the owner-action notice of one gate stop (02.8.2-T1, plan §4 UC6). The outbox interceptor turns it
    /// into the <c>autopilot_owner_action_required.v1</c> row of the caller's own <c>SaveChangesAsync</c>, so the
    /// notice and the gate verdict it belongs to commit or roll back together (00 §5.2).
    /// </summary>
    /// <remarks>
    /// Nothing of the episode changes here: a stop leaves it in <c>story_review</c>/<c>video_review</c> and the
    /// owner decides. The payload carries the reason code and the failing check ids only — never a title, a story
    /// text, a brief or a source URL (D19) — and the handler resolves what else the e-mail shows.
    /// </remarks>
    /// <param name="reason">The stop's code, one of <c>OwnerActionReasons</c>.</param>
    /// <param name="failures">The failing gate check ids; empty for a stop that failed no check.</param>
    /// <param name="copyKey">The copy key the run log shows for the reason (plan §6).</param>
    /// <param name="now">The caller's clock reading; the aggregate never reads the clock itself.</param>
    /// <exception cref="InvalidOperationException">The run has no episode, so no notice can name one.</exception>
    public void RaiseOwnerActionRequired(
        string reason,
        IReadOnlyList<string> failures,
        string copyKey,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(failures);
        ArgumentException.ThrowIfNullOrWhiteSpace(copyKey);

        var episodeId = EpisodeId ?? throw new InvalidOperationException(
            $"Autopilot run {Id} has no episode, so no owner-action notice can name one.");

        Raise(new AutopilotOwnerActionRequired(
            UserId,
            SeriesId,
            Id,
            episodeId,
            reason,
            failures.ToArray(),
            copyKey,
            now));
    }

    /// <summary>
    /// The state a paused run returns to, derived from <see cref="Step"/> and the attached ids (plan §3.5 row
    /// <c>paused → previous state</c>; the numbered rules are listed on <see cref="Resume"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="Step"/> is not a step of the ladder.</exception>
    private AutopilotRunState StateBeforePause()
    {
        if (Step == AutopilotRunStep.GoLive || Step == AutopilotRunStep.Consent)
        {
            return AutopilotRunState.Scheduled;
        }

        if (PublicationId is not null)
        {
            return AutopilotRunState.PublicationCreated;
        }

        if (WaitingFor is not null)
        {
            return AutopilotRunState.WaitingOwner;
        }

        return Step switch
        {
            AutopilotRunStep.VideoGate => AutopilotRunState.VideoGate,
            AutopilotRunStep.Render => AutopilotRunState.VideoGate,

            // The gate failed attempt 1 and spent attempt 2 without dispatching `…:story2` yet: resuming into the gate
            // would re-evaluate the story that just failed and waste the regeneration, so the run goes back to the
            // state that dispatches it (S71 CR-10). A run whose attempt 2 already landed has `:story2` under the same
            // step, and the generation step adopts that row instead of paying again.
            AutopilotRunStep.StoryGate when RenderAttempts < MaxRenderAttempts
                && Attempt >= MaxStoryAttempts => AutopilotRunState.StoryGenerating,
            AutopilotRunStep.Story or AutopilotRunStep.Story2 or AutopilotRunStep.StoryGate =>
                RenderAttempts >= MaxRenderAttempts ? AutopilotRunState.Rendering : AutopilotRunState.StoryGate,
            AutopilotRunStep.FindSources or AutopilotRunStep.FactSheet => AutopilotRunState.Researching,
            // `sources_required` without a `waiting_for` is the F33 answer: the owner pasted the links, so the run
            // continues into the story generation rather than back into the wait the guard would refuse.
            AutopilotRunStep.SourcesRequired => AutopilotRunState.StoryGenerating,
            AutopilotRunStep.Idea => EpisodeId is not null
                ? AutopilotRunState.StoryGenerating
                : AutopilotRunState.IdeaPicked,
            null or AutopilotRunStep.Authorization => AutopilotRunState.Planned,
            _ => throw new InvalidOperationException(
                $"Autopilot run {Id} cannot resume: '{Step}' is not a step of the run ladder (02.8.3 plan §3.5)."),
        };
    }
}
