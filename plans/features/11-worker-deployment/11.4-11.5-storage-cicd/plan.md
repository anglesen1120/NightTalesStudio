# 11.4.1, 11.5.1, 11.5.2, 11.5.3 — Object storage, application and node CI/CD, secrets, supply chain

> Parent: ../README.md — plan 11 Tech Lead review (facts K/E/P, findings F01–F28, decisions D1–D19, CR-1…CR-11, §8.1 canonical dependency table).
> Refines `../../11-worker-deployment.md` §4 Epics 11.4/11.5, §6 (replaced by `../README.md` §8.1), §7 gates, §11 estimate. Template `../../00-overview.md` §10.1.
> Legend: ✅ verified (§1 id or `file:line`) · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE]. Documentation only: no workflow, policy or script exists yet — everything below is specified.

## 1. Scope

Nine tasks, one file each (11 CR-15 added `11.4.1-T2`; T80 added `11.4.1-T3`):

| Story | Task | One line |
|---|---|---|
| 11.4.1 | `11.4.1-T1` | Bucket inventory, prefixes, lifecycle rules, SSE-S3, CORS, least-privilege identities, negative-access proof, `identities.template.json` (T82), and `nt-backup-<env>` with versioning + object lock (GOVERNANCE 29 d, `NoncurrentVersionExpiration` 29 d, T87) + the `Write`-only backup key (T88), the `NTS_CANARY_WRITE` `_canary/` identity (T85), the `NTS_OPS_READ` restore reader (T91), the `NTS_BACKUP_PROBE` `_canary/` gauge reader (T95) and the apply-window `NTS_OPS_ADMIN` (T92) (D20 H9, D21), applied over the S3 API by `apply-buckets.sh` |
| 11.4.1 | `11.4.1-T2` (S) | Self-hosted SeaweedFS on `storage-<env>` + `storage-backup-<env>` (`storage` subnet): compose + systemd, gateway TLS published on 443 (container 8333, `-s3.port.https` unset; T77/T78), internal-CA certs (T81), `sync-identities.sh` rendering `s3.json`/`security.toml`/`replication.toml` (T82), `weed admin`/`worker` lifecycle runner, metrics (D21, T80, 11 CR-15) |
| 11.4.1 | `11.4.1-T3` (S) | Media backup copy: dev-compose delete-propagation proof (`-doDeleteFiles=true`, versioned + locked `nt-backup-<env>`), `weed filer.backup -filerPath=/buckets/nt-media-<env> -filerProxy` with `[sink.s3] is_incremental = false` (T85) on `storage-backup-<env>` in staging + prod, `nts-storage-canary` + the `nts_backup_lag_seconds` gauge (T85), lifecycle execution + restore proof (T80, T87-T89, 11 CR-15) |
| 11.5.1 | `11.5.1-T1` | `e2e.yml` (compose + mock-mpt) and `deploy.yml` (five images, migration bundle before rollout, staging auto + smoke, prod with required reviewer, environments `main`-only + OIDC `job_workflow_ref` (D20 H7), migration connection from the environment (D20 H11), rolling Api, Worker stop/start, post-deploy smoke) |
| 11.5.1 | `11.5.1-T2` | `node.yml`: proxy pytest, `render_check.py --self-test`, `check_fixtures.py`, shellcheck (`worker-node/{bin,host,net,storage}`, `infra/checks`, `tools/ops`), adapter self-contained publish, always-running `node-gate` required job, `nts-node-<version>.tar.zst` + `MANIFEST.json` (ref/git_sha/run_id) + `SHA256SUMS` release asset published only from `main` runs (D20 H6/H19) |
| 11.5.2 | `11.5.2-T1` | Secret-manager paths `nts/<env>/<NAME>`, runtime injection into containers and `/etc/nts/*.env`, rotation runbook, secret scanning |
| 11.5.3 | `11.5.3-T1` | OIDC environment-scoped roles, prod reviewer, no `pull_request_target`, no secrets on PRs, SHA-pinned actions, minimal `permissions`, branch protection |
| 11.5.3 | `11.5.3-T2` (S) | cosign keyless signing + syft SBOM for images and the node bundle; deploy verifies |
| 11.5.3 | `11.5.3-T3` (XS) | `.github/dependabot.yml` (`nuget`, `npm`, `uv`, `docker`, `github-actions`) + lock enforcement in every job |

