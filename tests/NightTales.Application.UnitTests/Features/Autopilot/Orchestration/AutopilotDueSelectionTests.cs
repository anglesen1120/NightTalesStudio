using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Autopilot;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Autopilot.Orchestration;

/// <summary>
/// The tick's due selection (02.8.3-T1; review findings PF-1, SEC-2, PF-3): the scan asks the cross-user port for the
/// actionable states first and for the externally waiting ones only with the budget that is left, it orders both
/// passes by <c>updated_at</c>, and every due state belongs to exactly one of the two passes.
/// </summary>
/// <remarks>
/// The orchestrator runs over an empty provider on purpose: the scan, its order and its budget are what these tests
/// assert, and each selected run fails on the first scope lookup (logged, skipped, never aborting the scan), which
/// also proves that one failing run does not stop the others.
/// </remarks>
public sealed class AutopilotDueSelectionTests
{
    private static readonly Guid UserId = Guid.Parse("018f0000-0000-7000-8000-00000000000a");
    private static readonly Guid SeriesId = Guid.Parse("018f0000-0000-7000-8000-00000000000b");
    private static readonly Guid AuthorizationId = Guid.Parse("018f0000-0000-7000-8000-00000000000c");
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SlotAt = Now.AddDays(1);

    /// <summary>
    /// Two passes with one shared budget: the actionable pass takes <c>max</c>, the waiting pass only what the first
    /// one left, and both order by <c>updated_at</c> (PF-1's rotation, SEC-2's cross-tenant fairness).
    /// </summary>
    [Fact]
    public async Task DueScan_ServesTheActionablePassFirst_ThenTheWaitingPassWithTheRestOfTheBudget()
    {
        var work = new ScriptedSystemWork(
            Page(Pair()),
            Page(Pair()));
        var orchestrator = Orchestrator(work);

        var advanced = await orchestrator.AdvanceDueAsync(Now, 3, CancellationToken.None);

        advanced.ShouldBe(0, "every selected run threw on the empty provider scope, so none advanced");

        work.Calls.Count.ShouldBe(2);
        work.UnorderedCalls.ShouldBe(0, "the due scan must take the ordered overload");

        work.Calls[0].Batch.ShouldBe(3);
        work.Calls[1].Batch.ShouldBe(2, "the waiting pass only gets what the actionable pass left");

        var actionable = work.Calls[0].Where.Compile();
        var waiting = work.Calls[1].Where.Compile();

        actionable(InState(AutopilotRunState.Planned)).ShouldBeTrue();
        actionable(InState(AutopilotRunState.StoryGenerating)).ShouldBeTrue();
        actionable(InState(AutopilotRunState.Scheduled)).ShouldBeFalse("a scheduled run waits on the upload job");

        waiting(InState(AutopilotRunState.Scheduled)).ShouldBeTrue();
        waiting(InState(AutopilotRunState.AwaitingAuthorization)).ShouldBeTrue();
        waiting(InState(AutopilotRunState.Planned)).ShouldBeFalse();

        var planned = InState(AutopilotRunState.Planned);

        work.Calls[0].OrderKey(planned)
            .ShouldBe(planned.UpdatedAt, "both passes rotate on the row's last check stamp, never on start_at");
    }

    /// <summary>
    /// A saturated actionable pass is logged and the waiting pass is skipped: the tick says out loud that the budget,
    /// not the queue, ended the scan (PF-3).
    /// </summary>
    [Fact]
    public async Task DueScan_ActionablePassFillsTheBudget_SkipsTheWaitingPass()
    {
        var work = new ScriptedSystemWork(Page(Pair(), Pair()));
        var orchestrator = Orchestrator(work);

        await orchestrator.AdvanceDueAsync(Now, 2, CancellationToken.None);

        work.Calls.Count.ShouldBe(1);
        work.Calls[0].Batch.ShouldBe(2);
    }

    /// <summary>A zero budget touches neither the port nor the scopes.</summary>
    [Fact]
    public async Task DueScan_ZeroBudget_ReadsNothing()
    {
        var work = new ScriptedSystemWork(Page(Pair()));
        var orchestrator = Orchestrator(work);

        (await orchestrator.AdvanceDueAsync(Now, 0, CancellationToken.None)).ShouldBe(0);

        work.Calls.ShouldBeEmpty();
        work.UnorderedCalls.ShouldBe(0);
    }

