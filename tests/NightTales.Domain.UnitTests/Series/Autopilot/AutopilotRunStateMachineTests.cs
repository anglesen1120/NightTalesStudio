using NightTales.Domain.Series.Autopilot;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Series.Autopilot;

/// <summary>
/// The run state machine of 02.8.3 plan §3.5: every row of the transition table, the terminal set, the producing
/// set and the two rules that hold for every row (<c>outcome</c> on a stop, <c>finished_at</c> only when terminal).
/// </summary>
/// <remarks>
/// Pure unit tests: no clock, no database, no context. The arrangement of a run inside a state lives in
/// <see cref="AutopilotRunScenario"/>, which this file also hosts because both state-machine tests and the
/// aggregate tests walk the same machine.
/// </remarks>
public sealed class AutopilotRunStateMachineTests
{
    /// <summary>The five terminal states of plan §3.5 rule 3, in declaration order.</summary>
    private static readonly AutopilotRunState[] TerminalStates =
    [
        AutopilotRunState.Published,
        AutopilotRunState.PublishedPrivateLocked,
        AutopilotRunState.GateFailed,
        AutopilotRunState.CancelledByOwner,
        AutopilotRunState.Failed,
    ];

    /// <summary>The blocked states that write an outcome without finishing the run (plan §3.5 rule 1).</summary>
    private static readonly AutopilotRunState[] BlockedStates =
    [
        AutopilotRunState.WaitingOwner,
        AutopilotRunState.Paused,
        AutopilotRunState.AwaitingAuthorization,
    ];

    /// <summary>The producing set of plan §3.5 (the "one producing run per series" guard).</summary>
    private static readonly AutopilotRunState[] ProducingStates =
    [
        AutopilotRunState.Planned,
        AutopilotRunState.IdeaPicked,
        AutopilotRunState.Researching,
        AutopilotRunState.StoryGenerating,
        AutopilotRunState.StoryGate,
        AutopilotRunState.WaitingOwner,
        AutopilotRunState.Rendering,
        AutopilotRunState.VideoGate,
        AutopilotRunState.PublicationCreated,
    ];

