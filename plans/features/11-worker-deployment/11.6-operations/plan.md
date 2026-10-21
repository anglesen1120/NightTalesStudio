# 11.6.1, 11.6.2, 11.6.3 — Operations foundation: backups and restore, telemetry and alerts, capacity and cost

> Parent: [`../README.md`](../README.md) — plan 11 TL review (facts K/E/P, findings F01–F28, decisions D1–D19, CR-1…CR-11, §5 sequencing, §8.1 canonical dependency table). High-level story: [`../../11-worker-deployment.md`](../../11-worker-deployment.md) §3 Epic 11.6, §6 task table. Templates: `../../00-overview.md` §10.1 (sections 1–11), `../README.md` §6 (task file).
> Stories: **11.6.1 Backups and restore** (Must · S) · **11.6.2 Monitoring and alerts** (Must · M) · **11.6.3 Capacity and cost measurement** (Must · M); the `nts-admin` job image was the phase-S 11.6.4, dropped by DEC 10.14. Tasks: 11.6.1-T1 (S 0.5) · 11.6.2-T1 (M 1.0) · 11.6.3-T1 (M 1.0) = **2.5 d raw / 3.0 d buffered** (high-level §11 "11.6 Ops foundation 2.5", P12).
> Legend: ✅ read from source/docs (file + line) · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].

## 1. Scope

The plan-11 **operations** slice: it builds no product feature, it makes the M1 platform operable and produces the two measurements the owner's cost metering and scaling decisions rest on (videos/hour, cost/video).

**In scope**

- Backup configuration of the managed PostgreSQL 17 (PITR + WAL retention), daily snapshots, the assertion of the `nt-media-<env>` → `nt-backup-<env>` copy **owned by 11.4.1-T3** (`weed filer.backup` on `storage-backup-<env>`) [RULED — README §10 T74/T80] (D21, D20 H9) (11 CR-15), the assertion of the daily snapshots of the `storage-<env>` volume (7 d; the backup volume is not snapshotted, T87, D21), the separate offline `DP_KEK` copy, and the **monthly timed restore drill** into a scratch DB — the DB restore, the deletion re-application and the two storage legs (one object restored from `nt-backup-<env>` — a noncurrent version when the object was deleted, T79 — one volume snapshot restored into a scratch volume) — including re-application of `data_deletion_requests` completed after the backup timestamp (D17, F27, 13.3.1-T1 soft input); `docs/runbooks/restore-drill.md` (README §3 assigns this file to 11.6.1-T1).
- The OpenTelemetry Collector in the app network, the telemetry backend from 11.2.1-T1, the dashboards, the D17 alert rules plus D21's four storage rules, and a drill that proves one alert fires end to end.
- The 20-render capacity test at concurrency 1/2/4 on the prod node, the NFR-05-1 check, peak resource records, compute cost/video and the final config values (§5).

**Out of scope — consume, never fork:** the IaC module, envs, the SeaweedFS hosts and their volumes/snapshots/`nt-backup-<env>` (`storage-<env>`, `storage-backup-<env>`), the `weed` images, `filer.backup` and the buckets' policies/lifecycle (11.2.2-T1/T3/T4, 11.4.1-T1/T2/T3, D21) (11 CR-15); the PostgreSQL resource, collector/backend hosting, firewall (11.2.2-T1/T3/T4); telemetry conventions, redaction, exporter wiring, Hangfire dashboard, health probes, transport metric names (09.9.3); node metric names, `NodeHealthPoller`, `INodeSelector` (11.3.3-T1/T2); the `gpu` block in `render-node-health.v1` (11.3.5-T4, D7/F21); loudness/render checks (05.5.1-T1, 05.5.2-T1, 11.3.5-T3); the studio UI and autopilot alert copy (10, 02.8.2-T3, 02.8.3-T4). Explicitly **not backed up**: adapter durable store (lost-task rule, F27) and node disks/releases (rebuilt by `nts-node deploy`, D12).

## 2. Confirmed facts