**Out of scope, owner in brackets.** Cloud resources, network, LB, PostgreSQL, registry, IaC module (`11.2.2-T1`…`T4`, `11.2.3-T1`); node host/units/`nts-node` (`11.3.1-T1`…`T3`); node network + TLS tests (`11.3.2-T1/T2`); registry/dispatch (`11.3.3-T1/T2`); adapter + contract (`11.3.5-T1`…`T4`); dev Compose and `tools/dev/storage-init.sh` (`11.1.1-T1/T2`, `11.1.2-T1/T2`); `backend.yml` + migrations job (`09.1.5-T1`, `09.2.2-T2`); `web.yml` (`10.1.5`); local gitleaks hooks (`.githooks/*`, `.gitleaksignore` — `01.1.2-T4`); the timed restore drill (`11.6.1-T1`); telemetry/alerts (`11.6.2-T1`); capacity/cost (`11.6.3-T1`). This folder consumes those artefacts and never forks their logic (README §3 D2).

**Why.** Everything the app, the operator and the node need to *exist in an environment* lives here: where bytes live, how code and bundles arrive, where secrets come from, and why a stolen CI token or poisoned dependency cannot reach production. Gate 11-M1 (`../README.md` §7) cannot close without it ("staging deploys automatically, prod by approval; migrations as a separate step").

**Done (folder level).** All nine task files' acceptance checks pass; the 11.4.1 names are the written source for `04.2.3-T1`, `03.1.2-T1`, `05.3.2-T2`; `deploy.yml` runs staging on `main` and prod only through `environment: production`; `node.yml` publishes a verifiable bundle from `main` runs only (`ref`/`git_sha`/`run_id`) behind the required `node-gate` (D20 H6/H19); the media→backup copy is `11.4.1-T3`'s `filer.backup` with the `Write`-only (delete markers) backup credential and the object lock as the delete guard (T88, D21); no workflow holds a long-lived cloud key; no secret in argv, logs, SBOM or bundle (D20 H11).

## 2. Confirmed facts

| Id | Fact | Source |
|---|---|---|
| ✅ E13/E14/E15 | OIDC needs `id-token: write`; environments on a private repo need GitHub Pro/Team (fallback: `workflow_dispatch` from `main`, trust bound to workflow + ref); fork workflows get no secrets and `pull_request_target` is privileged | README §1 E13–E15, §2 F17, §10 O4 |
| ✅ E11/E12/E16 | cosign + syft Apache-2.0 (MinIO/Garage/Grafana/Renovate AGPL-3.0 → rejected, SeaweedFS emulator); Dependabot supports `uv`, `nuget`, `npm`, `docker`, `github-actions` | README §1 E11/E12/E16, D14/D16 |
| ⚠️ E17 | Secret scanning free on public repos; private org repos need Secret Protection; user-owned private repos not stated → local fallback required | README §1 E17 |
| ✅ P4/P7/P11/P12 | 09.10 (settings) is S, so P reads config from env/secret manager and `worker_nodes` keys come from it (P: ciphertext null); the fetcher network table is `02.5.2-T3`'s, implemented by `11.2.2-T2`; §9 re-estimate 11.4 = 1.5, 11.5 = 3.25 (P 2.75, S 0.5) (T84, 11 CR-15) | README §1 P4/P7/P11/P12, §3 D13, §9 |
| ✅ D15 | Bucket names + prefixes `music/`, `music-evidence/`; secret paths `nts/<env>/<NAME>` for the bootstrap set (`DB_CONNECTION`, `JWT_SIGNING_KEY`, `DP_KEK`, `STORAGE_ACCESS_KEY`, `STORAGE_SECRET_KEY`, `FETCHER_API_TOKEN`, `OPENAI_API_KEY`, `SMTP_PASSWORD`, `YOUTUBE_CLIENT_SECRET`, `RENDER_NODE_<NAME>_API_KEY`); D20 H3/T51 splits them per service: `nts/<env>/{api,worker,fetcher,shared}/<NAME>` and `nts/<env>/node/<name>/<NAME>` — names verbatim from the README, enumerated per task below | README §3 D15 |
| ✅ D11 | Node keys from the secret manager: `ADAPTER_API_KEY`, `ADAPTER_API_KEY_NEXT`, `NTS_MPT_API_KEY`, `NTS_PROXY_API_KEY`, `VIENEU_API_KEY`, `NODE_OPENAI_IMAGE_KEY`, `NTS_MUSIC_READ_KEY`/`NTS_MUSIC_READ_SECRET`; rotation = NEXT window, no drain | README §3 D11, §2 F24 |
| ✅ D16 | Workflows `e2e.yml`/`node.yml`/`deploy.yml`; images `api`, `worker`, `fetcher`, `web`, `mock-mpt` tag `sha-<git sha>` + digest; `nts-migrate` before rollout; staging auto + smoke; prod in `environment: production` + required reviewer; OIDC; Api rolling on `/health/ready`; Worker stop → start; Dependabot weekly grouped; locks `--locked-mode`/`npm ci`/`uv sync --frozen` | README §3 D16 |
| ✅ D8/D12/D14/D17/D19 | Bundle `nts-node-<version>.tar.zst` + `MANIFEST.json` + `SHA256SUMS`, never env files/keys/models; `/etc/nts/*.env` written by `nts-node`; dev buckets from `tools/dev/storage-init.sh` (`11.1.1-T2`); `nt-backup-<env>` keeps a deleted object ≤ 30 days after the delete (GOVERNANCE 29 d + `NoncurrentVersionExpiration` 29 d, T87), `DP_KEK` one offline copy; S6 = no secrets on PRs, SHA-pinned actions, OIDC, reviewer for prod | README §3 D8/D12/D14/D17/D19 |
| ✅ 11 §4 | Media private/versioning off/SSE on; public previews "public-read or CDN" (CDN side superseded: public-read via `media.<domain>`, no CDN in P, T75, 11 CR-15); temp uploads deleted after 1 day; retention `09.7.3`; bootstrap secrets at runtime, integration secrets in 09.10 (S) + break-glass; 11.5.3 = OIDC, reviewers, no secrets on PRs, no PR-code checkout under `pull_request_target`, SHA pinning, signed images + SBOM verified at deploy, node deps from `uv.lock` with hashes, weights pinned by revision + sha256 | `../../11-worker-deployment.md:93-94,102-108` |
| ✅ Preview contract | `voice-previews/<slug>.v<n>.mp3` (`audio/mpeg`, `Cache-Control: public, max-age=31536000, immutable`, never overwritten, HEAD-verified before the catalog is updated — storage CLI "⚠️ not fixed yet"; `04-voice-proxy/README.md:234`, `04.2.3-T1:16,49-53`) and `style-previews/<name>.v1.webp` (`image/webp`, same header; `03-image-generation/README.md:75,114`, `03.1.2-T1:25`) both live in `nt-public-<env>` | `04`, `03` read ✅ |
| ✅ 05 D15 S7 | Node music key read-only, prefix `music/` only, `music-evidence/` not readable from nodes; operator key `NTS_MUSIC_WRITE_KEY`/`_SECRET` (`nts.music.write`); credentials via `curl --config -` on stdin, never argv; negative `PUT` with the node key → 403 | `../05-video-assembly-subtitles/README.md:359-360`; `05.3.2-T2:33,61-62` |
| ✅ CI reuse | `09.2.2-T2:19,50` — `dotnet ef migrations bundle --self-contained -r linux-x64 -o artifacts/nts-migrate`, run once with `--connection`; `09.1.5-T1:34-37,64,84` — `global-json-file`, `dotnet restore --locked-mode`, `format --verify-no-changes`, `permissions: contents: read`, 40-char SHA pins; `01.1.2-T4:15,85-88` — gitleaks installed for the commit hook (licence expected MIT, ⚠️ plan 01 U1), `.gitleaksignore` = fingerprint + reason, TL-reviewed | those files ✅ |
| ✅ F22/F23 | `node.yml` is new work; 11.5.3-T2 keeps signing/SBOM (S), 11.5.3-T3 is the P Dependabot/lock part; weight pins verified in `11.3.1-T1` | README §2 F22/F23 |