    /// <summary>
    /// The plan §3.5 transition table as <c>(from, to, expected)</c>: the explicit rows, the three "any" rules it
    /// states in prose (<c>any non-terminal → paused</c>, <c>any → failed</c>, <c>paused → previous state</c>), and
    /// a negative sample for every family of row so a widened <c>CanTransition</c> cannot pass unnoticed.
    /// </summary>
    public static TheoryData<AutopilotRunState, AutopilotRunState, bool> TransitionTable
    {
        get
        {
            var table = new TheoryData<AutopilotRunState, AutopilotRunState, bool>
            {
                // The explicit rows, quoted from the table in plan order.
                { AutopilotRunState.Planned, AutopilotRunState.AwaitingAuthorization, true },
                { AutopilotRunState.AwaitingAuthorization, AutopilotRunState.Planned, true },
                { AutopilotRunState.Planned, AutopilotRunState.IdeaPicked, true },
                { AutopilotRunState.IdeaPicked, AutopilotRunState.StoryGenerating, true },
                { AutopilotRunState.IdeaPicked, AutopilotRunState.Researching, true },
                { AutopilotRunState.IdeaPicked, AutopilotRunState.WaitingOwner, true },
                { AutopilotRunState.Researching, AutopilotRunState.WaitingOwner, true },
                { AutopilotRunState.WaitingOwner, AutopilotRunState.StoryGenerating, true },
                { AutopilotRunState.StoryGenerating, AutopilotRunState.StoryGate, true },
                { AutopilotRunState.StoryGenerating, AutopilotRunState.StoryGenerating, true },
                { AutopilotRunState.StoryGate, AutopilotRunState.Rendering, true },
                { AutopilotRunState.StoryGate, AutopilotRunState.StoryGenerating, true },
                { AutopilotRunState.StoryGate, AutopilotRunState.GateFailed, true },
                { AutopilotRunState.StoryGate, AutopilotRunState.WaitingOwner, true },
                { AutopilotRunState.WaitingOwner, AutopilotRunState.Rendering, true },
                { AutopilotRunState.Rendering, AutopilotRunState.VideoGate, true },
                { AutopilotRunState.VideoGate, AutopilotRunState.PublicationCreated, true },
                { AutopilotRunState.VideoGate, AutopilotRunState.GateFailed, true },
                { AutopilotRunState.VideoGate, AutopilotRunState.WaitingOwner, true },
                { AutopilotRunState.WaitingOwner, AutopilotRunState.PublicationCreated, true },
                { AutopilotRunState.PublicationCreated, AutopilotRunState.VetoWindow, true },
                { AutopilotRunState.VetoWindow, AutopilotRunState.Scheduled, true },
                { AutopilotRunState.VetoWindow, AutopilotRunState.CancelledByOwner, true },
                { AutopilotRunState.Scheduled, AutopilotRunState.Published, true },
                { AutopilotRunState.Scheduled, AutopilotRunState.PublishedPrivateLocked, true },
            };

            foreach (var state in Enum.GetValues<AutopilotRunState>())
            {
                if (AutopilotRunStateMachine.IsTerminal(state))
                {
                    // "any" never includes a terminal state (plan §3.5 rule 3).
                    continue;
                }

                if (state != AutopilotRunState.Paused)
                {
                    table.Add(state, AutopilotRunState.Paused, true);
                }

                table.Add(state, AutopilotRunState.Failed, true);

                if (state == AutopilotRunState.Paused)
                {
                    // `paused → the state its step implies`: every non-terminal state is a candidate, and
                    // AutopilotRun.AdvanceTo pins the derived one.
                    foreach (var target in Enum.GetValues<AutopilotRunState>())
                    {
                        if (!AutopilotRunStateMachine.IsTerminal(target) && target != AutopilotRunState.Paused)
                        {
                            table.Add(state, target, true);
                        }
                    }
                }
            }

            // The negative sample: one impossible move per family.
            table.Add(AutopilotRunState.Published, AutopilotRunState.Rendering, false);
            table.Add(AutopilotRunState.Published, AutopilotRunState.Failed, false);
            table.Add(AutopilotRunState.Published, AutopilotRunState.Paused, false);
            table.Add(AutopilotRunState.Failed, AutopilotRunState.Planned, false);
            table.Add(AutopilotRunState.GateFailed, AutopilotRunState.Failed, false);
            table.Add(AutopilotRunState.CancelledByOwner, AutopilotRunState.Paused, false);
            table.Add(AutopilotRunState.PublishedPrivateLocked, AutopilotRunState.Published, false);
            table.Add(AutopilotRunState.Planned, AutopilotRunState.Scheduled, false);
            table.Add(AutopilotRunState.Planned, AutopilotRunState.Published, false);
            table.Add(AutopilotRunState.IdeaPicked, AutopilotRunState.PublicationCreated, false);
            table.Add(AutopilotRunState.Researching, AutopilotRunState.StoryGenerating, false);
            table.Add(AutopilotRunState.StoryGenerating, AutopilotRunState.Rendering, false);
            table.Add(AutopilotRunState.StoryGate, AutopilotRunState.VideoGate, false);
            table.Add(AutopilotRunState.WaitingOwner, AutopilotRunState.IdeaPicked, false);
            table.Add(AutopilotRunState.Rendering, AutopilotRunState.Rendering, false);
            table.Add(AutopilotRunState.VideoGate, AutopilotRunState.Rendering, false);
            table.Add(AutopilotRunState.PublicationCreated, AutopilotRunState.PublicationCreated, false);
            table.Add(AutopilotRunState.VetoWindow, AutopilotRunState.Published, false);
            table.Add(AutopilotRunState.Scheduled, AutopilotRunState.IdeaPicked, false);
            table.Add(AutopilotRunState.Scheduled, AutopilotRunState.GateFailed, false);
            table.Add(AutopilotRunState.Paused, AutopilotRunState.Paused, false);
            table.Add(AutopilotRunState.Paused, AutopilotRunState.Published, false);

            return table;
        }
    }

    [Theory]
    [MemberData(nameof(TransitionTable))]
    public void RunStateMachine_TransitionTable_HoldsEveryPlanRow(
        AutopilotRunState from,
        AutopilotRunState to,
        bool expected)
    {
        AutopilotRunStateMachine.CanTransition(from, to).ShouldBe(
            expected,
            $"plan §3.5 says ({from} → {to}) is {(expected ? "a row" : "not a row")} of the table");
    }

