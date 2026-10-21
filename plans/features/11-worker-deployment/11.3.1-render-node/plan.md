# 11.3.1 / 11.3.2 / 11.3.4 — Render node: GPU host, services, bundle and deploy, private network, cleanup

> Parent: ../README.md — plan 11 TL review: facts K1–K9/E1–E19/P1–P13, findings F01–F28, decisions D1–D19, §8.1 canonical dependency table, gates §7. Stories: **11.3.1** (host image, GPU stack, systemd services, bundle + deploy), **11.3.2** (firewall, TLS, egress, keys), **11.3.4** (node cleanup). Upstream contracts: 01 D2/D9/D13, 03 D12, 04 D1/D5/D8/D13/D14, 05 D2/D7/D8/D9/D14/D15, 09 §5.6.

## 1. Scope

**In scope.** One reproducible, container-free GPU render node on Ubuntu 24.04 LTS x86_64 (D8) running the four D10 services as systemd units under separate unprivileged users, with a versioned read-only release layout and symlinked state (D8), a driver/ffmpeg/venv/weights stack pinned in `worker-node/pins.env` and `worker-node/cuda-libs/` (D9), an operator-only deploy path (`worker-node/bin/nts-node`, D12), a deny-by-default network with a pinned self-signed adapter certificate (D11), per-node keys from the secret manager, and an hourly orphan cleanup (11.3.4-T1).

Deliverables: `worker-node/host/` (setup, `cuda-libs/`, `gpu-smoke.sh`), `worker-node/systemd/*.service|*.timer`, `worker-node/env/*.env.example`, `worker-node/bin/install-release.sh`, `worker-node/bin/nts-node`, `worker-node/net/nftables.conf`, `worker-node/net/make-adapter-cert.sh` [INFERENCE, §10 item 2], `worker-node/bin/cleanup-orphans.py`, `docs/runbooks/node-deploy.md`.

**Out of scope.** The adapter and its contract (11.3.5-T1…T4), mock-mpt (11.1.2), `worker_nodes`/`NodeHealthPoller`/`INodeSelector`/`RenderNodeConnectionFactory` (11.3.3), the OpenTofu module, provider firewall as IaC and the bastion (11.2.2-T1/T4), buckets (11.4.1-T1), CI workflows (11.5.1), the proxy source and its unit file (04.1.5-T1), capacity/cost (11.6.3-T1), telemetry backend (11.6.2-T1). No containers on the node (D9); no `h264_nvenc` (T4, E4 ⚠️); no self-hosted AGPL component (E12).

**Consumed, not owned here** (no logic is forked): `worker-node/pins.env` + `lib.sh` (01.1.1-T2, 01.1.2-T2), `worker-node/bin/render-mpt-config.py` with the node profile (01.2.2-T2, 05.1.1-T2), `worker-node/bin/fetch-whisper-model.sh` + `worker-node/mpt/whisper-large-v3-turbo.sha256` (01.2.3-T1), `worker-node/CARRY-OVER.md` (01.3.3-T1), `worker-node/bin/{check-fonts,check-music,sync-music}.sh` (05.2.2-T1, 05.3.1-T1, 05.3.2-T2), `worker-node/checks/render_check.py` (05.5.1-T1), `worker-node/bin/mpt-upgrade-diff.py` (05.1.1-T1), `worker-node/proxy/systemd/vieneu-proxy.service` (04.1.5-T1).

## 2. Confirmed facts