## 3. Domain and data

No entity, table or migration is added ([DECISION]); the data is policies, credentials and build artefacts.

**3.1 Buckets** (`<env>` ∈ {`dev`, `staging`, `prod`}; dev names per D14).

| Bucket | Visibility | Lifecycle | Consumers |
|---|---|---|---|
| `nt-media-<env>` (private, no versioning, SSE on) | private | `tmp/`: expire 1 day (✅ 11 §4) | app (`STORAGE_ACCESS_KEY`/`_SECRET`, presigned GET); `11.6.1-T1` |
| `nt-public-<env>` (SSE on) | public-read on `voice-previews/` + `style-previews/` only | none (immutable keys) | `04.2.3-T1`, `03.1.2-T1`, `09.4.2-T1` (`previewUrl`) |
| `nt-music-<env>` (private, SSE on) | private | none | `05.3.2-T2` |
| `nt-backup-<env>` (private, SSE on) | private | `NoncurrentVersionExpiration` 29 days, no current-version `Expiration` (T87; versioning + object lock GOVERNANCE 29 d at creation) | `11.4.1-T3`, `11.6.1-T1`; the copy from `nt-media-<env>` is `11.4.1-T3`'s `filer.backup` with a `Write`-only (delete markers) credential and the object lock as the delete guard (D20 H9, T88) |

**3.2 Prefixes.** `voice-previews/` (`audio/mpeg`, `public, max-age=31536000, immutable`, `<slug>.v<n>.mp3`, never overwritten); `style-previews/` (`image/webp`, same header, `<name>.v1.webp`); `music/`; `music-evidence/` (never readable from a node); `tmp/` in `nt-media-<env>` (the 1-day prefix [INFERENCE]); `_canary/` in `nt-media-<env>` (the lag canary `heartbeat`, put-only `NTS_CANARY_WRITE`, T85); the `filer.backup` sink directory `/nt-media-<env>` in `nt-backup-<env>` (D21). Uploads use a conditional put (`If-None-Match: *`) where supported ⚠️, else HEAD-before-put (D20 H9).

