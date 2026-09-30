using ErrorOr;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Pipeline;

/// <summary>
/// QA (09.1.3-T2) adversarial probes over the real replay path: a stored <c>response_type</c> that no
/// longer names this build's success type, and a stored payload that does not deserialize to it, are both
/// refused as a reused key (422) without running the handler and without trusting the stored type name
/// (09.1.3-T2 Failure handling, "the response type was renamed between deploy and replay").
/// </summary>
public sealed class IdempotencyReplayIntegrityTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task Idempotency_Replay_TamperedStoredResponseType_Returns422_WithoutHandler()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);
        var runner = new PipelineProbeRunner(
            Database,
            new TestCurrentUser { Id = user.Id },
            clock,
            (_, _, _) => ValueTask.FromResult<ErrorOr<Guid>>(Guid.CreateVersion7()));

        const string key = "key-type-tamper";
        var command = new PipelineProbeCommand(Body: "tamper", IdempotencyKey: key);

        (await runner.SendAsync(command, ct)).IsError.ShouldBeFalse();

        // The name of another CLR type that exists and whose payload deserializes cleanly: an
        // implementation that deserialized whatever name the row carries would take that path.
        await Database.ExecuteAsync(
            $"UPDATE idempotency_keys SET response_type = 'System.DateTime', response = '\"2020-01-01T00:00:00+00:00\"'::jsonb "
            + $"WHERE user_id = '{user.Id}' AND key = '{key}'",
            ct);

        var replayed = await runner.SendAsync(command, ct);

        replayed.IsError.ShouldBeTrue("a stored type that is not the success type is a reused key");
        replayed.FirstError.Code.ShouldBe(ErrorCodes.IdempotencyKeyReused);
        runner.Executions.ShouldBe(1, "the replay never runs the handler");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM idempotency_keys WHERE user_id = '{user.Id}' AND key = '{key}'")
            .ShouldBe(1, "the refused replay leaves the row itself intact");
    }

    [Fact]
    public async Task Idempotency_Replay_UnusableStoredPayload_Returns422_WithoutHandler()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);
        var runner = new PipelineProbeRunner(
            Database,
            new TestCurrentUser { Id = user.Id },
            clock,
            (_, _, _) => ValueTask.FromResult<ErrorOr<Guid>>(Guid.CreateVersion7()));

        const string key = "key-payload-tamper";
        var command = new PipelineProbeCommand(Body: "payload", IdempotencyKey: key);

        (await runner.SendAsync(command, ct)).IsError.ShouldBeFalse();

        await Database.ExecuteAsync(
            $"UPDATE idempotency_keys SET response = '\"not-a-guid\"'::jsonb "
            + $"WHERE user_id = '{user.Id}' AND key = '{key}'",
            ct);

        var replayed = await runner.SendAsync(command, ct);

        replayed.IsError.ShouldBeTrue();
        replayed.FirstError.Code.ShouldBe(ErrorCodes.IdempotencyKeyReused);
        runner.Executions.ShouldBe(1);
    }

    [Fact]
    public async Task Idempotency_Replay_StoredRowLockedButUncompleted_RunsTheHandlerOnce()
    {
        // The row exists, is not expired, has the same body, and carries no response: the documented
        // "cannot happen after the twin committed" branch treats it as this request's own claim.
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);
        var runner = new PipelineProbeRunner(
            Database,
            new TestCurrentUser { Id = user.Id },
            clock,
            (_, _, _) => ValueTask.FromResult<ErrorOr<Guid>>(Guid.CreateVersion7()));

        const string key = "key-uncompleted";
        var command = new PipelineProbeCommand(Body: "uncompleted", IdempotencyKey: key);

        var first = await runner.SendAsync(command, ct);
        first.IsError.ShouldBeFalse();

        await Database.ExecuteAsync(
            $"UPDATE idempotency_keys SET response = NULL, response_type = NULL "
            + $"WHERE user_id = '{user.Id}' AND key = '{key}'",
            ct);

        var second = await runner.SendAsync(command, ct);

        second.IsError.ShouldBeFalse();
        runner.Executions.ShouldBe(2, "a row with no stored response is this request's own claim");
        second.Value.ShouldNotBe(first.Value);
    }
}
