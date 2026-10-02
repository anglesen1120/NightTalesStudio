using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using NightTales.Domain.Rendering;
using NightTales.Infrastructure.Rendering;

namespace NightTales.Worker.Cli;

/// <summary>
/// The operator surface of the render-node registry (11 README D13, plan §5): <c>nts-worker nodes list</c>
/// and <c>nts-worker nodes set-state &lt;name&gt; active|draining</c>, run as the Worker binary in argument
/// mode with no web listener (D13/D12).
/// </summary>
/// <remarks>
/// <para>
/// Both verbs run <see cref="WorkerNodeSeeder"/> first, so the registry matches <c>RenderNodes:Nodes</c>
/// before an operator reads or changes it (plan §7). The exit codes are plan §5's: <c>0</c> applied or
/// listed, <c>1</c> an unknown node or a configuration/database failure, <c>2</c> bad usage — including
/// <c>set-state &lt;name&gt; down</c>, which is health's decision and not an operator one.
/// </para>
/// <para>
/// Nothing secret ever reaches stdout or stderr: <c>list</c> prints the key's <em>source</em>
/// (<c>env</c>/<c>db</c>) and never a value, a hint or a ciphertext, and <c>set-state</c> writes only the node
/// name and the two states (11 README S4).
/// </para>
/// </remarks>
public static class NodesCommand
{
    /// <summary>The verb an operator types (<c>nts-worker nodes …</c>).</summary>
    public const string Verb = "nodes";

    /// <summary>The applied/listed exit code (plan §5).</summary>
    public const int Success = 0;

    /// <summary>The unknown-node / configuration / database exit code (plan §5).</summary>
    public const int Failure = 1;

    /// <summary>The bad-usage exit code (plan §5).</summary>
    public const int Usage = 2;

    private const string ListVerb = "list";
    private const string SetStateVerb = "set-state";
    private const string ActiveLiteral = "active";
    private const string DrainingLiteral = "draining";
    private const string Action = "render_node.state_changed";
    private const string OperatorActor = "system:operator";

    /// <summary>Usage line, printed on stderr for a malformed invocation.</summary>
    public const string UsageLine = "usage: nts-worker nodes list | nts-worker nodes set-state <name> active|draining";

    /// <summary>
    /// Runs the <c>nodes</c> verb. <paramref name="args"/>[0] is <c>nodes</c>; the caller (the Worker's
    /// <c>Program</c>) has already matched it.
    /// </summary>
    /// <param name="args">The process arguments.</param>
    /// <param name="services">The built host's service provider.</param>
    /// <param name="cancellationToken">The process's cancellation token.</param>
    /// <returns>The process exit code of plan §5.</returns>
    public static async Task<int> RunAsync(
        string[] args,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(services);

        if (args.Length < 2)
        {
            return BadUsage();
        }

        return args[1] switch
        {
            ListVerb when args.Length == 2 => await ListAsync(services, cancellationToken),
            SetStateVerb when args.Length == 4 => await SetStateAsync(args[2], args[3], services, cancellationToken),
            _ => BadUsage(),
        };
    }

    /// <summary>
    /// Prints one line per registered node — name, base URL, capacity, state, last heartbeat, GPU model and
    /// the key's source — with no secret in it (plan §5 UC1).
    /// </summary>
    private static async Task<int> ListAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();

        var seed = await SeedAsync(scope, cancellationToken);
        if (seed != Success)
        {
            return seed;
        }