**3.3 Credentials** (one per consumer, least privilege).

| Env names | Secret-manager path | Grants | Used by |
|---|---|---|---|
| `STORAGE_ACCESS_KEY` / `STORAGE_SECRET_KEY` (✅ D15) | `nts/<env>/STORAGE_ACCESS_KEY`, `…/STORAGE_SECRET_KEY` | rw `nt-media-<env>` and read on the two preview prefixes; **no write** on `nt-public-<env>`; **no** `nt-music-*`, **no** `nt-backup-*` (D20 H9) | Api, Worker (`04.2.3-T1`, `03.1.2-T1`, 09.7.x) |
| `NTS_MUSIC_WRITE_KEY` / `NTS_MUSIC_WRITE_SECRET` (✅ 05 D15 S7) | `nts/<env>/NTS_MUSIC_WRITE_KEY`, `…_SECRET` [INFERENCE] | write `music/` + `music-evidence/` | operator `upload_music.py` (`05.3.2-T2`) |
| `NTS_MUSIC_READ_KEY` / `NTS_MUSIC_READ_SECRET` (✅ D11) | `nts/<env>/NTS_MUSIC_READ_KEY`, `…_SECRET` | **read** `music/` only; `PUT`/`POST`/`DELETE` denied; `music-evidence/` denied | `sync-music.sh` on the node (`05.3.2-T2`) |
| `BACKUP_ACCESS_KEY` / `BACKUP_ACCESS_KEY_SECRET` (T91, T94) | `nts/<env>/storage/BACKUP_ACCESS_KEY`, `…/BACKUP_ACCESS_KEY_SECRET` | `Write` on `nt-backup-<env>` only — SeaweedFS has no `DeleteObjectVersion` action (`Write` covers all deletes), so the object lock is the guard; never `BypassGovernanceRetention`/`Admin` (T88); `filer.backup` reads the primary over the filer port, not S3 | `11.4.1-T3` (the replication sink identity) |
| `NTS_CANARY_WRITE` / `NTS_CANARY_WRITE_SECRET` (T91) | `nts/<env>/storage/NTS_CANARY_WRITE`, `…_SECRET` | **put only** on `nt-media-<env>/_canary/` (the `heartbeat` object); no read, no other prefix (T85) | `nts-storage-canary.timer` on `storage-<env>` (`11.4.1-T3`) |
| `NTS_OPS_READ` / `NTS_OPS_READ_SECRET` (T91) | `nts/<env>/storage/NTS_OPS_READ`, `…_SECRET` | `Read`/`List` on `nt-backup-<env>` only, no write — the named read identity for the restore leg, never `BACKUP_ACCESS_KEY` (T88/T91); never rendered on a host (T95) | `11.4.1-T3` restore proof, `11.6.1-T1` restore drill |
| `NTS_BACKUP_PROBE` / `NTS_BACKUP_PROBE_SECRET` (T95) | `nts/<env>/storage/NTS_BACKUP_PROBE`, `…_SECRET` | `Read` on `nt-backup-<env>/nt-media-<env>/_canary/*` only — no `List`, no write; `0600` env file on `storage-backup-<env>` | `11.4.1-T3` lag gauge |
| `NTS_OPS_ADMIN` / `NTS_OPS_ADMIN_SECRET` (T92) | `nts/<env>/storage/NTS_OPS_ADMIN`, `…_SECRET` | `Admin` — operator Mac over the VPN for `apply-buckets.sh`, `head-bucket`, `list-buckets` and real restores; in `s3.json` only during an apply window (`sync-identities.sh --with-admin`), removed by the next plain run; sole exception to T88's "no `Admin`" | `11.4.1-T1` apply window, `11.4.1-T2` render/remove; runbook restores (T93) |
| `NTS_PREVIEW_WRITE_KEY` / `NTS_PREVIEW_WRITE_SECRET` (D20 H9) | `nts/<env>/NTS_PREVIEW_WRITE_KEY`, `…_SECRET` (Keychain `nts.preview.write`) | **put only** on `voice-previews/` and `style-previews/`; no read, no other prefix | operator preview upload (`04.2.3-T1`, `03.1.2-T1`) |

**3.4 Runtime-injected secrets.** The D15 bootstrap set plus the D11 node set above; integration secrets are env break-glass values in P because 09.10 is S (✅ P4). `DP_KEK` has one owner-held offline copy (D17).

**3.5 Build artefacts.** Images `api`/`worker`/`fetcher`/`web`/`mock-mpt` (`sha-<git sha>` + digest, `11.5.1-T1`); `artifacts/nts-migrate` (`09.2.2-T2`); `nts-node-<version>.tar.zst` + `MANIFEST.json` + `SHA256SUMS` (`11.5.1-T2`); SBOM per image and per bundle (`11.5.3-T2`, S).