| Id | Fact used here (not restated) |
|---|---|
| K1, K2 | MPT's own GPU image is unusable for its own lock (cuDNN 8 vs `ctranslate2 4.7.1`) and its lock has no `nvidia-*` wheels → the CUDA libs must come from elsewhere (F04, D9). |
| K3 | `IMAGEIO_FFMPEG_EXE` is how MPT is told which ffmpeg to use → the node must have exactly one (F06). |
| K4 | Whisper is a process-wide singleton read from `<mpt root>/models/whisper-<size>/model.bin`; a load failure is silent (F05) → `gpu-smoke.sh` loads it explicitly. |
| K5 | `root_dir()` = realpath of the package; `storage/`, `resource/songs`, `models/` are created under it (F07). |
| K6, K7, K8 | VieNeu `cuda` extra = torch/torchaudio 2.8.0 (`cu128` index); GPU base Ubuntu 24.04 + Python 3.12 + `uv sync --frozen --no-dev --extra cuda`; 0.9 GB idle / 1.1 GB peak VRAM. |
| K9 | `requires-python = ">=3.11"`; MPT runs on 3.11 (D8). |
| E1, E2, E3 | faster-whisper needs cuBLAS 12 + cuDNN 9 via `LD_LIBRARY_PATH` before Python starts (`nvidia-cudnn-cu12==9.*`); fp16 large-v2 ≈ 4.5 GB VRAM, no turbo row (F28: ≥ 16 GB, prefer 24 GB); CUDA 12.8 GA needs driver ≥ 570.26. |
| E4 ⚠️, E6, E7, E8 | NVENC in Ubuntu's ffmpeg package unconfirmed → `NTS_MPT_VIDEO_CODEC=libx264` in P (T4); `GetCertHashString(HashAlgorithmName.SHA256)` is the fingerprint format; Kestrel body-limit default vs the 2 MiB cap; `--self-contained` publish → no .NET runtime on the node. |
| P1, P3, P5, P7, P13 | `render-node.v1` frozen by 05.4.1-T2 (port 8090, D15 S1–S8/P1–P7); the proxy keeps `proxy-health.v1` embedded; rotation uses `ADAPTER_API_KEY_NEXT` (no drain); `worker_nodes` carries `tls_cert_sha256`; NFR-05-1 is measured on this node. |
| F04–F07 | The four node-image findings this folder closes (CUDA libs, offline whisper path, one ffmpeg, writable state). |
| F14, F15, F18, F24, F28 | Egress model, certificate procedure, bundle/deploy path, rotation without drain, GPU size. |
| D6, D6a, D7 | Adapter config names and the post-processing invocation this folder must make possible; health `gpu` block; cached assets job. |
| D8, D9, D10, D11, D12, D19 | Node layout, GPU/media stack, unit table, network/TLS/keys, deploy procedure, security rules. |

## 3. Domain and data

No new database object. The node holds only filesystem state, all rebuildable (F27: node disks are rebuilt, never restored).

| Path | Owner / mode | Notes |
|---|---|---|
| `/opt/nts/releases/<version>/{mpt,vieneu,proxy,adapter,worker-node,seed,venvs/{mpt,vieneu,proxy,cuda-libs}}` | root, read-only | D8; `<version>` = bundle version `<yyyymmdd>-<git sha 7>` (D12) |
| `/opt/nts/current` | root symlink | the active release; the only thing a rollback moves |
| `/var/lib/nts/mpt/storage`, `…/storage/tasks` | `nts-mpt:nts-render 2770` (setgid) | task dirs; the adapter reads them and writes `final-1.norm.mp4` |
| `/var/lib/nts/mpt/config.toml` | `nts-mpt` | rendered `--profile node`; symlinked into the release (F07) |
| `/var/lib/nts/music/songs`, `/var/lib/nts/models`, `/var/lib/nts/hf/{mpt,vieneu}` | `music/songs` = `nts-music:nts-render 2750`; `models` = `nts-mpt`; `hf/<service>` = that service | `resource/songs` + `models` symlinks; each Python service has its own `HF_HOME=/var/lib/nts/hf/<service>` and `ReadWritePaths` [RULED — README §10 T48/T73 (D20 H5), T64 (D20 H17)] |
| `/var/lib/nts/adapter/tasks.db` (WAL) | `nts-adapter` | the adapter's durable store (D6) — **the cleanup's reference set** |
| `/var/lib/nts/relay/` | `nts-adapter` | relay JSONL + image files (03 D5/D12) |
| `/etc/nts/{mpt,vieneu,vieneu-proxy,adapter,music-sync}.env` | `0640 root:<service user>` (`music-sync.env` = `root:nts-music`) | regenerated by `nts-node sync-env --node <name>` from the secret manager (`nts/<env>/node/<name>/<NAME>`, D8/D15) and asserted by `deploy`; never hand-edited, never in the bundle [RULED — README §10 T48/T73 (D20 H5), T51 (D20 H3), T52 (D20 H8)] |
| `/etc/nts/tls/{adapter.crt,adapter.key}` | `adapter.key 0600 nts-adapter` | D11; the fingerprint goes into the Worker config |

