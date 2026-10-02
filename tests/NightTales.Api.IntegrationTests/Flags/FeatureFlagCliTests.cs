using NightTales.Application.Features.Platform.Flags.Commands;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using NightTales.Worker;
using NightTales.Worker.Cli;
using Npgsql;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Flags;

/// <summary>
/// The <c>nts-worker flags</c> verb of 09.9.2-T2 (09 README BD13) over the real Worker graph:
/// <c>set</c> changes the row and writes exactly one operator audit row in the same transaction, an
/// unchanged set is a no-op, and the exit codes are the task's contract (0 success, 1 validation,
/// 2 database failure).
/// </summary>
/// <remarks>
/// The verb runs against <see cref="WorkerHost"/>, which builds the production Worker service collection
/// on a plain <c>IHost</c> — no Kestrel, no route table. That is also the evidence for the "no listener"
/// acceptance check: the verb needs nothing but the graph, and <c>Program</c> returns before it maps the
/// health surface (asserted here on <c>IsFlagsVerb</c> and on the argv filter the host builder applies).
/// </remarks>
public sealed class FeatureFlagCliTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Key = "youtube_publish";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Set_writes_row_and_audit_in_one_transaction()
    {
        await ResetAsync();
        await SetFlagAsync(Key, enabled: false);

        await using var host = await StartHostAsync();
        var (exit, _, _) = Run(host, ["flags", "set", Key, "on"]);

        exit.ShouldBe(0);

        (await Database.QueryStringsAsync(
            $"SELECT enabled::text FROM feature_flags WHERE key = '{Key}'",
            Ct)).ShouldBe(["true"]);

        var rows = await AuditRowsAsync();
        rows.Count.ShouldBe(1, "one transition writes exactly one row");
        rows[0].ShouldContain("system", Case.Sensitive, "actor_type");
        rows[0].ShouldContain("support", Case.Sensitive, "category");
        rows[0].ShouldContain("internal", Case.Sensitive, "visibility");
        rows[0].ShouldContain(SetFeatureFlagCommandHandler.AuditAction);
        rows[0].ShouldContain(SetFeatureFlagCommandHandler.OperatorActor);
        rows[0].ShouldContain(Key);
        rows[0].ShouldNotContain("password", Case.Insensitive, "no secret or content in the payload (BD16)");

        // QA-FL-1: the ruled second eviction path is real — the same transaction queues the outbox row the
        // Worker's FeatureFlagsChangedHandler consumes.
        var outbox = await OutboxRowsAsync();
        outbox.Count.ShouldBe(1, "one transition queues exactly one event");
        outbox[0].ShouldContain("feature_flags_changed.v1");
        outbox[0].ShouldContain("|null|", Case.Sensitive, "a [PlatformEvent] carries no user_id");
        outbox[0].ShouldContain(Key);
        outbox[0].ShouldContain("true");
    }

    /// <summary>A refused audit insert at commit rolls back the transition and reports a typed failure.</summary>
    [Fact]
    public async Task Set_with_a_failing_audit_writer_changes_neither_row()
    {
        await ResetAsync();
        await SetFlagAsync(Key, enabled: false);
        await Database.ExecuteAsync(
            $"ALTER TABLE audit_logs ADD CONSTRAINT nts_flags_refuse_audit CHECK (action <> '{SetFeatureFlagCommandHandler.AuditAction}') NOT VALID",
            Ct);

        try
        {
            await using var host = await StartHostAsync();
            var (exit, output, error) = Run(host, ["flags", "set", Key, "on"]);

            exit.ShouldBe(2);
            output.ShouldBeEmpty();
            error.ShouldNotBeNullOrWhiteSpace();
            error.ShouldContain("service_unavailable", Case.Sensitive, "the commit maps the provider failure to a typed error");
            error.ShouldNotContain("nts_flags_refuse_audit");
            error.ShouldNotContain("   at ");
            (await Database.QueryStringsAsync(
                $"SELECT enabled::text FROM feature_flags WHERE key = '{Key}'",
                Ct)).ShouldBe(["false"], "the update commits only with its audit row (D5)");
            (await AuditRowsAsync()).ShouldBeEmpty();
            (await OutboxRowsAsync()).ShouldBeEmpty();
        }
        finally
        {
            await Database.ExecuteAsync("ALTER TABLE audit_logs DROP CONSTRAINT nts_flags_refuse_audit", Ct);
        }
    }

    [Fact]
    public async Task Set_twice_reports_changed_false()
    {
        await ResetAsync();
        await SetFlagAsync(Key, enabled: false);

        await using var host = await StartHostAsync();
        var first = Run(host, ["flags", "set", Key, "on"]);
        var second = Run(host, ["flags", "set", Key, "on"]);

        first.Exit.ShouldBe(0);
        first.Output.ShouldContain("changed");
        second.Exit.ShouldBe(0);
        second.Output.ShouldContain("unchanged");

        (await AuditRowsAsync()).Count.ShouldBe(1, "a set that changed nothing is not audited");
        (await OutboxRowsAsync()).Count.ShouldBe(1, "and it queues no second event either");
    }

    /// <summary>
    /// The second eviction path of the RULED design (09.9.2-T2 §Eviction): a changed set commits one
    /// <c>feature_flags_changed.v1</c> outbox row with the row, the audit and the NOTIFY, and a no-op
    /// set writes none.
    /// </summary>
    [Fact]
    public async Task Set_commits_the_change_event_to_the_outbox()
    {
        await ResetAsync();
        await SetFlagAsync(Key, enabled: false);

        await using var host = await StartHostAsync();

        var (exit, _, _) = Run(host, ["flags", "set", Key, "on"]);
        exit.ShouldBe(0);

        var rows = await Database.QueryStringsAsync(
            "SELECT type || '|' || coalesce(user_id::text, 'null') || '|' || payload::text FROM outbox_messages "
            + "WHERE type = 'feature_flags_changed.v1'",
            Ct);
        rows.Count.ShouldBe(1, "the writer feeds the Worker's outbox eviction path in the same transaction");
        rows[0].ShouldContain("feature_flags_changed.v1");
        rows[0].ShouldContain(Key);
        rows[0].ShouldNotContain("password", Case.Insensitive, "no secret or content in the payload (BD16)");

        // A set that changed nothing adds neither the audit row (asserted above) nor the event.
        Run(host, ["flags", "set", Key, "on"]);
        (await Database.QueryStringsAsync(
            "SELECT type FROM outbox_messages WHERE type = 'feature_flags_changed.v1'",
            Ct)).Count.ShouldBe(1, "the no-op set publishes nothing");
    }

    /// <summary>Both writers reach their UPDATE before the row lock is released; only one transition exists.</summary>
    [Fact]
    public async Task Concurrent_sets_commit_one_state_and_one_audit()
    {
        await ResetAsync();
        await SetFlagAsync(Key, enabled: false);
        await using var host = await StartHostAsync();
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var command = new NpgsqlCommand("SELECT key FROM feature_flags FOR UPDATE", connection, transaction))
        {
            await command.ExecuteScalarAsync(Ct);
        }

        var first = Task.Run(() => Run(host, ["flags", "set", Key, "on"]), Ct);
        var second = Task.Run(() => Run(host, ["flags", "set", Key, "on"]), Ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            while ((await Database.QueryStringsAsync(
                       "SELECT pid::text FROM pg_stat_activity WHERE datname = current_database() "
                       + "AND wait_event_type = 'Lock' AND query LIKE 'UPDATE%feature_flags%'",
                       timeout.Token)).Count != 2)
            {
                await Task.Delay(10, timeout.Token);
            }
        }
        finally
        {
            await transaction.RollbackAsync(Ct);
            await Task.WhenAll(first, second);
        }

        first.Result.Exit.ShouldBe(0);
        second.Result.Exit.ShouldBe(0);
        new[] { first.Result.Output, second.Result.Output }
            .Count(output => output.Contains("(changed)", StringComparison.Ordinal)).ShouldBe(1);
        new[] { first.Result.Output, second.Result.Output }
            .Count(output => output.Contains("(unchanged)", StringComparison.Ordinal)).ShouldBe(1);
        (await Database.QueryStringsAsync(
            $"SELECT enabled::text FROM feature_flags WHERE key = '{Key}'", Ct)).ShouldBe(["true"]);
        (await AuditRowsAsync()).Count.ShouldBe(1);
        (await OutboxRowsAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Unknown_key_exits_1()
    {
        await ResetAsync();
        await using var host = await StartHostAsync();

        var (unknownRow, _, _) = Run(host, ["flags", "set", "billing_enabled", "on"]);
        var (badShape, _, _) = Run(host, ["flags", "set", "Not-A-Key", "on"]);
        var (badState, _, _) = Run(host, ["flags", "set", Key, "enabled"]);
        var (noVerb, _, _) = Run(host, ["flags"]);

        unknownRow.ShouldBe(1, "a valid key no row has is a not_found the operator fixes on the command line");
        badShape.ShouldBe(1);
        badState.ShouldBe(1);
        noVerb.ShouldBe(1);
        (await AuditRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Newline_key_is_validation_failed_before_any_database_lookup()
    {
        await using var host = new WorkerHost(Database, new FakeClock())
            .WithSetting(
                $"ConnectionStrings:{PersistenceOptions.ConnectionStringName}",
                "Host=127.0.0.1;Port=1;Database=nts_flags_down;Username=nts;Password=nts;Timeout=1");
        await host.StartAsync(Ct);
        var (exit, output, error) = Run(host, ["flags", "set", Key + "\n", "on"]);
        exit.ShouldBe(1, "validation precedes the unreachable database");
        output.ShouldBeEmpty();
        error.ShouldContain("validation_failed");
    }

    [Fact]
    public async Task Database_down_exits_2_without_audit()
    {
        await ResetAsync();
        await SetFlagAsync(Key, enabled: false);

        await using var host = new WorkerHost(Database, new FakeClock())
            .WithSetting(
                $"ConnectionStrings:{PersistenceOptions.ConnectionStringName}",
                "Host=127.0.0.1;Port=1;Database=nts_flags_down;Username=nts;Password=nts;Timeout=1");

        await host.StartAsync(Ct);
        var (exit, _, error) = Run(host, ["flags", "set", Key, "on"]);

        exit.ShouldBe(2);
        error.ShouldNotContain("Host=", Case.Insensitive, "never print a connection string");
        error.ShouldNotContain("   at ", Case.Sensitive, "never print a stack trace");
        (await Database.QueryStringsAsync(
            $"SELECT enabled::text FROM feature_flags WHERE key = '{Key}'",
            Ct)).ShouldBe(["false"]);
        (await AuditRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task List_database_down_exits_2()
    {
        await ResetAsync();
        await using var host = new WorkerHost(Database, new FakeClock())
            .WithSetting(
                $"ConnectionStrings:{PersistenceOptions.ConnectionStringName}",
                "Host=127.0.0.1;Port=1;Database=nts_flags_down;Username=nts;Password=nts;Timeout=1");

        await host.StartAsync(Ct);
        var (exit, output, error) = Run(host, ["flags", "list"]);

        exit.ShouldBe(2, "a listing cannot fail for a validation reason (09.9.2 plan §6)");
        output.ShouldBeEmpty();
        error.ShouldNotContain("Host=", Case.Insensitive, "never print a connection string");
    }

    [Fact]
    public async Task List_prints_unused_rules_marker()
    {
        await ResetAsync();
        await SetFlagAsync(Key, enabled: true);
        await SetFlagAsync("tiktok_publish", enabled: false);

        await using var host = await StartHostAsync();
        var (exit, output, _) = Run(host, ["flags", "list"]);

        exit.ShouldBe(0);
        output.ShouldContain(Key);
        output.ShouldContain("tiktok_publish");
        output.ShouldContain("on", Case.Sensitive, "an enabled flag prints its state");
        output.ShouldContain("off", Case.Sensitive, "a disabled one too");
        output.ShouldContain(FlagsCommand.RulesMarker);
    }

    /// <summary>
    /// The verb is a verb, and the host builder drops the words a configuration provider would reject: the
    /// path that handles them returns before Kestrel is started, so <c>nts-worker flags list</c> binds no
    /// port.
    /// </summary>
    [Fact]
    public void Flags_verb_binds_no_port()
    {
        FlagsCommand.IsFlagsVerb(["flags", "list"]).ShouldBeTrue();
        FlagsCommand.IsFlagsVerb(["flags", "set", Key, "off"]).ShouldBeTrue();
        FlagsCommand.IsFlagsVerb(["--seed"]).ShouldBeFalse();
        FlagsCommand.IsFlagsVerb([]).ShouldBeFalse();

        WorkerApplication.HostArguments(["flags", "set", Key, "on", "--urls=http://127.0.0.1:1"])
            .ShouldBe(["--urls=http://127.0.0.1:1"], "the verb words never reach the host's configuration");
    }

    /// <summary>Builds and starts the production Worker graph on a plain <c>IHost</c> (no Kestrel).</summary>
    private async Task<WorkerHost> StartHostAsync()
    {
        var host = new WorkerHost(Database, new FakeClock());
        await host.StartAsync(Ct);

        return host;
    }

    /// <summary>Runs the verb over a started host and returns its exit code and both streams.</summary>
    private static (int Exit, string Output, string Error) Run(WorkerHost host, string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = FlagsCommand.Run(host.Services, args, output, error, Ct);

        return (exit, output.ToString(), error.ToString());
    }

    /// <summary>
    /// The one audit row of a flag change, flattened so the assertions read the stored text values:
    /// <c>actor_type|category|visibility|action|data</c>.
    /// </summary>
    private Task<IReadOnlyList<string>> AuditRowsAsync() =>
        Database.QueryStringsAsync(
            "SELECT actor_type || '|' || category || '|' || visibility || '|' || action || '|' || data::text "
            + $"FROM audit_logs WHERE action = '{SetFeatureFlagCommandHandler.AuditAction}'",
            Ct);

    /// <summary>
    /// The flag-change events this class queued, flattened as <c>type|user_id|payload</c> so the assertions
    /// read the stored values (a null <c>user_id</c> is the platform-event marker).
    /// </summary>
    private Task<IReadOnlyList<string>> OutboxRowsAsync() =>
        Database.QueryStringsAsync(
            "SELECT type || '|' || coalesce(user_id::text, 'null') || '|' || payload::text "
            + "FROM outbox_messages WHERE type = 'feature_flags_changed.v1'",
            Ct);

    /// <summary>
    /// Clears the flag rows and the operator audit rows this class writes. The class shares one database
    /// (09 README D13) and <c>audit_logs</c> is append-only, so without this one test would count another
    /// test's rows.
    /// </summary>
    private Task ResetAsync() =>
        Database.ExecuteAsync(
            $"DELETE FROM audit_logs WHERE action = '{SetFeatureFlagCommandHandler.AuditAction}'; "
            + "DELETE FROM outbox_messages WHERE type = 'feature_flags_changed.v1'; "
            + "DELETE FROM feature_flags;",
            Ct);

    private Task SetFlagAsync(string key, bool enabled) =>
        Database.ExecuteAsync(
            "INSERT INTO feature_flags (key, enabled, rules, created_at, updated_at) "
            + $"VALUES ('{key}', {(enabled ? "true" : "false")}, '{{}}', now(), now()) "
            + "ON CONFLICT (key) DO UPDATE SET enabled = EXCLUDED.enabled, updated_at = now()",
            Ct);

}