**3.6 Environments.** `dev` = Compose (D14), not a deploy target; `staging` = cloud, deploy on every `main` push, **no GPU node** by default (D1) → renders against `mock-mpt`; `prod` = cloud, one GPU node, only through `environment: production`. OpenAI projects per env (P8): `nts-text-<env>`, `nts-images-<env>`.

## 4. Use cases

| # | Actor | Trigger → flow | Success |
|---|---|---|---|
| UC1 | operator | new environment → `11.4.1-T1`'s `apply-buckets.sh` creates the buckets → `check-buckets.sh --env <env>` | exit 0, all 11.4.1 assertions pass |
| UC2 | Api / browser | presigned GET of a private output → preflight (if `Range`) + GET | 200 + `Access-Control-Allow-Origin: <web origin>`, `Content-Length`, `X-Content-SHA256` |
| UC3 | node | catalog revision changes → `sync-music.sh` reads `music/…` | exit 0; `music-evidence/…` GET and any `PUT` with the node key → 403 |
| UC4 | operator | upload a preview → PUT with the immutable header → HEAD | 200; `previewStorageKey` written only after verification |
| UC5 | developer | PR → `backend.yml`, `web.yml`, `node.yml`, `e2e.yml` with no secrets | green on a clean tree, red on a real failure, no secret in a log |
| UC6 | CI / owner | `main` push → build → images → `nts-migrate` on staging → rollout → smoke; prod only after the reviewer | staging and prod serve the new digest |
| UC7 | operator | rotate a node adapter key → `ADAPTER_API_KEY_NEXT` window (F24) | no drain, no Worker 401 |
| UC8 | operator / owner | bad release → re-deploy the previous digest, `nts-node rollback`; leaked credential → `secrets-rotation.md` + the offline `DP_KEK` copy | previous behaviour restored; all consumers on the new value |

## 5. API and events

- **No new application route, no new `render-node.v1` route, no event** ([DECISION]). Presigned URLs are minted by the Api (09.7.x); this folder defines only the bucket policy that makes them work.
- **Frozen storage contract:** the four bucket names, the five prefixes, public read only on the two preview prefixes, and CORS on `nt-media-<env>` and `nt-public-<env>` for `GET`/`HEAD`/`OPTIONS` from the web origin(s), `Range` allowed, `Content-Length`/`Content-Range`/`ETag`/`X-Content-SHA256` exposed.
- **Workflow triggers:** `e2e.yml` on `pull_request` (app/worker/web/compose paths) and `workflow_dispatch`, no environment, no secrets; `node.yml` on `pull_request` and push to `main`/`workflow_dispatch` for `worker-node/**`, `src/NightTales.RenderNode.*/**`, `src/NightTales.MptAdapter/**`, `tools/mock-mpt/**`, `tests/NightTales.{RenderNode,MptAdapter,MockMpt}.Tests/**`, `infra/**`, `tools/ops/**`, `seed/**` (artifact always, release asset only on `refs/heads/main`, D20 H6/H19); `deploy.yml` on push to `main` and `workflow_dispatch` with environments `staging` (auto) and `production` (reviewer), each allowing branch `main` only (D20 H7), and `id-token: write` — no long-lived cloud key.
- **OIDC claims** in the cloud trust policy: `repo:<owner>/<repo>:environment:staging` and `…:environment:production` **plus** `job_workflow_ref = <owner>/<repo>/.github/workflows/deploy.yml@refs/heads/main` (D20 H7, E13/E14); without GitHub Pro the prod job is `workflow_dispatch`-only with the trust bound to `repo:<owner>/<repo>:ref:refs/heads/main` + the workflow file (F17/O4).
- **Release assets:** `nts-node-<version>.tar.zst`, `MANIFEST.json` (carrying `ref`, `git_sha`, `run_id`), `SHA256SUMS`, published only by `node.yml` runs on `refs/heads/main` (no tag releases, D20 H6); images referenced by digest.

## 6. UI

No product UI. Operator surfaces only: GitHub environments `staging`/`production` (`production` with the owner as required reviewer); `docs/runbooks/secrets-rotation.md` (owner `11.5.2-T1`) covering `DP_KEK` offline restore, `FETCHER_API_TOKEN` and the adapter NEXT window; the GitHub release page holding the node bundle (D12).

## 7. Jobs and integrations

**`e2e.yml`:** `compose-up` (the `09.1.1-T2`/`11.1.1-T1` file, wait for `service_healthy`) → `e2e` (10.4 suite against `mock-mpt`) → `compose-down` (always, `--volumes`). Needs `11.1.1-T1`, `11.1.1-T2`, `11.1.2-T2`, `09.1.5-T1`.

