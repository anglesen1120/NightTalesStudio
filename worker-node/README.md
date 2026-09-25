# Worker node runbook (M0 trial, Mac)

Contract: `plans/features/01-environment-setup/README.md` (D1–D14, amendment CR-11 / DEC-5). Progress: `/STATUS.md`.

## Machine

Captured 2026-09-25 (01.1.1-T1 step 0). DEC-5: this Mac is the implementation machine (supersedes DEC-2).

| Item | Value | Command |
|---|---|---|
| macOS | 27.0 (26A428), arm64 | `sw_vers`, `uname -m` |
| CPU / RAM | Apple M4 / 16 GB (17179869184 bytes) | `sysctl -n machdep.cpu.brand_string hw.memsize` |
| Internal disk | FileVault on, 67 GiB free | `fdesetup status`, `df -h /System/Volumes/Data` |
| Repo volume `Project` (`disk7s1`) | APFS over USB, External, `Owners: Disabled`, `Encrypted: No`, `FileVault: No`, 474 GiB free, mounted `noowners` | `diskutil info /Volumes/Project`, `mount` |
| D4 ports 8000/8001/8002/8080, 8501–8599 | free | `lsof -nP -iTCP -sTCP:LISTEN` |

## Trusted execution location

D13 mode: accepted-risk

Recorded 2026-09-25 (DEC-6, owner): the `Project` volume stays **unencrypted, ownership off**. The owner accepts the risk that anyone holding the drive, or any local process, can read `.nts/src/mpt/config.toml` (OpenAI keys in plain text, M8). **The one hard rule: keys never leave this Mac.** It is enforced by:

- the `.gitignore` rules `.nts/`, `config.toml`, `*.env`, `.env*`;
- the gitleaks `pre-commit` and `pre-push` hooks (`core.hooksPath=.githooks`, fail closed; § Secret scanning);
- the Keychain (D3) as the only source of every key: scripts never print keys or pass them in argv.

Compensating controls: hard limits of $30 org, $5 story and $25 image (D7), and immediate revocation in the OpenAI dashboard if the drive is lost (01.2.4-T3). `lib.sh` `_nts_trusted_location` returns 0 in this mode. The previous `volume` mode (encryption + `enableOwnership`) is kept in `lib.sh`; switch back by editing the line above.

Evidence on 2026-09-25: `Owners: Disabled`, `Encrypted: No`, `FileVault: No` (`diskutil info /Volumes/Project`).

## Layout

D1 runtime split, amended by CR-11 (DEC-5): the repo holds only tracked, secret-free files. The runtime lives in

```sh
export NTS_HOME=/Volumes/Project/NightTalesStudio/.nts   # in ~/.zshrc; <repo>/.nts, gitignored
```

The runtime sits inside the project folder, as the owner asked (DEC-5). `.nts/` is ignored by `.gitignore`, and `lib.sh` `nts_home` refuses any other path inside the repo, and refuses `.nts` itself if it is not ignored. The volume has 474 GiB free; the internal disk has 67 GiB. `.nts` is excluded from Time Machine (`tmutil isexcluded` → `[Excluded]`). `chmod 700` is applied but has no effect while the volume has `Owners: Disabled` (E1), so secrets wait for the D13 `volume` check. MPT writes secrets into `config.toml` in its own root and replaces symlinks (M8), so its clone must sit on a trusted volume.

| repo (tracked) | `$NTS_HOME` (never tracked) |
|---|---|
| `worker-node/README.md` runbook | `src/mpt/` MPT clone @ `MPT_COMMIT` (+ `.venv`, `config.toml` 0600) |
| `worker-node/pins.env` pins (D2) | `src/vieneu/` VieNeu clone @ `VIENEU_COMMIT` (+ `.venv`) |
| `worker-node/bin/*.sh`, `*.py` scripts (D9) | `src/proxy-venv/` (plan 04) |
| `worker-node/mpt/` templates | `hf/` `HF_HOME` for VieNeu weights |
| `worker-node/vieneu/` `env.example`, `voices.snapshot.json`, `weights.sha256` | `logs/<service>-<YYYYmmdd-HHMMSS>.log` |
| `worker-node/proxy/` (plan 04) | `runs/<run_id>/` media copies |
| `worker-node/music/LICENSES.csv` | `music/` verified CC0 audio |
| `trial/` measurements, tools, fixtures, baselines, run metadata | `current_run` |
| `.githooks/pre-commit`, `pre-push` | `exec/<sha>/` (export mode only, unused) |