        try
        {
            var nodes = await scope.ServiceProvider.GetRequiredService<INodeRegistry>()
                .AllAsync(cancellationToken);

            foreach (var node in nodes)
            {
                Console.Out.WriteLine(Line(node));
            }

            return Success;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine($"nts-worker nodes list failed: {exception.Message}");

            return Failure;
        }
    }

    /// <summary>
    /// Applies <c>active</c> or <c>draining</c> to one node and records exactly one internal
    /// <c>audit_logs</c> row in the same transaction as the change (plan §5, 09.3.6-T1).
    /// </summary>
    private static async Task<int> SetStateAsync(
        string name,
        string literal,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var target = literal switch
        {
            ActiveLiteral => WorkerNodeState.Active,
            DrainingLiteral => WorkerNodeState.Draining,
            // `down` is health's decision; the CLI treats it as bad usage rather than inventing a code.
            _ => (WorkerNodeState?)null,
        };

        if (target is null)
        {
            return BadUsage();
        }

        using var scope = services.CreateScope();

        var seed = await SeedAsync(scope, cancellationToken);
        if (seed != Success)
        {
            return seed;
        }

        try
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var node = await db.WorkerNodes.SingleOrDefaultAsync(row => row.Name == name, cancellationToken);
            if (node is null)
            {
                Console.Error.WriteLine($"nts-worker nodes set-state: unknown render node '{name}'.");

                return Failure;
            }

            var before = node.State;
            if (before == target.Value)
            {
                // Idempotent: the verb succeeds, prints the same state twice and writes no audit row, because
                // nothing was applied (plan §5).
                Console.Out.WriteLine(Transition(name, before, target.Value));

                return Success;
            }

            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();

            await unitOfWork.BeginAsync(cancellationToken);

            var applied = node.SetOperatorState(target.Value);
            if (applied.IsError)
            {
                await unitOfWork.RollbackAsync(cancellationToken);

                return BadUsage();
            }

            // The old/new states only: the payload carries no key, no hint and no ciphertext (S4). The
            // operator label is the `data.actor` merge of 09 README BD13.
            var data = new JsonObject
            {
                ["node"] = name,
                ["from"] = Wire(before),
                ["to"] = Wire(target.Value),
            };

            await audit.WriteAsync(
                new AuditEntry(
                    ActorUserId: null,
                    ActorType: ActorType.System,
                    SubjectUserId: null,
                    Action: Action,
                    Category: Category.Support,
                    Visibility: Visibility.Internal,
                    EntityType: "worker_node",
                    EntityId: node.Id,
                    DataJson: data.ToJsonString(),
                    Ip: null,
                    UserAgent: null,
                    ActorLabel: OperatorActor,
                    OccurredAt: clock.UtcNow),
                cancellationToken);

            var committed = await unitOfWork.CommitAsync(cancellationToken);
            if (committed.IsError)
            {
                Console.Error.WriteLine($"nts-worker nodes set-state failed: {committed.FirstError.Description}");

                return Failure;
            }

            Console.Out.WriteLine(Transition(name, before, target.Value));

            return Success;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine($"nts-worker nodes set-state failed: {exception.Message}");

            return Failure;
        }
    }

    /// <summary>
    /// Runs the seeder before a verb, so a fresh row is listed and an operator's change lands on the row the
    /// configuration describes (plan §7). A missing adapter key is exit 2 (the start-up convention), any
    /// other refusal or a seeder failure inside this boundary is exit 1 — the plan §5 table maps every
    /// configuration/database failure, so a throwing seed must not escape the verb's exit code.
    /// </summary>
    private static async Task<int> SeedAsync(IServiceScope scope, CancellationToken cancellationToken)
    {
        var seeder = scope.ServiceProvider.GetRequiredService<WorkerNodeSeeder>();

        try
        {
            var seeded = await seeder.RegisterAsync(cancellationToken);

            if (!seeded.IsError)
            {
                return Success;
            }

            foreach (var error in seeded.Errors)
            {
                Console.Error.WriteLine($"nts-worker: {error.Description}");
            }

            return WorkerNodeRegistration.CodeFor(seeded.Errors);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The same wording WorkerNodeRegistration.Run uses for a start-up failure: the variable or
            // configuration path may be named, never a key value (S4).
            Console.Error.WriteLine($"nts-worker: render node registration failed: {exception.Message}");

            return Failure;
        }
    }

    private static int BadUsage()
    {
        Console.Error.WriteLine(UsageLine);

        return Usage;
    }

    private static string Transition(string name, WorkerNodeState before, WorkerNodeState after) =>
        $"node {name}: {Wire(before)} -> {Wire(after)}";

    /// <summary>The one line of <c>nodes list</c>: the plan §5 columns, in that order, with no secret.</summary>
    private static string Line(WorkerNode node) =>
        string.Join(
            "  ",
            node.Name,
            node.BaseUrl.ToString(),
            node.Capacity.ToString(CultureInfo.InvariantCulture),
            Wire(node.State),
            node.LastHeartbeatAt?.ToString("O", CultureInfo.InvariantCulture) ?? "-",
            node.GpuModel ?? "-",
            $"key={(node.ApiKeyCiphertext is null ? "env" : "db")}");

    /// <summary>The stored wire literal of a state (<c>active</c>/<c>draining</c>/<c>down</c>).</summary>
    private static string Wire(WorkerNodeState state) =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(state.ToString());
}