Invariants: a release dir is never written after `install-release.sh` finishes; every mutable path is a symlink out of the release; each unit's `ReadWritePaths` is exactly its own state dir (D10); no env file, key or model file is inside the bundle (D12); disk use is a health value (`DiskHealthV1`, D7) from one `statvfs` on `/var/lib/nts/mpt/storage` [RULED — README §10 T63 (D20 H16)] and an alert at 80 % / `disk_low` at 85 % (D17).

## 4. Use cases

| # | Actor | Use case |
|---|---|---|
| UC-1 | Operator | `nts-node provision --env <env> --node <name>` on a bare Ubuntu 24.04 host: host setup, users, layout, venvs, weights, units, env files, firewall, certificate, first deploy, smoke. |
| UC-2 | Operator | Ship a new bundle: drain → copy → verify → install → swap → restart → smoke → activate; any failure rolls back to the previous `current`. |
| UC-3/4 | Operator | Rotate a node credential: adapter key via `ADAPTER_API_KEY_NEXT` (F24, P5) with no drain; certificate via drain → new cert → `RenderNodes:Nodes[i]:TlsCertSha256` → activate. |
| UC-5 | Timer | `nts-cleanup.timer` removes task dirs and relay JSONL the store no longer references and that are older than 24 h. |
| UC-6/7 | Worker / Auditor | Poll `/v1/health` and dispatch (only 8090 over the private link answers); re-run the port scan and egress matrix and get the same result (11-node gate, parent §7). |

## 5. API and events

This folder adds **no** route; it must not break `render-node.v1` (P1).

- The node serves `GET /v1/health` (`render-node-health.v1`, D7) on the private address `:8090` over TLS; the Worker poller is the only client (D13). The additive `gpu` block / `gpu_unavailable` reason comes from a 15 s `nvidia-smi` sample; `disk` from `statvfs` on `/var/lib/nts/mpt/storage`; `assets` from `check-fonts.sh` and `check-music.sh --json` at start and every `Adapter__AssetsRefreshSeconds` (600 s), never per request (D7 P1).
- `GET /v1/renders/{id}` and `/video` are store-only (F19). The node's obligation is that the served `final-1.norm.mp4` exists in the task dir and that `storage/tasks` is readable/writable by `nts-render` (D8).
- Operator-facing "API": the `nts-node` verbs (D12) and `nts-worker nodes set-state` (11.3.3-T1). `nts-node deploy` calls the latter; this folder never touches the database.
- Events: none. The node publishes nothing; its metrics reach the platform only through the Worker (D13, F21).

## 6. UI

None. Operator surfaces are the `nts-node` CLI (one line per step, exit 0/1/2 per 01 D9), `journalctl -u <unit>` and `gpu-smoke.sh` output. No web UI, no MPT WebUI (01 D14), no node dashboard; `/v1/health` is for the Worker.

## 7. Jobs and integrations

**systemd units (D10).** All: `Restart=on-failure`, `NoNewPrivileges=yes`, `ProtectSystem=strict`, `ProtectHome=yes`, `PrivateTmp=yes`, `EnvironmentFile=/etc/nts/<unit>.env` (see §10 item 3), `ReadWritePaths=` only that service's D8 state dir.

| Unit | User | Network | Start-up chain / integrations |
|---|---|---|---|
| `vieneu.service` | `nts-vieneu` | `IPAddressDeny=any` + `IPAddressAllow=127.0.0.1 ::1` | binds `127.0.0.1:8000`; `VIENEU_MAX_STREAMS` ≥ `NTS_MPT_MAX_CONCURRENT` (04 D5); `HF_HUB_OFFLINE=1`, `HF_HOME=/var/lib/nts/hf/vieneu` [RULED — README §10 T47 (D20 H4), T64 (D20 H17)] |
| `vieneu-proxy.service` | `nts-proxy` | same | file owned by 04.1.5-T1 (`worker-node/proxy/systemd/vieneu-proxy.service`); venv `/opt/nts/current/venvs/proxy`; `After=vieneu.service` |
| `mpt.service` | `nts-mpt` | same | `ExecStartPre=` render config → `--check --profile node` → `check-fonts.sh` → `check-music.sh`; `IMAGEIO_FFMPEG_EXE=/usr/bin/ffmpeg`, `LD_LIBRARY_PATH` from `venvs/cuda-libs`, `HF_HUB_OFFLINE=1`; `After=vieneu-proxy.service` |
| `mpt-adapter.service` | `nts-adapter` | loopback + Worker CIDR inbound; egress per D11 | `After=mpt.service`; HTTPS 8090 on the private address; `ReadWritePaths=/var/lib/nts/adapter /var/lib/nts/relay /var/lib/nts/mpt/storage/tasks` |
| `nts-cleanup.service` + `.timer` | `nts-adapter` | none | hourly (11.3.4-T1) |
| `nts-music-sync.service` | `nts-music` | 443 to the storage endpoint's IP set only | run by `nts-node deploy` and on a catalog revision change (05.3.2-T2); its env file is `0640 root:nts-music` [RULED — README §10 T48/T73 (D20 H5)] |