## Tools

```sh
brew install uv gitleaks shellcheck      # git 2.49.0 and ffmpeg 9.0.2 were already installed
worker-node/bin/env-versions.sh | uv run --no-project --python 3.12 python -m json.tool
```

Versions on 2026-09-25: Homebrew 7.0.6, git 2.49.0, uv 0.12.18, ffmpeg 9.0.2 (`libmp3lame`, native `aac`), gitleaks 8.30.1, shellcheck 0.11.0. `env-versions.sh` prints the 16-key machine manifest that `start_run.sh` stores in `run.json.env` (01.3.1).

## Tool licences

| Tool | Licence | Source | Checked |
|---|---|---|---|
| gitleaks | MIT | `brew info --json=v2 gitleaks`; https://github.com/gitleaks/gitleaks/blob/master/LICENSE | 2026-09-25 |
| uv | Apache-2.0 OR MIT | `brew info --json=v2 uv` | 2026-09-25 |
| shellcheck | GPL-3.0-or-later (local dev tool only, never shipped or linked) | `brew info --json=v2 shellcheck` | 2026-09-25 |
| MPT (MoneyPrinterTurbo, in `.nts`) | MIT @ `3d5f4e421927d61f3eac729cf4b711ac0b69d688` | `.nts/src/mpt/LICENSE`; `app/services/upload_post.py` is never enabled or called (renderer guards upload_post_*) | 2026-09-25 |

## Pins

`worker-node/pins.env` is the only place versions live (D2). `lib.sh` `load_pins` accepts exactly 18 keys (`OPENAI_BASE_URL` must be `https://<host>/v1`, 01 DEC-9; `OPENAI_IMAGE_API` `images|responses`, `OPENAI_IMAGE_HOST_MODEL`, `OPENAI_EFFORT_IMAGE_HOST`/`OPENAI_EFFORT_CHAT` `none|minimal|low|medium|high|xhigh`, 01 DEC-11), 40-hex SHAs and `3.x` Pythons. Resolved 2026-09-25 (01.1.1-T2):

| Repo | Pin | `.python-version` | `requires-python` | `uv.lock` | Version | Note |
|---|---|---|---|---|---|---|
| harry0703/MoneyPrinterTurbo | `3d5f4e421927d61f3eac729cf4b711ac0b69d688` | 3.11 | >=3.11 | yes | 1.3.7 | candidate kept; `main` = `ad5496f1` is 2 ahead (LLM provider "Cheaper Inference", subtitle font path validation) — re-read M-facts before moving |
| pnnbao97/VieNeu-TTS | `c1390abbdb2eedcdf58eafb546966c06ce27af71` | 3.12 | >=3.10 | yes | 3.8.3 | `sync=frozen` works |
| pnnbao-ump/VieNeu-TTS-v3-Turbo (HF) | `61b85e3d937fbbacb387714180e8182823512523` | — | — | — | — | apache-2.0 |
| OpenMOSS-Team/MOSS-Audio-Tokenizer-Nano-ONNX (HF) | `ceff0d0749bfb3fa2d61149794ec6feef0d1e1ae` | — | — | — | — | apache-2.0; ONNX codec loaded at the pin (CR-12) |
| mobiuslabsgmbh/faster-whisper-large-v3-turbo (HF) | `0a363e9161cbc7ed1431c9597a8ceaf0c4f78fcf` | — | — | — | — | 01.2.3 |

Method: `git ls-remote` for code, `https://huggingface.co/api/models/<repo>/revision/main` field `sha` for models (U4 answered: the field exists). A pin change is its own commit, then `bootstrap.sh`.

## Secrets

Keychain generic passwords, account `$USER` (D3): `nts.openai.story`, `nts.openai.image` (owner, `set`), `nts.mpt.api_key`, `nts.vieneu.api_key`, `nts.proxy.api_key` (`init-local`, 32 random bytes hex).

