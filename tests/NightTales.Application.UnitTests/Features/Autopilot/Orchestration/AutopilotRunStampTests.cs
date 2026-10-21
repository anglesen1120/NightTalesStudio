using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Autopilot;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Security;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Autopilot.Orchestration;

/// <summary>
/// The due scan's check stamp on the exits that are neither a move nor an idle answer (D-8): a run whose handler
/// throws keeps its <c>updated_at</c> without the stamp, so it heads every pass of its class for ever and starves the
/// rows behind it once enough of them poison their slot.
/// </summary>
/// <remarks>
/// <para>
/// The orchestrator opens its own DI scope, pins <see cref="ISystemContext"/> and resolves the tenant-filtered
/// <see cref="IAppDbContext"/>, so this suite builds exactly that scope over the real (PostgreSQL) test database and
/// registers one handler: the one that throws. Graph: not applicable (the worktree is not indexed).
/// </para>
/// <para>
/// The run is reachable only through the ordered cross-user read, which the fake below answers with the seeded pair;
/// the scan's own predicate is the fake's concern, not this case's.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class AutopilotRunStampTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The plan §3.1 document the settings reader accepts, so the scan reaches the handler.</summary>
    private const string ValidSettings = """
        {
          "schema_version": 1,
          "cadence": { "days_of_week": ["mon", "thu"], "publish_time": "19:00", "time_zone": "Asia/Ho_Chi_Minh" },
          "daily_cap": 1,
          "buffer_days": 2,
          "privacy": "private",
          "veto_window_hours": 12,
          "pre_audit_mode": "keep_cadence_private",
          "pre_audit_pause_after": 3,
          "gate_thresholds_ref": "default",
          "tone": "atmospheric, restrained",
          "banned_themes": [],
          "render_lead_minutes": null,
          "story_lead_minutes": null
        }
        """;

    /// <summary>
    /// A handler that throws on a due run: the tick stamps the row with its clock reading so the next pass goes to
    /// the rows nobody has looked at, and the run keeps its state — the stamp is not a move.
    /// </summary>
    [Fact]
    public async Task DueScan_HandlerThrows_StampsTheRunSoItRotates()
    {
        var world = await SeedAsync();
        var clock = new FakeClock(Now);
        var step = new ThrowingStep();
        var orchestrator = Orchestrator(world, clock, step);

        var advanced = await orchestrator.AdvanceDueAsync(Now, 1, Ct);

        advanced.ShouldBe(0, "a run whose step failed counts as nothing handled");
        step.Calls.ShouldBe(1, "the scan reached the throwing handler");

        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }, clock);
        var run = await db.AutopilotRuns.SingleAsync(candidate => candidate.Id == world.RunId, Ct);

        run.State.ShouldBe(AutopilotRunState.Planned, "the check stamp is not a move");
        run.UpdatedAt.ShouldBe(
            Now,
            $"the scan stamps its own reading; the seed wrote {world.SeededAt:O}, which must not be the same instant");
    }

    /// <summary>The orchestrator over the real scope: one pinned user, one context, one throwing handler.</summary>
    /// <param name="world">The seeded owner and run.</param>
    /// <param name="clock">The tick's clock reading.</param>
    /// <param name="step">The handler the scan reaches.</param>
    /// <returns>The orchestrator under test.</returns>
    private AutopilotRunOrchestrator Orchestrator(World world, FakeClock clock, ThrowingStep step)
    {
        var services = new ServiceCollection();

        services.AddScoped<ScopedCurrentUser>();
        services.AddScoped<ICurrentUser>(provider => provider.GetRequiredService<ScopedCurrentUser>());
        services.AddScoped<ISystemContext>(provider => provider.GetRequiredService<ScopedCurrentUser>());
        services.AddScoped<IAppDbContext>(provider =>
            Database.CreateAppDbContext(provider.GetRequiredService<ICurrentUser>(), clock));
        services.AddScoped<IAutopilotStepHandler>(_ => step);

        var provider = services.BuildServiceProvider();

        return new AutopilotRunOrchestrator(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new OneRunSystemWork(world.RunId, world.UserId),
            new AutopilotRunOptions(),
            new AutopilotRunMetrics(),
            clock,
            NullLogger<AutopilotRunOrchestrator>.Instance);
    }

    /// <summary>One owner, one autopilot series with a valid settings document, and one due planned run.</summary>
    /// <returns>The ids the case asserts on.</returns>
    private async Task<World> SeedAsync()
    {
        var actor = new TestCurrentUser();

        // The seed writes its rows four hours before the tick, so the stamp the scan writes is recognisable: a run
        // whose handler throws keeps this instant unless the scan stamps it.
        var clock = new FakeClock(Now.AddHours(-4));
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        actor.Id = user.Id;

        var series = await new SeriesBuilder(Database, actor, clock)
            .CreateAsync(user.Id, autopilotEnabled: true, autopilotSettings: ValidSettings, ct: Ct);

        var run = await new AutopilotRunBuilder(Database, actor, clock).CreateAsync(
            user.Id,
            series.Id,
            startAt: Now.AddHours(-4),
            now: Now.AddHours(-4),
            state: AutopilotRunState.Planned,
            ct: Ct);

        return new World(user.Id, run.Id, run.UpdatedAt);
    }

    private sealed record World(Guid UserId, Guid RunId, DateTimeOffset SeededAt);

    /// <summary>The handler of a poison run: it counts its call and throws, like a step whose port is broken.</summary>
    private sealed class ThrowingStep : IAutopilotStepHandler
    {
        public int Calls { get; private set; }

        public AutopilotRunState State => AutopilotRunState.Planned;

        public ValueTask<StepResult> HandleAsync(AutopilotStepContext context, CancellationToken ct)
        {
            Calls++;

            throw new InvalidOperationException("the handler of this run always throws");
        }
    }

    /// <summary>The scan's cross-user read, answered with one seeded pair and nothing else.</summary>
    private sealed class OneRunSystemWork(Guid runId, Guid userId) : ISystemWorkQuery
    {
        public IAsyncEnumerable<(Guid Id, Guid UserId)> PendingAsync<T>(
            Expression<Func<T, bool>> where,
            int batch,
            CancellationToken ct)
            where T : Entity, IUserOwned =>
            Empty(ct);

        public async IAsyncEnumerable<(Guid Id, Guid UserId)> PendingAsync<T, TKey>(
            Expression<Func<T, bool>> where,
            Expression<Func<T, TKey>> orderBy,
            int batch,
            [EnumeratorCancellation] CancellationToken ct)
            where T : Entity, IUserOwned
        {
            await Task.CompletedTask;

            yield return (runId, userId);
        }

        private static async IAsyncEnumerable<(Guid Id, Guid UserId)> Empty(
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;

            yield break;
        }
    }
}