    [Fact]
    public void RunStateMachine_IllegalTransition_Throws()
    {
        var published = AutopilotRunScenario.RunAt(AutopilotRunState.Published);

        var thrown = Should.Throw<InvalidOperationException>(
            () => published.AdvanceTo(AutopilotRunState.Rendering, step: null, AutopilotRunScenario.Now));

        thrown.Message.ShouldContain("published", Case.Insensitive, "the exception names the move it refused");
        thrown.Message.ShouldContain("rendering", Case.Insensitive);
        published.State.ShouldBe(AutopilotRunState.Published, "a refused transition must leave the row untouched");

        // The rule is the machine's, not one pair's: the other terminal states refuse the same move.
        var cancelled = AutopilotRunScenario.RunAt(AutopilotRunState.CancelledByOwner);
        Should.Throw<InvalidOperationException>(
            () => cancelled.AdvanceTo(AutopilotRunState.Planned, step: null, AutopilotRunScenario.Now));
        Should.Throw<InvalidOperationException>(
            () => cancelled.Fail("render_failed", AutopilotRunScenario.Now));

        // And a jump that skips the ladder is refused from a live state too.
        var planned = AutopilotRunScenario.RunAt(AutopilotRunState.Planned);
        Should.Throw<InvalidOperationException>(
            () => planned.AdvanceTo(AutopilotRunState.Scheduled, step: null, AutopilotRunScenario.Now));
    }

    [Theory]
    [MemberData(nameof(EveryState))]
    public void RunStateMachine_OutcomeWrittenOnlyWhenStopping(AutopilotRunState state)
    {
        var run = AutopilotRunScenario.RunAt(state);
        var stopping = AutopilotRunStateMachine.WritesOutcome(state);
        var terminal = AutopilotRunStateMachine.IsTerminal(state);

        run.State.ShouldBe(state);

        // Rule 1: `outcome` is written on every stop and cleared while the run continues.
        run.Outcome.ShouldBe(AutopilotRunStateMachine.OutcomeFor(state));
        if (stopping)
        {
            run.Outcome.ShouldNotBeNull($"a run in {state} stopped, so its outcome answers why");
        }
        else
        {
            run.Outcome.ShouldBeNull($"a run in {state} continues, so its outcome is empty");
        }

        // Rule 1, second half: `finished_at` is written only in a terminal state, so a blocked run is not "done".
        (run.FinishedAt is not null).ShouldBe(terminal);
        if (terminal)
        {
            run.FinishedAt.ShouldBe(AutopilotRunScenario.Now);
        }

        // The three states the acceptance check names explicitly.
        switch (state)
        {
            case AutopilotRunState.Scheduled:
                run.Outcome.ShouldBeNull();
                run.FinishedAt.ShouldBeNull();
                break;
            case AutopilotRunState.WaitingOwner:
                run.Outcome.ShouldBe(AutopilotRunOutcome.WaitingOwner);
                run.FinishedAt.ShouldBeNull();
                break;
            case AutopilotRunState.Published:
                run.Outcome.ShouldBe(AutopilotRunOutcome.Published);
                run.FinishedAt.ShouldBe(AutopilotRunScenario.Now);
                break;
            default:
                break;
        }
    }

