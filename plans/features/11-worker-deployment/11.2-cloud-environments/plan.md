# 11.2.1, 11.2.2 — Cloud environments: provider decision, staging IaC and the production account

> Parent: [`../README.md`](../README.md) — plan 11 review, findings F01–F28, decisions D1–D19, §8.1 canonical task dependencies. High-level: [`../../../11-worker-deployment.md`](../../../11-worker-deployment.md) §4 (Epic 11.2), §5.1, §5.6, §6. Layout: [`../../../00-overview.md`](../../../00-overview.md) §10.1, §4.1.
>
> Names below (projects, types, env vars, paths, codes, config keys) are copied from `../README.md` D1–D19. Facts are cited by id, never restated. Legend: ✅ read from source · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].

## 1. Scope

| Story | Title | Phase | Estimate |
|---|---|---|---|
| 11.2.1 | Hosting evaluation — provider chosen on evidence | P · M0 | S |
| 11.2.2 | Staging environment from IaC (+ fetcher subnet, + SignalR LB, + prod instance) | P · M1 | M |

**In scope**

- **11.2.1-T1** — score 2–3 providers against `11 §5.6` with dated official price pages; record the decision (provider, region, GPU type, prices) in `DECISIONS.md`; publish the checklist the other tasks consume.
- **11.2.2-T1** — the OpenTofu code: `infra/modules/platform` (network + subnets `app`/`render`/`fetcher`/`storage` (T77), the two self-hosted storage hosts with their encrypted volumes and `storage-sg`/`storage-backup-sg`, internal root CA (T81), registry, hosts, LB, DNS, TLS, VPN/bastion) and `infra/modules/render-node`, with `infra/envs/staging` as the first instantiation (D15, D21; 11 CR-15), plus `infra/bootstrap` for the managed PostgreSQL 17 instance + `nts_tofu` (`pg` state backend, T76/T83 — the platform module references the instance, `R`, and creates only the app database/roles).
- **11.2.2-T2** — the `fetcher` subnet, implementing the container/network table 02.5.2-T3 owns (P11) and proving it with that table's egress test.
- **11.2.2-T3** — LB routing for SignalR: WebSockets only for `/hubs/*`; sticky sessions only as the documented fallback (E5, P10).
- **11.2.2-T4** — `prod` from the same module in the same account (F08, O2): GPU node count 1, the two production OpenAI projects consumed as values, staging GPU count 0.
- ~~Prod on its own account/project set~~ — dropped with phase S (DEC 10.14); one account serves prod.

**Out of scope (owned elsewhere — do not duplicate)**

| Not here | Owner |
|---|---|
| Node OS, NVIDIA driver, `cuda-libs/`, whisper model, ffmpeg pin, systemd units, bundle/deploy | 11.3.1-T1…T3 (D8–D12) |
| Node nftables, `make-adapter-cert.sh`, port-scan and node-egress tests | 11.3.2-T1/T2 (D11) |
| `worker_nodes`, `RenderNodes:Nodes` binding, `NodeHealthPoller`, `INodeSelector`, node metrics | 11.3.3-T1/T2 (D13) |
| Bucket policies, lifecycle, signed URLs | 11.4.1-T1 |
| SeaweedFS install, gateway TLS, `s3.json` identities, admin/worker on the storage hosts | 11.4.1-T2 (D21; 11 CR-15) — `filer.backup` + the restore proof are 11.4.1-T3's (T80) |
| `backend/web/e2e/node/deploy` workflows, OIDC job wiring, Dependabot | 09.1.5-T1, 10.1.5, 11.5.1-T1/T2, 11.5.3-T3 (D16) |
| Secret values, rotation runbooks, secret scanning | 11.5.2-T1 |
| Backups, restore drill, telemetry dashboards, capacity/cost | 11.6.1-T1, 11.6.2-T1, 11.6.3-T1 (D17) |
| Compose stack, mock-mpt | 11.1.1-T1/T2, 11.1.2-T1/T2 (D14) |

