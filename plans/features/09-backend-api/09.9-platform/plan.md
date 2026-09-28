# 09.9.2 (read side), 09.9.3, 09.9.4, 09.9.5 — Platform: feature-flag read side, observability, hardening, OpenAPI contract gate
> Parent: ../README.md (Part B)

## 1. Scope

Wave 2 (**P**) platform slice of epic 09.9: the feature-flag **read** service, the logging/tracing/metrics pipeline, security hardening, and the OpenAPI contract gate. Planning only — no code.

**Deliverable tasks (this folder).** `09.9.2-T2`, `09.9.3-T1`, `09.9.3-T2`, `09.9.4-T1`, `09.9.5-T1` — 3.5 d raw (B8.1, B9).

**In scope.**
- `09.9.2-T2` (BD13, WF04, W16): `IFeatureFlags.IsEnabledAsync(string key, ct)` over `feature_flags` (00 §6), 30 s cache + eviction on `FeatureFlagsChanged`, endpoint filter `RequireFeature("<key>")` → **404 `feature_disabled`** (09 §5.4; `plans/09-backend-api.md:423`), operator CLI `nts-worker flags set <key> on|off` + audit (`support`/`internal`). Consumed by 02.7, 02.8.3 and 06 (BD1).
- `09.9.3-T1` (BD12, WF09): Serilog compact JSON to stdout + `Serilog.Sinks.OpenTelemetry` (W4); OpenTelemetry traces/metrics AspNetCore + HttpClient (W2) + `Npgsql.OpenTelemetry` (W3) and **no** EF Core instrumentation (WF09); Hangfire job spans; OTLP export to the collector owned by 11 D17; `/health/live`, `/health/ready` (BD12); the five custom metrics frozen for 11.6.2-T1.
- `09.9.3-T2`: redaction of `access_token`, `ticket`, `token`, `code`, `state` in logs **and** spans (`url.query`/`url.full`), plus a capture test (09 §4 09.9.3 second bullet; `plans/09-backend-api.md:233`).
- `09.9.4-T1` (BD12, WF15, W1): security headers, CORS = `App:PublicOrigin` only, request-size limits, the Data Protection key ring persisted in PostgreSQL and encrypted with the `DP_KEK` certificate, `ITokenProtector` (purpose `tokens.v1/{connectionId}`), NuGet audit + a `dotnet list package --vulnerable` CI step (W10).
- `09.9.5-T1` (BD12, D14, W11): the `oasdiff breaking` gate in `.github/workflows/backend.yml` against `main`, the versioning rule for accepted breaking changes, and the `ProblemDetails` schema component in the generated document.

**Out of scope (owned elsewhere — do not duplicate).**
- 09.9.1 admin endpoints and the flag **write** API, plus `/internal/jobs` (Hangfire dashboard) hosting: `09.9.1` is **S** (W17); the dashboard itself is `09.2.6` (wave 1, signed). This folder only adds the job-span filter and the dashboard's access-log line.
- `SecretString` log masking policy and `system_settings` purposes: `09.10.1-T1` (S). `09.9.3-T2` adds the query/property redaction set only.
- Rate limiting policies `auth`/`paid`/`hub`: `09.3.5-T1` (BD14).
- The Angular client and its generator: plan 10 (`09.9.5-T1` publishes the spec and the gate; `10.1.4` regenerates).
- The second *test table* for cross-tenant checks: `09.2.3-T2` (S) — it consumes the gate, it does not own it.

**Consumers that make this folder hard-gated:** 02.7 / 02.8.3 (`feature_disabled`), 06 (flags), 11.6.2-T1 (metrics, traces, alerts), `09.10.4-T1` (DP key ring; the TOTP store uses `IDataProtectionProvider.CreateProtector("identity.totp.v1")` directly, **not** `ITokenProtector` **[RULED — README §B10 WT43]**), `09.2.3-T2` + `10.1.4` (spec diff).

## 2. Confirmed facts

