# 11.3.3 — Render node registry, health polling and dispatch
> Parent: ../README.md — facts K1–K9 / E1–E18 / P1–P13, findings F01–F28, decisions D1–D19, canonical dependency table §8.1, task template §6. Sections follow `../../00-overview.md` §10.1. Legend: ✅ read from source · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].

## 1. Scope

Story **11.3.3** (`worker_nodes` registry, health poller, node selector) — README §0/§8, D13. **In scope, 2 tasks / 1.5 d raw:** the `worker_nodes` migration + `WorkerNode` entity + `WorkerNodeSeeder` + `RenderNodeConnectionFactory` + `NodeHealthPoller` + `INodeHealthCache` + the `nts-worker nodes` verbs (**11.3.3-T1**, M); `INodeSelector`/`NodeSelector` dispatch rules, free-slot derivation from `render_jobs`, `render_worker_unavailable`, the drift alert and the D13 node metrics (**11.3.3-T2**, S).

**Out of scope:** the node, its units, firewall and pinned-certificate *generation* (11.3.1-T1/T2, 11.3.2-T1/T2 — this story only consumes `tls_cert_sha256`, F15/D11); `mpt-adapter` routes and `render-node.v1` DTOs (11.3.5-T1, `NightTales.RenderNode.Contract` — never re-declared here, F02/F03); `render_jobs` + `SubmitRenderJob`/`PollRenderJob` (09.6.1/09.6.3) and `MptRenderClient` (09.6.2-T1, which builds on `RenderNodeConnectionFactory`, CR-3); the admin settings UI that edits the adapter key (09.10, **S**; in P the column stays null — F09/D13/P4); capacity/cost measurement (11.6.3-T1, which *reads* the metrics exported here).

**Gate position:** both tasks are `P · M1` (README §8.1) in the M1 order line `11.3.5-T1 → {11.1.2-T1, 11.3.3-T1, 11.3.5-T2}` (README §5). 11.3.3-T1 **must not** depend on 09.6.3 (circular — F11); the arrow is 11.3.3 → 09.6.2-T1/09.6.3-T1.

## 2. Confirmed facts

- `worker_nodes` columns, owner `09.10.1 / 11`, in P with the key from the secret manager and `api_key_ciphertext` null: P7 (00 §6 L311), 09 README D9 L231, CR-2. Registry/poller/CLI contract: README **D13** L235–240; findings F09 (no settings writer in P), F10 (not Hangfire — P9), F11 (dependency direction), F12 (freshness/flapping), F13 (one shared pinned-TLS factory), F26 (`http://` Development-only).
- `render_jobs` states `queued → submitted → running → ingesting → succeeded | failed | cancelled`, plus `worker_node_id`: 00 §7 L332, 00 §6 L279 ✅. Free slots count **`submitted` + `running`** only (D13).
- `render_worker_unavailable` → **503**, "No worker capacity; job stays queued": `plans/09-backend-api.md` §5.4 L415 ✅; codes live in 09's closed catalogue (09 README D7, 09.1.4 L72–73) — this story adds none.
- `render-node-health.v1`: `status`, `reasons[]` ⊆ {`mpt_unreachable`, `mpt_unauthorized`, `tts_proxy_not_ok`, `image_relay_not_ok`, `font_missing`, `music_missing`, `disk_low`, `config_mismatch`, `codec_unavailable`}, `capacity { max_concurrent_tasks, active_tasks, accepting }`, `mpt`, `tts_proxy`, `image_relay`, `assets`, `gpu` (additive, F21) — 05 README D4 L211–224 ✅. `NTS_MPT_MAX_CONCURRENT ∈ 1–8` (05 README L138), so a node's `capacity` is a slot count in that range.
- 09 README D1 L87–122 (Domain → `ErrorOr` only; Application → Domain + EF core; Infrastructure → Npgsql EF + naming conventions) ✅; D8 (snake_case, `DateTimeOffset` UTC → `timestamptz`, enum `CHECK` via `builder.HasEnumCheck<TEnum>(column)`, UUIDv7 ids from `Guid.CreateVersion7()`, `IAppDbContext` DbSet per entity, `IReadDb.Query<T>()`) ✅; D9 migration name `<yyyyMMddHHmm>_<Owner>_<What>` ✅; D3 `IClock` ✅.
- `audit_logs` columns (00 §6 L297) and owner 09.3.6-T1 (`AuditBehavior`, `plans/09-backend-api.md` 09.3.6 L142–147, 09 README D9 L229) ✅. D13 fixes the actor label `system:operator` ⚠️ — no 09 row spells that literal (Open item 1).
- Pinned-cert API E6 ✅; advisory lock `pg_try_advisory_lock(hashtext('node_health_poller'))` (D13); metrics/logging rules 09.9.3 L231–236 ✅; test kit D18 ✅ and fixtures 05 D14 L346 ✅.