**Integrations.** (a) 05.1.1-T2's node profile reads `/etc/nts/mpt.env` and writes `/var/lib/nts/mpt/config.toml`; both must exist before `mpt.service` starts. (b) The adapter's post-processing runs `/usr/bin/python3.12 /opt/nts/current/worker-node/checks/render_check.py` (D6a), so the release carries `worker-node/checks/` and the OS provides `/usr/bin/python3.12` (D8). (c) `sync-music.sh` runs from the release with the read-only music credential in `/etc/nts/music-sync.env` (D11, 05 D15 S7). (d) `node.yml` (11.5.1-T2) builds the bundle; `nts-node` is its only installer. (e) `gpu-smoke.sh` is both the 11-node gate item and the deploy smoke.

## 8. Security and privacy

D19 mapped to this folder:

| Rule | Implementation |
|---|---|
| S1 | Provider firewall (11.2.2-T1) + `worker-node/net/nftables.conf`: inbound only SSH from the VPN/bastion range and TCP 8090 from the Worker subnet; the adapter binds the private address, never `0.0.0.0` (D6). |
| S2 | `IPAddressDeny=any` + `IPAddressAllow=127.0.0.1 ::1` on `vieneu`, `vieneu-proxy`, `mpt` (never `localhost`: it admits the resolver stub `127.0.0.53`); proven by a `systemd-run` deny test inside each unit's sandbox, not by reading the unit file [RULED — README §10 T47 (D20 H4)]. |
| S3 | Pinned TLS end to end: self-signed ECDSA P-256 per node, fingerprint into `RenderNodes:Nodes[i]:TlsCertSha256`; the node proves the certificate it serves. |
| S4 | Every key lives in the secret manager (`nts/<env>/node/<name>/<NAME>`, D15/T51) and reaches a unit only through its `0640` env file, regenerated by `nts-node sync-env` and asserted against the secret manager by `deploy`; never in argv, logs or the bundle; `ADAPTER_API_KEY_NEXT` exists only during a rotation window [RULED — README §10 T51 (D20 H3), T52 (D20 H8)]. |
| S5 | Env files `0640 root:<service user>`; state dirs owned per service; `storage/tasks` setgid so the adapter writes the normalised file without running as `nts-mpt`. |
| S7 | Logs stay on the node: journald only, `MaxRetentionSec=7day`, no shipping, no task content in script output (ids and hashes only). |
| S8 | `cleanup-orphans.py` deletes only under the two state roots it owns, only when the store no longer references the path, and never follows a symlink out of those roots. |
| Q1, Q3, Q4, Q5 | Health runs no per-request check; no script reads a whole media file (1 MiB chunk hashes); one whisper model per node with VRAM checked before activation; `nts-node deploy` waits for `active_tasks = 0` with a bounded timeout instead of retrying forever. |

Least privilege: each unit runs as its own user with no login shell (D8); `nts-adapter` is the only user holding a TLS private key; `nts-mpt` is the only writer of the MPT storage root; `nts-cleanup.service` has no network.

## 9. Test plan

