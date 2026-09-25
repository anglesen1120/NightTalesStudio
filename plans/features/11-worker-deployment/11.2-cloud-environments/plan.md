# 11.2.1, 11.2.2, 11.2.3 — Cloud environments: provider decision, staging IaC and the production account

> Parent: [`../README.md`](../README.md) — plan 11 review, findings F01–F28, decisions D1–D19, §8.1 canonical task dependencies. High-level: [`../../../11-worker-deployment.md`](../../../11-worker-deployment.md) §4 (Epic 11.2), §5.1, §5.6, §6. Layout: [`../../../00-overview.md`](../../../00-overview.md) §10.1, §4.1.
>
> Names below (projects, types, env vars, paths, codes, config keys) are copied from `../README.md` D1–D19. Facts are cited by id, never restated. Legend: ✅ read from source · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].

## 1. Scope

| Story | Title | Phase | Estimate |
|---|---|---|---|
| 11.2.1 | Hosting evaluation — provider chosen on evidence | P · M0 | S |
| 11.2.2 | Staging environment from IaC (+ fetcher subnet, + SignalR LB, + prod instance) | P · M1 | M |
| 11.2.3 | Production on its own account and OAuth projects | S · M2 | M |

**In scope**

- **11.2.1-T1** — score 2–3 providers against `11 §5.6` with dated official price pages; record the decision (provider, region, GPU type, prices) in `DECISIONS.md`; publish the checklist the other tasks consume.
- **11.2.2-T1** — the OpenTofu code: `infra/modules/platform` (network + subnets `app`/`render`/`fetcher`, managed PostgreSQL 17 + PITR, buckets, registry, hosts, LB, DNS, TLS, VPN/bastion, remote state) and `infra/modules/render-node`, with `infra/envs/staging` as the first instantiation (D15).
- **11.2.2-T2** — the `fetcher` subnet, implementing the container/network table 02.5.2-T3 owns (P11) and proving it with that table's egress test.
- **11.2.2-T3** — LB routing for SignalR: WebSockets only for `/hubs/*`; sticky sessions only as the documented fallback (E5, P10).
- **11.2.2-T4** — `prod` from the same module in the same account (F08, O2): GPU node count 1, the two production OpenAI projects consumed as values, staging GPU count 0.
- **11.2.3-T1** (S) — prod on its own account/project set with its own keys; separate Google/TikTok OAuth projects (06/07).

**Out of scope (owned elsewhere — do not duplicate)**

| Not here | Owner |
|---|---|
| Node OS, NVIDIA driver, `cuda-libs/`, whisper model, ffmpeg pin, systemd units, bundle/deploy | 11.3.1-T1…T3 (D8–D12) |
| Node nftables, `make-adapter-cert.sh`, port-scan and node-egress tests | 11.3.2-T1/T2 (D11) |
| `worker_nodes`, `RenderNodes:Nodes` binding, `NodeHealthPoller`, `INodeSelector`, node metrics | 11.3.3-T1/T2 (D13) |
| Bucket policies, lifecycle, signed URLs | 11.4.1-T1 |
| `backend/web/e2e/node/deploy` workflows, OIDC job wiring, Dependabot | 09.1.5-T1, 10.1.5, 11.5.1-T1/T2, 11.5.3-T3 (D16) |
| Secret values, rotation runbooks, secret scanning | 11.5.2-T1 |
| Backups, restore drill, telemetry dashboards, capacity/cost | 11.6.1-T1, 11.6.2-T1, 11.6.3-T1 (D17) |
| Compose stack, mock-mpt | 11.1.1-T1/T2, 11.1.2-T1/T2 (D14) |