## 3. Domain and data

**Domain (`src/NightTales.Domain/Rendering/`)** — `ErrorOr` only (09 D1):

```csharp
public enum WorkerNodeState { Active, Draining, Down }

public sealed class WorkerNode : Entity                 // NOT IUserOwned: system table, no tenant filter (09 D10)
{
    public string Name { get; private set; }
    public Uri BaseUrl { get; private set; }                 // the node's mpt-adapter; https unless Development (F26)
    public byte[]? ApiKeyCiphertext { get; private set; }    // null in P (D13, F09)
    public string? ApiKeyHint { get; private set; }          // null in P
    public int Capacity { get; private set; }                // 1–8 slots
    public WorkerNodeState State { get; private set; }
    public DateTimeOffset? LastHeartbeatAt { get; private set; }
    public string? GpuModel { get; private set; }
    public string TlsCertSha256 { get; private set; }        // 64 upper hex, no colons (D11)

    public static ErrorOr<WorkerNode> Create(string name, Uri baseUrl, int capacity,
                                            string? tlsCertSha256, string? gpuModel);  // seeds Active; null pin only for http:// Development (T39)
    public ErrorOr<Success> RecordHeartbeat(DateTimeOffset at);      // monotonic, never backwards
    public ErrorOr<Success> MarkDown(DateTimeOffset at);             // poller only
    public ErrorOr<Success> MarkActive(DateTimeOffset at);           // poller only, after 2 good pulls
    public ErrorOr<Success> SetOperatorState(WorkerNodeState state); // Active <-> Draining only (D13 verbs)
    public ErrorOr<Success> ApplyConfig(Uri baseUrl, int capacity, string tlsCertSha256, string? gpuModel); // seeder upsert, state untouched
}
```

The poller is the only writer of `Down`/`Active`; `Draining` is operator-only; `SetOperatorState` rejects `Down` (the CLI maps that to usage exit 2 — no new error code); `MarkActive` from `Draining` is a no-op success (health does not cancel a drain). F12, D13.

**Migration `<ts>_11_WorkerNodes`** (owner 11.3.3-T1, CR-3 to 09 D9; `<ts>` = `yyyyMMddHHmm`):

```sql
CREATE TABLE worker_nodes (
    id                 uuid        NOT NULL,
    name               text        NOT NULL,
    base_url           text        NOT NULL,
    api_key_ciphertext bytea       NULL,
    api_key_hint       text        NULL,
    capacity           integer     NOT NULL,
    state              text        NOT NULL,
    last_heartbeat_at  timestamptz NULL,
    gpu_model          text        NULL,
    tls_cert_sha256    text        NULL,      -- NULL only for an http:// Development node (mock), T39
    created_at         timestamptz NOT NULL,
    updated_at         timestamptz NOT NULL,
    CONSTRAINT pk_worker_nodes PRIMARY KEY (id),
    CONSTRAINT ck_worker_nodes_state CHECK (state IN ('active','draining','down')),
    CONSTRAINT ck_worker_nodes_capacity CHECK (capacity BETWEEN 1 AND 8),
    CONSTRAINT ck_worker_nodes_tls_sha256 CHECK ((tls_cert_sha256 IS NULL AND base_url LIKE 'http://%') OR tls_cert_sha256 ~ '^[0-9A-F]{64}$')
);
CREATE UNIQUE INDEX ux_worker_nodes_name ON worker_nodes (name);
```

EF configuration (`src/NightTales.Infrastructure/Persistence/Configurations/WorkerNodeConfiguration.cs`):

