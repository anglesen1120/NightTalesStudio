using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Rendering;
using NightTales.Domain.Rendering;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using NightTales.Worker.Cli;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Rendering;

/// <summary>
/// The operator surface of the registry (11.3.3-T1 plan §5/§9): <c>nodes list</c> prints the §5 columns and
/// never a key, and <c>nodes set-state</c> applies <c>active</c>/<c>draining</c>, writing exactly one
/// <c>audit_logs</c> row in the same transaction. The exit codes are the plan's: 0 applied or listed, 1 an
/// unknown node, 2 bad usage — including <c>set-state &lt;name&gt; down</c>, which is health's decision.
/// </summary>
[Trait("Category", "Integration")]
public sealed class NodesCommandTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
    private const string Pin = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    // Console is process-global: the redirection is serialised across this class's tests so a capture never
    // races another test's output.
    private static readonly SemaphoreSlim ConsoleGate = new(1, 1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task List_PrintsNoKeyValue()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        using var host = BuildHost(clock, "alpha", "beta");

        var first = await RunAsync(host.Services, "nodes", "list");
        first.Code.ShouldBe(0);
        first.Output.ShouldContain("alpha  https://alpha.example/  2  active  -  -  key=env", Case.Sensitive);

        // A row whose key came from the settings UI shows the key's source, never the key (F09/S4).
        await using (var db = Database.CreateAppDbContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE worker_nodes SET api_key_ciphertext = '\\x01'::bytea, api_key_hint = '0001' WHERE name = 'beta'",
                Ct);
        }

        var second = await RunAsync(host.Services, "nodes", "list");

        second.Code.ShouldBe(0);
        second.Output.ShouldContain("beta  https://beta.example/  2  active  -  -  key=db", Case.Sensitive);
        second.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(
            2,
            "one line per configured node");

        Regex.IsMatch(second.Output, "[0-9A-Fa-f]{32,}", RegexOptions.None, TimeSpan.FromSeconds(1))
            .ShouldBeFalse("no key, hint or ciphertext of 32+ hex characters may appear on stdout (S4)");
    }

    [Fact]
    public async Task SetState_WritesAuditRow()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        using var host = BuildHost(clock, "alpha");

        var run = await RunAsync(host.Services, "nodes", "set-state", "alpha", "draining");

        run.Code.ShouldBe(NodesCommand.Success, customMessage: run.Error);
        run.Output.Trim().ShouldBe("node alpha: active -> draining");

        var node = await RowAsync("alpha");
        node.State.ShouldBe(WorkerNodeState.Draining);

        SqlProbe.QueryStrings(
                Database,
                "SELECT actor_type || '|' || action || '|' || category || '|' || visibility || '|' || entity_type "
                + "|| '|' || (entity_id = '" + node.Id + "')::text "
                + "|| '|' || (data->>'node') || '|' || (data->>'from') || '|' || (data->>'to') "
                + "|| '|' || (data->>'actor') "
                + "|| '|' || coalesce(actor_user_id::text, '-') || '|' || coalesce(subject_user_id::text, '-') "
                + "|| '|' || coalesce(ip::text, '-') || '|' || coalesce(user_agent, '-') "
                + "FROM audit_logs WHERE action = 'render_node.state_changed'")
            .ShouldBe(
                ["system|render_node.state_changed|support|internal|worker_node|true|alpha|active|draining|system:operator|-|-|-|-"],
                "the §5 row shape: actor type system, internal visibility, entity_id the node id, no user");

        SqlProbe.QueryStrings(
                Database,
                "SELECT key FROM audit_logs, jsonb_each_text(data) WHERE action = 'render_node.state_changed' ORDER BY key")
            .ShouldBe(["actor", "from", "node", "to"], "data carries the safe summary only — no key, hint or ciphertext");

        SqlProbe.Count(Database, "SELECT count(*) FROM audit_logs").ShouldBe(1);
    }

    [Fact]
    public async Task SetState_Idempotent_Exit0()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        using var host = BuildHost(clock, "alpha");

        (await RunAsync(host.Services, "nodes", "set-state", "alpha", "draining")).Code.ShouldBe(NodesCommand.Success);
        var repeat = await RunAsync(host.Services, "nodes", "set-state", "alpha", "draining");

        repeat.Code.ShouldBe(NodesCommand.Success, "setting the state the node already has is idempotent");
        repeat.Output.Trim().ShouldBe("node alpha: draining -> draining");
        (await RowAsync("alpha")).State.ShouldBe(WorkerNodeState.Draining);
        SqlProbe.Count(Database, "SELECT count(*) FROM audit_logs")
            .ShouldBe(1, "an idempotent repeat applies nothing, so it records nothing");
    }

    [Fact]
    public async Task SetState_Down_Exit2()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        using var host = BuildHost(clock, "alpha");

        // The seeder creates the row (as at start-up), so the refusal is about the verb, not a missing node.
        (await RunAsync(host.Services, "nodes", "list")).Code.ShouldBe(NodesCommand.Success);

        var run = await RunAsync(host.Services, "nodes", "set-state", "alpha", "down");

        run.Code.ShouldBe(NodesCommand.Usage);
        run.Error.ShouldNotBeEmpty();
        (await RowAsync("alpha")).State.ShouldBe(WorkerNodeState.Active, "down is health's decision (D13)");
        SqlProbe.Count(Database, "SELECT count(*) FROM audit_logs").ShouldBe(0);
    }

    [Fact]
    public async Task SetState_UnknownNode_Exit1()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        using var host = BuildHost(clock, "alpha");

        var run = await RunAsync(host.Services, "nodes", "set-state", "ghost", "active");

        run.Code.ShouldBe(NodesCommand.Failure);
        run.Error.ShouldContain("ghost", Case.Sensitive);
        SqlProbe.Count(Database, "SELECT count(*) FROM audit_logs").ShouldBe(0);
    }

    [Fact]
    public async Task BadUsage_Exit2()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        using var host = BuildHost(clock, "alpha");

        (await RunAsync(host.Services, "nodes")).Code.ShouldBe(NodesCommand.Usage);
        (await RunAsync(host.Services, "nodes", "set-state", "alpha")).Code.ShouldBe(NodesCommand.Usage);
        (await RunAsync(host.Services, "nodes", "explode")).Code.ShouldBe(NodesCommand.Usage);
    }

    [Fact]
    public async Task List_EmptyRegistry_PrintsNothing_Exit0()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        using var host = BuildHost(clock);

        var run = await RunAsync(host.Services, "nodes", "list");

        run.Code.ShouldBe(NodesCommand.Success);
        run.Output.Trim().ShouldBeEmpty("the plan's table allows ≥ 0 rows");
    }

    [Fact]
    public async Task List_MissingKey_Exits2_NamingVariableNotValue()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        using var host = BuildHostMissingKey(clock, "beta", "alpha", "beta");

        var run = await RunAsync(host.Services, "nodes", "list");

        run.Code.ShouldBe(NodesCommand.Usage, "a missing adapter key fails the verb before it lists");
        run.Error.ShouldContain("RENDER_NODE_BETA_API_KEY", Case.Sensitive);
        run.Error.ShouldNotContain("alpha-key-value", Case.Sensitive);
        run.Error.ShouldNotContain("beta-key-value", Case.Sensitive);
    }

    [Fact]
    public async Task List_SeederThrows_Exits1_InsideTheVerbBoundary()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        // The seeder resolves the bound RenderNodes options inside RegisterAsync; a configuration that
        // fails validation throws there, before any row is written (review F5 — a throwing seed must not
        // escape the verb's exit-code boundary).
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"ConnectionStrings:{NightTales.Infrastructure.Persistence.PersistenceOptions.ConnectionStringName}"] =
                Database.ConnectionString,
            ["RenderNodes:HealthPollSeconds"] = "0",
            ["RenderNodes:Nodes:0:Name"] = "alpha",
            ["RenderNodes:Nodes:0:BaseUrl"] = "https://alpha.example/",
            ["RenderNodes:Nodes:0:Capacity"] = "2",
            ["RenderNodes:Nodes:0:TlsCertSha256"] = Pin,
            [RenderNodeApiKey.VariableName("alpha")] = "alpha-key-value",
        };
        using var host = RenderNodeTestHost.Build(Database, clock, settings);

        var run = await RunAsync(host.Services, "nodes", "list");

        run.Code.ShouldBe(
            NodesCommand.Failure,
            "a seeding failure is plan §5's 'configuration/database failure' — exit 1, not an unhandled exception");
        run.Error.ShouldContain("nts-worker:", Case.Sensitive);
        run.Error.ShouldNotContain("alpha-key-value", Case.Sensitive);
    }

    private RenderNodeTestHost BuildHost(FakeClock clock, params string[] nodeNames) =>
        BuildHost(clock, missingKey: null, nodeNames);

    /// <summary>Like <see cref="BuildHost(FakeClock, string[])"/>, but <paramref name="missingKey"/>'s node key is left unset.</summary>
    private RenderNodeTestHost BuildHostMissingKey(FakeClock clock, string missingKey, params string[] nodeNames) =>
        BuildHost(clock, missingKey, nodeNames);

    private RenderNodeTestHost BuildHost(FakeClock clock, string? missingKey, string[] nodeNames)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"ConnectionStrings:{NightTales.Infrastructure.Persistence.PersistenceOptions.ConnectionStringName}"] =
                Database.ConnectionString,
        };

        for (var index = 0; index < nodeNames.Length; index++)
        {
            var name = nodeNames[index];
            settings[$"RenderNodes:Nodes:{index}:Name"] = name;
            settings[$"RenderNodes:Nodes:{index}:BaseUrl"] = $"https://{name}.example/";
            settings[$"RenderNodes:Nodes:{index}:Capacity"] = "2";
            settings[$"RenderNodes:Nodes:{index}:TlsCertSha256"] = Pin;
            if (!string.Equals(name, missingKey, StringComparison.Ordinal))
            {
                settings[RenderNodeApiKey.VariableName(name)] = $"{name}-key-value";
            }
        }

        return RenderNodeTestHost.Build(Database, clock, settings, Environments.Development);
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(
        IServiceProvider services,
        params string[] args)
    {
        await ConsoleGate.WaitAsync(Ct);

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var error = new StringWriter(CultureInfo.InvariantCulture);

        try
        {
            Console.SetOut(output);
            Console.SetError(error);

            var code = await NodesCommand.RunAsync(args, services, Ct);

            return (code, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            ConsoleGate.Release();
        }
    }

    private async Task<WorkerNode> RowAsync(string name)
    {
        await using var db = Database.CreateAppDbContext();

        return await db.WorkerNodes.SingleAsync(node => node.Name == name, Ct);
    }
}