| Id | Fact | Source |
|---|---|---|
| E10 | PostgreSQL 17 PITR = base backup + continuous WAL archive | README §1 E10 ✅ |
| E11 | Prometheus, Alertmanager, OpenTelemetry Collector (+contrib), Jaeger Apache-2.0 | README §1 E11 ✅ |
| E12 | MinIO, Garage, self-hosted Grafana, Renovate are **AGPL-3.0** → no self-hosted component here | README §1 E12 ✅ |
| E4 | Ubuntu `ffmpeg` having `h264_nvenc` is **not** confirmed officially | README §1 E4 ⚠️ |
| K8 | VieNeu RTX 3060 12 GB bf16: 16 real-time streams default; VRAM 0.9 GB idle / 1.1 GB peak at 16 | README §1 K8 ✅ |
| E2 | faster-whisper large-v2 fp16 beam 5 → 4,525 MB; `batch_size=8` fp16 → 6,090 MB (no turbo row) | README §1 E2 ✅ |
| P13 | Mac trial numbers are a baseline only; NFR-05-1 (≤ 20 min per 10-min video) is measured on the GPU node **by plan 11** | README §1 P13; 05 D15 P7 ✅ |
| P1 / 05 D4 | `render-status.v1.stage ∈ queued · voice (5–29) · subtitles (30–39) · images (40–49) · assembling (50–99) · checking · done`; `percent` never decreasing; terminal payload carries `output` (bytes/sha256, `narration_seconds`) and `check` | 05 README §5.3 ✅ |
| D17 | Backup tiers and the deletion re-application rule; telemetry (OTLP → collector in the app network; node metrics only through the Worker; node logs stay on the node); the alert list with initial thresholds; the capacity-test definition and outputs | README D17 ✅ |
| D13 | Node metrics `render_node_up{node}`, `render_node_free_slots{node}`, `render_node_gpu_util_pct{node}`, `render_node_gpu_mem_used_bytes{node}`, `render_node_gpu_temp_celsius{node}` (T28), `render_node_disk_used_pct{node}` (one `statvfs`, T63), `render_node_health_reasons_total{node,reason}`, `render_node_active_tasks_drift_total{node}`, `render_node_cert_expiry_days{node}` (T57), `render_dispatch_unavailable_total`; 30 s poll, `down` after 120 s, dispatchable covers disk/gpu/codec/`capacity.accepting` | README D13, D20 H12/H16/H21 ✅ |
| D15 | `nt-backup-<env>` bucket; secret-manager path `nts/<env>/<NAME>` carrying `DP_KEK`, `DB_CONNECTION`, `STORAGE_*`, `OPENAI_API_KEY` | README D15 ✅ |
| D21 | Self-hosted SeaweedFS per env: VMs `storage-<env>`/`storage-backup-<env>` in their own `storage` subnet (T77), attached volumes, daily snapshots on the primary volume only (7 d, T87); S3 gateway TLS-only (`-s3.port=8333 -s3.cert.file -s3.key.file`, `-s3.port.https` unset, T78) published on the private address as TCP 443 → `https://storage-<env>.internal`, and behind `media.<domain>`/`media.staging.<domain>` (`GET`/`HEAD` only, T75/T83); `s3.json`/`security.toml`/`replication.toml` rendered by `sync-identities.sh` (T82); `BACKUP_ACCESS_KEY` = `Write` on `nt-backup-<env>` only, never `BypassGovernanceRetention`/`Admin` — the object lock is the delete guard (T88; markers only, T79); `nt-backup-<env>` versioning + object lock GOVERNANCE 29 d + `NoncurrentVersionExpiration` 29 d (T87 supersedes T79's 30 d); the copy is `weed filer.backup -filerPath=/buckets/nt-media-<env> -filerProxy -doDeleteFiles=true` (11.4.1-T3, T79/T80); `-metricsPort` 9327 scraped from `worker-sg` (T78); alerts gateway 5xx > 1 %/5 min, `nts_backup_lag_seconds` > 900 (T85), no writable volumes, volume disk ≥ 80 %/85 % | README §3 D21, §10 T74-T95 ✅ |
| D9 / T4 / F06 | One ffmpeg `/usr/bin/ffmpeg` per node; `NTS_MPT_VIDEO_CODEC = libx264` in P until 11.6.3 records an `h264_nvenc` run that passes 05 D10 **and** is faster | README D9, §10 T4 ✅ |
| D10 | `VIENEU_MAX_STREAMS` ≥ `NTS_MPT_MAX_CONCURRENT` | README D10 ✅ |
| D6 / F20 | `Adapter:CheckConcurrency` (1) and `Adapter:CheckTimeoutSeconds` (900), re-set from the 05.5.2-T1 wall times and 11.6.3; post-processing competes with MPT for CPU | README D6, F20 ✅ |
| F27 | Adapter durable state needs no backup (lost-task rule); node disks are rebuilt, never restored; `DP_KEK` stored separately | README F27, D17 ✅ |
| F28 | GPU ≥ 16 GB VRAM required, 24 GB preferred (E2-based inference); **11.6.3 records the measured peak** | README F28 ✅ |
| 05 P6/P7 | `ADAPTER_MPT_THREADS = max(1, floor(cpu_cores ÷ max_concurrent_tasks))`, and a `max_concurrent_tasks` above what 11.6.3 measured is a TL decision; `mpt_client.py poll --timeout` 3600 s, a poll timeout is **not** a failed render; NFR-05-1 measured on the node | 05 README D15 P6/P7 ✅ |
| 09.9.3 | Serilog JSON + OTel (ASP.NET Core, HttpClient, EF Core, Npgsql); `traceId`, hashed `userId`, `episodeId`; secrets and story text never logged; query values redacted before export; Hangfire dashboard `/internal/jobs` behind the MFA cookie; `/health/live`, `/health/ready`; custom metrics `render_jobs_active`, `render_duration_seconds`, `openai_cost_usd_total`, `youtube_quota_used`, `outbox_lag_seconds` | `plans/09-backend-api.md:231-236` ✅ |
| 03 hand-over | Relay metrics to scrape: `image_calls_total{status,error_code}`, `image_relay_wait_seconds`, `image_relay_rejections_total{code}`; alert on `invalid_api_key` and on quota codes | `plans/11-worker-deployment.md:269` ✅ |
| 02.8.2 / 02.8.3 | Alerts: `autopilot_gate_total{result="fail"}` > 50 % of verdicts over 24 h; any `autopilot_pause_total` increment; `autopilot_run_stuck_total` (same `state` > 60 min) | `02.8.2…/plan.md:340`; `02.8.3…/plan.md:574` ✅ |
| 00 §11 | Data row: daily backups + PITR, **30-day retention**, KEK backed up separately, restore tested monthly; alert thresholds owned by 08 §5.2, so D17 carries initial values only | `plans/00-overview.md:411-412`; README D17 ✅ |

## 3. Domain and data

**No new domain type, no new table, no migration.** Operations read existing stores; their only artefacts are backup objects, alert state and documentation.

| Data | Use |
|---|---|
| `data_deletion_requests` (13) | read, then re-applied during a restore: rows with `completed_at >` the backup's PITR timestamp must land before reopen (D17, 13.3.1-T1) |
| `render_jobs` (+`quality_report`), `worker_nodes`, `outbox_messages` | queue depth, outbox lag, render success rate, dispatch counters (D13, 09.9.3) |
| `usage_records` / cost ledger, `youtube_quota_used` | OpenAI cost/day vs the budget line; YouTube quota > 80 % (D17) |
| Backup artefacts | PITR/WAL and PostgreSQL snapshots inside the managed service; media copies in the `nt-backup-<env>` bucket on `storage-backup-<env>` (SeaweedFS versioning — deleted objects remain as noncurrent versions, T79; written by 11.4.1-T3's `filer.backup`, T80); daily provider snapshots of the `storage-<env>` volume only 7 d (T87); `DP_KEK` in the owner's offline store only (D21) (11 CR-15) |
| Measurement record | DECISIONS §10.4 and the final config values (11.6.3-T1) |

Retention (D17, `00-overview.md:412`): WAL archive 7 days (PITR source), PostgreSQL daily snapshots 30 days (provider restore into a new instance), `nt-media-<env>` copies in `nt-backup-<env>` held as versions — `NoncurrentVersionExpiration` 29 days (with object lock GOVERNANCE 29 d, T87) purges a deleted object's data within 30 days of its primary delete marker (no current-version expiry), on `storage-backup-<env>` (SeaweedFS), primary storage-volume snapshots 7 days (backup volume not snapshotted, T87) (11 CR-15), `DP_KEK` indefinite and offline (never a DB backup or bucket) — and therefore deleted data ages out of **all** tiers within 30 days, which is 13's deletion window promise.

## 4. Use cases

- **UC1 Restore after an incident.** Pick the target timestamp, assert archive lag ≤ RPO target, PITR into the **scratch** DB, re-apply deletions, smoke read, then the two storage legs (one object from `nt-backup-<env>`, one volume snapshot), record the elapsed time as the measured RTO; closes when the runbook's timed table has a real row (D21) (11 CR-15).
- **UC2 Deletion survives a restore.** Enumerate `data_deletion_requests` with `completed_at` in `(backup_time, now]` and assert each is re-applied before the system serves data — a story deleted last week must not come back (D17, `00-overview.md:412`).
- **UC3 The operator sees a problem before the user does.** One platform board (API latency/errors, queue depth, outbox lag, render success rate, OpenAI cost/day, YouTube quota), one node board (GPU util/mem/temp, disk, free slots, health reasons, relay metrics) and one storage board (gateway 5xx ratio, `nts_backup_lag_seconds` (T85), writable volumes, volume disk; D21) (11 CR-15).
- **UC4 Alert fires and is acknowledged.** Provoke one real condition on staging, observe the notification, record the detection latency, clear it, leave the rule enabled.
- **UC5 Capacity and cost decided with numbers.** 20 renders at 1/2/4 give per-stage wall times, peaks, failures and cost/video; the owner changes `NTS_MPT_MAX_CONCURRENT` and the cost meter (owner) on evidence (D17, F28, P13).

## 5. API and events

**No new public API; no change to `render-node.v1`.** This folder consumes what other plan-11 tasks deliver: `render-node-health.v1` (incl. the additive `gpu { util_pct, mem_used_mb, mem_total_mb, temp_c, sampled_at }`, D7/F21) from 11.3.5-T4; `render-status.v1` from 11.3.5-T1/T2; relay metrics from 03.4.3; `/metrics`, `/health/live`, `/health/ready`, `/internal/jobs` from 09.

**Metric contract consumed by 11.6.2-T1** (names frozen by their owners — this folder wires, never renames): app — `render_jobs_active`, `render_duration_seconds`, `openai_cost_usd_total`, `youtube_quota_used`, `outbox_lag_seconds`, plus HTTP server metrics for latency/5xx (09.9.3); node — the seven D13 names plus `render_node_gpu_temp_celsius`, `render_node_active_tasks_drift_total` and `render_node_cert_expiry_days` [RULED — README §10 T70/T72] (D20 H21); relay — the three 03 names; autopilot — `autopilot_gate_total{gate,result}`, `autopilot_gate_check_failed_total{check}`, `autopilot_gate_duration_seconds`, `moderation_flags_total{stage,category}`, `autopilot_gate_dry_run_remaining`, `autopilot_pause_total{reason}`, `autopilot_run_stuck_total`; storage — the SeaweedFS host metrics scraped from `-metricsPort` 9327 on `storage-<env>`/`storage-backup-<env>` (open to `worker-sg` only — the collector runs on the Worker host, T16/T78): S3 gateway request/error (5xx) counts, writable volume slots, volume disk used %, plus `nts_backup_lag_seconds` pushed over OTLP/HTTP 4318 by `check-storage-host.sh --metrics` on `storage-backup-<env>` (canary lag; `filer.backup` has no metrics port, T85 — `worker-sg` opens 4318 to `storage-backup-sg`, T86) [INFERENCE: exact metric names, fixed by 11.4.1-T2's pin] (D21) (11 CR-15).

| Key | P default | Final owner |
|---|---|---|
| `NTS_MPT_MAX_CONCURRENT` | provisioning value | 11.6.3-T1 (evidence) |
| `VIENEU_MAX_STREAMS` | ≥ concurrency (D10); 16 on a 12 GB card (K8) | 11.6.3-T1 |
| `Adapter:CheckConcurrency` | 1 (D6) | 11.6.3-T1 |
| `Adapter:CheckTimeoutSeconds` | 900 (D6/F20) | 11.6.3-T1 |
| `NTS_MPT_VIDEO_CODEC` | `libx264` (D9, T4) | 11.6.3-T1 (only if the `h264_nvenc` run passes 05 D10 **and** is faster) |
| `ADAPTER_MPT_THREADS` | `max(1, floor(cpu_cores ÷ max_concurrent_tasks))` (05 P6) | 11.6.3-T1 (recompute) |

## 6. UI

No studio screen (10 owns every screen). Three operator surfaces: (1) **alert notifications** on the channel chosen in 11.2.1-T1 (provider-native or the hosted backend's; the 02.8.3-T4 e-mail path is the fallback), carrying node names and codes only — no story text, no ids beyond those (D19 S7, `02.8.3…/plan.md:478`); (2) **dashboards** — a platform board, a node board and a storage board (D21) (11 CR-15), English titles, units labelled; (3) the **runbook** `docs/runbooks/restore-drill.md`, the human interface for UC1/UC2 with copy-pasteable commands, the two storage-restore legs and the timed table.

## 7. Jobs and integrations

| Integration | Mechanism | Owner |
|---|---|---|
| PITR, WAL archive, daily snapshots | managed PostgreSQL feature (E10) | IaC (11.2.2), configured by 11.6.1-T1 |
| `nt-media-<env>` → `nt-backup-<env>` | `weed filer.backup -filerPath=/buckets/nt-media-<env> -filerProxy -doDeleteFiles=true` on `storage-backup-<env>` reads the primary filer (8888/18888 via `storage-backup-sg`, T78) and writes through `sink.s3` with `BACKUP_ACCESS_KEY` (`Write` on `nt-backup-<env>` only, never `BypassGovernanceRetention`/`Admin` — SeaweedFS cannot separate `DeleteObjectVersion`, the object lock is the guard, T88); primary deletes propagate as delete markers, `NoncurrentVersionExpiration` 29 d purges the data afterwards (T87) | **11.4.1-T3** owns the copy [RULED — README §10 T79/T80] (D21, D20 H9) (11 CR-15); `nt-backup-<env>` + versioning + object lock + lifecycle are 11.4.1-T1's; 11.6.1-T1 asserts it |
| SeaweedFS hosts, TLS, identities, metrics | compose + `nts-storage.service` per host; `sync-identities.sh` renders `s3.json`, `security.toml` and `replication.toml` (atomic, `0600`, values never in argv/ps, T82); `-metricsPort` 9327 scraped by the collector from `worker-sg` (T78/T86) | **11.4.1-T2**; VMs/volumes/DNS/`storage` subnet 11.2.2-T1 (D21, T77) (11 CR-15) |
| Storage volume snapshots | daily provider snapshots of the `storage-<env>` volume only, 7 d (the backup volume is not snapshotted, T87) | 11.2.2-T1's module; 11.6.1-T1 verifies one and restores it in the drill |
| Restore drill | operator-run script + runbook, monthly | 11.6.1-T1. [DECISION] no automated restore job — a schedule able to overwrite a database has no place in the Worker |
| Telemetry export | OTLP (gRPC) from Api/Worker → collector in the app network → backend | collector deployed via 11.2.2, config owned by 11.6.2-T1 |
| Node metrics | pull-only: `NodeHealthPoller` (30 s, advisory lock, D13) exports what it fetched; nodes never push (D17) | 11.3.3-T1/T2 |
| Alert evaluation | the chosen backend's rule engine, rules stored in the repo | 11.6.2-T1 |
| Capacity driver | script submitting 20 jobs through the normal platform path, reading `render-status.v1` | 11.6.3-T1 |

No Hangfire recurring job is added here (P9: a sub-minute recurring job is unreliable; the only timer-like integrations are the managed PostgreSQL provider's backup schedule and `filer.backup`'s own continuous loop on `storage-backup-<env>`, D21) (11 CR-15).

## 8. Security and privacy

- **Backup confidentiality and isolation.** Provider-side encryption at rest; `DP_KEK` exported **once** to the owner's offline store, never in a bucket, a DB backup, a repo or a log (D17, F27), asserted by acceptance. The drill targets a **scratch** instance with no public endpoint and no production traffic, refuses any other host (exit 2) and never mutates production (`00-overview.md:412`).
- **Least privilege and secrets.** Drill credential: read backups + create the scratch instance (and create the scratch volume from a snapshot); the copy identity is `BACKUP_ACCESS_KEY` on `storage-backup-<env>`, limited to `Write` on `nt-backup-<env>` — never `BypassGovernanceRetention`/`Admin`; the object lock is the delete guard (T88; markers only, T79; `filer.backup` reads the primary through the filer port, not an S3 identity, D21) (11 CR-15); the object-restore leg reads through `https://storage-backup-<env>.internal` over the VPN/bastion range on 443 (T86) with the `NTS_OPS_READ` identity (T91), never `BACKUP_ACCESS_KEY`; collector ingest: app network only. Secrets come from env files (`/etc/nts/*.env`, 0640 root:<service user>, D8/D11) or stdin — never argv, logs, the runbook or the bundle (D19 S4/S5); scripts follow 01 D9.
- **Telemetry content and exposure.** No story text, prompts, source text, tokens, cookies or node-local paths: 09.9.3 owns the redaction processors, 05 D4 states "never in a status: node paths, MPT URIs, script, terms, prompts", D19 S7 keeps adapter logs to ids/hashes/codes — 11.6.2-T1 only verifies with a payload scan. The Hangfire dashboard stays behind its MFA cookie (09.9.3); collector/backend endpoints are not public; alert messages carry ids, node names and codes only.
- **Deny-by-default network and cost safety.** Collector in the app subnet; the node never connects into it (D11, D13); the capacity test uses the normal authenticated Worker path. That test spends real image/narration money and GPU time: capped at 20 + the A/B set, aborted on infrastructure failure, watched against the D17 cost/day rule (P8).

## 9. Test plan

**11.6.1-T1** — drill script against a production-looking host → exit 2, no restore; full timed drill on staging with a recorded duration; deletion re-application over a fixture set applied twice → identical end state; archive lag at drill start ≤ RPO target, stopped archive → exit 1; retention assertion (WAL 7 d, snapshots 30 d, `nt-backup-<env>` `NoncurrentVersionExpiration` 29 d (T87), primary storage-volume snapshots 7 d) against three fixture configs; storage legs (D21) (11 CR-15): `nts_backup_lag_seconds` ≤ 900 and a stopped `filer.backup`/canary → exit 1 (T85), a restored object (noncurrent version after a delete, T79) compared by sha256, a snapshot older than 24 h → exit 1; `DP_KEK` absent from the bucket listing and the repo tree; every fenced runbook command passes `bash -n`; no secret visible in `ps` arguments or output.

**11.6.2-T1** — collector config validation in CI; `check_metrics_contract.sh` positive on the shipped config and negative with one metric name deleted (exit 1); rule-file validation plus a threshold drift test against D17/D21 (25 rules now — 06 CR-10 added platform-incident and deletion-due rules, 12 CR-8 added three billing rules, 07 CR-10 added the TikTok active-user-cap rule, 11 CR-15 added the four storage rules (gateway 5xx > 1 %/5 min, `nts_backup_lag_seconds` > 900 (T85), no writable volumes, volume disk ≥ 80 %/85 %) — incl. the drift, cert-expiry and queued-too-long ones [RULED — README §10 T70] (D20 H21)); dashboard import against staging; alert drill with a recorded detection latency; payload scan for a seeded story sentence, a token and a node path → absent.

**11.6.3-T1** — `capacity_run.py --self-test` parsing the 05 D14 `render-status.v1` fixtures into report rows (stage extraction, the NFR-05-1 formula at 300/600/900 s narration, cost arithmetic against a fixture price); staging dry run against mock-mpt over 3 renders (no GPU spend); sampler check (monotonic timestamped series; a missing `gpu` block is an error, not a zero); manual prod-node run watched on the node dashboard.

**Manual:** one full operator restore drill (11.6.1), one acknowledged alert drill (11.6.2), one real capacity run (11.6.3).

## 10. Tasks and re-estimate

| Task | Title | Area | Size | Depends on | Blocks |
|---|---|---|---|---|---|
| 11.6.1-T1 | Backups, retention and timed monthly restore drill | Ops | S (0.5) | 11.2.2-T4, 11.4.1-T1, 11.4.1-T3 (11 CR-15); 13.3.1-T1 soft | — |
| 11.6.2-T1 | Telemetry collector, dashboards, alert rules and alert drill | Ops | M (1.0) | 11.2.1-T1, 11.3.3-T2, 09.9.3-T1, 11.4.1-T2, 11.4.1-T3 (11 CR-15) | 08.4.3-T1 (08 CR-5), 07.3.3-T1 |
| 11.6.3-T1 | 20-render capacity and cost test on the prod node | Ops/BA | M (1.0) | 11.3.1-T3, 11.3.2-T2, 09.6.3-T1 | — (billing plan dropped by DEC 10.14) |
| **Total** | | | **2.5 d raw / 3.0 d buffered** | | |

Header cells equal `../README.md` §8.1 exactly (row 408); 11 CR-15 (T80) puts 11.4.1-T3 in the Depends cells of 11.6.1-T1 and 11.6.2-T1 (the 11.6.1-T1 and 11.6.2-T1 §8.1 rows carry the same cells with the `(11 CR-15)` tag). All three tasks are `P · M1` (README §5: "M1 order … → 11.3.1-T3 → 11.6.3-T1"); the phase-S `nts-admin` job image (09 W3 CR-6) is dropped by DEC 10.14.

**Re-estimate note.** The high-level 11 §6 sized 11.6.1 S, 11.6.2 M, 11.6.3 M = 2.5 d (P12; §11 "11.6 Ops foundation 2.5") and this folder keeps that: the expensive mechanisms are owned elsewhere (IaC, the collector container, the metric sources, the render harness). Two additions fit inside the same sizes: 11.6.1-T1 also owns the timed record, the deletion re-application check (UC2) and the two storage-restore legs (D21) (11 CR-15); 11.6.3-T1 also owns the final value list including `VIENEU_MAX_STREAMS` and the codec decision. **11 CR-15 adds no day here** (2.5 d raw / 3.0 d buffered unchanged): the SeaweedFS hosts, TLS and metrics are 11.4.1-T2's +0.5 d P and `filer.backup` + restore proof 11.4.1-T3's +0.5 d P (T80); this folder only asserts, drills and alerts (D21) (T74). A second capacity run after tuning is **not** budgeted. The former **`nts-admin` job image** (+0.5 d raw / +0.6 d buffered, `09 W3 CR-6`: the one-shot image for the phase-S break-glass CLI) is dropped with phase S (DEC 10.14), so this folder stays at the high-level 2.5 d.

**Open items**

- [RULED — README §10 T31] RPO ≤ 15 min / RTO ≤ 4 h are NFR-11-3 (`plans/11-worker-deployment.md` §3) and the drill's pass line.
- [RULED — README §10 T74/T80, 11 CR-15] staging/prod storage is self-hosted SeaweedFS (D21): 11.2.2-T1 creates the hosts/volumes/snapshots/`storage` subnet, 11.4.1-T2 the `weed` services, TLS, identities and metrics, 11.4.1-T1 `nt-backup-<env>` with versioning + object lock + `NoncurrentVersionExpiration` 29 d (T79/T87), 11.4.1-T3 `filer.backup` and the restore proof (T80); 11.6.1-T1 asserts and drills, 11.6.2-T1 carries the four storage rules. No provider object-storage product is scored (11.2.1-T1's hard gates are block volumes, two zones, egress price).
- [OPEN] **SeaweedFS metric names** (gateway 5xx, writable slots, volume disk — `nts_backup_lag_seconds` is the canary gauge, not a SeaweedFS metric, T85) are fixed by the pinned image 11.4.1-T2 installs; §5 carries them as [INFERENCE] until step 5 of 11.4.1-T2 records them (11 CR-15).
- [OPEN] **Alert thresholds are owned by 08 §5.2** (D17). 11.6.2-T1 ships D17's initial values; when 08 lands with different ones, raise the mismatch rather than silently re-pinning.
- [RULED — README §10 T27] No metrics route on the node: the Worker derives `image_calls_total`, `image_relay_rejections_total` from terminal `images.v1` reports and `image_relay_tokens_available` from health; `image_relay_wait_seconds` stays node-local.
- [RULED — README §10 T28] `render_node_gpu_temp_celsius{node}`.
- [RULED — README §10 T29] Script paths accepted as named here (`tools/ops/*`, `otel-collector/config.yml`, `worker-node/checks/capacity_run.py`).
- [OPEN] **`h264_nvenc` availability unverified** (E4 ⚠️); `NTS_MPT_VIDEO_CODEC` stays `libx264` unless one run passes 05 D10 *and* is faster (T4). A failed A/B is a recorded outcome.
- [OPEN] **`ADAPTER_MPT_THREADS` has no §8.1 owner** beyond 05 P6's formula; this plan recomputes it from the measured concurrency and records the value.
- [RULED — README §10 T38] **Second capacity run after tuning** (codec switch or a higher concurrency) is outside the 1.0 d; needs a TL ruling if the owner wants a confirmation run before public autopilot.
- [OPEN] **Alert delivery channel** comes from 11.2.1-T1; if that choice cannot reach the owner, the 02.8.3-T4 e-mail path is the [INFERENCE] fallback.
- [OPEN] **Deletion re-application mechanism** belongs to the soft dependency 13.3.1-T1; 11.6.1-T1 consumes it and does not define how a request is executed.

## 11. Rollout

**Order (M1).** 11.6.1-T1 and 11.6.2-T1 run as soon as their dependencies land (in parallel with the node work); 11.6.3-T1 is last in the M1 order (README §5) because it needs a real node, a real deploy path, verified network/TLS and 09.6.3.

1. **Backups first (11.6.1-T1)**, right after `prod` exists (11.2.2-T4) and the SeaweedFS hosts run with `filer.backup` started and proven (11.4.1-T3, T80) — a platform without PITR must not accept renders. Configure, assert the retention (incl. the primary storage-volume snapshots — backup volume not snapshotted, T87 — D21) (11 CR-15), run the first drill immediately (not after a month) — DB restore, object restore from `nt-backup-<env>` (noncurrent version after a delete, T79), snapshot restore — and only then start the monthly cadence from that baseline. Rollback: the drill only creates scratch resources; deleting the scratch instance/volume and re-running from a snapshot is always safe.
2. **Telemetry second (11.6.2-T1)**, after the backend choice (11.2.1-T1), the node metric source (11.3.3-T2), the storage `-metricsPort` scrape (11.4.1-T2, D21) (11 CR-15) and a running `filer.backup` to alert on (11.4.1-T3, T80): collector up, dashboards imported, 25 rules (incl. the four storage ones) loaded with delivery enabled, then the drill. Rollback: disable delivery (keep the rules) if the channel is noisy; stop the collector without touching the apps (buffered telemetry is lost, no correctness impact).
3. **Capacity test last (11.6.3-T1)**, after `nts-node deploy` (11.3.1-T3) and the network/TLS work (11.3.2-T2) are verified and 09.6.3 can dispatch. Operator-run; no public autopilot until the numbers are in. Rollback: revert the values to the provisioning defaults (one env change + unit restart), no data affected.

**Gate for the owner.** Do not enable public autopilot (02.8.3) before (a) PITR plus a green drill record exist — DB restore, one object restored from `nt-backup-<env>` and one volume snapshot restored (D21) (11 CR-15), (b) the D17 + D21 alerts are live and one has fired in a drill, (c) `NTS_MPT_MAX_CONCURRENT` and cost/video are recorded in DECISIONS §10.4 and in the cost meter (owner; 12.1.1 is deleted by DEC 10.14).