```csharp
public sealed class WorkerNodeConfiguration : IEntityTypeConfiguration<WorkerNode>
{
    public void Configure(EntityTypeBuilder<WorkerNode> b)
    {
        b.ToTable("worker_nodes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired().HasMaxLength(64);
        b.HasIndex(x => x.Name).IsUnique();
        b.Property(x => x.BaseUrl).HasConversion(u => u.ToString(), s => new Uri(s)).IsRequired().HasMaxLength(256);
        b.Property(x => x.ApiKeyCiphertext);                        // bytea, null in P
        b.Property(x => x.ApiKeyHint).HasMaxLength(16);
        b.Property(x => x.Capacity).IsRequired();
        b.Property(x => x.State).HasConversion<string>().IsRequired().HasMaxLength(16);
        b.HasEnumCheck<WorkerNodeState>(nameof(WorkerNode.State));  // 09 D8 convention
        b.Property(x => x.LastHeartbeatAt);                         // timestamptz, UTC only
        b.Property(x => x.GpuModel).HasMaxLength(64);
        b.Property(x => x.TlsCertSha256).HasMaxLength(64); // null only for http:// Development nodes (T39)
        b.Property(x => x.CreatedAt).IsRequired();
        b.Property(x => x.UpdatedAt).IsRequired();
    }
}
```

No `IUserOwned` → no `"tenant"` filter and no `IgnoreQueryFilters` allow-list entry (09 D10). Ids are UUIDv7 from the domain factory; the DB never generates ids (09 D8). **No derived columns**: active tasks always come from `render_jobs` (00 §6 L311 note). One row per node — no index beyond the unique name.

## 4. Use cases

- **UC1 Operator `nodes list`** → name, base_url, capacity, state, last_heartbeat_at, gpu_model, key source (`env`/`db`); never a key value (S4).
- **UC2 Operator `nodes set-state <name> draining`** → row `draining`, dispatch stops picking it, running jobs untouched (D12 deploy precondition).
- **UC3 Operator `nodes set-state <name> active`** → row `active`; picked again once a fresh health pull exists (§7).
- **UC4 Worker start / seeder** → config nodes upserted by `name`; existing `state`/`last_heartbeat_at` untouched; a DB row missing from config → `down`, never deleted.
- **UC5 Poller, no valid answer for 120 s** → `active` → `down`; `render_node_up` and reason counters emitted.
- **UC6 Poller, a `down` node answers twice consecutively** → `active` (hysteresis, F12).
- **UC7 Dispatcher asks for a node** → node with most free slots, ties by `name` (09.6.3-T1 calls `PickAsync`).
- **UC8 Nothing dispatchable** → `render_worker_unavailable` → 503, `render_jobs.status` stays `queued`; [RULED — README §10 T60] (D20 H14) this is not an error: `SubmitRenderJob` reschedules itself after `RenderNodes:HealthPollSeconds` while the job is `queued`, outside the outbox retry budget, and an ops alert fires after `Render:QueueAlertMinutes` (360) in `queued` (09 §5.4 L415; 11 CR-3 to 09.6.3; rule in 11.6.2-T1).
- **UC9 Drift** → health `capacity.active_tasks` ≠ derived count for 3 pulls → warning + drift metric, **no** state change.
- **UC10 Node removed from `RenderNodes:Nodes`** → seeder marks it `down`; the row stays for audit.
- **UC11 Operator `set-state <name> down`** → refused, usage exit 2 (the verb set is `active|draining`).

## 5. API and events

No HTTP route and no outbox event. Operator surface = the Worker binary's argument mode (D13): `nts-worker` = `NightTales.Worker` with arguments and **no** web listener.

```csharp
// src/NightTales.Worker/Cli/NodesCommand.cs — Worker Program.cs dispatches args[0] == "nodes"
// nts-worker nodes list
// nts-worker nodes set-state <name> active|draining
static Task<int> RunAsync(string[] args, IServiceProvider services, CancellationToken ct);
```

| Verb | stdout | exit 0 | exit 1 | exit 2 |
|---|---|---|---|---|
| `nodes list` | one line per node: `name  base_url  capacity  state  last_heartbeat_at  gpu_model  key=env\|db` | ≥ 0 rows | DB/config error | bad usage |
| `nodes set-state <name> <state>` | `node <name>: <old> -> <new>` | applied (idempotent if already in that state) | unknown node, DB error | bad usage / state ∉ {`active`,`draining`} |