**`deploy.yml` (ordered):** `build` (matrix of the five images, digest-pinned bases) → `push` (tag `sha-<git sha>`, digests in the job output) → `migrate-staging` (one-shot; `ConnectionStrings__Postgres` from the environment, `::add-mask::`-ed, never argv, D20 H11) → `rollout-staging` (Api rolling ×2 on `/health/ready`, Worker stop → start, then fetcher) → `smoke-staging` → `prod` (environment + reviewer, same substeps) → `smoke-prod`. A failure in `build`, `push` or `migrate-*` blocks every later job.

**`node.yml`:** `proxy` (pytest of `worker-node/proxy` and the 04 proxy suites) → `checks` (`render_check.py --self-test`, `check_fixtures.py`, `infra/scoring/score.py --self-test`, `worker-node/checks/capacity_run.py --self-test`) → `shellcheck` (`worker-node/{bin,host,net,storage}/*.sh`, `infra/checks/*.sh`, `tools/ops/*.sh`, `--severity=warning`) → `adapter` (`dotnet publish -r linux-x64 --self-contained`) → `bundle` (D12 layout, `MANIFEST.json` with `ref`/`git_sha`/`run_id`, `SHA256SUMS`) → `release` (assets only from `refs/heads/main`, D20 H6); an always-running `node-gate` job aggregates these and is the required status check (D20 H19). Nothing GPU-bound runs in CI (D18).

**Integrations.** `11.3.1-T3` consumes the bundle; `11.3.5-T4` supplies the publish output; `09.2.2-T2` supplies `nts-migrate`; 09.7.x mints presigned URLs against the 11.4.1 policy; `04.2.3-T1`/`03.1.2-T1`/`05.3.2-T2` use the 11.4.1 names; `11.4.1-T3` writes `nt-backup-<env>` (`filer.backup`, T80), `11.6.1-T1` reads it for the drill; `11.6.2-T1` alerts on deploy/smoke failures (thresholds owned by 08 §5.2).

## 8. Security and privacy

- **S6 (D19).** Deploys use OIDC only (`id-token: write`, E13) to environment-scoped roles — no long-lived cloud key in CI. `pull_request` jobs get no secrets and `pull_request_target` is forbidden (E15). Every third-party action is pinned by a 40-char commit SHA; `permissions:` minimal per job (`contents: read` by default).
- **Least privilege, deny by default.** One credential per consumer (3.3); the node key reads `music/` only; the app key cannot touch music or backup buckets and has **no write** on `nt-public-<env>`; the operator preview key is put-only on the two preview prefixes (D20 H9); the backup key is `Write`-only on `nt-backup-<env>` — no `DeleteObjectVersion` action exists, the object lock is the delete guard and no identity gets `BypassGovernanceRetention`/`Admin` except the time-boxed `NTS_OPS_ADMIN` apply window (T88/T92); `NTS_CANARY_WRITE` is put-only on `_canary/` (T85); `NTS_BACKUP_PROBE` reads only `nt-backup-<env>/nt-media-<env>/_canary/*` (T95); public read only on the two preview prefixes via bucket policy, never bucket-wide; CORS allow-lists the web origin(s), never `*`.
- **Secret hygiene.** Credentials reach tools through `curl --config -`/env files on stdin (05 D15 S3/S7), never argv; a value fetched in CI is masked with `::add-mask::` at once and passed through the environment, never argv (D20 H11); workflows never `echo` a secret and never pass one as a build arg; the bundle carries no env file (D12); SBOMs list packages, never values; a leaked value is rotated, not merely removed from a commit.
- **At rest and rotation.** Provider secret manager at the per-service paths `nts/<env>/{api,worker,fetcher,shared}/<NAME>` and `nts/<env>/node/<name>/<NAME>` (D20 H3/T51); bucket policies and `s3.json` identities are applied inside SeaweedFS by `11.4.1-T1`/`11.4.1-T2` (D21; T67/H20 superseded by T74 — there is no provider bucket resource); `DP_KEK` has one offline copy outside the DB backups (D17); `ADAPTER_API_KEY_NEXT` gives the no-drain adapter window (F24); `main` requires a PR + status checks (incl. `node-gate`, D20 H19) and blocks force-push.
- **Scanning.** GitHub secret scanning + push protection where E17 allows; otherwise the gitleaks build already installed for `01.1.2-T4` runs over the full diff in CI and fails closed (licence expected MIT, ⚠️ check before close).
- **Supply chain.** SHA-pinned actions bumped by Dependabot; `dotnet restore --locked-mode`, `npm ci`, `uv sync --frozen` in every job so a lock edit is visible; S: keyless cosign signing + syft SBOM per image and per bundle, verified at deploy; node Python deps from `uv.lock` with hashes and weights pinned by revision + sha256 (verified in `11.3.1-T1`).

## 9. Test plan