    /// <summary>
    /// Every non-terminal, non-paused state belongs to exactly one pass, so adding a state to the machine without
    /// classifying it cannot silently drop it out of the tick (the starvation PF-1 describes, by another route).
    /// </summary>
    [Fact]
    public void EveryDueState_IsInExactlyOnePass()
    {
        var actionable = AutopilotRunOrchestrator.TickDriven(Now).Compile();
        var waiting = AutopilotRunOrchestrator.ExternalWait(Now).Compile();
        var due = AutopilotRunOrchestrator.Due(Now).Compile();

        var dueStates = Enum.GetValues<AutopilotRunState>()
            .Where(state => !AutopilotRunStateMachine.IsTerminal(state) && state != AutopilotRunState.Paused)
            .ToList();

        dueStates.Count.ShouldBe(12, "the 02.8.3 plan §3.5 machine's non-terminal states, paused aside");

        foreach (var state in dueStates)
        {
            var run = InState(state);

            due(run).ShouldBeTrue($"{state} is due while its start_at has arrived");

            var inActionable = actionable(run);
            var inWaiting = waiting(run);

            (inActionable ^ inWaiting).ShouldBeTrue($"{state} must be in exactly one scan pass");
        }
    }

    private static AutopilotRunOrchestrator Orchestrator(ISystemWorkQuery work) =>
        new(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            work,
            new AutopilotRunOptions(),
            new AutopilotRunMetrics(),
            new FakeClock(Now),
            NullLogger<AutopilotRunOrchestrator>.Instance);

    private static (Guid Id, Guid UserId) Pair() => (Guid.CreateVersion7(), UserId);

    /// <summary>One page of the script: the pairs a single ordered call may hand out.</summary>
    /// <param name="pairs">The pairs of this page.</param>
    /// <returns>The page.</returns>
    private static (Guid Id, Guid UserId)[] Page(params (Guid Id, Guid UserId)[] pairs) => pairs;

    /// <summary>
    /// One run per state, walked up the plan §3.5 ladder from <see cref="AutopilotRunState.Planned"/>: the moved half
    /// needs no database, and the publishing half only needs the ladder rows.
    /// </summary>
    /// <param name="state">The state the run must be in.</param>
    /// <returns>The run, started in the past so it is due.</returns>
    private static AutopilotRun InState(AutopilotRunState state)
    {
        var run = AutopilotRun.Plan(UserId, SeriesId, SlotAt, Now.AddMinutes(-60), AuthorizationId, Now.AddHours(-2));

        switch (state)
        {
            case AutopilotRunState.Planned:
                break;
            case AutopilotRunState.AwaitingAuthorization:
                run.AdvanceTo(state, AutopilotRunStep.Authorization, Now);
                break;
            case AutopilotRunState.IdeaPicked:
                run.AdvanceTo(state, AutopilotRunStep.Idea, Now);
                break;
            case AutopilotRunState.Researching:
                run.AdvanceTo(AutopilotRunState.IdeaPicked, AutopilotRunStep.Idea, Now);
                run.AdvanceTo(state, AutopilotRunStep.FindSources, Now);
                break;
            case AutopilotRunState.StoryGenerating:
                run.AdvanceTo(AutopilotRunState.IdeaPicked, AutopilotRunStep.Idea, Now);
                run.AdvanceTo(state, AutopilotRunStep.Story, Now);
                break;
            case AutopilotRunState.StoryGate:
                run.AdvanceTo(AutopilotRunState.IdeaPicked, AutopilotRunStep.Idea, Now);
                run.AdvanceTo(AutopilotRunState.StoryGenerating, AutopilotRunStep.Story, Now);
                run.AdvanceTo(state, AutopilotRunStep.StoryGate, Now);
                break;
            case AutopilotRunState.WaitingOwner:
                run.AdvanceTo(AutopilotRunState.IdeaPicked, AutopilotRunStep.Idea, Now);
                run.SetWaitingFor(WaitingFor.Story);
                run.AdvanceTo(state, step: null, Now);
                break;
            case AutopilotRunState.Rendering:
                run.AdvanceTo(AutopilotRunState.IdeaPicked, AutopilotRunStep.Idea, Now);
                run.AdvanceTo(AutopilotRunState.StoryGenerating, AutopilotRunStep.Story, Now);
                run.AdvanceTo(AutopilotRunState.StoryGate, AutopilotRunStep.StoryGate, Now);
                run.AdvanceTo(state, step: null, Now);
                break;
            case AutopilotRunState.VideoGate:
                ToVideoGate(run);
                break;
            case AutopilotRunState.PublicationCreated:
                ToVideoGate(run);
                run.AdvanceTo(state, AutopilotRunStep.VideoGate, Now);
                break;
            case AutopilotRunState.VetoWindow:
                ToVideoGate(run);
                run.AdvanceTo(AutopilotRunState.PublicationCreated, AutopilotRunStep.VideoGate, Now);
                run.AdvanceTo(state, step: null, Now);
                break;
            case AutopilotRunState.Scheduled:
                ToVideoGate(run);
                run.AdvanceTo(AutopilotRunState.PublicationCreated, AutopilotRunStep.VideoGate, Now);
                run.AdvanceTo(AutopilotRunState.VetoWindow, step: null, Now);
                run.AdvanceTo(state, AutopilotRunStep.Consent, Now);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(state), state, "The terminal states are not due rows and are asserted apart.");
        }