`set-state` writes exactly one `audit_logs` row in the **same transaction** as the change: `actor_type = 'system'`, `actor_user_id`/`subject_user_id` null, `action = 'render_node.state_changed'`, `category = 'support'`, `visibility = 'internal'`, `entity_type = 'worker_node'`, `entity_id = <uuid>`, `data jsonb = {"node","from","to","actor":"system:operator"}` (safe summary; no key/ciphertext; `ip`/`user_agent` null). Action/category/visibility are [INFERENCE] — Open item 2.

`render_worker_unavailable` is 09's code (09 §5.4 L415, closed catalogue 09.1.4 L72–73); 11.3.3-T2 only returns it through `ErrorOr`, and [RULED — README §10 T60] (D20 H14) it is a retry signal, not a failure: 09.6.3 reschedules the still-`queued` job after `RenderNodes:HealthPollSeconds` outside the outbox retry budget and alerts after `Render:QueueAlertMinutes` (360):

```csharp
// src/NightTales.Application/Abstractions/Rendering/INodeSelector.cs
public interface INodeSelector { Task<ErrorOr<WorkerNode>> PickAsync(CancellationToken ct); }
```

**Metrics** (OTel, 09.9.3 conventions; D13): `render_node_up{node}`, `render_node_free_slots{node}`, `render_node_gpu_util_pct{node}`, `render_node_gpu_mem_used_bytes{node}`, `render_node_disk_used_pct{node}`, `render_node_health_reasons_total{node,reason}`, `render_dispatch_unavailable_total` — all on the existing meter (09.9.3-T1 owns the pipeline). Split: the poller tick (T1) emits `render_node_up` + `render_node_health_reasons_total`; **T2** registers the other six (incl. `render_node_gpu_temp_celsius`, T28) as observable gauges/counters reading `INodeHealthCache` and the derived load, so a scrape never touches a node (Q1). [RULED — README §10 T57] (D20 H12) the pinned-TLS validation callback also checks the presented certificate's `NotAfter`; **11.3.3-T1** owns `render_node_cert_expiry_days{node}` (whole days to expiry, from the presented certificate; the 30-day alert rule lives in 11.6.2-T1). Cardinality is bounded by the node count; `node` is a name, never a URL or key.

## 6. UI

None in P — no admin screen touches nodes (09.10 settings UI is **S**, F09/P4). The only human surface is the §5 CLI table (stable columns, no colour, no secrets). Render progress is 09/10's concern (09.6.3 → `RenderProgress`); this story contributes no SignalR payload.

## 7. Jobs and integrations

**`NodeHealthPoller`** — Worker `BackgroundService` (not Hangfire: F10/P9; same pattern as `OutboxDispatcher`, 09 README D11):

```csharp
// src/NightTales.Worker/Rendering/NodeHealthPoller.cs
public sealed class NodeHealthPoller : BackgroundService
{
    // RenderNodes:HealthPollSeconds = 30, RenderNodes:DownAfterSeconds = 120, per-pull timeout 5 s (D13)
    protected override async Task ExecuteAsync(CancellationToken stoppingToken);
}
```

Per tick (D13, F12):
1. **Single-instance guard** — `SELECT pg_try_advisory_lock(hashtext('node_health_poller'))` on a dedicated connection; false → log once and retry next tick (Q5).
2. Read `worker_nodes` in its own DI scope via `IReadDb.Query<WorkerNode>()` (system read — no tenant filter, 09 D11 pattern).
3. Poll all nodes with **bounded** concurrency (`SemaphoreSlim` 4, `Task.WhenAll`) and a per-pull 5 s timeout; `HttpClient` from `RenderNodeConnectionFactory`; `GET /v1/health`.
4. **Valid answer** = HTTP 200 with a body parsing as `render-node-health.v1`, **or** HTTP 503 with such a body (the node answered but is not ok). Connect error, pin mismatch, timeout, 401 or an unparseable body = no answer.
5. Valid answer → `RecordHeartbeat(now)`, replace the node's `INodeHealthCache` entry, reset its failure streak; after **2** consecutive valid answers a `down` node → `MarkActive`. No answer → increment the streak, and when `now − last_heartbeat_at ≥ DownAfterSeconds` (120 s) with state `active` → `MarkDown`. `draining` is never touched.
6. Emit `render_node_up{node}` and `render_node_health_reasons_total{node,reason}` (one per parsed reason; a node that did not answer gets the local label `node_unreachable` [INFERENCE]).
7. **Drift check** — derived active count (shared `INodeLoadReader`, also used by the selector) vs `capacity.active_tasks`; 3 consecutive mismatches → warning log `{node, derived, reported}` + drift metric; never a state change.
8. One `SaveChanges` per tick (≤ one UPDATE per node). An exception inside a tick is caught and logged; the `BackgroundService` and the other Worker services keep running.