| Check | How | Expected |
|---|---|---|
| Bucket inventory, policy + lifecycle | `worker-node/storage/check-buckets.sh --env <env>` [INFERENCE: path] | exit 0, one line per bucket/prefix; `tmp/` = `Expiration.Days 1`, `nt-backup-<env>` = `NoncurrentVersionExpiration` 29 days (T87); missing `--env` → 2 |
| Public scoping + CORS | anonymous `curl -I` of a `voice-previews/` object and of a key outside the two prefixes; `curl -X OPTIONS` preflight from the web origin on a presigned URL | 200 then 403; `Access-Control-Allow-Origin: <web origin>` with `Range` allowed |
| Credential negative tests | `PUT music/<key>` and `PUT nt-media/<key>` with the node key; `GET music-evidence/…` with it; `GET nt-music-<env>` with the app key | all 403 (05.3.2-T2:62 shape) |
| Preview upload contract | HEAD → PUT → HEAD of `04.2.3-T1` against dev | 200, `audio/mpeg`, immutable cache header; second PUT of the same key refused |
| Workflow shape | review every `permissions:`; `grep -n 'uses:' .github/workflows/*.yml` | no `pull_request_target`, no `secrets.` in a PR job, every `uses:` SHA-pinned |
| Deploy order + bundle | scratch `workflow_dispatch` of `deploy.yml` on staging; `./artifacts/nts-migrate` reading `ConnectionStrings__Postgres` from the environment on an empty scratch DB | `migrate-staging` before `rollout-staging`; `prod` refuses without the reviewer and outside branch `main`; no connection value in argv or the log (D20 H7/H11); bundle exits 0 with `__EFMigrationsHistory` present (09.2.2-T2:50) |
| Node CI + bundle integrity | `node.yml` on a PR touching any H19 path; `node-gate` required; `SHA256SUMS` verification + `tar --zstd -tf` vs `MANIFEST.json` (which carries `ref`/`git_sha`/`run_id`) | pytest, self-test, fixture check and shellcheck pass; `node-gate` green; no missing/extra file, no `*.env`, key or model file; a release asset exists only for `main` runs (D20 H6/H19) |
| Rotation + scanning + locks | adapter rotation twice on staging; CI diff scan + local `gitleaks dir . --redact`; hand-edit a lock on a scratch branch | zero Worker 401, runbook steps match; both scans exit 0 while a planted fake AWS key turns it red; the lock-edited job fails |
| Signing (S) | `cosign verify` per image digest and for the bundle; syft SBOM present | signature verifies against the workflow identity; SBOM lists the expected runtime packages |

## 10. Tasks and re-estimate

| Task | Title | Area | Size | Depends on | Blocks |
|---|---|---|---|---|---|
| 11.4.1-T1 | Object storage buckets, prefixes, credentials, CORS and the backup bucket | Ops | S (0.5) | 11.4.1-T2 | 11.3.2-T2, 05.3.2-T2, 04.2.3-T1, 03.1.2-T1, 11.4.1-T3, 11.6.1-T1 |
| 11.4.1-T2 | SeaweedFS storage hosts: install, TLS, identities, lifecycle runner, metrics | Ops | S (0.5) | 11.2.2-T1, 11.2.2-T4, 11.5.2-T1 | 11.4.1-T1, 11.4.1-T3, 11.6.2-T1 |
| 11.4.1-T3 | Media backup copy: delete propagation, `filer.backup`, lifecycle proof, restore test | Ops | S (0.5) | 11.4.1-T1, 11.4.1-T2 | 11.6.1-T1, 11.6.2-T1 |
| 11.5.1-T1 | Application CI: `e2e.yml` and `deploy.yml` | Ops | M (1.0) | 09.1.5-T1, 11.1.1-T1, 11.1.1-T2, 11.1.2-T2, 11.2.2-T1, 11.2.2-T4 | 11.5.3-T1, 11.5.3-T2, 11.5.3-T3, 10.10.2-T1 (10 CR-15), 08.2.1-T2 |
| 11.5.1-T2 | Node CI: `node.yml`, adapter publish and the node bundle | Ops | S (0.5) | 09.1.5-T1, 11.3.5-T1 | 11.3.1-T3, 11.5.3-T2, 11.5.3-T3 |
| 11.5.2-T1 | Secret manager paths, runtime injection and rotation runbook | Ops | S (0.5) | 11.2.1-T1, 11.2.2-T1 | 11.2.2-T4, 11.4.1-T2 (11 CR-15), 11.6.4-T1 (09 W3 CR-6) |
| 11.5.3-T1 | Workflow hardening: OIDC, permissions, SHA pinning, branch protection | Ops | S (0.5) | 11.5.1-T1 | 11.2.3-T1 |
| 11.5.3-T2 | Signing and SBOM (cosign keyless + syft) | Ops | S (0.5) | 11.5.1-T1, 11.5.1-T2, 11.3.1-T3 | — |
| 11.5.3-T3 | Dependabot and lock enforcement | Ops | XS (0.25) | 11.5.1-T1, 11.5.1-T2, 11.3.1-T1; 01.3.3-T1 soft (lock list) | — |