```sh
worker-node/bin/secrets.sh init-local            # created|unchanged ×3
worker-node/bin/secrets.sh check [--local] [--acl]
worker-node/bin/secrets.sh set nts.openai.story  # owner: paste at the prompt, never after the command
worker-node/bin/secrets.sh rotate-local          # services stopped; then restart VieNeu, proxy, MPT
```

Local values are stored through `security -i` on stdin (the value is never in argv) and verified by read-back. The argv fallback is **not** in use. `check --acl` takes ≈ 25 s (`security dump-keychain -a`). Never type a key after a command; always type it at the prompt.

## Bootstrap

```sh
worker-node/bin/bootstrap.sh [mpt|vieneu|proxy|all]    # default all
```

Summary per project: `<project> <sha> python=<x.y> sync=<frozen|unlocked> strip=<ok|skipped|n/a>`. Exit codes: 0 ok · 1 HEAD ≠ pin, local changes, Python mismatch, frozen sync failed, strip left audio · 2 bad argument, missing tool, invalid pins, non-clone dir, wrong remote, fetch failure, pin not found. The `proxy` target (04 CR-4) runs `uv sync --frozen --project worker-node/proxy` into `$NTS_HOME/src/proxy-venv` and is skipped only while `worker-node/proxy/` is absent. Never edit `$NTS_HOME/src/*` by hand: an upstream change is a new pin.

VieNeu weights: `worker-node/bin/fetch-vieneu-weights.sh --fetch|--record|--verify` (`worker-node/vieneu/weights.sha256`, 15 files).

Whisper model: `worker-node/bin/fetch-whisper-model.sh [--verify]` → `$NTS_HOME/src/mpt/models/whisper-large-v3-turbo/` (order: `bootstrap.sh mpt` → `fetch-whisper-model.sh`). Pin `WHISPER_MODEL_REVISION` + sha256 list `worker-node/mpt/whisper-large-v3-turbo.sha256` (7 files, ~1.5 GB, licence MIT at pin `0a363e91`, 2026-09-25; upstream repo renamed to `dropbox-dash/faster-whisper-large-v3-turbo`, `mobiuslabsgmbh` redirects). Offline: MPT picks up `models/whisper-<model_size>` with `HF_HUB_OFFLINE=1`, no hub call. Note: this Mac has blackholed IPv6 — the script probes v6 and falls back to an AF_INET `snapshot_download` (same call `hf download` makes).

## Secret scanning

gitleaks 8.30.1 (v8.19+ syntax). Activation per clone: `git config core.hooksPath .githooks`.

- `pre-commit`: `gitleaks git --pre-commit --staged --redact --no-banner --verbose`.
- `pre-push`: scans exactly the pushed range with `--log-opts`.

Both hooks fail closed if gitleaks is missing.

- Tree scan (`.nts/` excluded, because it holds upstream clones): `t=$(mktemp -d); git ls-files -co --exclude-standard -z | tar --null -T - -cf - | tar -xf - -C "$t"; (cd "$t" && gitleaks dir . --redact --no-banner); rm -rf "$t"`.
- History scan: `gitleaks git . --redact --no-banner`.
- Log: 2026-09-25 tree scan exit 0 and history scan exit 0, after one reviewed false positive (`.gitleaksignore`: `plans/00-overview.md:412`, prose "outbox/SignalR"; the fingerprint is line-bound, so re-pin it if that line moves).
- Policy: no `.gitleaks.toml`, no `--no-verify`. A leaked real key is **rotated** (01.2.4-T3 or `secrets.sh rotate-local`), not only removed.
- Keys must never be published (DEC-6).
- Scan reports go to `$NTS_HOME/logs/`.
- Run a history scan at 01-ready, at 01-closed, and after any rebase or `--no-verify`.
- Proof of the hooks: the throwaway-repo steps in 01.1.2-T4 step 5.

## Services