**`INodeHealthCache`** (Application interface, Infrastructure singleton; read by the selector and the T2 gauges — never re-polled per request, Q1):

```csharp
public sealed record NodeHealthSnapshot(
    string NodeName, DateTimeOffset PulledAt, bool HttpOk, string Status,
    IReadOnlyList<string> Reasons, int MaxConcurrentTasks, int ActiveTasks, bool Accepting,
    double? GpuUtilPct, long? GpuMemUsedMb, double? DiskUsedPct);

public interface INodeHealthCache
{
    void Set(NodeHealthSnapshot snapshot);
    bool TryGet(string nodeName, out NodeHealthSnapshot? snapshot);
    IReadOnlyCollection<NodeHealthSnapshot> All();
}
```

**`RenderNodeConnectionFactory`** (Infrastructure, D13/F13; shared with 09.6.2-T1 by CR-3):

```csharp
public interface IRenderNodeConnectionFactory { HttpClient Create(WorkerNode node); }
public sealed class RenderNodeConnectionFactory : IRenderNodeConnectionFactory { ... }   // Infrastructure
// SocketsHttpHandler:
//   SslOptions.RemoteCertificateValidationCallback = (_, cert, _, _) =>
//       cert is not null && string.Equals(cert.GetCertHashString(HashAlgorithmName.SHA256),
//                                         node.TlsCertSha256, StringComparison.OrdinalIgnoreCase);
//   the same callback also rejects an expired certificate (NotAfter < now) and emits render_node_cert_expiry_days{node} — H12/T57
//   AllowAutoRedirect = false; PooledConnectionLifetime = 5 min; UseProxy = false [INFERENCE: D11 uses a firewall, no proxy]
// DefaultRequestHeaders["x-api-key"] = <RENDER_NODE_<NAME>_API_KEY>; Timeout = 5 s (callers may override)
// Create() throws when scheme != https or the pin is missing, unless ASPNETCORE_ENVIRONMENT == Development (F26, D14 `mock`)
```

**`WorkerNodeSeeder`** — runs at Worker start and before the first CLI verb: binds `RenderNodes:Nodes:[{Name, BaseUrl, Capacity, TlsCertSha256, GpuModel}]`, upserts by `name` via `ApplyConfig` (state/heartbeat kept), marks config-absent rows `down` (never deletes), verifies `RENDER_NODE_<NAME>_API_KEY` (upper-snake of `Name`, from the secret manager). A missing key fails startup with exit **2**, naming the variable only [INFERENCE: exit-code convention 01 D9].

**Integration edges:** 09.6.2-T1 consumes `IRenderNodeConnectionFactory`; 09.6.3-T1 consumes `INodeSelector`; 11.3.1-T3 (`nts-node deploy`) calls `nts-worker nodes set-state` for drain/activate (D12); 11.6.2-T1/11.6.3-T1 consume the metrics. Nothing here calls MPT or the adapter, and no node port is opened (S1, D11).

## 8. Security and privacy

- **S3 (D13/D19):** every Worker → node call goes through `RenderNodeConnectionFactory` with an exact SHA-256 pin; `http://` or a missing pin is refused outside `Development` (F26, D14 `mock` node). No other code path may build a node `HttpClient` (asserted by a test).
- **S4:** the key is read from the environment/secret manager into the handler's default header — never argv, logs, `audit_logs.data` or the bundle; `nodes list` prints `key=env|db` only; `ApiKeyHint`/ciphertext are never written by this story (null in P, F09); TLS errors log the node name and fingerprints, not the key or body.
- **Least privilege / no tenant filter:** `worker_nodes` is system data (09 D10) — no `IUserOwned`, no `IgnoreQueryFilters`; the CLI is operator-only (app host or bastion, never HTTP).
- **Deny by default:** any failure to get a valid answer removes the node from dispatch within 120 s; a pin mismatch is a failure, never a fallback to unvalidated TLS; exactly one pull per node per interval (no retry storm).
- **Bounded work (Q1/Q5):** 30 s interval, advisory-lock single instance, 5 s per-pull timeout, one DB query per selector call, memory-only cache (reasons, counters, gauges — no secrets, and node names/GPU model are not user data; S7/09.9.3 L232).