**Non-goals** — no Kubernetes or cluster scheduler (hosts run containers on a plain OS image [INFERENCE, 11 §5.1]); no GPU autoscaling in P (node count is a fixed module input); no CDN (11 §5.1 shows one for web static, D15's resource list has none — [OPEN] §10); no application configuration (everything arrives via the secret manager, D15).

**Deliverables** — `infra/bootstrap` (T76, 11 CR-15), `infra/modules/platform`, `infra/modules/render-node`, `infra/envs/staging`, `infra/envs/prod`, `infra/checks/*` + `infra/README.md`, and the dated scoring record in `DECISIONS.md`.

## 2. Confirmed facts

| Id | Obligation for this folder | Evidence |
|---|---|---|
| D1 | one module for `staging`+`prod` with per-env variables; staging has no GPU node by default (mock-mpt is its render node); a staging node is on demand for upgrades; P = every task in this folder (phase S dropped, DEC 10.14) | `../README.md` D1 ✅ |
| D15 | OpenTofu (MPL-2.0); state in the `pg` backend, database `nts_tofu` (T76 — no object storage is used, 11 CR-15); `infra/modules/platform` + `infra/envs/{staging,prod}`; GPU node as `infra/modules/render-node` per node (count 0 staging); resource list; secret paths `nts/<env>/<NAME>` with the per-service split `nts/<env>/{api,worker,fetcher,shared}/<NAME>`, `nts/<env>/node/<name>/<NAME>` (D20 H3/T51) and `nts/<env>/storage/<NAME>` (storage hosts only, 11 CR-15, T91) | `../README.md` D15 ✅ |
| D17 | managed PITR (WAL 7 d, snapshots 30 d); `nt-media-<env>` copied to `nt-backup-<env>` with deleted objects aging out within 30 d of their delete (T87); OTLP to a Collector in the app network; backend chosen in 11.2.1, never self-hosted AGPL | `../README.md` D17; E10, E11, E12, O6 ✅ (11 CR-15) |
| D21 | self-hosted SeaweedFS in every env (owner O7): this folder builds `storage-<env>` + `storage-backup-<env>` (VMs in their own `storage` subnet (T77), encrypted block volume `prevent_destroy`, `storage-sg`/`storage-backup-sg` (T78/T86), DNS `storage-<env>.internal`/`storage-backup-<env>.internal`, the internal root CA (T81), daily snapshots 7 d on the primary volume only — the backup volume is not snapshotted (T87), sizing) and the LB route `media.<domain>` / `media.staging.<domain>` (`GET`/`HEAD` only, T83); **no managed object storage and no object-storage product is scored**; buckets are created by 11.4.1-T1 through the S3 API, not as IaC resources; 11.4.1-T2 installs SeaweedFS on the hosts; live gateway/bucket/media proofs are 11.4.1-T2's/T1's (T90) | `../README.md` D21 ✅ (11 CR-15) |
| T74 | 11.2.1-T1's storage hard gates: attachable block volumes with encryption at rest, snapshots and a published GB-month price, in two availability zones (or locations) for `storage-backup-<env>`, plus a published internet egress price for `media.<domain>` (T83: `media.staging.<domain>` in staging); H9's replication path is superseded by `weed filer.backup` (11.4.1-T3, T80) | `../README.md` §10 T74 ✅ (11 CR-15) |
| T76 | state backend is `pg` in `nts_tofu` (the backend locks, so T15's object-storage locking scheme is superseded); `infra/bootstrap` creates the env's managed PostgreSQL instance + `nts_tofu`, and `modules/platform` references the instance (`R`, T83) — bootstrap's local state is encrypted with OpenTofu state encryption ⚠️ (verified in 11.2.2-T1 step 1) and kept offline by the owner | `../README.md` §10 T76/T83 ✅ (11 CR-15) |
| D11 | node TLS/keys/egress live on the node; this folder only builds the network carrying them (render subnet; Worker → node 8090; no node → app connections); FQDN egress rules only if 11.2.1 proves support | `../README.md` D11, F14 ✅ |
| D13 | `RenderNodes:Nodes[i]` = `{ Name, BaseUrl, Capacity, TlsCertSha256, GpuModel }`; key `RENDER_NODE_<NAME>_API_KEY`; dispatch needs Worker → node health 200 over pinned TLS | `../README.md` D13 ✅ |
| D16 | `deploy.yml` builds `sha-<git sha>` images → registry → staging rollout → prod behind the `production` environment; OIDC to env-scoped roles | `../README.md` D16; E13 ✅ |
| F14 | egress allow-lists are per-unit systemd/nftables, not an egress proxy; provider FQDN egress support ⚠️ until 11.2.1 | `../README.md` F14 ✅ |
| F16, E12 | no self-hosted AGPL anywhere: MinIO, Garage, Grafana, Renovate are AGPL → telemetry backend must be provider-native or hosted | `../README.md` F16, D17 ✅ |
| F28 | GPU ≥ 16 GB VRAM required, 24 GB preferred, one GPU per node | `../README.md` F28, D9 ✅ |
| P8 | separate OpenAI organizations/projects per env: `nts-text-<env>` (text) and `nts-images-<env>` (node image models only, hard budget) | `../README.md` P8; `../../../DECISIONS.md` §10.5, §10.11 ✅ |
| P10, E5 | the hub accepts WebSockets only (`skipNegotiation`); sticky sessions are only the fallback | `../README.md` P10, E5 ✅ |
| P11 | the `fetcher` container/network table is 02.5.2-T3's; 11.2.2-T2 implements and tests it | `../README.md` P11; `../../02-story-writing/02.5.1-sources-fetcher/02.5.2-T3-fetcher-host-container-network.md:118-135` ✅ |
| P12 | 11.2 high-level 3.25 d (P 2.25, S 1.0); detailed 3.75 d (P 2.75, S 1.0); +0.5 d from F08 | `../README.md` P12, §9 ✅ |
| 11 §5.1 | topology, incl. fetcher internet-only egress with no route to PostgreSQL/storage/render/metadata and no DB/storage credentials; SignalR WebSockets-only or sticky | `../../../11-worker-deployment.md` §5.1 ✅ |
| 11 §5.6 | checklist: GPU types + VRAM, hourly/monthly price from a dated official page, billing granularity, stop/start, region + latency to VN/OpenAI/YouTube, managed PostgreSQL with PITR, attachable encrypted block volumes with snapshots and a GB-month price in two zones and an internet egress price for `media.<domain>` (T74; no object-storage product, 11 CR-15), private networking, data terms/GDPR/VN rules ⚠️, support, VN payment options | `../../../11-worker-deployment.md` §5.6 ✅ |
| 11 §4 (former 11.2.3) | separate accounts/keys and OAuth projects — dropped with phase S (DEC 10.14); the separate text OpenAI project with a hard budget stays a P config duty of 11.2.2-T1 | `../../../11-worker-deployment.md` §4 ✅ |
| E10 | PostgreSQL 17 PITR = base backup + continuous WAL archive → the managed offering must state both | `../README.md` E10 ✅ |
| E11 | OpenTofu MPL-2.0 acceptable; OTel Collector, Prometheus, Alertmanager, Jaeger Apache-2.0 | `../README.md` E11 ✅ |
| 00 §13 | M0 is a parallel lane; 11.2.1-T1 is an external wait | `../README.md` §5 ✅ |

Not to be assumed: provider FQDN egress support (F14 ⚠️), two-zone/location availability of the block volume for `storage-backup-<env>` (T74 ⚠️ until 11.2.1-T1 rules), OpenTofu state encryption on the `pg` backend (T76 ⚠️, verified in 11.2.2-T1 step 1; 11 CR-15), any price (11.2.1-T1 reads dated official pages), `h264_nvenc` availability (E4 ⚠️, irrelevant here).

## 3. Domain and data

**Environments** — `dev` (Compose, D14, not this folder), `staging`, `prod`. `<env>` is used literally, so every name is derivable: `nt-media-<env>`, `nt-public-<env>`, `nt-music-<env>`, `nt-backup-<env>`, `storage-<env>`/`storage-backup-<env>`, `storage-<env>.internal`/`storage-backup-<env>.internal`, `nts/<env>/<NAME>` (11 CR-15); the media host is `media.staging.<domain>` (staging) / `media.<domain>` (prod) (T83). `staging` runs `ASPNETCORE_ENVIRONMENT=Staging` (never Development) and its render node is mock-mpt over HTTPS on 8090 with its own self-signed certificate pinned in the staging `RenderNodes:Nodes` config like a real node (D20 H2; node pinning is the D11 node pattern, unrelated to the T81 internal CA).

| Logical resource | Owner | Shape | Notes |
|---|---|---|---|
| Network | 11.2.2-T1 | one per env | private by default; no public IP except the LB and the bastion |
| Subnet `app` | 11.2.2-T1 | API ×2, Worker ×1 (the OTel Collector runs as a container on the Worker host [INFERENCE]), LB, private gateways | the only subnet a render node may reply to |
| Subnet `render` | 11.2.2-T1 | GPU nodes from `infra/modules/render-node` | inbound TCP 8090 from `worker-sg` only (D20 H3; D11) |
| Subnet `fetcher` | 11.2.2-T2 | fetcher ×1 | internet-only egress; the only route into `app` is `api:8081` (02.5.2-T3); the fetcher host and its role belong to 11.2.2-T2 only (D20 H3); `fetcher-sg` is never a `storage-sg` source (T77) |
| Subnet `storage` | 11.2.2-T1 | `storage-<env>` and `storage-backup-<env>` VMs (in another zone/location for the backup) | keeps the hosts off the `app` subnet so D11's node "drop to the app subnet" rule is untouched (T77) |
| Managed PostgreSQL 17 | `infra/bootstrap` (T76/T83) creates it; the platform module references it (`R`) and creates the app database/roles | private endpoint, PITR on, WAL archive on, daily snapshots, `prevent_destroy`; also hosts the `pg`-backend database `nts_tofu` for the env state | E10; retention per D17; T76, T83, 11 CR-15 |
| `storage-<env>` host | 11.2.2-T1 (VM) / 11.4.1-T2 (software) | one VM in the `storage` subnet (T77), one attached block volume `/var/lib/nts/seaweedfs` (XFS, provider-encrypted, `prevent_destroy`), `storage-sg`, DNS `storage-<env>.internal`, daily volume snapshots 7 d; 50 GB staging / 200 GB prod | D21 ✅; the gateway is published on TCP 443 and is the only storage port reachable off the host (T77/T78, 11 CR-15) |
| `storage-backup-<env>` host | 11.2.2-T1 (VM) / 11.4.1-T2 (software) | a second VM in another availability zone (or location) with its own volume sized like the primary — never snapshotted (T87) — and `storage-backup-sg` (22/443 from the VPN/bastion range + 9327 from `worker-sg` only, T86), DNS `storage-backup-<env>.internal`; holds only `nt-backup-<env>` | D21 ✅; `weed filer.backup -filerProxy` reads the primary filer (8888/18888 from `storage-backup-sg` only) and writes here (H9 amended by T74/T78; 11 CR-15) |
| Buckets | 11.4.1-T1 (create + policy) | `nt-media-<env>`, `nt-public-<env>`, `nt-music-<env>`, `nt-backup-<env>` on `storage-<env>`/`storage-backup-<env>`, through the S3 API — **not** IaC resources | prefixes `music/`, `music-evidence/` (D15, 05 D8/S7); 11.4.1-T1 owns SSE defaults, CORS, lifecycle and the object lock on `nt-backup-<env>` (D21, D20 H9/H20; 11 CR-15) |
| Container registry | 11.2.2-T1 | per account, per-env repositories | images `api`, `worker`, `fetcher`, `web`, `mock-mpt`, `nts-migrate` (D16) |
| App hosts | 11.2.2-T1 (API + Worker) / 11.2.2-T2 (fetcher) | API ×2 + Worker ×1, containers; the fetcher host is 11.2.2-T2's only | 2 API instances for rolling rollout (D16); security groups `api-sg`/`worker-sg`/`fetcher-sg` (D20 H3) |
| Load balancer | 11.2.2-T1 (+ T3) | HTTPS 443 (80 → 301), TLS ≥ 1.2, managed cert, `/hubs/*` WebSockets; `media.<domain>` (prod) / `media.staging.<domain>` (staging, T83) → the `storage-<env>` S3 gateway with methods `GET`/`HEAD` only, `Host` unchanged (T75) | DNS `app.<domain>` (prod), `staging.<domain>`, the media host (11 CR-15) |
| VPN / bastion | 11.2.2-T1 | operator entry for SSH and `nts-node deploy` | D11, O5 |
| Secret manager | 11.2.2-T1 (paths) / 11.5.2-T1 (values) | per-service paths `nts/<env>/{api,worker,fetcher,shared}/<NAME>` (`shared` = `DB_CONNECTION`, `DP_KEK`, `STORAGE_*`) and `nts/<env>/node/<name>/<NAME>` (D20 H3/T51) and `nts/<env>/storage/<NAME>` (`BACKUP_ACCESS_KEY`, `NTS_CANARY_WRITE`, `NTS_OPS_READ`, `NTS_BACKUP_PROBE`, `NTS_OPS_ADMIN` — each `NAME`/`_SECRET` pair — and `SEAWEEDFS_JWT_SIGNING_KEY`; T91/T92/T95) | values never in state or CI; the API role cannot read `worker/*`/`node/*`/`storage/*`; only the storage hosts' role reads `storage/*` + the T94 exact names (`shared/STORAGE_ACCESS_KEY`/`STORAGE_SECRET_KEY`, `NTS_MUSIC_WRITE_KEY`/`_SECRET`, `NTS_MUSIC_READ_KEY`/`_SECRET`, `NTS_PREVIEW_WRITE_KEY`/`_SECRET`) (T91/T94) |
| GPU node | `infra/modules/render-node` | one instantiation per node; `staging` 0, `prod` 1 (F08) | node `Name` = D13's `RenderNodes:Nodes[i].Name` |
| Internal root CA | 11.2.2-T1 (+ bootstrap) | provider private CA, else an owner-generated offline root; issues `storage-<env>.internal` + `storage-backup-<env>.internal` | T81: installed in host/node trust stores and mounted into containers; no client pins a cert; operator Mac uses `AWS_CA_BUNDLE`/`REQUESTS_CA_BUNDLE` |

**Module interface (what other plans read).**

- `platform` inputs: `env`, `region`, `domain`, `gpu_node_count`, node list (name + GPU type), CIDR plan, image tags, telemetry backend values, DNS zone id.
- `platform` outputs (minimal, non-secret): `lb_address`, `app_base_url`, `staging_base_url`, `db_host`, `db_port`, `db_name`, `bucket_names` (env-derived constants — the buckets are 11.4.1-T1's, created over the S3 API, D21), `storage_hosts` (list of `{ name, private_ip }`), `storage_endpoint` = `https://storage-<env>.internal` (`Storage:ServiceUrl`; the gateway is published on TCP 443, T77/T78), `storage_public_url` = `https://media.<domain>` (prod) / `https://media.staging.<domain>` (staging, T83) (`Storage:PublicServiceUrl`), `registry_url`, `subnet_ids`, `bastion_endpoint`, `otel_collector_endpoint`, `secret_path_prefix` (11 CR-15).
- `render-node` inputs: env, subnet id, node name, GPU type, SSH/key reference, port `8090`; outputs: `private_ip`, `node_name`, `subnet_id`, `gpu_model` — the tuple the operator copies into `RenderNodes:Nodes[i]` (`BaseUrl = https://<private_ip>:8090`, `GpuModel`).
- **Secret rule:** no output and no state attribute holds a secret value. Outputs carry secret-manager *paths* only; `DB_CONNECTION` and every other D15 secret is written by 11.5.2-T1 at the provider path and read at runtime, never by OpenTofu.

**State and config** — one state per env in the `pg` backend (T76, 11 CR-15): the database `nts_tofu`, which also locks; `infra/bootstrap` holds only the PostgreSQL instance + `nts_tofu`, its local state encrypted with OpenTofu state encryption ⚠️ (verified in 11.2.2-T1 step 1) and kept offline by the owner; `.tfvars` committed with placeholders only (`domain`, region, node names, `gpu_node_count`), real values via `TF_VAR_*` from an env file in `$NTS_HOME` [INFERENCE, 01 secrets rule]; `tfplan` never committed (resolved config).

## 4. Use cases

| Id | Actor | Trigger | Success |
|---|---|---|---|
| UC-1 | Owner (11.2.1-T1) | M0 | 2–3 providers scored against `11 §5.6` with dated official price pages → decision recorded in `DECISIONS.md` → `11-M0` gate closes |
| UC-2 | Operator | `tofu -chdir=infra/envs/staging apply` | network + 4 subnets (`api-sg`/`worker-sg`/`storage-sg`/`storage-backup-sg`), PG 17 with PITR (the `infra/bootstrap` instance, `R`, T83), the two storage hosts in the `storage` subnet + encrypted volumes + the internal CA root (T77/T81), registry, 3 app hosts + the collector container, LB + DNS + TLS (`media.staging.<domain>` included, T83), VPN/bastion, secret paths exist (11 CR-15); `gpu_node_count = 0`; `https://staging.<domain>/health/ready` → 200 with mock-mpt as render node (`ASPNETCORE_ENVIRONMENT=Staging`, mock-mpt HTTPS with a pinned self-signed cert, D20 H2) |
| UC-3 | Auditor (11.2.2-T2) | after UC-2 | the 02.5.2-T3 egress matrix holds inside the fetcher host (see §9 check 15) |
| UC-4 | Developer / 10 e2e | after UC-2 | WebSocket upgrade on `/hubs/*` → 101 with `skipNegotiation`, no affinity; the sticky rule exists but is off |
| UC-5 | Operator | after 11.5.1-T1 exists | `tofu -chdir=infra/envs/prod apply` with `gpu_node_count = 1`; prod renders on the real node (`11-node` gate); `app.<domain>` serves |
| UC-7 | Operator | drift or disaster | `tofu plan` on an applied env is empty; `tofu destroy` on `staging` leaves `prod` untouched (separate state) |

## 5. API and events

No HTTP API and no event type is added here. The interfaces are IaC-level:

| Interface | Consumed by | Contract |
|---|---|---|
| applied `render-node` module → `RenderNodes:Nodes[i]` | 11.3.3-T1 (D13) | `BaseUrl` (private IP, HTTPS 8090), `GpuModel`, `Name`; the TLS fingerprint is produced **on the node** by `make-adapter-cert.sh` (11.3.2-T2), never by IaC |
| `RENDER_NODE_<NAME>_API_KEY` path | 11.5.2-T1 → Worker config | path `nts/<env>/<NAME>`; value never read by IaC |
| `registry_url`, tags `sha-<git sha>` | 11.5.1-T1 (`deploy.yml`) | pull with an env-scoped OIDC role |
| OIDC role per env | 11.5.1-T1 | staging job and `production`-environment job assume different roles (D16, E13) |
| `db_host`/`db_port`/`db_name`, bucket names, `app_base_url` | 09/10 config, `deploy.yml` smoke | read-only plan outputs; smoke = `GET https://<host>/health/live`, `/health/ready` |
| `storage_hosts`, `storage_endpoint`, `storage_public_url` | 11.4.1-T1/T2, 09.7.2-T1, 11.5.2-T1 | `https://storage-<env>.internal` (S3 gateway on TCP 443, `Storage:ServiceUrl`) and `https://media.<domain>` / `https://media.staging.<domain>` (`Storage:PublicServiceUrl`, presign-only through the LB with `GET`/`HEAD`, T75/T83); names/endpoints only, never a key (11 CR-15) |
| `otel_collector_endpoint` | 09.9.3, 11.6.2-T1 | OTLP in the `app` subnet; node metrics reach it only through the Worker (D13, D17) |
| `staging_base_url` | 10.1.5 e2e, 11.5.1-T1 | `https://staging.<domain>` |
| DNS | public | `app.<domain>`, `staging.<domain>` → the respective LB; `media.<domain>` / `media.staging.<domain>` → the `storage-<env>` gateway via the LB (T75, T83, 11 CR-15); private zone `storage-<env>.internal`/`storage-backup-<env>.internal` → the hosts' private addresses (T77) |

`render-node.v1` (P1) and the hub transport settings are unchanged; 11.2.2-T3 changes only LB routing for `/hubs/*`.

## 6. UI

No user interface. Operator surface = the OpenTofu CLI plus the scripts this folder ships (01 D9 conventions: `set -euo pipefail`, shellcheck clean, idempotent, exit 0 ok / 1 check failed / 2 usage-environment):

- `infra/envs/<env>/{main.tf,variables.tf,outputs.tf,versions.tf,backend.tf,terraform.tfvars.example}`
- `infra/bootstrap/{main.tf,versions.tf,backend.tf}` — the PostgreSQL instance + `nts_tofu` database, local encrypted state (T76, 11 CR-15)
- `infra/modules/platform/…`, `infra/modules/render-node/…`
- `infra/checks/assert-plan.sh <env> <plan.json>` — `tofu show -json` assertions (§8, §9)
- `infra/checks/check-ws.sh <url>` — `/hubs/*` WebSocket-upgrade probe
- `infra/checks/check-egress.sh` — the 02.5.2-T3 egress matrix, run from a probe sidecar sharing the fetcher container's network namespace (`--network container:<fetcher>`, D20 H13)
- `infra/README.md` — one page: what each env creates, apply order, `TF_VAR_*` file location, how the node tuple feeds D13's config. It restates D15/D16 only; no new architecture.

## 7. Jobs and integrations

| Integration | Contract |
|---|---|
| 11.5.2-T1 (secrets) | 11.2.2-T1 creates the paths, 11.5.2-T1 fills them; `prod` (11.2.2-T4) depends on it (§8.1) |
| 11.5.1-T1 (`deploy.yml`) | reads registry URL, env-scoped OIDC role, `staging_base_url`; prod job behind the `production` environment with the owner as required reviewer (D16); both environments allow branch `main` only and each role's OIDC trust pins `job_workflow_ref = <owner>/<repo>/.github/workflows/deploy.yml@refs/heads/main` plus the environment claim (D20 H7) |
| 11.4.1-T1 (buckets) | creates the four buckets on the storage hosts this module builds (SSE-S3 default, CORS, lifecycle, object lock on `nt-backup-<env>`, `nt-public-<env>` readability) through the S3 API — not IaC resources (D21) — and applies nothing through 11.2.2-T2 (D20 H20; 11 CR-15) |
| 11.4.1-T2 (SeaweedFS hosts) | installs and runs SeaweedFS on the two VMs (compose + systemd), gateway TLS on TCP 443 (T77/T78), `s3.json`/`security.toml`/`replication.toml` rendered by `sync-identities.sh` (T82), `weed admin`/`weed worker`, metrics 9327 (D21, T80; 11 CR-15) |
| 11.4.1-T3 (backup + restore) | starts `weed filer.backup -filerPath=/buckets/nt-media-<env> -filerProxy -doDeleteFiles=true` on `storage-backup-<env>` writing to `nt-backup-<env>` (`[sink.s3] is_incremental = false` — `true` drops deletes and date-prefixes keys, T85; one `-initialSnapshot` start after a volume loss/rebuild, T89) with `BACKUP_ACCESS_KEY` = `Write` on `nt-backup-<env>` only, never `BypassGovernanceRetention`/`Admin` (SeaweedFS cannot separate `DeleteObjectVersion`, so the object lock is the delete guard, T88), and proves the lifecycle job + media restore (D21, T80; 11 CR-15) |
| 11.6.1-T1 (backups) | configures the PITR windows and asserts the `weed filer.backup` copy into `nt-backup-<env>` before running the restore drill (D21, T80; 11 CR-15) |
| 11.6.2-T1 (telemetry) | uses the collector endpoint and the 11.2.1 backend choice; no self-hosted AGPL (F16, O6) |
| 02.5.2-T3/T4 (fetcher) | 02.5.2-T3 adds the container to the optional local compose file only (daily dev is native, 11 CR-16); 11.2.2-T2 owns the cloud subnet |
| 11.3.2-T1/T2 (node firewall) | needs the render subnet + Worker CIDR; T1 outputs `subnet_ids` |
| 11.3.1-T3 (`nts-node deploy`) | the T1 bastion/VPN endpoint is the transport (O5) |

No scheduled job lives here. `tofu apply` is operator-driven; CI may run a plan review (`tofu plan -detailed-exitcode`) but never `apply` in P [INFERENCE].

## 8. Security and privacy

| Rule | Enforced by | Verified by |
|---|---|---|
| S1 no node port internet-reachable | render subnet has no public IP and no `0.0.0.0/0` ingress; 8090 only from `worker-sg` (D20 H3) | `assert-plan.sh`; external scan is 11.3.2-T2's |
| deny-by-default | every ingress/egress rule enumerated; no "allow all" egress except the fetcher's 80/443 | `assert-plan.sh` fails on any `0.0.0.0/0` ingress other than LB 443/80 |
| metadata endpoint | every host runs with IMDS disabled, or IMDSv2 with hop limit 1 where the provider needs it at boot; the fetcher host adds a `DOCKER-USER` drop to `169.254.169.254` (D20 H10) | `check-egress.sh` (fetcher probe to `169.254.169.254`); `assert-plan.sh` (IMDS setting + no public IP on app hosts) |
| LB exposure | 443 + 80→301 only; TLS ≥ 1.2, provider-managed cert | `curl -sSI http://<host>` → 301; `openssl s_client -tls1_1` fails |
| database | private endpoint, no public access, storage encrypted, app role without superuser | plan JSON assertion + 09 connection string |
| buckets | created through the S3 API on `storage-<env>` (no IaC resource, D21); private by default, SSE-S3 on; only `nt-public-<env>` is readable | 11.4.1-T1 owns this |
| storage hosts and volumes | volumes provider-encrypted and `prevent_destroy`; the gateway on TCP 443 is the only storage service reachable off the host (from `api-sg`/`worker-sg`, the render subnet and the LB) while filer 8888/18888 open only to `storage-backup-sg` (`filer.backup -filerProxy`), metrics 9327 only to `worker-sg`, 22/443 only to the VPN/bastion range (`storage-sg` and `storage-backup-sg`, T86), and master/volume/admin ports stay host-local; nothing from `fetcher-sg` (T77/T78); `storage-backup-sg` has no other inbound and `worker-sg` adds inbound 4318 from `storage-backup-sg` for the `nts_backup_lag_seconds` push (T85/T86); writes need an `s3.json` identity; the internal CA root lives in every trust store (T81); daily volume snapshots 7 d on the primary volume only (T87) (D21, 11 CR-15) | plan JSON assertion; `check-storage-host.sh` (11.4.1-T2) |
| OpenTofu state | `pg` backend in `nts_tofu`; the app role has no rights on that database [INFERENCE]; bootstrap state encrypted and kept offline (T76, 11 CR-15) | `assert-plan.sh` + 11.5.2-T1 |
| secrets never in state/argv/tfvars/plans/logs | paths only; `TF_VAR_*` from `$NTS_HOME` | `assert-plan.sh` greps plan JSON and state for the D15 secret **values**; `git grep -I` over `infra/` |
| least privilege | per-env OIDC roles pinned to `job_workflow_ref` + environment claim (D20 H7); the staging role cannot touch prod resources; the API role cannot read `worker/*`, `node/*` or `storage/*` (D20 H3, T91); the fetcher host holds no DB/storage credentials | D16, E13; 11.5.2-T1 |
| operator access | SSH only via bastion/VPN; no operator key on prod hosts | plan review [INFERENCE from O5] |
| data terms / VN rules ⚠️ | provider data-processing terms, region, retention are scored in 11.2.1-T1 | the `DECISIONS.md` record |
| logs | no node logs shipped (D8); LB access log keeps path + status only | plan review |

Privacy: this folder stores no personal data itself; PostgreSQL does (PITR per D17). The retention windows here (WAL 7 d, snapshots 30 d) are what plan 13 must disclose in the deletion scopes.

## 9. Test plan

Checks needing a live account are ⚠️ and run at implementation; the rest run on the planning machine's `tofu` binary.

| # | Command | Expected |
|---|---|---|
| 1 | `tofu fmt -check -recursive infra/` | exit 0, no diff |
| 2 | `tofu -chdir=infra/envs/staging init -backend=false && tofu -chdir=infra/envs/staging validate` | `Success! The configuration is valid.` |
| 3 | same for `infra/envs/prod` | success |
| 4 | `shellcheck infra/checks/*.sh` (also part of 11.5.1-T2's `node.yml`) | exit 0 |
| 5 | `tofu -chdir=infra/envs/staging plan -out=tfplan && tofu -chdir=infra/envs/staging show -json tfplan > plan.json && infra/checks/assert-plan.sh staging plan.json` | exit 0: subnets exactly `app`,`render`,`fetcher`,`storage` (T77); `gpu_node_count = 0`; two storage instances in the `storage` subnet in two zones/locations, each with one attached encrypted volume carrying `prevent_destroy`, the 7-day snapshot retention on the primary volume and none on the backup volume (T87); `storage-sg` opening 443 to `api-sg`/`worker-sg`/the render subnet/the LB plus 22/443 to the VPN/bastion range, 8888/18888 from `storage-backup-sg` only, 9327 from `worker-sg` only — never `fetcher-sg`, `storage-backup-sg` only 22/443 to the VPN/bastion range + 9327 from `worker-sg`, `worker-sg` inbound 4318 from `storage-backup-sg` (T78/T85/T86); DNS `storage-<env>.internal`/`storage-backup-<env>.internal`; the internal CA root in every host trust store (T81); LB 443 with TLS ≥ 1.2, WebSockets on `/hubs/*` and the `media.staging.<domain>` route limited to `GET`/`HEAD` (T83); no `0.0.0.0/0` ingress except LB 443/80; no public IP on app or storage hosts; no secret value (11 CR-15) |
| 6 | same for `prod` | exit 0 plus: exactly 1 `render-node` instance and the `storage-prod`/`storage-backup-prod` resources with `media.<domain>` DNS (11 CR-15) |
| 7 | `git grep -nE '(sk-|BEGIN [A-Z ]*PRIVATE KEY|AKIA|password\s*=)' infra/` | no match |
| 8 | ⚠️ `tofu apply` on staging, then `tofu plan -detailed-exitcode` | apply succeeds; second plan exits 2 with an empty diff (idempotent) |
| 9 | `assert-plan.sh` against a copy with an added `0.0.0.0/0` ingress rule | exit 1 naming the resource (harness negative test) |
| 10 | ⚠️ `dig +short app.<domain>` / `staging.<domain>` | the LB address |
| 11 | ⚠️ `curl -sS -o /dev/null -w '%{http_code}' https://staging.<domain>/health/live` | 200 |
| 12 | ⚠️ `curl -sSI http://staging.<domain>` | 301 to `https://` |
| 13 | ⚠️ `infra/checks/check-ws.sh wss://staging.<domain>/hubs/<path>` | HTTP 101 upgrade; run twice, served by either instance (no affinity required) |
| 14 | ⚠️ `psql "$(secret nts/staging/DB_CONNECTION)" -c 'select 1'` from an app host / from the fetcher host | ok / refused |
| 15 | ⚠️ `infra/checks/check-egress.sh` from a probe sidecar on the fetcher host with `--network container:<fetcher>` (the shipped image has no shell, D20 H13) | the 02.5.2-T3 matrix: `api:8081` ok; `https://example.com` ok; `postgres:5432` fail; storage endpoint fail; render address fail; `169.254.169.254` fail (IMDS off / `DOCKER-USER` drop, D20 H10); app-subnet port other than 8081 fail |
| 16 | ⚠️ `tofu destroy -chdir=infra/envs/staging` after lifting the `prevent_destroy` guards on the disposable staging storage volumes (`infra/envs/<env>` guards the volumes only; the PG guard lives in `infra/bootstrap`, T90; D21, 11 CR-15), then `tofu plan` on prod | staging gone; prod plan still empty |
| 18 | ⚠️ `curl -sS -o /dev/null -w '%{http_code}' -X PUT https://media.staging.<domain>/nt-public-staging/probe` (unsigned) | PUT → 405 (the LB carries `GET`/`HEAD` only, T75); the live GET media proof is 11.4.1-T1's acceptance — this folder's checks stay infrastructure-only (T83, T90, 11 CR-15) |

#8, #16, #17 are destructive by design on `staging` (and on the new prod account) — the reason `prod` keeps its own state. No check in this folder runs a build, a test suite or a render.

## 10. Tasks and re-estimate

| Task | Title | Area | Size | Depends on | Blocks |
|---|---|---|---|---|---|
| 11.2.1-T1 | Provider scoring and the dated decision record | BA | S (0.5) | — | 11.2.2-T1, 11.3.1-T1, 11.5.2-T1, 11.6.2-T1 |
| 11.2.2-T1 | `platform` + `render-node` modules and the `staging` environment | Ops | M (1.0) | 11.2.1-T1 | 11.2.2-T2, 11.2.2-T3, 11.2.2-T4, 11.3.2-T1, 11.4.1-T2 (11 CR-15), 11.5.1-T1, 11.5.2-T1 |
| 11.2.2-T2 | Fetcher subnet with internet-only egress | Ops | S (0.5) | 11.2.2-T1, 02.5.2-T3 | — |
| 11.2.2-T3 | Load balancer: WebSockets only for `/hubs/*` | Ops | XS (0.25) | 11.2.2-T1 | — |
| 11.2.2-T4 | `prod` from the same module, GPU node count 1 | Ops | S (0.5) | 11.2.2-T1, 11.5.2-T1 | 11.5.1-T1, 11.6.1-T1, 11.4.1-T2 (11 CR-15) |

Total **3.75 d** (P 2.75, S 1.0) against the high-level 3.25 d (P 2.25, S 1.0): the +0.5 d is F08's `prod`-in-P work, matching `../README.md` §9.

**Open items**

1. [INFERENCE] Task titles are my wording — `../README.md` §8/§8.1 lists ids and sizes only. A rename is a one-line edit in seven files.
2. [INFERENCE] Task files sit flat in this folder per `../README.md` §0 ("each feature folder holds `plan.md` … and one `<task-id>-<slug>.md` per task"); if the convention is one subfolder per story, the six files move unchanged.
3. Resolved in part (11.2.1-T1, **proposed** 2026-09-29): the provider is chosen — Azure `southeastasia` (Singapore), with GCP `asia-southeast1` as the only other non-excluded finalist (blocked on the PostgreSQL PITR evidence) and AWS scored but ineligible under the locked D21 "no AWS account" decision — and the provider-native names (Azure) for network/subnets/NSGs, managed PostgreSQL, the two storage VMs, their managed disks, the snapshot policy, the internal CA (T81 offline root), registry, secret manager, Application Gateway/DNS and VPN/bastion are listed in `DECISIONS.md` §10.4.1 and `infra/scoring/README.md`. The tables above stay generic until the TL applies them in the same pass as 11.2.2-T1; the ⚠️ provider question is closed by that block, whose latency line is the pre-apply check for 11.2.2-T1 step 1.
4. [RULED — README §10 T76/T83 (supersedes T15), 11 CR-15] State lives in the `pg` backend, database `nts_tofu`; the backend locks, so T15's object-storage locking scheme is superseded. `infra/bootstrap` creates the env's PostgreSQL instance + `nts_tofu` (the platform module references the instance and creates only the app database/roles, T83); bootstrap's local state is encrypted with OpenTofu state encryption ⚠️ (verified in 11.2.2-T1 step 1) and kept offline by the owner.
5. [RULED — README §10 T25: 11.2.2-T1 owns the static hosting/CDN resource, 10.1.5 the build] 11 §5.1 shows a CDN for web static but D15's resource list has none and no owner is named. Candidate: 10.1.5 or 11.5.1-T1.
7. [RULED — README §10 T58 / D20 H13] The fetcher image has no shell (02.5.2-T3), so live check #15 runs from a probe sidecar on the fetcher host with `--network container:<fetcher>`; the probe is not part of the shipped image.

## 11. Rollout

- **M0 (parallel lane, 00 §13):** 11.2.1-T1 — starts without a cloud account; `11-M0` closes when the decision is in `DECISIONS.md`.
- **M1 order:** `infra/bootstrap` (T76: the PostgreSQL instance + `nts_tofu`, local state encrypted and offline) → 11.2.2-T1 → (11.2.2-T2 after 02.5.2-T3 lands its table; 11.2.2-T3 after T1 to avoid a write conflict on the same files) → 11.2.2-T4 after 11.5.2-T1 has filled the secret paths → then 11.5.1-T1 builds `deploy.yml` against the outputs.
- **Flags (module inputs, not app feature flags):** `gpu_node_count` (staging 0, prod 1), the node list (name + GPU type), the telemetry backend from 11.2.1-T1, and `enable_prod` = the existence of `infra/envs/prod`.
- **Gates:** `11-M0` (§10 item 1); `11-M1` "staging deploys automatically, prod by approval" needs T1 + T4 + 11.5.1-T1 (`../README.md` §7).
- **Rollback:** staging is disposable (`tofu destroy`, own state); prod changes go through a reviewed plan diff attached to the change; the storage volumes carry `lifecycle { prevent_destroy = true }` [INFERENCE: an IaC detail, not a contract; the buckets are not IaC resources, D21, so the guard is on the volumes — `infra/envs/<env>` guards the volumes only while the PG instance guard lives in `infra/bootstrap`, T90, 11 CR-15] and the PG instance carries it in the `infra/bootstrap` state (T83), so a destroy of prod state cannot delete data (11 CR-15); the prod DNS record keeps a short TTL (e.g. 300 s [INFERENCE]) so any future cutover stays cheap, although no account move is planned (DEC 10.14).
- **Hand-over:** after `apply`, the operator copies the render-node tuple into D13's `RenderNodes:Nodes[i]` and `RENDER_NODE_<NAME>_API_KEY` values into the secret manager (11.5.2-T1's runbook) — the boundary between this folder and the node work (11.3.x).