| Service | Start (own terminal, foreground) | Ready when | Stop |
|---|---|---|---|
| VieNeu 127.0.0.1:8000 | `worker-node/bin/start-vieneu.sh` (`--check` preflight, `--verify-weights`) | `READY vieneu http://127.0.0.1:8000 after Ns` | Ctrl-C (exit 0) |
| VieNeu proxy 127.0.0.1:8001 | `worker-node/bin/start-proxy.sh` (`--check` validates config + catalog without binding) | `GET /health` → 200 `proxy-health.v1` | Ctrl-C (proxy's exit code returned) |
| MPT API 127.0.0.1:8080 | `worker-node/bin/start-mpt-api.sh` | `mpt-api ready on 127.0.0.1:8080` | Ctrl-C (exit 0) |
| Usage tap 127.0.0.1:8002 | `trial/tools/start-tap.sh` | `usage tap listening on 127.0.0.1:8002` in the teed log | Ctrl-C |

Smoke: `worker-node/bin/smoke.sh` runs `smoke-vieneu.sh`, `smoke-mpt.sh`, `smoke-proxy.sh` (sorted glob), then `check-binds.sh`, then the `git.hooks` row. Refresh the VieNeu voice snapshot only after a review: `smoke-vieneu.sh --update-snapshot`. Logs: `$NTS_HOME/logs/vieneu-<ts>.log`, `$NTS_HOME/logs/proxy-<ts>.log`, `$NTS_HOME/logs/mpt-api-<ts>.log`.

### MPT API (01.2.2)

`start-mpt-api.sh` verifies the tap/proxy listeners (SEC-04) **before any Keychain read**, then renders `worker-node/mpt/config.template.toml` into `.nts/src/mpt/config.toml` (0600, atomic, guarded) via `worker-node/bin/render-mpt-config.py` and refuses to start on any failure. `bootstrap.sh mpt` re-runs `worker-node/bin/strip-bundled-music.sh` (hash allow-list = the `sha256` column of `worker-node/music/LICENSES.csv`); `worker-node/bin/install-music.sh` installs the licensed tracks from `.nts/music/`. No WebUI in M0 (D14): nothing may listen on 8501–8599. Redacted last-render state: `.nts/mpt-config.last.redacted.toml`. Logs: `.nts/logs/mpt-api-<ts>.log`.

### Usage tap (01.3.1-T2)

`trial/tools/start-tap.sh` runs `trial/tools/usage_tap.py` in the foreground on `127.0.0.1:8002` (D4). The tap forwards `/v1/*` to the pinned upstream — the origin of `OPENAI_BASE_URL` in `worker-node/pins.env` (`https://api.aixingialaire.shop`, 01 CR-15 / DEC-9). Bodies pass through unchanged except for the 01 DEC-11 image translation and effort injection described below. It appends exactly one D6 JSON line per call to `trial/runs/<run_id>/openai_usage.jsonl` (`no-run` when `$NTS_HOME/current_run` is absent). It never stores or logs a header, key, prompt, message content or image bytes (F02, D6).

**Image path and effort (01 DEC-11).** With `OPENAI_IMAGE_API=responses` in `pins.env`, the tap translates MPT's `POST /v1/images/generations {model, prompt, n:1, size}` into `POST /v1/responses` on `OPENAI_IMAGE_HOST_MODEL` with `reasoning.effort = OPENAI_EFFORT_IMAGE_HOST` and the `image_generation` tool (`model`, `size`, `quality`). It maps the reply back to `{"data":[{"b64_json":…}]}`. `n > 1` → 400 `tap_image_n_unsupported`; a 200 without a completed image → 502 `tap_image_missing`; any other upstream error passes through. `/chat/completions` and `/responses` bodies without an effort get `OPENAI_EFFORT_CHAT`. D6 lines add `request.reasoning_effort` and `via` (`responses` or null). The relay ignores the requested size and quality and reports only host-model tokens, so image cost stays `n/a:no-price` ([OPEN]). `start-mpt-api.sh --no-tap` bypasses the translation, and images then fail.

**The tap must be running before any MPT render**: the rendered `config.toml` points `openai_base_url` and `openai_image_base_url` at `http://127.0.0.1:8002/v1`, and `start-mpt-api.sh` refuses to read the Keychain unless `8002` is owned by this `usage_tap.py` with `tap.json.upstream` equal to the pinned origin (SEC-04). Without the tap, MPT calls fail with connection refused.

Once bound, `usage_tap.py` writes `$NTS_HOME/tap.json` (`pid`, `upstream`, `image_quality_injection`, `image_api`, `image_host_model`, `effort_image_host`, `effort_chat`, `timeout_s`, `port`). Request log: `$NTS_HOME/logs/tap-<YYYYmmdd-HHMMSS>.log`, one line per request (`ts method endpoint status latency_ms`, no headers/bodies).

⚠️ `TAP_IMAGE_QUALITY` (`low|medium|high|auto`) injects a `quality` field into `/images/generations` bodies that omit it. Keep it **unset** until the 03 quality decision (CR-3) — the accepted M0 posture is injection off; `tap.json.image_quality_injection` records the active value for the run manifest. With `OPENAI_IMAGE_API=responses` the injected value is copied into the `image_generation` tool, but the relay ignores it (asked `low`, got `medium` on 2026-09-25). It does **not** cap cost.

Stop: Ctrl-C (own terminal, D9). Env overrides (stub tests only): `TAP_UPSTREAM=http://127.0.0.1:<port>` plus `TAP_RUNS_DIR` outside `trial/runs` — a stub-mode tap refuses to write into real run directories (`tap_stub_upstream_forbidden`, NTS01-SEC-04). `TAP_UPSTREAM_TIMEOUT_S` must stay ≥ 300 s against the real upstream (M4).

### RAM sampler (01.3.2-T1)

`trial/tools/rss_sampler.sh <run_id> [--interval 1] [--duration <s>]` — started in the background by `trial/tools/start_run.sh` (01.3.1-T3), one per active run. It samples every second and writes `$NTS_HOME/runs/<run_id>/rss_raw.csv` (17 cols: per-tree pid/RSS-KiB/nproc for VieNeu:8000, proxy:8001, MPT:8080, WebUI:8501, `swap_used_mb`, `pressure_level`, `free_pct`, `sample_ms`). Roots are the TCP listeners on the D4 ports, refreshed every 10 s; a tree = listener + all descendants (covers ffmpeg children, F04). Whisper runs inside the MPT process, so its RAM is only attributable by stage window, not by process (M3).

Stop: SIGTERM/SIGINT, `--duration`, or `$NTS_HOME/current_run` changing/cleared — whichever first. On stop it appends the `all` summary row (peaks MiB, swap start/peak, worst pressure, min free %, `mpt_nproc_max`, `sample_ms` p95) to the tracked `trial/runs/<run_id>/rss.csv`; `finish_run.py` appends `stage:<key>` rows later. Service down → `pid` empty, `rss`/`nproc` 0, loop keeps running. Log: `$NTS_HOME/runs/<run_id>/rss_sampler.log`; pid file removed on exit.

Probes (2026-09-25, macOS 27, M4): `ps -o rss` **is KiB** — `loginwindow` ps rss 20048 KiB vs `top -l 1` `19.6m` (Q7 ✅). `vm.swapusage` → `used = 3915.12M` parses per spec regex. `kern.memorystatus_vm_pressure_level` → `1` and `memory_pressure` → `System-wide memory free percentage: 51%` both work without sudo (Q6 ✅). `sample_ms` tick cost ≈ 30–50 ms mean / ~110 ms cold-start spike; selftest report prints measured mean/p95 and sampler CPU.

`--selftest <s>` needs no run: spawns a throwaway HTTP server on 127.0.0.1:18099 with a 50 MiB child, samples it as `mpt`, prints tree RSS/nproc and overhead, kills the tree. Self-test dir stays under `$NTS_HOME/runs/sampler-selftest-<ts>/`.

Measurement limits (also in `trial/schema.md`): tree RSS counts shared/mmap'd pages per process → peaks are **upper bounds** of private use; 1 s sampling misses sub-second spikes → peaks are **lower bounds** of the true max for bursts; whisper/render share the MPT process (window attribution only); `footprint`/`vmmap --summary` noted as optional private-memory comparison in T4, not in the loop.

### VieNeu proxy (04.1)

`worker-node/bin/start-proxy.sh` runs `nts_vieneu_proxy` in the foreground on `127.0.0.1:8001` (04 D1/D13): `require_port_free 8001`, then `nts.proxy.api_key` and `nts.vieneu.api_key` from the Keychain into the child's environment only (never argv, never logged), `VIENEU_BASE_URL=http://127.0.0.1:8000`, `VOICE_CATALOG_FILE=$NTS_REPO_ROOT/seed/voices.v1.json`, `PROXY_MAX_CONCURRENT=2`. The child is `$NTS_HOME/src/proxy-venv/bin/python -m nts_vieneu_proxy` (04 CR-6), output tee'd to `$NTS_HOME/logs/proxy-<YYYYmmdd-HHMMSS>.log`, child's exit code returned.

`start-proxy.sh --check` runs `Settings.from_env` + `load_catalog` in-process and prints `config OK` without binding 8001 — safe while the proxy runs. Smoke: `worker-node/bin/smoke-proxy.sh` (also run by `smoke.sh`). Common failures: port busy → exit 2 from `require_port_free` before any Keychain read; `VieNeu is not listening on 127.0.0.1:8000` → start VieNeu first; `proxy venv missing` → `bootstrap.sh proxy`; `invalid: …` from `--check` → fix the named env var or `seed/voices.v1.json`.

Node (M1): `worker-node/proxy/systemd/vieneu-proxy.service` (D13 hardening), installed by 11.3.1-T2. Package docs and licence table: `worker-node/proxy/README.md`, `worker-node/proxy/LICENSES.md`.

## Measured run (01.3.1-T3)

One run = one MPT task = one row in `trial/measurements.csv` (D8: written only by `finish_run.py`, never by hand). Needs the tap (8002), VieNeu (8000), the plan 04 proxy (8001) and the MPT API (8080) running, the rendered MPT config pointing at the tap, and no other active run (`$NTS_HOME/current_run`, D11).

```bash
RUN_ID="$(trial/tools/start_run.sh vi landscape "<topic>" 5 --status calibration --voice <name>)"   # prints only the id
# … submit the render to the MPT API (video_count = 1), note the task id …
uv run --no-project --python 3.12 trial/tools/finish_run.py "$RUN_ID" --task-id <mpt task id> --dry-run   # 58 name=value lines, writes nothing
uv run --no-project --python 3.12 trial/tools/finish_run.py "$RUN_ID" --task-id <mpt task id>             # appends the row
# next day (UTC day, Q10), from the relay/OpenAI dashboard:
uv run --no-project --python 3.12 trial/tools/finish_run.py "$RUN_ID" --dashboard-cost <usd> --dashboard-day <YYYY-MM-DD>
```

- `start_run.sh` writes `trial/runs/<id>/run.json` (pins, heads, `env-versions.sh`, redacted config digest, tap settings), sets `current_run` and starts the RAM sampler. Exit 2: bad args, a run already active, a service port not listening, or 8002 not owned by the tap. Exit 1: a head differs from its pin, or the MPT config base URLs are not the tap (`--no-tap`: they must not be).
- `finish_run.py` stops the sampler, parses the MPT log (stage windows from the `## …` markers, 01 CR-17), the tap JSONL, the proxy log and the sampler file, probes the video, validates the row and only then writes `stages.json`, `ffprobe.json`, `rss.csv` stage rows, `run.json` finish fields and the CSV row. Validation failure → exit 1, nothing written. Script text and the video copy go to `$NTS_HOME/runs/<id>/`, never under `trial/`.
- Failed MPT task: add `--status failed` (the failure is data). Crash or abandoned run: `finish_run.py <id> --abort` (no row; clears `current_run`).
- Parser tests: `uv run --no-project --python 3.12 -m unittest discover -s trial/tools/tests -v`.

## Undo

| What | Command |
|---|---|
| Runtime tree | stop services; `tmutil removeexclusion "$NTS_HOME"; rm -rf -- "$NTS_HOME"` (check `printf '%s\n' "$NTS_HOME"` first; default `<repo>/.nts`) |
| Local Keychain items | `for s in nts.mpt.api_key nts.vieneu.api_key nts.proxy.api_key; do security delete-generic-password -a "$USER" -s "$s" >/dev/null; done` |
| Hooks | `git config --unset core.hooksPath` |
| Tools | `brew uninstall uv gitleaks shellcheck` (git and ffmpeg were pre-installed) |
| `NTS_HOME` export | remove the two NightTalesStudio lines from `~/.zshrc` |