    [Fact]
    public void RunStateMachine_TerminalStates_AreExhaustive()
    {
        var all = Enum.GetValues<AutopilotRunState>();

        all.Length.ShouldBe(18, "plan §3.5 has eighteen states and the CHECK constraint must match");

        var terminal = all.Where(AutopilotRunStateMachine.IsTerminal).ToArray();
        terminal.ShouldBe(TerminalStates, ignoreOrder: false);

        // Every state is exactly one of terminal / non-terminal …
        foreach (var state in all)
        {
            (AutopilotRunStateMachine.IsTerminal(state) && AutopilotRunStateMachine.IsProducing(state))
                .ShouldBeFalse($"{state} cannot be terminal and producing at once");

            var outgoing = all.Count(to => AutopilotRunStateMachine.CanTransition(state, to));

            if (AutopilotRunStateMachine.IsTerminal(state))
            {
                // … a terminal state is the end of the machine: no row leaves it.
                outgoing.ShouldBe(0, $"{state} is terminal, so nothing may follow it");
                AutopilotRunStateMachine.WritesOutcome(state).ShouldBeTrue($"{state} is a stop");
                AutopilotRunStateMachine.OutcomeFor(state).ShouldNotBeNull();
                continue;
            }

            outgoing.ShouldBeGreaterThan(0, $"{state} is not terminal, so the tick must be able to move it on");
        }

        // The outcome of a terminal state is one of the five terminal outcomes, never a blocked one.
        foreach (var state in terminal)
        {
            var outcome = AutopilotRunStateMachine.OutcomeFor(state);
            outcome.ShouldNotBeNull();
            AutopilotRunStateMachine.IsTerminal(AutopilotRunStateMachine.StateFor(outcome.Value))
                .ShouldBeTrue($"{outcome} is terminally {state}");
        }

        // The blocked states stop without finishing, and every stopping state maps to itself through its outcome.
        foreach (var state in BlockedStates)
        {
            AutopilotRunStateMachine.IsTerminal(state).ShouldBeFalse($"{state} is a stop, not an end");
            AutopilotRunStateMachine.WritesOutcome(state).ShouldBeTrue();

            var outcome = AutopilotRunStateMachine.OutcomeFor(state);
            outcome.ShouldNotBeNull($"a run in {state} stopped, so its outcome answers why");
            AutopilotRunStateMachine.StateFor(outcome.Value).ShouldBe(state);
        }
    }

    [Fact]
    public void RunStateMachine_ProducingStates_ExcludePausedAndBlockedAuthorization()
    {
        var producing = Enum.GetValues<AutopilotRunState>()
            .Where(AutopilotRunStateMachine.IsProducing)
            .ToArray();

        producing.ShouldBe(ProducingStates, ignoreOrder: false);
        AutopilotRunStateMachine.IsProducing(AutopilotRunState.Paused)
            .ShouldBeFalse("a paused run is held, so it must not block the next slot");
        AutopilotRunStateMachine.IsProducing(AutopilotRunState.AwaitingAuthorization)
            .ShouldBeFalse("a run blocked on a re-accept has produced nothing");
        AutopilotRunStateMachine.IsProducing(AutopilotRunState.VetoWindow)
            .ShouldBeFalse("production is over once the veto notice is out (plan §3.5)");
        AutopilotRunStateMachine.IsProducing(AutopilotRunState.Scheduled)
            .ShouldBeFalse("the publication belongs to 06; the series may feed the next slot");
    }

    [Fact]
    public void AutopilotRunStep_IsBehind_FollowsTheLadderAndItsOneLoop()
    {
        // Nothing done yet: every step may run.
        AutopilotRunStep.IsBehind(null, AutopilotRunStep.Idea).ShouldBeTrue();
        AutopilotRunStep.IsBehind(null, AutopilotRunStep.GoLive).ShouldBeTrue();

        // A completed step is never executed twice.
        AutopilotRunStep.IsBehind(AutopilotRunStep.Idea, AutopilotRunStep.Idea).ShouldBeFalse();
        AutopilotRunStep.IsBehind(AutopilotRunStep.Consent, AutopilotRunStep.Consent).ShouldBeFalse();

        // Forward along the ladder, including the true-crime branch.
        AutopilotRunStep.IsBehind(AutopilotRunStep.Authorization, AutopilotRunStep.Idea).ShouldBeTrue();
        AutopilotRunStep.IsBehind(AutopilotRunStep.Idea, AutopilotRunStep.FindSources).ShouldBeTrue();
        AutopilotRunStep.IsBehind(AutopilotRunStep.FactSheet, AutopilotRunStep.SourcesRequired).ShouldBeTrue();
        AutopilotRunStep.IsBehind(AutopilotRunStep.SourcesRequired, AutopilotRunStep.Story).ShouldBeTrue();
        AutopilotRunStep.IsBehind(AutopilotRunStep.Story, AutopilotRunStep.StoryGate).ShouldBeTrue();
        AutopilotRunStep.IsBehind(AutopilotRunStep.StoryGate, AutopilotRunStep.Story2).ShouldBeTrue();
        AutopilotRunStep.IsBehind(AutopilotRunStep.Story2, AutopilotRunStep.Render).ShouldBeTrue();
        AutopilotRunStep.IsBehind(AutopilotRunStep.Render, AutopilotRunStep.VideoGate).ShouldBeTrue();
        AutopilotRunStep.IsBehind(AutopilotRunStep.VideoGate, AutopilotRunStep.Publication).ShouldBeTrue();
        AutopilotRunStep.IsBehind(AutopilotRunStep.Publication, AutopilotRunStep.Consent).ShouldBeTrue();
        AutopilotRunStep.IsBehind(AutopilotRunStep.Consent, AutopilotRunStep.GoLive).ShouldBeTrue();

        // The regenerate loop: the gate is ahead of story2 again, and nothing else loops back.
        AutopilotRunStep.IsBehind(AutopilotRunStep.Story2, AutopilotRunStep.StoryGate).ShouldBeTrue();
        AutopilotRunStep.IsBehind(AutopilotRunStep.StoryGate, AutopilotRunStep.Story).ShouldBeFalse();
        AutopilotRunStep.IsBehind(AutopilotRunStep.Render, AutopilotRunStep.StoryGate).ShouldBeFalse();
        AutopilotRunStep.IsBehind(AutopilotRunStep.Story, AutopilotRunStep.Story2).ShouldBeTrue();

        // A name outside the vocabulary fails loudly instead of stalling the run.
        Should.Throw<ArgumentException>(
            () => AutopilotRunStep.IsBehind(null, "story_gate:2"));
        Should.Throw<ArgumentException>(
            () => AutopilotRunStep.IsBehind("made_up_step", AutopilotRunStep.Render));
        AutopilotRunStep.IsKnown("story_gate:2").ShouldBeFalse();
        AutopilotRunStep.IsKnown(AutopilotRunStep.StoryGate).ShouldBeTrue();
    }