| Id | Fact used here |
|---|---|
| W1 | `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` **10.0.12**, MIT, net10.0; `PersistKeysToDbContext<TContext>()` requires the context to implement `IDataProtectionKeyContext` |
| W2 | OpenTelemetry .NET: Extensions.Hosting + OTLP exporter **1.19.1**, Instrumentation.AspNetCore + .Http **1.19.0** (stable), Apache-2.0; **`Instrumentation.EntityFrameworkCore` has never had a stable release** (latest `1.19.1-beta.1`) |
| W3 | `Npgsql.OpenTelemetry` **10.0.3** stable emits PostgreSQL spans (`AddNpgsql()`) |
| W4 | `Serilog.Sinks.OpenTelemetry` **4.2.0** Apache-2.0; `Serilog.Formatting.Compact` 3.0.0 |
| W10 | `NuGetAuditMode` defaults to `all` for net10.0; `dotnet list package --vulnerable` is available in CI |
| W11 | oasdiff: Apache-2.0, "detect breaking changes in OpenAPI specs" |
| W16 | 02.7/02.8.3 answer **404 `feature_disabled`** by flag; `feature_flags` rows are seeded by `09.2.5-T1` |
| W17 | P has one seeded owner and **no public sign-up**; `09.9.1` (admin, incl. the flag write API) is **S** |
| WF04 | No flag read service in P → **09.9.2-T2** (BD13) |
| WF09 | 09 named EF Core instrumentation, which is pre-release only → DB spans via `Npgsql.OpenTelemetry` (BD12) |
| WF15 | Data Protection key-ring encryption at rest needs a concrete mechanism → `PersistKeysToDbContext<AppDbContext>()` + `ProtectKeysWithCertificate` with the PFX from secret `DP_KEK` (BD12) |
| BD12 | Serilog + OTel + Hangfire spans + OTLP to the 11 D17 collector; `/health/live`, `/health/ready`; security headers; CORS = `App:PublicOrigin`; `MaxRequestBodySize` 1 MiB default; DP per WF15; `ITokenProtector.Protect(string purposeSuffix, string plaintext)`; `oasdiff breaking` against `main` in CI |
| BD13 | `IFeatureFlags.IsEnabledAsync(string key, ct)`, cache 30 s, `FeatureFlagsChanged` evicts; `RequireFeature("youtube_publish")` → 404 `feature_disabled`; `nts-worker flags set <key> on|off` + audit (`support`, `internal`) |
| BD16 | No secret/token/e-mail/IP in logs; health endpoints read caches only; no N+1; bounded work |
| D11 | `outbox_lag_seconds` and `outbox_dead_letters_total` belong to the outbox dispatcher; `trace_parent` is copied onto every outbox row |
| D13 | Harness: `ApiFactory`, `TestAuthHandler`, `WorkerHost`, `InlineJobScheduler`, `FakeClock`, `PostgresFixture` (real PostgreSQL 17 on `NTS_TEST_PG`; 09 W3 CR-35); no mocking library — hand-written fakes |
| D14 | The OpenAPI document is generated at build to `src/NightTales.Api/openapi.json`; `09.9.5` owns the contract diff |
| N12 | `Serilog.AspNetCore` **10.0.0** is already in the `NightTales.Api` package list — `09.9.3-T1` adds sinks/formatters and the OTel packages, it does not re-add Serilog |
| 00 §6:306 | `feature_flags(key, enabled, rules jsonb)` — created by the `09.2.2` `InitialCore` migration, seeded by `09.2.5-T1` |
| 00 §6:305 | `audit_logs(... category ∈ {account_security, episodes, publishing, billing, support}, visibility ∈ {user, internal} …)` |
| 00 §6:271 | `data_protection_keys(friendly_name, xml)` — ASP.NET Core Data Protection key ring, owner `09.9.4-T1` (D9, W2 CR-5) |
| 11 D17 / O6 | Apps export OTLP to an OpenTelemetry Collector in the app network; backend chosen in 11.2.1 |
| 11 §10:284 | `DP_KEK` is a **shared** secret at `nts/<env>/shared/DP_KEK`, readable by api and worker; exported once to the owner's offline store, never in DB backups |
| 11 §10:242 | `nts-worker` = `NightTales.Worker` invoked with arguments, **no web listener**; `nodes list` / `nodes set-state` already exist, writing `audit_logs` as `system:operator` (11.3.3-T1) |
| 11.6.2-T1:71 | Names are frozen by their owners: app metrics `render_jobs_active`, `render_duration_seconds`, `openai_cost_usd_total`, `youtube_quota_used`, `outbox_lag_seconds`, plus HTTP server metrics for latency/5xx (09.9.3); a test `traceId` from the Api and a job span from the Worker must be visible in the backend |

## 3. Domain and data

**No new aggregate, no new table in 09.9.2-T2 / 09.9.3-T1 / 09.9.3-T2 / 09.9.5-T1.** Two existing tables are read, one is created.

- `feature_flags` (00 §6:306) — read-only here. EF: `FeatureFlag` entity, `DbSet<FeatureFlag> FeatureFlags` on `IAppDbContext`/`AppDbContext` (`key` PK, `enabled bool`, `rules jsonb` nullable, snake_case per D8). The row set is created by the `09.2.2-T2` `InitialCore` migration and seeded by `09.2.5-T1` (B8.1 dependency). **Not `IUserOwned`** → the tenant filter (D10) does not apply; the table is platform-wide by design, and only the read service and the operator CLI touch it.
- `data_protection_keys` (00 §6:271) — created by `09.9.4-T1`, migration `<yyyyMMddHHmm>_09_DataProtectionKeys` (D9 naming rule):

