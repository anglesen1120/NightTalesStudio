using NightTales.Api.IntegrationTests.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Audit;

/// <summary>
/// The slot-order property of 09.3.6-T1 over the real Infrastructure (plan §3): an idempotent replay
/// short-circuits in slot 5 — <em>outside</em> Audit, slot 6 — so two calls with one
/// <c>Idempotency-Key</c> write exactly one <c>audit_logs</c> row.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AuditBehaviorTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task KeyedReplay_WritesOneRowForTwoCalls()
    {
        var userId = (await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: Ct)).Id;
        var runner = new AuditPipelineProbeRunner(Database, new TestCurrentUser { Id = userId }, new FakeClock(Start));
        var command = new AuditProbeCommand("replay", IdempotencyKey: Guid.CreateVersion7().ToString());

        var first = await runner.SendAsync(command, Ct);
        var second = await runner.SendAsync(command, Ct);

        first.IsError.ShouldBeFalse();
        second.IsError.ShouldBeFalse();
        second.Value.ShouldBe(first.Value, "the replay answers with the stored response");
        runner.Executions.ShouldBe(1, "the handler ran once for both calls");

        Rows().ShouldBe(1, "slot 5 short-circuits before slot 6 could write a second row");
    }

    private long Rows()
        => SqlProbe.Count(
            Database,
            "SELECT count(*) FROM audit_logs WHERE action = 'probe.audited' AND data->>'probe' = 'replay'");
}