    /// <summary>Every declared state, one theory row each.</summary>
    public static TheoryData<AutopilotRunState> EveryState
    {
        get
        {
            var data = new TheoryData<AutopilotRunState>();
            foreach (var state in Enum.GetValues<AutopilotRunState>())
            {
                data.Add(state);
            }

            return data;
        }
    }
}

/// <summary>
/// Arranges one <see cref="AutopilotRun"/> per plan §3.5 state, walking the machine the way the orchestrator does,
/// so the tests can start from any state without re-deriving the path each time.
/// </summary>
internal static class AutopilotRunScenario
{
    /// <summary>The plan §3.4 worked example's clock reading.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The slot of the plan §3.4 worked example (Thu 2026-10-08 19:00 +07:00).</summary>
    public static readonly DateTimeOffset SlotAt = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The owner of every arranged run.</summary>
    public static readonly Guid UserId = Guid.CreateVersion7();

    /// <summary>The series of every arranged run.</summary>
    public static readonly Guid SeriesId = Guid.CreateVersion7();

    /// <summary>The authorization the arranged run relied on.</summary>
    public static readonly Guid AuthorizationId = Guid.CreateVersion7();

    /// <summary>A planned run: the shape every other arrangement starts from.</summary>
    /// <returns>A fresh aggregate, so no test shares a row with another.</returns>
    public static AutopilotRun Planned() => AutopilotRun.Plan(UserId, SeriesId, SlotAt, Now, AuthorizationId, Now);