        run.State.ShouldBe(state);

        return run;
    }

    private static void ToVideoGate(AutopilotRun run)
    {
        run.AdvanceTo(AutopilotRunState.IdeaPicked, AutopilotRunStep.Idea, Now);
        run.AdvanceTo(AutopilotRunState.StoryGenerating, AutopilotRunStep.Story, Now);
        run.AdvanceTo(AutopilotRunState.StoryGate, AutopilotRunStep.StoryGate, Now);
        run.AdvanceTo(AutopilotRunState.Rendering, step: null, Now);
        run.AdvanceTo(AutopilotRunState.VideoGate, AutopilotRunStep.Render, Now);
    }

    /// <summary>One recorded scan call: the filter, the order key and the batch it asked for.</summary>
    /// <param name="Where">The filter the orchestrator passed.</param>
    /// <param name="OrderBy">The order key the orchestrator passed.</param>
    /// <param name="Batch">The batch the orchestrator passed.</param>
    private sealed record Call(
        Expression<Func<AutopilotRun, bool>> Where,
        Expression<Func<AutopilotRun, DateTimeOffset>> OrderBy,
        int Batch)
    {
        /// <summary>The key this call sorts by, read off <paramref name="run"/>.</summary>
        /// <param name="run">The run to read it from.</param>
        /// <returns>The sort key.</returns>
        public DateTimeOffset OrderKey(AutopilotRun run) => OrderBy.Compile()(run);
    }

    /// <summary>
    /// A port that hands out one scripted page per ordered call, so a test states the scan's passes as a script. The
    /// unordered member is counted and refuses: the due scan must not use it.
    /// </summary>
    /// <param name="pages">One page of pairs per ordered call, in call order.</param>
    private sealed class ScriptedSystemWork(params (Guid Id, Guid UserId)[][] pages) : ISystemWorkQuery
    {
        private int _page;

        public List<Call> Calls { get; } = [];

        public int UnorderedCalls { get; private set; }

        public async IAsyncEnumerable<(Guid Id, Guid UserId)> PendingAsync<T>(
            Expression<Func<T, bool>> where,
            int batch,
            [EnumeratorCancellation] CancellationToken ct)
            where T : Entity, IUserOwned
        {
            UnorderedCalls++;

            await Task.CompletedTask;

            throw new InvalidOperationException("The due scan must take the ordered overload.");
#pragma warning disable CS0162 // Unreachable code: kept so the method is a valid async iterator.
            yield break;
#pragma warning restore CS0162
        }

        public async IAsyncEnumerable<(Guid Id, Guid UserId)> PendingAsync<T, TKey>(
            Expression<Func<T, bool>> where,
            Expression<Func<T, TKey>> orderBy,
            int batch,
            [EnumeratorCancellation] CancellationToken ct)
            where T : Entity, IUserOwned
        {
            Calls.Add(new Call(
                (Expression<Func<AutopilotRun, bool>>)(object)where,
                (Expression<Func<AutopilotRun, DateTimeOffset>>)(object)orderBy,
                batch));

            var page = _page < pages.Length ? pages[_page] : [];
            _page++;

            foreach (var pair in page.Take(batch))
            {
                await Task.Yield();
                yield return pair;
            }
        }
    }
}