| # | Check | Proof |
|---|---|---|
| T-1 | Host image reproducible | `gpu-smoke.sh` exit 0 (driver ≥ `NVIDIA_DRIVER`; `torch.cuda.is_available()`; whisper loads from the D8 path with the unit's `LD_LIBRARY_PATH`; ffmpeg has the configured encoder); a second `provision` changes nothing. |
| T-2 | One ffmpeg, one driver | `command -v ffmpeg` → `/usr/bin/ffmpeg`; `dpkg-query -W -f='${Version}' ffmpeg` = `FFMPEG_APT`; `nvidia-smi --query-gpu=driver_version` ≥ `NVIDIA_DRIVER`; the adapter's codec check (D7 P5) passes against that binary. |
| T-3 | Units hardened and ordered | `systemctl show <unit> -p NoNewPrivileges -p ProtectSystem -p IPAddressDeny -p IPAddressAllow`; `systemd-analyze verify`; a cold restart of `mpt-adapter.service` brings up the whole chain. |
| T-4 | Binds | `ss -ltn`: 8090 on the private address only; 8000/8001/8080/8091 on `127.0.0.1` only. |
| T-5 | External exposure | `nmap -Pn -p- <node public IP>` from outside → only the bastion path (22) or nothing; recorded in the runbook. |
| T-6 | Egress | as `nts-adapter` `curl -sS https://api.openai.com` succeeds; the storage endpoint is reached only by `nts-music` through `sync-music.sh` (its 443 rule names the storage IP set); as `nts-mpt`/`nts-vieneu`/`nts-proxy` everything fails; the app subnet, PostgreSQL and `169.254.169.254` fail for everyone [RULED — README §10 T48/T73 (D20 H5)]. |
| T-7 | TLS pin | `openssl s_client -connect <private ip>:8090 -showcerts` fingerprint equals the printed value and `RenderNodes:Nodes[i]:TlsCertSha256`; a wrong pin is refused by 11.3.3-T1's factory test. |
| T-8 | Deploy / rollback | a deliberately corrupted bundle fails before `install-release.sh` and leaves `current` untouched; a healthy deploy moves `current`; `nts-node rollback` returns to the previous release, restarts and passes smoke. |
| T-9 | Cleanup | `cleanup-orphans.py --dry-run` lists exactly the planted orphan and never a referenced dir; `--apply` removes it; a referenced dir, a fresh orphan, a symlink pointing outside the roots and a missing store leave the tree unchanged; exit 0/1/2. |
| T-10 | No secret escapes | each secret value absent from the bundle tarball, `/etc/nts`, the unit files, one deploy's journal and `nts-node --help`; `stat -c '%a %U %G' /etc/nts/*.env` → `640 root:<user>`. |
| T-11 | Disk signal | health `disk.used_pct` comes from one `statvfs` on `/var/lib/nts/mpt/storage` and equals `df /var/lib/nts/mpt/storage` within 1 % [RULED — README §10 T63 (D20 H16)]; a filled test dir crosses the 80 % warning and `disk_low` at 85 % (D17). |

Node-only checks never run in CI; CI runs `shellcheck`, `bash -n`, `systemd-analyze verify` and `cleanup-orphans.py --self-test` (D18).

## 10. Tasks and re-estimate

| Task | Title | Area | Size | Depends on | Blocks |
|---|---|---|---|---|---|
| 11.3.1-T1 | GPU host base: driver, CUDA libs, venvs, weights, layout | Ops | M (1.0) | 11.2.1-T1, 01.3.3-T1, 01.3.3-T2 | 11.3.1-T2, 11.5.3-T3 |
| 11.3.1-T2 | Node services: D10 units, env examples, release installer, journald | Ops | M (1.0) | 11.3.1-T1, 11.3.5-T3, 11.3.5-T4, 04.1.5-T1, 05.1.1-T2, 05.2.2-T1, 05.3.1-T1, 05.3.2-T2 | 11.3.1-T3, 11.3.2-T1, 11.3.4-T1 |
| 11.3.1-T3 | Node bundle layout and `nts-node` provision/deploy/rollback/drain/activate | Ops | M (1.0) | 11.3.1-T2, 11.5.1-T2, 11.3.3-T1 | 11.6.3-T1 |
| 11.3.2-T1 | Provider firewall, nftables, adapter certificate, port scan | Ops | S (0.5) | 11.3.1-T2, 11.2.2-T1 | 11.3.2-T2 |
| 11.3.2-T2 | Egress rules and deny tests, node keys, rotation, key-leak runbook | Ops | S (0.5) | 11.3.2-T1, 11.4.1-T1 | 11.6.3-T1 |
| 11.3.4-T1 | `nts-cleanup` timer and `cleanup-orphans.py` | Ops | S (0.5) | 11.3.1-T2, 11.3.5-T2 | — |

Total **4.5 d raw** (T1 1.0 + T2 1.0 + T3 1.0 + 11.3.2-T1 0.5 + 11.3.2-T2 0.5 + 11.3.4-T1 0.5). This is the parent README §9 breakdown of the 11.3 epic (node host/services/deploy 2.0 → 3.0, F04–F07/F18; plus the 11.3.2/11.3.4 share of 6.5 → 11.0); no separate re-estimate is proposed.

**Open items.**

1. [RULED — README §10 T21] Host pins go to a separate `worker-node/host/pins.env`; the signed 13-key `worker-node/pins.env` is unchanged (no CR to 01).
2. `make-adapter-cert.sh` path: D2 names the artefact but no directory; `worker-node/net/` is the only folder in the ownership map mentioning it → `worker-node/net/make-adapter-cert.sh`. [INFERENCE]
3. Env-file names vs unit names: D8 lists five env files (`mpt`, `vieneu`, `vieneu-proxy`, `adapter`, `music-sync`, README.md:195), D10's units are `mpt`, `vieneu`, `vieneu-proxy`, `mpt-adapter`, `nts-cleanup`, `nts-music-sync`, and D10 says every unit has `EnvironmentFile=/etc/nts/<unit>.env` (README.md:218). So `mpt-adapter.service` needs `/etc/nts/adapter.env`, `nts-music-sync.service` `/etc/nts/music-sync.env`, and `nts-cleanup.service` has none defined. This folder keeps the D8 names; `nts-cleanup.service` gets none. [RULED — README §10 T22] `LD_LIBRARY_PATH` is fixed in the unit through `/opt/nts/current/…`, so a rollback also rolls back the CUDA libs.
4. `nts-node provision` bootstrap: `provision` does host setup + first deploy, but the script lives inside the bundle provisioning has not installed yet. This folder assumes the operator extracts the bundle (or a checkout at the same commit) into a temp dir and runs `worker-node/bin/nts-node` from there. [RULED — README §10 T29/T30] accepted.
5. `cuda-libs` interpreter: D9's path `<cuda-libs>/lib/python3.11/site-packages/nvidia/...` means the project must be synced with `MPT_PYTHON` (3.11) although the node's stdlib tooling is 3.12. [RULED — README §10 T22] 3.11.

## 11. Rollout

1. **Before a node exists.** 11.2.1-T1 (provider, region, GPU ≥ 16 GB VRAM, dated prices) → 11.2.2-T1 (network, subnets, bastion, firewall) → this folder. 01.3.3-T1/T2 (CARRY-OVER) is the weights/locks input; `worker-node/pins.env` + D8 become the source once 11.3.1-T1 lands (CR-7).
2. **Order here.** 11.3.1-T1 → 11.3.1-T2 (after 11.3.5-T3/T4 and the four 05 script tasks) → {11.3.1-T3, 11.3.2-T1, 11.3.4-T1}; 11.3.2-T1 → 11.3.2-T2 (after 11.4.1-T1). Nothing here runs before 11.3.5-T3/T4: the units depend on the adapter's post-processing and health.
3. **Staging first.** Per D1/O2 staging has no GPU node by default; the upgrade rehearsal uses an on-demand staging node from the same scripts, then destroys it. Prod is provisioned once and afterwards receives bundles only.
4. **Gate 11-node (parent §7).** `gpu-smoke.sh` exit 0; adapter health 200 with `gpu`, `tts_proxy`, `image_relay`, `assets` ok; the D14 fixtures replay green against the real adapter; port scan and egress matrix green; one trial story passes 05 D10. Only then is the node dispatchable (D13) and may 11.6.3-T1 measure capacity.
5. **Rollback.** `nts-node rollback` (previous `current`, restart, smoke, activate), 3 releases kept. A bad pin is a `git revert` of the `pins.env` commit plus a redeploy. A bad certificate is `make-adapter-cert.sh` + a config change + activate. Keys roll with `ADAPTER_API_KEY_NEXT` (no drain). Node disks are never restored from backup (F27).
6. **Flags.** None. `NTS_MPT_VIDEO_CODEC=libx264` is a value, not a flag; NVENC stays off until 11.6.3-T1 proves it (T4, E4 ⚠️).