Cells copied from `../README.md` §8.1 (canonical). Sum **4.75 d raw**, P 4.25 / S 0.5 → buffered **5.7 d** at the 1.2 factor; matches §9 rows "11.4 Storage 1.5" (T1 0.5 + T2 0.5 + T3 0.5, T80) and "11.5 CI/CD + secrets 3.25 (P 2.75, S 0.5)" (corrected by T84). Re-estimate +1.0 d P (11.4.1-T2 + 11.4.1-T3, owner O7, 11 CR-15).

**Open items**

- [RULED — README §10 T23] `.github/dependabot.yml`: 09.1.5-T1 creates it, 11.5.3-T3 extends it.
- [RULED — 11 CR-13] The storage CLI and its credential handling were left unfixed (`04.2.3-T1:16`); `11.4.1-T1` pins the CLI + the stdin/env-file rule; the storage is self-hosted SeaweedFS (D21, T74), so no provider product is chosen in `11.2.1-T1`.
- `[INFERENCE]` Names this folder introduces that D1–D19 do not fix: the `tmp/` media prefix; `worker-node/storage/` with `check-buckets.sh` and the policy JSON files; the secret path `nts/<env>/NTS_MUSIC_WRITE_KEY`/`_SECRET`. The `s3.json` identity paths are no longer inference: `nts/<env>/storage/{BACKUP_ACCESS_KEY,NTS_CANARY_WRITE,NTS_OPS_READ,NTS_BACKUP_PROBE,NTS_OPS_ADMIN}`/`…_SECRET` plus `SEAWEEDFS_JWT_SIGNING_KEY` (T91/T94, readable only by the storage hosts' role; each pair `<NAME>` + `<NAME>_SECRET` — `BACKUP_SECRET_KEY` is not used).
- [RULED — README §10 T34] CORS needs the web origin as configuration; no name exists today (`nts/<env>/APP_ORIGIN` proposed). `10.1.5`/`10.3.x` own the web origin value.
- [RULED — README §10 T75] public-read for `nt-public-<env>` goes through `https://media.<domain>` (LB → SeaweedFS gateway, `GET`/`HEAD` only); no CDN in P.
- [RULED — README §10 T35] `.github/CODEOWNERS` already carries an entry for `tests/NightTales.Architecture.Tests/AllowLists.cs` (`09.1.2-T1:55-59`); `11.5.3-T1` adds `.github/workflows/**` and `infra/**` to the same file — a soft dependency on `09.1.2-T1` that §8.1 does not carry.
- [RULED — README §10 T36] E17 does not say whether secret scanning works on a user-owned private repo; if not, the local gitleaks fallback is the only scan in P and its licence (expected MIT, ⚠️ plan 01 U1) must be confirmed before `11.5.2-T1` closes.

## 11. Rollout

1. **Order** (from §8.1 and `../README.md` §5): `11.5.2-T1` after `11.2.1-T1`/`11.2.2-T1`, then `11.4.1-T2` (also needs `11.2.2-T4`) → `11.4.1-T1` → `11.4.1-T3`; `11.5.1-T1` after `11.2.2-T4` and the dev tasks; `11.5.1-T2` after `11.3.5-T1`; `11.5.3-T1` after `11.5.1-T1`; `11.5.3-T3` after `11.5.1-T2`; `11.5.3-T2` (S) last, at M2, once node deploys exist.
2. **Gates.** `11.4.1-T1` closes before `11.3.2-T2` (node music key) and `05.3.2-T2` can be exercised against a real bucket; `11.4.1-T3` closes before `11.6.1-T1` (backup copy live) and `11.6.2-T1` (lag alert); `11.5.1-T2` before `11.3.1-T3` can install a release; `11.5.1-T1` before gate 11-M1; `11.5.3-T1` before `11.2.3-T1` (S).
3. **Flags and environments.** No feature flag: buckets exist per environment and the workflows are live from the first `main` commit. `e2e.yml`/`node.yml` are PR-safe (no secrets); prod is gated by the environment reviewer; without GitHub Pro prod deploy is `workflow_dispatch`-only from `main` (F17/O4).
4. **Rollback.** App: re-dispatch `deploy.yml` pinned to the previous `sha-<git sha>` digest (images immutable, no rebuild). Migration: expand-only (09 NFR-09-6), so a revert is a new forward migration. Node: `nts-node rollback` + `gpu-smoke.sh` (D12). Secrets: keep the previous value until the drill passes, then delete; `ADAPTER_API_KEY_NEXT` avoids a drain (F24). Buckets: policies re-applied from IaC; a rollback never deletes objects (lifecycle is the only deletion path).