```csharp
public sealed class DataProtectionKeyConfiguration : IEntityTypeConfiguration<DataProtectionKey>
{
    public void Configure(EntityTypeBuilder<DataProtectionKey> b)
    {
        b.ToTable("data_protection_keys");
        b.HasKey(k => k.Id);
        b.Property(k => k.FriendlyName).HasColumnName("friendly_name").HasMaxLength(256).IsRequired();
        b.Property(k => k.Xml).HasColumnName("xml").IsRequired();          // ciphertext: the DP key ring is itself DP-encrypted
        b.HasIndex(k => k.FriendlyName).IsUnique();
    }
}
```

  `AppDbContext` gains `IDataProtectionKeyContext` with `DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();` (W1). `ReadDbContext` never reads it. The `xml` column stores the key-ring payload already encrypted with the `DP_KEK` certificate (WF15) — a DB dump alone is not enough to unprotect tokens.
- Key ring file layout on disk (local dev only) stays outside the repo under `$NTS_HOME` (plan 01); in PostgreSQL the row is the only store, so no `PersistKeysToFileSystem` in the Api/Worker composition roots.
- `FeatureFlagsChanged` is an **event**, not a table: raised by the flag write path, serialised into `outbox_messages` with type `feature_flags_changed.v1` **[RULED — README §B10 WT13]**.
- `rules jsonb` is **not interpreted in P**: the reader answers from `enabled` only (BD13 defines a boolean service). Percentage/targeted rollout belongs to `06.5.2` (S). A non-null `rules` value must not change P behaviour **[RULED — README §B10 WT13]**.

## 4. Use cases

