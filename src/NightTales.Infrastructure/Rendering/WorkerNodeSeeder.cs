using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Rendering;
using NightTales.Domain.Common;
using NightTales.Domain.Rendering;

namespace NightTales.Infrastructure.Rendering;

/// <summary>
/// Registers the nodes of <c>RenderNodes:Nodes</c> in <c>worker_nodes</c> (11 README D13, F09): in P there
/// is no settings UI, so this seeder — run at Worker start and before the first <c>nts-worker nodes</c> verb —
/// is the only registration path.
/// </summary>
/// <remarks>
/// <para>
/// The upsert is keyed on <c>name</c>: a configured node is inserted (<c>Create</c>, state
/// <c>active</c>) or updated in place (<c>ApplyConfig</c>) with its <c>state</c> and
/// <c>last_heartbeat_at</c> untouched, so a rolling restart can neither un-drain a node nor fake a fresh
/// answer. A row that is no longer in the configuration is flipped to <c>down</c> — never deleted — so the
/// registry keeps its history (UC10).
/// </para>
/// <para>
/// Each node's adapter key is verified against the secret manager's variable
/// (<see cref="RenderNodeApiKey"/>) <em>before</em> anything is written: a missing key is a start-up
/// failure (exit 2 for the Worker, 1 for a CLI verb) naming the variable and never a value (11 README S4).
/// Every entry is also validated up front through <see cref="WorkerNode.Create"/>, so a bad capacity or pin
/// fails the whole run with nothing written.
/// </para>
/// </remarks>
/// <param name="db">The scope's tracked write model; the seeder is the only writer of the registry's shape.</param>
/// <param name="options">The bound <c>RenderNodes</c> section.</param>
/// <param name="configuration">The secret accessor entry point: <c>RENDER_NODE_&lt;NAME&gt;_API_KEY</c>.</param>
/// <param name="clock">The Worker's clock, so the stranded rows' <c>updated_at</c> is testable (09 README D3).</param>
/// <param name="logger">One line per run: how many rows were inserted, updated and stranded.</param>
public sealed partial class WorkerNodeSeeder(
    IAppDbContext db,
    IOptions<RenderNodesOptions> options,
    IConfiguration configuration,
    IClock clock,
    ILogger<WorkerNodeSeeder> logger)
{
    /// <summary>The failure of a configured node whose adapter key is absent (exit 2 at start-up).</summary>
    public const string MissingApiKeyCode = "render_node_api_key_missing";

    /// <summary>The failure of a configuration that names the same node twice.</summary>
    public const string DuplicateNameCode = "render_node_name_duplicate";

    /// <summary>Applies <c>RenderNodes:Nodes</c> to the registry. All-or-nothing: one <c>SaveChanges</c> at the end.</summary>
    /// <param name="ct">The host's start-up (or CLI) cancellation token.</param>
    /// <returns><c>Success</c>, or the first configuration failure — the caller maps it to its exit code.</returns>
    public async Task<ErrorOr<Success>> RegisterAsync(CancellationToken ct)
    {
        var configured = options.Value.Nodes;
        var desired = new List<WorkerNode>(configured.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in configured)
        {
            var name = candidate.Name?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                return Error.Validation(WorkerNode.NameInvalidCode, "Every RenderNodes:Nodes entry must carry a name.");
            }

            if (!names.Add(name))
            {
                return Error.Validation(DuplicateNameCode, $"The render node '{name}' is declared more than once in RenderNodes:Nodes.");
            }

            var variable = RenderNodeApiKey.VariableName(name);
            if (string.IsNullOrWhiteSpace(configuration[variable]))
            {
                // Only the variable's name is reported: the value never reaches a message (S4).
                return Error.Validation(
                    MissingApiKeyCode,
                    $"The render node '{name}' has no adapter key: the configuration value '{variable}' is required.",
                    new Dictionary<string, object> { ["variable"] = variable });
            }

            if (!Uri.TryCreate(candidate.BaseUrl?.Trim(), UriKind.Absolute, out var baseUrl))
            {
                return Error.Validation(
                    WorkerNode.BaseUrlInvalidCode,
                    $"The render node '{name}' base URL is not an absolute URL: RenderNodes:Nodes[i]:BaseUrl.");
            }

            var fresh = WorkerNode.Create(name, baseUrl, candidate.Capacity, candidate.TlsCertSha256, candidate.GpuModel);
            if (fresh.IsError)
            {
                return fresh.Errors;
            }

            desired.Add(fresh.Value);
        }

        var existing = await db.WorkerNodes.ToListAsync(ct);
        var byName = existing.ToDictionary(node => node.Name, StringComparer.Ordinal);
        var created = 0;
        var updated = 0;

        foreach (var node in desired)
        {
            if (byName.TryGetValue(node.Name, out var row))
            {
                // The validated values of the fresh aggregate are applied to the existing row, which keeps
                // its state and its last heartbeat (the upsert rule of D13).
                var applied = row.ApplyConfig(node.BaseUrl, node.Capacity, node.TlsCertSha256, node.GpuModel);
                if (applied.IsError)
                {
                    return applied.Errors;
                }

                updated++;
                continue;
            }

            db.WorkerNodes.Add(node);
            created++;
        }

        var stranded = 0;
        foreach (var row in existing.Where(node => !names.Contains(node.Name)))
        {
            // A node removed from configuration is marked down, never deleted (UC10).
            row.MarkDown(clock.UtcNow);
            stranded++;
        }

        await db.SaveChangesAsync(ct);

        LogSeeded(created, updated, stranded);

        return Result.Success;
    }

    [LoggerMessage(
        EventId = 2300,
        Level = LogLevel.Information,
        Message = "render-node.seeder.completed created={Created} updated={Updated} down={Down}")]
    private partial void LogSeeded(int created, int updated, int down);
}