## 9. Test plan

DB/HTTP tests live in `tests/NightTales.Api.IntegrationTests` (Worker jobs run in-process there, 09 README D2); pure logic in `NightTales.Domain.UnitTests` / `NightTales.Application.UnitTests`; fakes are hand-written from `NightTales.TestKit` — **no mocking library** (09 README D1).

| Suite (class) | Representative tests | Asserts |
|---|---|---|
| `NightTales.Domain.UnitTests/Rendering/WorkerNodeStateTransitionTests` | `SetOperatorState_RejectsDown`, `MarkActive_FromDraining_IsNoOp`, `RecordHeartbeat_NeverMovesBackwards`, `Create_RejectsBadSha256` | D13 state rules, F12 |
| `NightTales.Api.IntegrationTests/Rendering/WorkerNodesMigrationTests` (real PostgreSQL 17 via `PostgresFixture`, `NTS_TEST_PG` — no Testcontainers, 11 CR-16) | `Migration_CreatesWorkerNodesWithConstraints`, `UniqueName_RejectsDuplicate`, `CheckRejectsUnknownState`, `DownMigration_DropsTable` | 00 §6 L311 columns, D13 constraints |
| `.../WorkerNodeSeederTests` | `SeedsConfiguredNodes`, `Upsert_KeepsStateAndHeartbeat`, `RowMissingFromConfig_GoesDown_NotDeleted`, `MissingKey_FailsStartupWithExit2` | D13, UC4/UC10 |
| `.../RenderNodeConnectionFactoryTests` (self-signed cert made in-test ⚠️) | `AcceptsExactlyPinnedSha256`, `RejectsOtherCertificate`, `RefusesHttpOutsideDevelopment`, `AllowsHttpInDevelopment`, `DisablesRedirects`, `RejectsExpiredCertificate`, `EmitsCertExpiryDaysGauge`, `NeverLogsKey` | D13, F26, S3/S4 [RULED — README §10 T57] (D20 H12) |
| `.../NodeHealthPollerTests` (scripted `HttpMessageHandler`) | `Http200_StoresHeartbeatAndCache`, `Http503WithValidBody_CountsAsAnswer`, `Down_After120sWithoutAnswer`, `Up_AfterTwoConsecutiveGoodPulls`, `Draining_NeverChangedByPoller`, `SecondInstance_DoesNotPoll`, `DriftAlert_AfterThreePulls` | D13, F10/F12, Q5, UC5/UC6/UC9 |
| `.../NodeSelectorTests` (and `NodeSelectorQueryTests`) | `PicksHighestFreeSlots`, `TieBreak_ByName`, `IgnoresStaleHealth_Over90s`, `IgnoresNotHttpOk_503`, `CapsByHealthMaxConcurrentTasks`, `CountsOnlySubmittedAndRunning`, `NoCandidate_ReturnsRenderWorkerUnavailable`, `SingleQueryPerPick` | D13, 00 §7 L332, 09 §5.4 L415 |
| `.../NodesCommandTests` | `List_PrintsNoKeyValue`, `SetState_WritesAuditRow`, `SetState_Down_Exit2`, `SetState_Idempotent_Exit0` | §5, 09.3.6-T1 row shape |
| Fixture replay `NightTales.RenderNode.Tests` (D18, 05 D14 L346) | `render-node-health.v1.{ok,proxy-down,disk-low}.json` through `RenderNodeJson.Tolerant` → `NodeHealthSnapshot` | the poller parses the real wire shape |

No GPU, no real node, no network in CI (D18); every HTTP test uses a scripted handler or `TestServer`.

## 10. Tasks and re-estimate

| Task | Title | Area | Size | Depends on | Blocks |
|---|---|---|---|---|---|
| 11.3.3-T1 | `worker_nodes` registry, pinned-TLS connection factory and health poller | BE | M (1.0) | 09.2.2-T2, 11.3.5-T1, 09.3.6-T1 (09 W2 CR-8) | 11.3.3-T2, 11.3.1-T3, 09.6.2-T1 |
| 11.3.3-T2 | `INodeSelector` dispatch rules and node metrics | BE | S (0.5) | 11.3.3-T1 | 09.6.3-T1, 11.6.2-T1 |