| # | Actor | Given → When → Then |
|---|---|---|
| UC1 | Operator | `youtube_publish` is off, a user calls a publication endpoint → **404** `feature_disabled` (no 403, no 409, no information about the flag's existence beyond the code), within the same ProblemDetails shape as any other error (D7) |
| UC2 | Operator | runs `nts-worker flags set youtube_publish off` → `feature_flags.enabled` flips, an `audit_logs` row (`category = support`, `visibility = internal`, `actor_type = system`, `data.actor = "system:operator"`, ids + new value only) commits in the same transaction, and the change is observable by every Api/Worker instance within `FeatureFlags:CacheSeconds` and in practice immediately (eviction) |
| UC3 | Operator | a flag row is edited directly in the DB (break-glass) → after ≤ 30 s every instance serves the new value even if no eviction happened (TTL backstop) |
| UC4 | Operator | an incident on a render → one trace shows the Api request span (`traceId` from the `LoggingBehavior` line, `plans/features/09-backend-api/09.1.3-mediator-pipeline/plan.md:74`), the outbox row's `trace_parent` (D11) and the Worker Hangfire job span |
| UC5 | Operator | the outbox stops draining → `outbox_lag_seconds` crosses 30 s and the 11.6.2-T1 alert fires from the exported metric, not from a log grep |
| UC6 | Orchestrator | probes `GET /health/live` → 200 while the process is up, no DB/storage/node access; probes `GET /health/ready` → 200 `Healthy` when DB + storage + node registry are usable, **503** when the database is unreachable, and `Degraded` **200** when only storage or the node registry is down |
| UC7 | Attacker / browser | a request from an origin other than `App:PublicOrigin` gets no CORS permission; a cross-site `POST` with the refresh cookie is rejected by the `Origin` check (BD3); a 2 MiB body on a default endpoint is rejected before the handler runs |
| UC8 | Caller of `ITokenProtector` | `Protect(purposeSuffix, plaintext)` → `Unprotect` round-trips for the same purpose, and fails for a different purpose (purpose isolation), with no plaintext in logs |
| UC9 | Developer | adds a route, removes a property or changes an enum value → CI `backend.yml` fails with the oasdiff finding list unless a new API version prefix is introduced |
| UC10 | Developer | adds a package with a known advisory → CI fails (NuGet audit / `dotnet list package --vulnerable`) |

## 5. API and events

**No new user-facing route.** New cross-cutting surfaces only.

### 5.1 Flag guard (09.9.2-T2)

```csharp
// Application/Abstractions/Flags/IFeatureFlags.cs
public interface IFeatureFlags
{
    Task<bool> IsEnabledAsync(string key, CancellationToken ct);
}

// Infrastructure/Persistence/Flags/CachedFeatureFlags.cs — the only implementation
public sealed class CachedFeatureFlags(IFeatureFlagReader reader, IMemoryCache cache, IClock clock) : IFeatureFlags;
// unknown key  → false (fail-closed) + a rate-limited warning log (key only, no user data)
// cache key    → "flags:v1:{key}"; entries hold (bool enabled, DateTimeOffset loadedAt)

// Api/Http/Filters/RequireFeatureFilter.cs — Minimal API endpoint filter
public static class FeatureFilters
{
    public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> RequireFeature(string key);
}
```

- `RequireFeature("youtube_publish")` resolves `IFeatureFlags` from `HttpContext.RequestServices`; when the flag is off it returns the **same** ProblemDetails a handler error would produce, produced through the 09.1.4-T1 catalogue (code → **404**), so the body is `{ type, title, status: 404, code: "feature_disabled", traceId }` (D7). It must not be implemented as an exception, and must not write a 403.
- Guarded endpoints are not edited here: 02.7 / 02.8.3 / 06 add `.RequireFeature(...)` to their own routes (BD1).
- Caching: `FeatureFlags:CacheSeconds` default **30**; a single miss loads one row via `IAppDbContext` (no N+1, no tracked query). Eviction path: `FeatureFlagsChanged` → the registered `IOutboxHandler<FeatureFlagsChanged>` evicts in the Worker, and the Api evicts on a PostgreSQL `NOTIFY feature_flags_changed` emitted in the writer's transaction and consumed by a `FeatureFlagsCacheInvalidator` `BackgroundService` (`LISTEN`) **[RULED — README §B10 WT13: the NOTIFY channel + the 30 s TTL; the invalidator type name remains INFERENCE]**. The 30 s TTL is the hard bound if a notification is lost (UC3).

### 5.2 Health endpoints (09.9.3-T1)

| Route | Auth | 200 | 503 | Body |
|---|---|---|---|---|
| `GET /health/live` | anonymous | always (process is up) | — | `{ "status": "Healthy", "totalDurationMs": 0, "checks": {} }` |
| `GET /health/ready` | anonymous | `Healthy`, plus `Degraded` when only storage/nodes are down | database unreachable | `HealthReport` JSON (ASP.NET Core writer), `Cache-Control: no-store` |

Tags: `db` (`AppDbContext.CanConnectAsync`, required), `storage` (`IBlobStore.ExistsAsync` on the marker object of `Storage:HealthMarkerKey`, degraded), `nodes` (`INodeHealthCache` from 11.3.3-T1: at least one node `active`, degraded). Readiness reads **caches only** — no per-request hashing, no node HTTP call (BD16, 11 README:240). Failure output carries the check name and status only; exception details are never returned or logged with secret-bearing data.

**[DECISION 09.9-D2]** a degraded storage/node state must not take the whole API out of the load balancer (only the database is a hard readiness failure → 503; storage or node-registry degradation → 200 with `Degraded`); the 11.6.2-T1 alerts cover the rest **[RULED — README §B10 WT15]**.

### 5.3 Telemetry contract (09.9.3-T1)

- Serilog: compact JSON (`Serilog.Formatting.Compact`, W4) to stdout, **replacing** the default console formatter; the OTel sink (`Serilog.Sinks.OpenTelemetry`, W4) forwards log events to the same OTLP endpoint as traces/metrics. Log event properties added by the API request middleware: `traceId`, `spanId`, `userId` (**hashed**, never raw — §8), `episodeId?`, `route`, `statusCode`, `durationMs`.
- OTel resource: `service.name` = `nts-api` / `nts-worker`, `service.version` = assembly informational version, `deployment.environment` = `ASPNETCORE_ENVIRONMENT`, `service.instance.id` = host + process start. Sampler: `ParentBased(TraceIdRatioBased(Otel:SampleRatio))`, default `1.0` in P; exporter `Otlp:Endpoint` default from `OTEL_EXPORTER_OTLP_ENDPOINT`, protocol gRPC; `Otlp:Enabled` default `true`.
- Traces: ASP.NET Core + HttpClient instrumentation (W2), `Npgsql.OpenTelemetry` `AddNpgsql()` (W3) for DB spans, **no EF Core instrumentation package** (WF09 — an architecture/package test asserts the package is absent from `Directory.Packages.props`). Hangfire job spans via a server filter registered by `JobPolicyFilterProvider`'s peer registration (D12): job id, queue, job name (method name only — the ids-only rule, D12 — means no arguments are recorded).
- Metrics — one `Meter` named `NightTales` **[INFERENCE]** exposing the frozen names; instruments are created once and injected as a singleton so 02/03/06/09.6.3 can record without touching the OTel SDK:

```csharp
// Application/Telemetry/NightTalesMetrics.cs  [INFERENCE: class/Meter names]
public sealed class NightTalesMetrics
{
    public const string MeterName = "NightTales";
    public Counter<long> RenderingStarted { get; }        // +1 on submit, −1 on terminal  → render_jobs_active
    public Histogram<double> RenderDuration { get; }      // render_duration_seconds { stage, outcome }
    public Counter<double> OpenAiCostUsd { get; }         // openai_cost_usd_total { operation }
    public Counter<long> OutboxDeadLetters { get; }       // outbox_dead_letters_total (D11)
    public void SetYoutubeQuotaUsed(double ratio);        // 0..1, observable gauge
    public void SetOutboxLag(TimeSpan lag);               // observable gauge, D11 dispatcher writes it
    public void ResetRenderJobsActive(long active);       // observable gauge, Worker owns the refresh
}
```

| Metric | Type | Unit | Tags | Recorded by |
|---|---|---|---|---|
| `render_jobs_active` | ObservableGauge&lt;long&gt; | `{job}` | — | Worker refresh loop (a 30 s tick reading `render_jobs` counts via `RenderMetricsQuery`, on the D10 allow-list — counts only, no rows) **[RULED — README §B10 WT14]** |
| `render_duration_seconds` | Histogram&lt;double&gt; | `s` | `stage`, `outcome` | 09.6.3-T1/T2 (submit → terminal), 03/05 stages |
| `openai_cost_usd_total` | Counter&lt;double&gt; | `USD` | `operation` | 02 (story), 03 (image) usage writers |
| `youtube_quota_used` | ObservableGauge&lt;double&gt; | `1` (0..1) | — | 06 quota reader |
| `outbox_lag_seconds` | ObservableGauge&lt;double&gt; | `s` | — | `OutboxDispatcher` (D11) |
| `outbox_dead_letters_total` | Counter&lt;long&gt; | `{message}` | — | `OutboxDispatcher` (D11) |

HTTP server metrics for latency/5xx come from the AspNetCore instrumentation (no custom instrument) — that is what 11.6.2-T1 consumes for the API board.

### 5.4 Events

| Event | Outbox type | Payload | Raised by | Handled by |
|---|---|---|---|---|
| `FeatureFlagsChanged` | `feature_flags_changed.v1` **[RULED — README §B10 WT13]** | `{ Key, Enabled, OccurredAt }` | the flag write path (CLI verb here; 09.9.1-T1 in S) | cache eviction (Worker handler + Api `LISTEN` invalidator) |

Platform events carry no `user_id` (D11 `[PlatformEvent]`); the audit row is written by the writer, not by a handler.

### 5.5 Contract gate (09.9.5-T1)

- `.github/workflows/backend.yml` gains a `contract` job: `needs: build-test`, `if: github.event_name == 'pull_request'`, `permissions: { contents: read }`. It downloads the `openapi` artifact of the **same run** (`actions/download-artifact`, path `src/NightTales.Api/openapi.json`), checks out `main` into `base/`, builds it and generates the base spec with the 09.1.5-T1 generation step (`--generate-openapi` fallback, `09.1.5-T1-backend-ci-workflow.md:63`), then runs the pinned oasdiff binary (W11; version **and** SHA-256 pinned as workflow constants at implementation) with `oasdiff breaking --fail-on ERR base/openapi.json openapi.json`.
- Versioning rule: within `/api/v1` only **additive** changes are allowed. A breaking finding fails the job unless the head spec introduces a new top-level prefix (`/api/v2/...`) while keeping `/api/v1` intact — the job then fails only if `info.version`'s major did not change with it.
- `ProblemDetails` must exist as `components.schemas.ProblemDetails` with `code`, `status`, `title`, `type`, `traceId`, so generated clients (`10.1.4`) have a typed error model; enforced by a unit test (`OpenApiDocumentTests`) and a `jq -e '.components.schemas.ProblemDetails'` step in the `contract` job.

## 6. UI

No Angular change and no user-facing surface in this folder (D14 keeps `/api/v1`). Operator-facing surfaces:

- `nts-worker flags list` / `flags set <key> on|off` — plain-text table (`key`, `enabled`, `rules: (unused in P)`), exit code 0 on success, 1 on validation failure (unknown key format), 2 on DB failure; no user data printed.
- The CI job summary lists every breaking finding (`oasdiff` output, human-readable) and the remediation hint ("bump the prefix / make it additive").
- The Hangfire dashboard at `/internal/jobs` is owned by `09.2.6` (wave 1) / `09.9.1` (S); 09.9.3-T1 only ensures the dashboard's request lines are redacted and carry `traceId`.

## 7. Jobs and integrations

- **No new background job.** `09.9.3-T1` integrates with Hangfire (job spans + queue-based tags) and with the `OutboxDispatcher` (metrics only). `09.9.2-T2` adds a CLI verb to the existing `nts-worker` host (11 §10:242) — no HTTP listener, no job.
- **Outbound integrations:** the OTLP collector from 11 D17 (gRPC, endpoint from config/secret); PostgreSQL (Npgsql spans); the S3 endpoint for the readiness marker (via `IBlobStore`, 09.7.2-T1); the node registry cache (11.3.3-T1).
- **Configuration keys** (all with defaults; `appsettings.json` + env override):

| Key | Default | Used by |
|---|---|---|
| `FeatureFlags:CacheSeconds` | `30` | 09.9.2-T2 |
| `Logging:UserHashSalt` **[INFERENCE]** | — (required in every env; secret `nts/<env>/shared/LOG_SALT`) | 09.9.3-T1/T2 |
| `Otlp:Enabled` | `true` | 09.9.3-T1 |
| `Otlp:Endpoint` | from `OTEL_EXPORTER_OTLP_ENDPOINT` | 09.9.3-T1 |
| `Otel:SampleRatio` | `1.0` | 09.9.3-T1 |
| `Logging:RedactionQueryKeys` | `access_token,ticket,token,code,state` | 09.9.3-T2 |
| `Kestrel:Limits:MaxRequestBodySize` | `1048576` (1 MiB, BD12) | 09.9.4-T1 |
| `App:PublicOrigin` | — (required; e.g. `https://app.nighttales.app`) | 09.9.4-T1 (CORS, BD3 Origin check) |
| `DataProtection:ApplicationName` | `nts-<env>` | 09.9.4-T1 |
| `Storage:HealthMarkerKey` | `health/marker.txt` | 09.9.3-T1 readiness |

## 8. Security and privacy

- **Least privilege.** The `contract` CI job runs with `contents: read`; the OTLP endpoint uses no long-lived credential (network-scoped collector); the CLI verb runs inside the Worker host with the worker secret path (`nts/<env>/worker/*`) and cannot listen on the network.
- **Secrets/tokens never logged.** `DP_KEK` (a secret-manager value) and the PFX password are read at startup and never logged; `ITokenProtector` never logs plaintext or ciphertext; the redaction set (§5.2 of 09.9.3-T2) covers query strings **and** named properties; `userId` is logged as an HMAC-SHA256 with `Logging:UserHashSalt` (stable for correlation, not reversible); e-mail addresses are never logged (BD16).
- **Hashing / constant-time.** All hashing uses `SHA256.HashData`; any comparison of a redaction or flag key against a stored secret uses `CryptographicOperations.FixedTimeEquals`. `ITokenProtector` uses Data Protection (AES-CBC + HMAC-SHA256 by default; the configured algorithm at implementation) — no custom crypto (WF15).
- **Key ring at rest.** Rows in `data_protection_keys` are DP-encrypted with the `DP_KEK` certificate; `DP_KEK` is exported once offline and excluded from DB backups (11 §10:264). Deleting or rotating the certificate without re-encrypting makes existing protected values unrecoverable — hence the key ring is never pruned by a job.
- **Tenant filter.** `feature_flags` and `data_protection_keys` are platform tables (not `IUserOwned`) and are never exposed by an endpoint in this folder; the metrics/health surfaces expose counts and statuses only. Nothing here may read another user's row.
- **404 for others' data.** Unchanged by this folder; the flag guard returns 404 (not 403) so a disabled feature is indistinguishable from a missing route (`plans/09-backend-api.md:423`).
- **Minimal error information.** Health responses and 503 bodies carry check names/status only; ProblemDetails descriptions are fixed English strings (D7).
- **Body/traffic limits.** 1 MiB default body cap (BD12), per-endpoint overrides stay with the owning endpoint (e.g. 09.6.1's render command); HSTS 1 y, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY` — the SPA CSP belongs to plan 10.
- **Dependency hygiene.** `NuGetAuditMode=all` (W10) + a `dotnet list package --vulnerable --include-transitive` step that fails on a High/Critical advisory; both run in the existing `build-test` job shape.

## 9. Test plan

xUnit (`tests/NightTales.Api.Tests`, `.../Infrastructure.Tests`, `.../TestKit` — names per file convention of Part A); harness per D13, no mocking library (hand-written fakes + `TestAuthHandler`).

| Class | Cases |
|---|---|
| `FeatureFlagsTests` | enabled/disabled row; unknown key → `false`; cache hit within 30 s returns the cached value (`FakeClock` advanced 29 s); `FakeClock` advanced 31 s re-reads; `FeatureFlagsChanged` evicts immediately; DB error → fail-closed `false`, no exception to the caller |
| `RequireFeatureFilterTests` | flag off → 404 body `code = feature_disabled`, `traceId` present, **no** handler execution; flag on → handler runs; unauthenticated request on a guarded route still gets 404 (not 401/403) |
| `FeatureFlagCliTests` | `flags set x off` changes `feature_flags.enabled`, writes exactly one `audit_logs` row (`category = support`, `visibility = internal`, `actor_type = system`, `data` has key + new value and no secrets), and the row + audit commit together (fault injection → neither) |
| `ObservabilityWiringTests` | `Npgsql.OpenTelemetry` registered and EF Core instrumentation **absent** (package assertion on `Directory.Packages.props` + service-collection assertion); Serilog compact JSON output parses as one JSON object per line; the OTel sink is configured when `Otlp:Enabled` |
| `HangfireSpanTests` | a job run under `WorkerHost` produces an activity named `job {queue}:{jobName}` with no argument values |
| `TracePropagationTests` | outbox row `trace_parent` equals the request activity id; the Worker dispatch continues the same trace |
| `HealthEndpointTests` | `/health/live` 200 with the DB stopped; `/health/ready` 200 `Healthy`; DB stopped → 503; storage unavailable → `Degraded` 200; no exception text in the body |
| `MetricsTests` | each frozen name is exported with the right unit/type; `render_jobs_active` reflects a seeded job set; `outbox_lag_seconds` reflects the oldest unprocessed row |
| `RedactionTests` | a hub connect (`?ticket=`) and a reset link (`?token=`, `?access_token=`) leave no raw value in captured logs **or** spans; `url.query`/`url.full` are redacted; a `Set-Cookie`/`Authorization` header value is not present; the positive control (an unlisted query key) is still visible |
| `SecurityHeadersTests` | HSTS/`X-Content-Type-Options`/`Referrer-Policy`/`X-Frame-Options` present on a 200 **and** on an error response; CSP absent from the API but present on the docs page |
| `CorsTests` | allowed origin gets `Access-Control-Allow-Origin: App:PublicOrigin`; another origin gets none; credentials only on `/api/v1/auth/*` |
| `RequestLimitTests` | a 2 MiB body on a default endpoint → 413; an overridden endpoint accepts its own limit |
| `DataProtectionKeyRingTests` | after a `Protect` call the ring has ≥ 1 row in `data_protection_keys`; the persisted `xml` contains no plaintext key material and the column does not equal the on-disk payload; a second app instance with the same `DP_KEK` unprotects the first instance's value; a wrong certificate fails |
| `TokenProtectorTests` | round-trip per purpose; `Unprotect` with a different purpose fails; null/empty inputs rejected; no plaintext in logs (assertion via the capture sink) |
| `VulnerabilityAuditTests` | `dotnet list package --vulnerable` output has no High/Critical entry (enforced in CI; the test is a local guard) |
| `OpenApiDocumentTests` | `components.schemas.ProblemDetails` exists with the five required properties; every catalogued error code has a documented response; `info.version` present |
| `ContractDiffTests` (job-level, `09.9.5-T1`) | the `contract` job is skipped on `push`; a deliberately breaking fixture spec fails the `oasdiff breaking --fail-on ERR` invocation; an additive fixture passes |

Integration tests never sleep (D13): cache expiry and health transitions are driven by `FakeClock` and direct service calls; the collector is a hand-written fake OTLP receiver in-process for the export assertions.

## 10. Tasks and re-estimate

| Task | Title | Area | Size | Depends on | Blocks |
|---|---|---|---|---|---|
| 09.9.2-T2 | (P) Feature-flag read service + `RequireFeature` + `nts-worker flags` (09 W2 CR-3) | BE | S (0.5) | 09.2.5-T1 | 02.8.3-T2 soft, 06.1.2-T1 (06 CR-8), 09.9.2-T1 (09 W3 CR-17), 06.5.2-T1, 13.4.1-T1 |
| 09.9.3-T1 | Serilog + OpenTelemetry (Npgsql instrumentation, no EF instrumentation) + health + custom metrics (09 W2 CR-3) | BE | M (1.0) | 09.1.1-T1, 09.2.6-T1 | 09.9.3-T2, 11.6.2-T1, 08.2.1-T1, 08.1.1-T1 |
| 09.9.3-T2 | Query-string redaction (`access_token`, `ticket`, `token`, `code`, `state`) in logs/spans + capture test | BE | S (0.5) | 09.9.3-T1, 09.8.1-T2 | — |
| 09.9.4-T1 | Security headers, CORS, Data Protection keys, vuln scan | BE | M (1.0) | 09.2.1-T1 | 09.10.4-T1, 06.1.2-T1 (06 CR-8), 09.10.1-T1 (09 W3 CR-17), 07.1.2-T1 |
| 09.9.5-T1 | OpenAPI doc + contract diff check | Ops | S (0.5) | 09.1.5-T1 | 09.2.3-T2 |

Folder total **3.5 d raw**, matching B9 ("09.9 (P part) … 3.5"). Dependency cells are copied verbatim from README B8.1 (the `Title` column matches the `09 §6` task table, `plans/09-backend-api.md:538–542`); B8.1 additionally records `Phase = P · M1` for all five tasks.

**Open items**
- **[RESOLVED — README §B10 WT13]** `feature_flags.rules jsonb` (00 §6:306) is **unused in P**: this plan reads `enabled` only, and a seeded non-empty `rules` must not affect P behaviour (rule evaluation is `06.5.2`, S).
- **[RESOLVED — README §B10 WT13]** the eviction outbox type string is fixed as `feature_flags_changed.v1` (the event type name `FeatureFlagsChanged` is per BD13).
- **[RESOLVED — README §B10 WT13]** cross-instance eviction uses a PostgreSQL `NOTIFY feature_flags_changed` + a `FeatureFlagsCacheInvalidator` `LISTEN` service (mirroring BD11's LISTEN pattern) with the 30 s TTL as backstop (only the invalidator type name remains `[INFERENCE]`).
- `09.9.3-T1` introduces the shared metrics holder (`Application/Telemetry/NightTalesMetrics.cs`, `Meter` = `NightTales`) so 02/03/06/09.6.3 can record without touching the SDK — new cross-plan type names **[INFERENCE]**; names in 11.6.2-T1 stay frozen.
- Audit action string for the flag CLI is not fixed in BD13 (`support`/`internal` are the category/visibility); this plan uses `feature_flag_changed` **[INFERENCE]**.
- **[RESOLVED — README §B10 WT14]** `render_jobs_active` needs a platform-wide read of `render_jobs` from the Worker; D10 restricts cross-user reads to `ISystemWorkQuery` and the allow-listed classes, and the read goes through the allow-listed `RenderMetricsQuery` (one entry, counts only, no rows).
- **[RESOLVED — README §B10 WT15]** readiness policy **[DECISION 09.9-D2]**: database failure → **503**; storage or node-registry degradation → **200** with `Degraded` (confirmed against 11's orchestrator expectations, 11.2.2/11.4).
- `README.md:831-833` — the `## B11. Definition-of-Ready review (wave 2)` section is present but empty, while the header status (`README.md:568`) states "TL DoR 2026-09-24 (B11)". Content appears to be missing; this plan was written against Part B bodies B0–B10.
- `plans/09-backend-api.md:234` requires the Hangfire dashboard at `/internal/jobs` behind the MFA cookie; W17 puts `09.9.1` in S, so in P the dashboard surface is `09.2.6`'s. Confirm that 09.9.3-T1 owns only the job spans/access-log redaction and not the dashboard route.
- BD12 writes the last hardening header as "`Frame-Options DENY`"; the actual HTTP header is `X-Frame-Options: DENY`, which is what the 09.9.4-T1 files specify (09:239 lists the header set without it). Confirm the intended header name.
- **[RESOLVED — README §B10 WT16]** `.github/workflows/backend.yml` is owned by the signed `09.1.5-T1`; 09.9.4-T1 (one `--vulnerable` step) and 09.9.5-T1 (the `contract` job) only **add jobs/steps** to it — no second owner, no CR required.
- 09.9.3-T2 proves the application-side path: the reverse-proxy/load-balancer access log of 11 must also strip the query string on `/hubs/*` and the auth paths; that proxy rule is not in any 09 task (09:233 states the requirement). Assign it to 11's reverse-proxy config or accept the residual.

## 11. Rollout

**Order.** `09.9.4-T1` (migration `_09_DataProtectionKeys`, headers, CORS, limits) must be deployed **before** any code that protects values with the Data Protection ring — the `ITokenProtector` users (`06`) **and** the TOTP store (`09.10.4-T1`, which calls `IDataProtectionProvider.CreateProtector("identity.totp.v1")` directly, not through `ITokenProtector`) **[RULED — README §B10 WT43]** — because the key ring table must exist and `DP_KEK` must be mounted in the api **and** worker containers (11 §10:284). `09.9.3-T1`/`T2` can ship independently and are observability-only. `09.9.5-T1` ships last, once `openapi.json` is stable (after the wave-2 routes land), because it turns previously-silent changes into CI failures.

**Migration order:** `data_protection_keys` (expand-only, no data backfill) → app deploy with `PersistKeysToDbContext` + `ProtectKeysWithCertificate` → first protected value written. No other migration in this folder; `feature_flags` already exists (`09.2.2`) and is seeded (`09.2.5-T1`).

**Feature flags.** `09.9.2-T2` is itself the flag mechanism, so it cannot be flagged; the rows it reads (`youtube_publish`, `tiktok_publish`) are seeded by `09.2.5-T1` and flipped by the CLI. Ship the guarded endpoints with the flag **off** in every environment except staging.

**Monitoring.** 11.6.2-T1 alerts on `outbox_lag_seconds` > 30 s, `render_duration_seconds` failure share, `openai_cost_usd_total` vs the budget line and `youtube_quota_used` > 80 %; the API board uses the AspNetCore HTTP metrics. Add a log-based alert for `code = feature_disabled` volume spikes only if the operator asks (not part of the alert file).

**Rollback.** Telemetry can be turned off with `Otlp:Enabled=false` without a redeploy of code; the redaction pipeline must never be disabled in production (it is the control that keeps tokens out of the collector). The flag guard is removed by deleting the `.RequireFeature(...)` call on a route (additive, no migration). Rolling back `09.9.4-T1` means restoring the previous app image — the `data_protection_keys` table stays (dropping it would destroy the ability to unprotect existing `refresh_tokens`/`user_action_tokens` values). Rolling back `09.9.5-T1` is making the `contract` job non-required in branch protection; the workflow file itself stays.
