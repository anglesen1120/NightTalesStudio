using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Rendering;
using NightTales.Domain.Common;
using NightTales.Domain.Rendering;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Rendering;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using NightTales.Worker.Cli;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Rendering;

/// <summary>
/// The configuration seeder of 11 README D13 (11.3.3-T1 plan §7, UC4/UC10): it inserts a configured node,
/// upserts by <c>name</c> without touching a row's state or heartbeat, flips a row removed from
/// configuration to <c>down</c> without deleting it, is idempotent across runs, writes everything in one
/// <c>SaveChanges</c>, and turns a missing adapter key into the start-up exit 2 that names the variable and
/// never the value.
/// </summary>
[Trait("Category", "Integration")]
public sealed class WorkerNodeSeederTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Heartbeat = Start.AddMinutes(3);
    private const string Pin = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SeedsConfiguredNodes()
    {
        RenderNodeDatabase.Reset(Database);
        await using var db = Database.CreateAppDbContext();
        var seeder = Seeder(
            db,
            Options(Node("alpha", "https://alpha.example/", 2), Node("beta", "https://beta.example/", 4)),
            Keys(("alpha", "alpha-key-value"), ("beta", "beta-key-value")));

        (await seeder.RegisterAsync(Ct)).IsError.ShouldBeFalse();

        var rows = await db.WorkerNodes.OrderBy(node => node.Name).ToListAsync(Ct);
        rows.Count.ShouldBe(2);
        rows.Select(node => node.Name).ShouldBe(["alpha", "beta"]);
        rows.ShouldAllBe(node => node.State == WorkerNodeState.Active);
        rows.ShouldAllBe(node => node.Capacity == (node.Name == "alpha" ? 2 : 4));

        // D13/F09: in P the key comes from the secret manager, so the DB's key columns stay null.
        rows.ShouldAllBe(node => node.ApiKeyCiphertext == null && node.ApiKeyHint == null);
    }

    [Fact]
    public async Task Upsert_KeepsStateAndHeartbeat()
    {
        RenderNodeDatabase.Reset(Database);
        await using var db = Database.CreateAppDbContext();
        var existing = WorkerNode.Create("alpha", new Uri("https://old.example/"), 2, Pin, null).Value;
        existing.RecordHeartbeat(Heartbeat);
        existing.SetOperatorState(WorkerNodeState.Draining);
        db.WorkerNodes.Add(existing);
        await db.SaveChangesAsync(Ct);

        var seeder = Seeder(
            db,
            Options(Node("alpha", "https://new.example/", 6, gpu: "RTX 4090")),
            Keys(("alpha", "alpha-key-value")));

        (await seeder.RegisterAsync(Ct)).IsError.ShouldBeFalse();

        var row = await db.WorkerNodes.SingleAsync(node => node.Name == "alpha", Ct);
        row.Id.ShouldBe(existing.Id, "the upsert is in place, not a delete+insert");
        row.BaseUrl.ToString().ShouldBe("https://new.example/");
        row.Capacity.ShouldBe(6);
        row.GpuModel.ShouldBe("RTX 4090");
        row.State.ShouldBe(WorkerNodeState.Draining, "a rolling restart must not un-drain a node");
        row.LastHeartbeatAt.ShouldBe(Heartbeat, "the seeder never fakes a fresh answer");
    }

    [Fact]
    public async Task RowMissingFromConfig_GoesDown_NotDeleted()
    {
        RenderNodeDatabase.Reset(Database);
        await using var db = Database.CreateAppDbContext();
        var kept = WorkerNode.Create("alpha", new Uri("https://alpha.example/"), 2, Pin, null).Value;
        var stranded = WorkerNode.Create("retired", new Uri("https://retired.example/"), 2, Pin, null).Value;
        stranded.SetOperatorState(WorkerNodeState.Draining);
        db.WorkerNodes.AddRange(kept, stranded);
        await db.SaveChangesAsync(Ct);

        var seeder = Seeder(
            db,
            Options(Node("alpha", "https://alpha.example/", 2)),
            Keys(("alpha", "alpha-key-value")));

        (await seeder.RegisterAsync(Ct)).IsError.ShouldBeFalse();

        var rows = await db.WorkerNodes.OrderBy(node => node.Name).ToListAsync(Ct);
        rows.Count.ShouldBe(2, "a node removed from configuration keeps its row (UC10)");
        rows.Single(node => node.Name == "alpha").State.ShouldBe(WorkerNodeState.Active);
        rows.Single(node => node.Name == "retired").State.ShouldBe(
            WorkerNodeState.Down,
            "a drained row that left configuration must read down, not draining");
    }

    [Fact]
    public async Task Seeder_IsIdempotentAcrossTwoRuns()
    {
        RenderNodeDatabase.Reset(Database);
        await using var db = Database.CreateAppDbContext();
        var seeder = Seeder(
            db,
            Options(Node("alpha", "https://alpha.example/", 2), Node("beta", "https://beta.example/", 4)),
            Keys(("alpha", "alpha-key-value"), ("beta", "beta-key-value")));

        (await seeder.RegisterAsync(Ct)).IsError.ShouldBeFalse();
        var firstRun = await db.WorkerNodes.OrderBy(node => node.Name).Select(node => node.Id).ToListAsync(Ct);

        (await seeder.RegisterAsync(Ct)).IsError.ShouldBeFalse();

        var secondRun = await db.WorkerNodes.OrderBy(node => node.Name).Select(node => node.Id).ToListAsync(Ct);
        secondRun.ShouldBe(firstRun, "the second run upserts the same rows instead of inserting new ones");
    }

    [Fact]
    public async Task Seeder_AppliesEverythingInOneSaveChanges()
    {
        RenderNodeDatabase.Reset(Database);
        var clock = new FakeClock(Start);
        var counter = new SaveChangesCounter();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Database.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new AuditableSaveChangesInterceptor(clock), counter)
            .Options;

        await using var db = new AppDbContext(options, new TestCurrentUser());
        var seeder = Seeder(
            db,
            Options(Node("alpha", "https://alpha.example/", 2), Node("beta", "https://beta.example/", 4)),
            Keys(("alpha", "alpha-key-value"), ("beta", "beta-key-value")));

        (await seeder.RegisterAsync(Ct)).IsError.ShouldBeFalse();

        counter.Count.ShouldBe(1, "the seeder is all-or-nothing: one SaveChanges at the end");
        (await db.WorkerNodes.CountAsync(Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task MissingKey_FailsStartupWithExit2()
    {
        RenderNodeDatabase.Reset(Database);
        await using var db = Database.CreateAppDbContext();
        var seeder = Seeder(
            db,
            Options(Node("alpha", "https://alpha.example/", 2), Node("beta", "https://beta.example/", 4)),
            Keys(("alpha", "alpha-key-value")));

        var result = await seeder.RegisterAsync(Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(WorkerNodeSeeder.MissingApiKeyCode);
        result.FirstError.Description.ShouldContain("RENDER_NODE_BETA_API_KEY", Case.Sensitive);
        result.FirstError.Description.ShouldNotContain("alpha-key-value", Case.Sensitive);

        (await db.WorkerNodes.CountAsync(Ct)).ShouldBe(0, "keys are verified before anything is written");

        // The start-up path maps a missing key to exit 2 (plan §7): the same verb the Worker runs in Program.
        var clock = new FakeClock(Start);
        using var host = RenderNodeTestHost.Build(
            Database,
            clock,
            Settings(("alpha", "alpha-key-value"), ("beta", null)));

        WorkerNodeRegistration.Run(host.Services, Ct).ShouldBe(2, "a missing key is the start-up exit 2");
    }

    private Dictionary<string, string?> Settings(params (string Name, string? Key)[] nodes)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"ConnectionStrings:{NightTales.Infrastructure.Persistence.PersistenceOptions.ConnectionStringName}"] =
                Database.ConnectionString,
        };

        for (var index = 0; index < nodes.Length; index++)
        {
            var (name, key) = nodes[index];
            settings[$"RenderNodes:Nodes:{index}:Name"] = name;
            settings[$"RenderNodes:Nodes:{index}:BaseUrl"] = $"https://{name}.example/";
            settings[$"RenderNodes:Nodes:{index}:Capacity"] = "2";
            settings[$"RenderNodes:Nodes:{index}:TlsCertSha256"] = Pin;

            if (key is not null)
            {
                settings[RenderNodeApiKey.VariableName(name)] = key;
            }
        }

        return settings;
    }

    private static WorkerNodeSeeder Seeder(
        IAppDbContext db,
        RenderNodesOptions options,
        IConfiguration configuration) =>
        new(
            db,
            Microsoft.Extensions.Options.Options.Create(options),
            configuration,
            new FakeClock(Start),
            NullLogger<WorkerNodeSeeder>.Instance);

    private static RenderNodesOptions Options(params RenderNodeOptions[] nodes) => new() { Nodes = [.. nodes] };

    private static RenderNodeOptions Node(
        string name,
        string baseUrl,
        int capacity,
        string? gpu = null) => new()
        {
            Name = name,
            BaseUrl = baseUrl,
            Capacity = capacity,
            TlsCertSha256 = Pin,
            GpuModel = gpu,
        };

    private static IConfiguration Keys(params (string Name, string Key)[] keys) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(keys.Select(key =>
                new KeyValuePair<string, string?>(RenderNodeApiKey.VariableName(key.Name), key.Key)))
            .Build();

    /// <summary>Counts the <c>SaveChanges</c> calls of the context it is attached to (11.3.3-T1 plan §7 step 4).</summary>
    private sealed class SaveChangesCounter : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Count++;

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