    /// <summary>
    /// A run standing in <paramref name="state"/>, reached by the transitions the plan's table allows.
    /// </summary>
    /// <param name="state">The state the test wants to observe.</param>
    /// <returns>The arranged aggregate.</returns>
    public static AutopilotRun RunAt(AutopilotRunState state)
    {
        switch (state)
        {
            case AutopilotRunState.Planned:
                return Planned();
            case AutopilotRunState.AwaitingAuthorization:
                return AwaitingAuthorization();
            case AutopilotRunState.IdeaPicked:
                return IdeaPicked();
            case AutopilotRunState.Researching:
                {
                    var run = IdeaPicked();
                    run.AdvanceTo(AutopilotRunState.Researching, AutopilotRunStep.FindSources, Now);
                    return run;
                }

            case AutopilotRunState.StoryGenerating:
                return StoryGenerating();
            case AutopilotRunState.StoryGate:
                return StoryGate();
            case AutopilotRunState.WaitingOwner:
                {
                    var run = StoryGate();
                    run.SetWaitingFor(WaitingFor.Story);
                    run.AdvanceTo(AutopilotRunState.WaitingOwner, AutopilotRunStep.StoryGate, Now);
                    return run;
                }

            case AutopilotRunState.Rendering:
                return Rendering();
            case AutopilotRunState.VideoGate:
                {
                    var run = Rendering();
                    run.AdvanceTo(AutopilotRunState.VideoGate, AutopilotRunStep.Render, Now);
                    return run;
                }

            case AutopilotRunState.PublicationCreated:
                return PublicationCreated();
            case AutopilotRunState.VetoWindow:
                {
                    var run = PublicationCreated();
                    run.AdvanceTo(AutopilotRunState.VetoWindow, step: null, Now);
                    return run;
                }

            case AutopilotRunState.Scheduled:
                return Scheduled();
            case AutopilotRunState.Published:
                {
                    var run = Scheduled();
                    run.Finish(AutopilotRunOutcome.Published, Now);
                    return run;
                }

            case AutopilotRunState.PublishedPrivateLocked:
                {
                    var run = Scheduled();
                    run.Finish(AutopilotRunOutcome.PublishedPrivateLocked, Now);
                    return run;
                }

            case AutopilotRunState.GateFailed:
                {
                    var run = StoryGate();
                    run.Finish(AutopilotRunOutcome.GateFailed, Now);
                    return run;
                }

            case AutopilotRunState.CancelledByOwner:
                {
                    var run = PublicationCreated();
                    run.AdvanceTo(AutopilotRunState.VetoWindow, step: null, Now);
                    run.Finish(AutopilotRunOutcome.CancelledByOwner, Now);
                    return run;
                }

            case AutopilotRunState.Paused:
                {
                    var run = Planned();
                    run.Pause(AutopilotPauseReason.OwnerPaused, Now);
                    return run;
                }

            case AutopilotRunState.Failed:
                {
                    var run = StoryGenerating();
                    run.Fail("story_moderated", Now);
                    return run;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "No arrangement for this state.");
        }
    }

    /// <summary>A run whose authorization is missing (row <c>planned → awaiting_authorization</c>).</summary>
    public static AutopilotRun AwaitingAuthorization()
    {
        var run = Planned();
        run.AdvanceTo(AutopilotRunState.AwaitingAuthorization, AutopilotRunStep.Authorization, Now);
        return run;
    }

    /// <summary>A run whose idea and episode exist (row <c>planned → idea_picked</c>).</summary>
    public static AutopilotRun IdeaPicked()
    {
        var run = Planned();
        run.AdvanceTo(AutopilotRunState.IdeaPicked, AutopilotRunStep.Idea, Now);
        run.AttachIdea(Guid.CreateVersion7());
        return run;
    }

    /// <summary>A run whose first generation is in flight (step <c>idea</c>, episode attached).</summary>
    public static AutopilotRun StoryGenerating()
    {
        var run = IdeaPicked();
        run.AttachEpisode(Guid.CreateVersion7());
        run.AdvanceTo(AutopilotRunState.StoryGenerating, step: null, Now);
        run.ConsumeStoryAttempt();
        return run;
    }

    /// <summary>A run whose story waits for its gate verdict (step <c>story</c>).</summary>
    public static AutopilotRun StoryGate()
    {
        var run = StoryGenerating();
        run.AdvanceTo(AutopilotRunState.StoryGate, AutopilotRunStep.Story, Now);
        return run;
    }

    /// <summary>A run whose single render is in flight (step <c>story_gate</c>, one render consumed).</summary>
    public static AutopilotRun Rendering()
    {
        var run = StoryGate();
        run.AdvanceTo(AutopilotRunState.Rendering, AutopilotRunStep.StoryGate, Now);
        run.ConsumeRenderAttempt();
        return run;
    }

    /// <summary>A run whose publication exists (step <c>video_gate</c>).</summary>
    public static AutopilotRun PublicationCreated()
    {
        var run = Rendering();
        run.AdvanceTo(AutopilotRunState.VideoGate, AutopilotRunStep.Render, Now);
        run.AdvanceTo(AutopilotRunState.PublicationCreated, AutopilotRunStep.VideoGate, Now);
        run.AttachPublication(Guid.CreateVersion7());
        return run;
    }

    /// <summary>A run whose consent is written (step <c>consent</c>, row <c>veto_window → scheduled</c>).</summary>
    public static AutopilotRun Scheduled()
    {
        var run = PublicationCreated();
        run.AdvanceTo(AutopilotRunState.VetoWindow, step: null, Now);
        run.AdvanceTo(AutopilotRunState.Scheduled, AutopilotRunStep.Consent, Now);
        return run;
    }
}