**Re-estimate: 1.5 d raw** (T1 1.0 + T2 0.5, unchanged from the README §8 mapping "11.3.3-T1 M → T1 M + T2 S (new)"), **1.8 d buffered** at the README's 1.2 factor. No new external dependency, licence or price; plan 11's 17.75 → 23.0 d re-estimate is unaffected by this folder.

**Open items**
1. [RULED — README §10 T5] `system:operator` (D13) has no home in `audit_logs`: 00 §6 L297 allows `actor_type ∈ {user, admin, system}` and 09.3.6 L143–145 fixes the row shape but no actor-label column. Proposal: `actor_type = 'system'` + `data.actor = 'system:operator'` — confirm or define it in 09.3.6-T1.
2. [RULED — README §10 T5] No source fixes the node verbs' audit `action` (`render_node.state_changed`), `category` (`support`) or `visibility` (`internal`) — confirm with 09.3.6-T1.
3. [RULED — README §10 T7] D13 gives 30 s / 120 s / 90 s as literals but names no key for the 90 s freshness window. Proposal: `RenderNodes:HealthStaleSeconds`.
4. [RULED — README §10 T26] An unreachable node has no wire reason (the 05 D4 `reasons` set is only sent by a node that answered), so `render_node_health_reasons_total` needs a local label (proposed `node_unreachable`) — confirm with 11.3.5-T4.
5. [RULED — README §10 T6] D13 fixes no reservation: two picks in one tick can both choose the last slot; the slot is consumed by 09.6.3-T1's `submitted` insert and an over-pick surfaces as adapter 429 `adapter_capacity_full` (05 F07). Confirm that a pick needs no reservation.
6. [RULED — README §10 T28 and D13] Metric details not fixed by D13: which task emits each instrument (split in §5), the drift metric name (proposed `render_node_active_tasks_drift_total`), and whether the 05 D4 `gpu`/`disk` fields map one-to-one (`mem_used_mb` → `render_node_gpu_mem_used_bytes`).
7. `[INFERENCE]` `capacity` bound 1–8 taken from `NTS_MPT_MAX_CONCURRENT ∈ 1–8` (05 README L138); D13/00 §6 give no bound.
8. `[INFERENCE]` Names not in any source: `WorkerNodeState`, `NodeHealthSnapshot` and `INodeHealthCache` members, `IRenderNodeConnectionFactory`, `NodeSelector`, `NodesCommand`, `INodeLoadReader`, `created_at`/`updated_at` on `worker_nodes`, the `RenderNodes:Nodes` → entity binding, `UseProxy = false`.
9. [RULED — README §10 T9] Seeder startup exit code 2 for a missing node key is inferred from 01 D9; D13 fixes only the key name.

## 11. Rollout

- **Order:** `09.2.2-T2` + `11.3.5-T1` → **11.3.3-T1** → **11.3.3-T2** → {`09.6.2-T1`, `09.6.3-T1`}; 11.3.1-T3 (`nts-node deploy`) may start once T1 lands (README §8.1, §5 M1 order).
- **No feature flag, no user-visible surface.** Both tasks are `P · M1` (flag column empty); the only operational switch is the DB `state`, driven by the CLI verbs.
- **Environments:** `Development` registers node `mock` at `http://mock-mpt:8090` (Development only, D14/F26); staging/prod register the real node as `https://…` with `RenderNodes:Nodes[i]:TlsCertSha256` (F15/D11). Staging has no GPU node by default (D15/F08), so `render_dispatch_unavailable_total` is expected non-zero there.
- **Migration rollout:** expand-only, applied by the migration bundle before the Worker rollout (D16); it creates one empty table, safe next to a running old Worker.
- **Verification at rollout:** `nodes list` shows every configured node once; with the node stopped `render_node_up{node}=0` and the row flips to `down` within 120 s, returning to `active` after 2 valid pulls; a render submitted while the only node is `draining` stays `queued` and the API answers 503 `render_worker_unavailable`; `set-state <name> draining` appears in `audit_logs`; the `11-node` gate row (README §7) confirms the adapter health the poller reads.
- **Rollback:** revert the Worker image (poller/selector gone, `worker_nodes` rows inert); reverting the migration is safe — registry rows are re-seedable from `RenderNodes:Nodes` and hold no user data; a `draining` row left by a failed deploy is cleared with `nodes set-state <name> active` (D12's `activate` wrapper).