**Non-goals** — no Kubernetes or cluster scheduler (hosts run containers on a plain OS image [INFERENCE, 11 §5.1]); no GPU autoscaling in P (node count is a fixed module input); no CDN (11 §5.1 shows one for web static, D15's resource list has none — [OPEN] §10); no application configuration (everything arrives via the secret manager, D15).

**Deliverables** — `infra/modules/platform`, `infra/modules/render-node`, `infra/envs/staging`, `infra/envs/prod`, `infra/checks/*` + `infra/README.md`, and the dated scoring record in `DECISIONS.md`.

## 2. Confirmed facts

| Id | Obligation for this folder | Evidence |
|---|---|---|
| D1 | one module for `staging`+`prod` with per-env variables; staging has no GPU node by default (mock-mpt is its render node); a staging node is on demand for upgrades; P = everything except 11.2.3-T1 | `../README.md` D1 ✅ |
| D15 | OpenTofu (MPL-2.0); state in the provider's object storage with locking ⚠️; `infra/modules/platform` + `infra/envs/{staging,prod}`; GPU node as `infra/modules/render-node` per node (count 0 staging); resource list; secret paths `nts/<env>/<NAME>` with the per-service split `nts/<env>/{api,worker,fetcher,shared}/<NAME>` and `nts/<env>/node/<name>/<NAME>` (D20 H3/T51) | `../README.md` D15 ✅ |
| D17 | managed PITR (WAL 7 d, snapshots 30 d); copies to `nt-backup-<env>` 30 d; OTLP to a Collector in the app network; backend chosen in 11.2.1, never self-hosted AGPL | `../README.md` D17; E10, E11, E12, O6 ✅ |
| D11 | node TLS/keys/egress live on the node; this folder only builds the network carrying them (render subnet; Worker → node 8090; no node → app connections); FQDN egress rules only if 11.2.1 proves support | `../README.md` D11, F14 ✅ |
| D13 | `RenderNodes:Nodes[i]` = `{ Name, BaseUrl, Capacity, TlsCertSha256, GpuModel }`; key `RENDER_NODE_<NAME>_API_KEY`; dispatch needs Worker → node health 200 over pinned TLS | `../README.md` D13 ✅ |
| D16 | `deploy.yml` builds `sha-<git sha>` images → registry → staging rollout → prod behind the `production` environment; OIDC to env-scoped roles | `../README.md` D16; E13 ✅ |
| F08, O2 | 11.2.2-T4 (P): `prod` from the same module, same account; 11.2.3 (S) moves prod to its own account | `../README.md` F08, O2 ✅ |
| F14 | egress allow-lists are per-unit systemd/nftables, not an egress proxy; provider FQDN egress support ⚠️ until 11.2.1 | `../README.md` F14 ✅ |
| F16, E12 | no self-hosted AGPL anywhere: MinIO, Garage, Grafana, Renovate are AGPL → telemetry backend must be provider-native or hosted | `../README.md` F16, D17 ✅ |
| F28 | GPU ≥ 16 GB VRAM required, 24 GB preferred, one GPU per node | `../README.md` F28, D9 ✅ |
| P8 | separate OpenAI organizations/projects per env: `nts-text-<env>` (text) and `nts-images-<env>` (node image models only, hard budget) | `../README.md` P8; `../../../DECISIONS.md` §10.5, §10.11 ✅ |
| P10, E5 | the hub accepts WebSockets only (`skipNegotiation`); sticky sessions are only the fallback | `../README.md` P10, E5 ✅ |
| P11 | the `fetcher` container/network table is 02.5.2-T3's; 11.2.2-T2 implements and tests it | `../README.md` P11; `../../02-story-writing/02.5.1-sources-fetcher/02.5.2-T3-fetcher-host-container-network.md:118-135` ✅ |
| P12 | 11.2 high-level 3.25 d (P 2.25, S 1.0); detailed 3.75 d (P 2.75, S 1.0); +0.5 d from F08 | `../README.md` P12, §9 ✅ |
| 11 §5.1 | topology, incl. fetcher internet-only egress with no route to PostgreSQL/storage/render/metadata and no DB/storage credentials; SignalR WebSockets-only or sticky | `../../../11-worker-deployment.md` §5.1 ✅ |
| 11 §5.6 | checklist: GPU types + VRAM, hourly/monthly price from a dated official page, billing granularity, stop/start, region + latency to VN/OpenAI/YouTube, managed PostgreSQL with PITR, S3-compatible storage + egress price, private networking, data terms/GDPR/VN rules ⚠️, support, VN payment options | `../../../11-worker-deployment.md` §5.6 ✅ |
| 11 §4 (11.2.3) | separate accounts/keys, separate Google/TikTok OAuth projects (06/07), separate text OpenAI project with a hard budget, second project for node image generation | `../../../11-worker-deployment.md` §4 ✅ |
| E10 | PostgreSQL 17 PITR = base backup + continuous WAL archive → the managed offering must state both | `../README.md` E10 ✅ |
| E11 | OpenTofu MPL-2.0 acceptable; OTel Collector, Prometheus, Alertmanager, Jaeger Apache-2.0 | `../README.md` E11 ✅ |
| 00 §13 | M0 is a parallel lane; 11.2.1-T1 is an external wait | `../README.md` §5 ✅ |

Not to be assumed: provider FQDN egress support (F14 ⚠️), object-storage backend locking (D15 ⚠️), any price (11.2.1-T1 reads dated official pages), `h264_nvenc` availability (E4 ⚠️, irrelevant here).

## 3. Domain and data

**Environments** — `dev` (Compose, D14, not this folder), `staging`, `prod`. `<env>` is used literally, so every name is derivable: `nt-media-<env>`, `nt-public-<env>`, `nt-music-<env>`, `nt-backup-<env>`, `nts/<env>/<NAME>`. `staging` runs `ASPNETCORE_ENVIRONMENT=Staging` (never Development) and its render node is mock-mpt over HTTPS on 8090 with its own self-signed certificate pinned in the staging `RenderNodes:Nodes` config like a real node (D20 H2).

| Logical resource | Owner | Shape | Notes |
|---|---|---|---|
| Network | 11.2.2-T1 | one per env | private by default; no public IP except the LB and the bastion |
| Subnet `app` | 11.2.2-T1 | API ×2, Worker ×1 (the OTel Collector runs as a container on the Worker host [INFERENCE]), LB, managed PG, private gateways | the only subnet a render node may reply to |
| Subnet `render` | 11.2.2-T1 | GPU nodes from `infra/modules/render-node` | inbound TCP 8090 from `worker-sg` only (D20 H3; D11) |
| Subnet `fetcher` | 11.2.2-T2 | fetcher ×1 | internet-only egress; the only route into `app` is `api:8081` (02.5.2-T3); the fetcher host and its role belong to 11.2.2-T2 only (D20 H3) |
| Managed PostgreSQL 17 | 11.2.2-T1 | private endpoint, PITR on, WAL archive on, daily snapshots | E10; retention per D17 |
| Buckets | 11.2.2-T1 (create) / 11.4.1-T1 (policy) | `nt-media-<env>`, `nt-public-<env>`, `nt-music-<env>`, `nt-backup-<env>` | prefixes `music/`, `music-evidence/` (D15, 05 D8/S7); 11.4.1-T1 owns the `nt-media-<env>` → `nt-backup-<env>` copy with a put-only replication credential and object lock where available (D20 H9) |
| Container registry | 11.2.2-T1 | per account, per-env repositories | images `api`, `worker`, `fetcher`, `web`, `mock-mpt`, `nts-migrate` (D16) |
| App hosts | 11.2.2-T1 (API + Worker) / 11.2.2-T2 (fetcher) | API ×2 + Worker ×1, containers; the fetcher host is 11.2.2-T2's only | 2 API instances for rolling rollout (D16); security groups `api-sg`/`worker-sg`/`fetcher-sg` (D20 H3) |
| Load balancer | 11.2.2-T1 (+ T3) | HTTPS 443 (80 → 301), TLS ≥ 1.2, managed cert, `/hubs/*` WebSockets | DNS `app.<domain>` (prod), `staging.<domain>` |
| VPN / bastion | 11.2.2-T1 | operator entry for SSH and `nts-node deploy` | D11, O5 |
| Secret manager | 11.2.2-T1 (paths) / 11.5.2-T1 (values) | per-service paths `nts/<env>/{api,worker,fetcher,shared}/<NAME>` (`shared` = `DB_CONNECTION`, `DP_KEK`, `STORAGE_*`) and `nts/<env>/node/<name>/<NAME>` (D20 H3/T51) | values never in state or CI; the API role cannot read `worker/*` or `node/*` |
| GPU node | `infra/modules/render-node` | one instantiation per node; `staging` 0, `prod` 1 (F08) | node `Name` = D13's `RenderNodes:Nodes[i].Name` |

**Module interface (what other plans read).**

- `platform` inputs: `env`, `region`, `domain`, `gpu_node_count`, node list (name + GPU type), CIDR plan, image tags, telemetry backend values, DNS zone id.
- `platform` outputs (minimal, non-secret): `lb_address`, `app_base_url`, `staging_base_url`, `db_host`, `db_port`, `db_name`, `bucket_names`, `registry_url`, `subnet_ids`, `bastion_endpoint`, `otel_collector_endpoint`, `secret_path_prefix`.
- `render-node` inputs: env, subnet id, node name, GPU type, SSH/key reference, port `8090`; outputs: `private_ip`, `node_name`, `subnet_id`, `gpu_model` — the tuple the operator copies into `RenderNodes:Nodes[i]` (`BaseUrl = https://<private_ip>:8090`, `GpuModel`).
- **Secret rule:** no output and no state attribute holds a secret value. Outputs carry secret-manager *paths* only; `DB_CONNECTION` and every other D15 secret is written by 11.5.2-T1 at the provider path and read at runtime, never by OpenTofu.

**State and config** — one state per env in the provider's object storage with locking (⚠️ D15); at-rest encryption with the provider's KMS; `.tfvars` committed with placeholders only (`domain`, region, node names, `gpu_node_count`), real values via `TF_VAR_*` from an env file in `$NTS_HOME` [INFERENCE, 01 secrets rule]; `tfplan` never committed (resolved config).

## 4. Use cases

| Id | Actor | Trigger | Success |
|---|---|---|---|
| UC-1 | Owner (11.2.1-T1) | M0 | 2–3 providers scored against `11 §5.6` with dated official price pages → decision recorded in `DECISIONS.md` → `11-M0` gate closes |
| UC-2 | Operator | `tofu -chdir=infra/envs/staging apply` | network + 3 subnets (`api-sg`/`worker-sg`), PG 17 with PITR, 4 buckets, registry, 3 app hosts + the collector container, LB + DNS + TLS, VPN/bastion, secret paths exist; `gpu_node_count = 0`; `https://staging.<domain>/health/ready` → 200 with mock-mpt as render node (`ASPNETCORE_ENVIRONMENT=Staging`, mock-mpt HTTPS with a pinned self-signed cert, D20 H2) |
| UC-3 | Auditor (11.2.2-T2) | after UC-2 | the 02.5.2-T3 egress matrix holds inside the fetcher host (see §9 check 15) |
| UC-4 | Developer / 10 e2e | after UC-2 | WebSocket upgrade on `/hubs/*` → 101 with `skipNegotiation`, no affinity; the sticky rule exists but is off |
| UC-5 | Operator | after 11.5.1-T1 exists | `tofu -chdir=infra/envs/prod apply` with `gpu_node_count = 1`; prod renders on the real node (`11-node` gate); `app.<domain>` serves |
| UC-6 | Owner (11.2.3-T1) | R2 planning | prod re-created in its own account with its own keys/registry/buckets/DNS zone; OAuth clients separate from staging |
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
| `otel_collector_endpoint` | 09.9.3, 11.6.2-T1 | OTLP in the `app` subnet; node metrics reach it only through the Worker (D13, D17) |
| `staging_base_url` | 10.1.5 e2e, 11.5.1-T1 | `https://staging.<domain>` |
| DNS | public | `app.<domain>`, `staging.<domain>` → the respective LB |

`render-node.v1` (P1) and the hub transport settings are unchanged; 11.2.2-T3 changes only LB routing for `/hubs/*`.

## 6. UI

No user interface. Operator surface = the OpenTofu CLI plus the scripts this folder ships (01 D9 conventions: `set -euo pipefail`, shellcheck clean, idempotent, exit 0 ok / 1 check failed / 2 usage-environment):

- `infra/envs/<env>/{main.tf,variables.tf,outputs.tf,versions.tf,backend.tf,terraform.tfvars.example}`
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
| 11.4.1-T1 (buckets) | extends T1's buckets with lifecycle/versioning/SSE and `nt-public-<env>` readability, owns the `nt-media-<env>` → `nt-backup-<env>` replication with a put-only credential and object lock where available (D20 H9), and applies its policies through this module's storage resources, not through 11.2.2-T2 (D20 H20) |
| 11.6.1-T1 (backups) | configures PITR windows and copies into `nt-backup-<env>` inside T1's resources |
| 11.6.2-T1 (telemetry) | uses the collector endpoint and the 11.2.1 backend choice; no self-hosted AGPL (F16, O6) |
| 02.5.2-T3/T4 (fetcher) | 02.5.2-T3 adds the container to the local compose file (CR-10); 11.2.2-T2 owns the cloud subnet |
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
| buckets | private + SSE on creation; only `nt-public-<env>` is readable | 11.4.1-T1 owns the rest |
| secrets never in state/argv/tfvars/plans/logs | paths only; `TF_VAR_*` from `$NTS_HOME` | `assert-plan.sh` greps plan JSON and state for the D15 secret **values**; `git grep -I` over `infra/` |
| least privilege | per-env OIDC roles pinned to `job_workflow_ref` + environment claim (D20 H7); the staging role cannot touch prod resources; the API role cannot read `worker/*` or `node/*` (D20 H3); the fetcher host holds no DB/storage credentials | D16, E13; 11.5.2-T1 |
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
| 5 | `tofu -chdir=infra/envs/staging plan -out=tfplan && tofu -chdir=infra/envs/staging show -json tfplan > plan.json && infra/checks/assert-plan.sh staging plan.json` | exit 0: subnets exactly `app`,`render`,`fetcher`; `gpu_node_count = 0`; 4 buckets; LB 443 with TLS ≥ 1.2 and WebSockets on `/hubs/*`; no `0.0.0.0/0` ingress except LB 443/80; no public IP on app hosts; no secret value |
| 6 | same for `prod` | exit 0 plus: exactly 1 `render-node` instance |
| 7 | `git grep -nE '(sk-|BEGIN [A-Z ]*PRIVATE KEY|AKIA|password\s*=)' infra/` | no match |
| 8 | ⚠️ `tofu apply` on staging, then `tofu plan -detailed-exitcode` | apply succeeds; second plan exits 2 with an empty diff (idempotent) |
| 9 | `assert-plan.sh` against a copy with an added `0.0.0.0/0` ingress rule | exit 1 naming the resource (harness negative test) |
| 10 | ⚠️ `dig +short app.<domain>` / `staging.<domain>` | the LB address |
| 11 | ⚠️ `curl -sS -o /dev/null -w '%{http_code}' https://staging.<domain>/health/live` | 200 |
| 12 | ⚠️ `curl -sSI http://staging.<domain>` | 301 to `https://` |
| 13 | ⚠️ `infra/checks/check-ws.sh wss://staging.<domain>/hubs/<path>` | HTTP 101 upgrade; run twice, served by either instance (no affinity required) |
| 14 | ⚠️ `psql "$(secret nts/staging/DB_CONNECTION)" -c 'select 1'` from an app host / from the fetcher host | ok / refused |
| 15 | ⚠️ `infra/checks/check-egress.sh` from a probe sidecar on the fetcher host with `--network container:<fetcher>` (the shipped image has no shell, D20 H13) | the 02.5.2-T3 matrix: `api:8081` ok; `https://example.com` ok; `postgres:5432` fail; storage endpoint fail; render address fail; `169.254.169.254` fail (IMDS off / `DOCKER-USER` drop, D20 H10); app-subnet port other than 8081 fail |
| 16 | ⚠️ `tofu destroy -chdir=infra/envs/staging`, then `tofu plan` on prod | staging gone; prod plan still empty |
| 17 | ⚠️ 11.2.3-T1: apply in account B, then `tofu plan` in account A; move DNS | account A unchanged; `app.<domain>` still 200 after the move |

#8, #16, #17 are destructive by design on `staging` (and on the new prod account) — the reason `prod` keeps its own state. No check in this folder runs a build, a test suite or a render.

## 10. Tasks and re-estimate

| Task | Title | Area | Size | Depends on | Blocks |
|---|---|---|---|---|---|
| 11.2.1-T1 | Provider scoring and the dated decision record | BA | S (0.5) | — | 11.2.2-T1, 11.3.1-T1, 11.5.2-T1, 11.6.2-T1 |
| 11.2.2-T1 | `platform` + `render-node` modules and the `staging` environment | Ops | M (1.0) | 11.2.1-T1 | 11.2.2-T2, 11.2.2-T3, 11.2.2-T4, 11.3.2-T1, 11.4.1-T1, 11.5.1-T1, 11.5.2-T1 |
| 11.2.2-T2 | Fetcher subnet with internet-only egress | Ops | S (0.5) | 11.2.2-T1, 02.5.2-T3 | — |
| 11.2.2-T3 | Load balancer: WebSockets only for `/hubs/*` | Ops | XS (0.25) | 11.2.2-T1 | — |
| 11.2.2-T4 | `prod` from the same module, GPU node count 1 | Ops | S (0.5) | 11.2.2-T1, 11.5.2-T1 | 11.5.1-T1, 11.6.1-T1, 11.2.3-T1 |
| 11.2.3-T1 | Prod on its own account and its own OAuth/OpenAI projects (S) | Ops | M (1.0) | 11.2.2-T4, 11.5.3-T1 | — |

Total **3.75 d** (P 2.75, S 1.0) against the high-level 3.25 d (P 2.25, S 1.0): the +0.5 d is F08's `prod`-in-P work, matching `../README.md` §9.

**Open items**

1. [INFERENCE] Task titles are my wording — `../README.md` §8/§8.1 lists ids and sizes only. A rename is a one-line edit in seven files.
2. [INFERENCE] Task files sit flat in this folder per `../README.md` §0 ("each feature folder holds `plan.md` … and one `<task-id>-<slug>.md` per task"); if the convention is one subfolder per story, the six files move unchanged.
3. ⚠️ Provider-native resource names are generic here (11.2.1-T1 has not chosen a provider). The decision record must name them and this plan be updated in the same pass.
4. [RULED — README §10 T15] Remote-state locking is ⚠️ in D15 ("provider feature"). If unsupported: a lease object in the same bucket checked by a `tofu` pre-flight, or single-writer CI. Settled inside 11.2.2-T1.
5. [RULED — README §10 T25: 11.2.2-T1 owns the static hosting/CDN resource, 10.1.5 the build] 11 §5.1 shows a CDN for web static but D15's resource list has none and no owner is named. Candidate: 10.1.5 or 11.5.1-T1.
6. Contradiction found: `plans/11-worker-deployment.md` §4 (Epic 11.2.3) puts the two prod OpenAI projects in **S**, while P8/F08/§8.1 put `prod` in **P** (11.2.2-T4). Resolution used here: `nts-text-prod`/`nts-images-prod` are referenced by 11.2.2-T4 in P; 11.2.3-T1 in S only moves accounts/keys and creates the Google/TikTok OAuth projects.
7. [RULED — README §10 T58 / D20 H13] The fetcher image has no shell (02.5.2-T3), so live check #15 runs from a probe sidecar on the fetcher host with `--network container:<fetcher>`; the probe is not part of the shipped image.

## 11. Rollout

- **M0 (parallel lane, 00 §13):** 11.2.1-T1 — starts without a cloud account; `11-M0` closes when the decision is in `DECISIONS.md`.
- **M1 order:** 11.2.2-T1 → (11.2.2-T2 after 02.5.2-T3 lands its table; 11.2.2-T3 after T1 to avoid a write conflict on the same files) → 11.2.2-T4 after 11.5.2-T1 has filled the secret paths → then 11.5.1-T1 builds `deploy.yml` against the outputs.
- **Flags (module inputs, not app feature flags):** `gpu_node_count` (staging 0, prod 1), the node list (name + GPU type), the telemetry backend from 11.2.1-T1, and `enable_prod` = the existence of `infra/envs/prod`.
- **Gates:** `11-M0` (§10 item 1); `11-M1` "staging deploys automatically, prod by approval" needs T1 + T4 + 11.5.1-T1 (`../README.md` §7).
- **Rollback:** staging is disposable (`tofu destroy`, own state); prod changes go through a reviewed plan diff attached to the change; buckets and the database carry `lifecycle { prevent_destroy = true }` [INFERENCE: an IaC detail, not a contract], so a destroy of prod state cannot delete data; the prod DNS record is the cutover lever for 11.2.3-T1 (short TTL, e.g. 300 s [INFERENCE]) and flipping it back is that task's rollback.
- **Hand-over:** after `apply`, the operator copies the render-node tuple into D13's `RenderNodes:Nodes[i]` and `RENDER_NODE_<NAME>_API_KEY` values into the secret manager (11.5.2-T1's runbook) — the boundary between this folder and the node work (11.3.x).
