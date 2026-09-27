# CARRY-OVER — Mac trial → GPU node (worker-node)

Date: 2026-09-26 · Author: PythonSpikes (subagent, M0 wave 1) ·
`worker-node/pins.env` last changed in commit `8bd9b76754f1203107a35f8d788fd9ef7ebbc5eb`.
Scope: everything the GPU node (plan 11) must reproduce from the Mac M0 trial (plan 01). Every value
names its source. Secret rows never carry values. `⚠️` marks an open item; §12 collects them.

## 1. Pins

`worker-node/pins.env` (D2) is the only place versions live. Values quoted verbatim.

| component | repo | full 40-char SHA | commit date | licence | source |
|---|---|---|---|---|---|
| MPT | `https://github.com/harry0703/MoneyPrinterTurbo.git` | `3d5f4e421927d61f3eac729cf4b711ac0b69d688` | 2026-09-21T20:52:09+08:00 | MIT ✅ M10 | `MPT_REPO` / `MPT_COMMIT`; `git -C $NTS_HOME/src/mpt show -s --format=%cI` |
| VieNeu | `https://github.com/pnnbao97/VieNeu-TTS.git` | `c1390abbdb2eedcdf58eafb546966c06ce27af71` | 2026-09-23T17:22:25+07:00 | Apache-2.0 ✅ V7 | `VIENEU_REPO` / `VIENEU_COMMIT`; `git -C $NTS_HOME/src/vieneu show -s --format=%cI` |
| faster-whisper (PyPI) | `mobiuslabsgmbh/faster-whisper-large-v3-turbo` (weights) | package `faster-whisper==1.1.0` | n/a:package-version | MIT ✅ W1 | `.nts/src/mpt/uv.lock` entry `faster-whisper 1.1.0` |

Other `pins.env` values that must be identical on the node:

| key | value | purpose |
|---|---|---|
| `MPT_PYTHON` | `3.11` | MPT venv interpreter ✅ M7 |
| `VIENEU_PYTHON` | `3.12` | VieNeu venv interpreter ✅ V3 |
| `PROXY_PYTHON` | `3.12` | proxy venv interpreter (04 CR-3) |
| `OPENAI_BASE_URL` | `https://api.aixingialaire.shop/v1` | relay upstream, not api.openai.com (01 DEC-9) |
| `OPENAI_IMAGE_API` | `responses` | tap translates images/generations → /v1/responses (01 DEC-11) |
| `OPENAI_IMAGE_HOST_MODEL` | `gpt-6-luna` | host model for the image_generation tool |
| `OPENAI_EFFORT_IMAGE_HOST` | `low` | effort injected on image calls |
| `OPENAI_EFFORT_CHAT` | `medium` | effort injected on chat/responses calls |

### MPT upgrade runbook (05.1.1-T1)

`worker-node/bin/mpt-upgrade-diff.py` (stdlib only, `uv run --no-project --python 3.12`) compares a
**candidate** MPT checkout with the three pinned baselines and exits 1 on any D2/D3/D4 drift; `--help`
prints the same six steps and the same safety notes, and `--json` emits
`mpt-upgrade-diff.report.v1` for CI/ops.

| baseline | file | holds |
|---|---|---|
| config | `worker-node/mpt/config.example.pinned.toml` (+ `.sha256`, 01.2.2-T1) | the pinned `config.example.toml` copy |
| params | `worker-node/contract/mpt-videoparams.pinned.json` (05.4.1-T1 S1) | `VideoParams.model_json_schema()` under `model_schema` |
| routes | `worker-node/contract/mpt-routes.pinned.json` (05.4.1-T1 S1) | the 13 OpenAPI paths under `paths` (+ `mpt_version`, `commit`) |

A green diff is only step 3 of six — do not start a bump without the whole list:

1. Bump `MPT_COMMIT` in `worker-node/pins.env` in its **own** commit (01 D2).
2. `git -C "$NTS_HOME/src/mpt" fetch && git -C "$NTS_HOME/src/mpt" worktree add "$NTS_HOME/src/mpt-candidate" "$MPT_COMMIT"`, then `cd "$NTS_HOME/src/mpt-candidate" && uv sync`.
3. `uv run --no-project --python 3.12 worker-node/bin/mpt-upgrade-diff.py --candidate "$NTS_HOME/src/mpt-candidate"` — **exit 0 required** before anything else.
4. Re-run the 05.4.1-T1 scenario subset **S0, S3, S5, S12** (pin check, route/OpenAPI inventory, validation, restart mid-task) against the candidate; record in `worker-node/contract/mpt-api.md`.
5. Refresh the three pinned snapshots **in the same commit as the bump** and re-run the diff — exit 0 against the refreshed baselines.
6. A confirmed BLOCKING change is escalated to the TL, not patched here: D2/D3/D4, `MptSubmitFields`, the D14 fixtures (05.4.1-T2), the adapter (11.3.5-T1) and mock-mpt (11.1.2-T1) move together. If the change is unacceptable, keep the old `MPT_COMMIT` and revert the bump commit.

**Safety — steps 2–3 execute the candidate's code**: only run commits fetched from the pinned
upstream, on a machine where that is acceptable. The child gets `/dev/null` stdin, a throwaway
`HOME`, its own process group (killed on timeout) and a minimal environment, but it still runs
with the operator's privileges and can read the filesystem and the network — this is environment
confinement, not a sandbox. The check cleans up after itself: a `config.toml`, `resource/` or
`storage/` the child created is removed again, one that already existed is left alone, and
`__pycache__` is kept. Child stderr is never echoed; on a failure it is written to
`$NTS_HOME/logs/mpt-upgrade-diff-<utc>.stderr.log` (0600) and the report names only the exception
class plus that path. A whole run is capped at 90 s (child `min(remaining, 60)`, git
`min(remaining, 10)`), and the child's stdout at 16 MiB.

Exit **2** (not 1) means the environment, never drift: a baseline that is missing, unreadable,
non-UTF-8 or the wrong shape (`paths` not an object of path → list of methods, `model_schema`
not an object); a candidate `config.example.toml` that is non-UTF-8 or unparsable TOML (diff (a)
is skipped); a candidate schema that has no `properties`, an unresolved `$ref`, a `$ref` cycle or
more than 10 000 schema nodes; child output with zero or two or more sentinel lines; a child
timeout, an oversized capture, or a candidate `HEAD` that differs from `MPT_COMMIT` in
`pins.env`. A candidate with **no** git metadata stays a warning (`"mpt_commit": null`), and a
dirty worktree (tracked modifications or untracked files, e.g. a stale `.venv` outside `.gitignore`) is a warning plus `"dirty": true` — the pin checkout
`.nts/src/mpt` is dirty by design (strip-bundled-music deletes tracked files), so a dirty pin
still exits 0.

BLOCKING: a `used` config key added/removed/default-changed/type-changed; a D3 `VideoParams` field
renamed/retyped/enum-changed/absent, or one whose `required` membership, numeric/length/pattern/
items-count bounds or nested `items` object shape changed; any field newly `required` (D3 or not);
a D4 route method set changed or the route removed; a `verify_token` dropped from any D4
**method**+path pair (`POST /api/v1/videos`, `GET`/`DELETE /api/v1/tasks/{task_id}`,
`GET /api/v1/download/{file_path}` — the pair, not the path, so a change on one method cannot hide
behind another); an unresolved `$ref`. Advisory only: a non-`used` config key changed, a `used`
key absent from **both** files (the guard is inert), the commented-out `video_codec` /
`whisper.initial_prompt` (T5), a non-D3 `VideoParams` field or a constraint change on one, a
non-D4 route, and the progress-constant set (T6, warning — never blocking). The text report has a
`## pins` block for the git warnings, and every candidate-derived string in it is control-escaped.
Values are key names, types and route paths — a secret key or a value carrying URL userinfo
appears as `<changed>` / `<set>` / `<empty>`, never its value.

Verified 2026-09-27 at the pin `3d5f4e421927d61f3eac729cf4b711ac0b69d688`
(`--candidate "$NTS_HOME/src/mpt"`): exit 0, `"ok": true`, `"blocking": []`, `mpt_commit` = the pin,
config baseline sha256 `c073fdfc86a894b5fa4121f7c5c7124ca90c3fe92c61ba58b8465c54caa4809b`, wall time
1.38 s (one fused child instead of two), `"dirty": true` (the pin is dirty by design) and
`git status --porcelain` byte-identical before and after; `--self-test` (58 synthetic cases,
including a stub-candidate run() seam) green. This tool does not replace 05.4.1-T1 S0.

## 2. Model weights

sha256 = file content hash, spot-checked on the Mac 2026-09-26 (`shasum -a 256`, all rows OK).
`placed at (Mac)` is under `HF_HOME=$NTS_HOME/hf` (`$NTS_HOME` = `<repo>/.nts`, CR-11).

| name | repo | revision | file | bytes | sha256 | placed at (Mac) |
|---|---|---|---|---|---|---|
| `VieNeu backbone` | `pnnbao-ump/VieNeu-TTS-v3-Turbo` | `61b85e3d937fbbacb387714180e8182823512523` | `config.json` | 1553 | `eee8e032cb936a60312f594a8156c086173a9c0255a545bd11a448f22a7c77ae` | `hf/hub/models--pnnbao-ump--VieNeu-TTS-v3-Turbo/snapshots/<rev>/` |
| `VieNeu backbone` | `pnnbao-ump/VieNeu-TTS-v3-Turbo` | `61b85e3d937fbbacb387714180e8182823512523` | `denoiser.onnx` | 42661414 | `b7621953291cfe05e695a9c0ff4255aa2f93239fc17c26627e18b7b6b8f72f0b` | `hf/hub/models--pnnbao-ump--VieNeu-TTS-v3-Turbo/snapshots/<rev>/` |
| `VieNeu backbone` | `pnnbao-ump/VieNeu-TTS-v3-Turbo` | `61b85e3d937fbbacb387714180e8182823512523` | `onnx_update/config.json` | 2152 | `17d89d414ee302a82db7b330bf57b4cdf8541569392119c81f552178cafcb79b` | `hf/hub/models--pnnbao-ump--VieNeu-TTS-v3-Turbo/snapshots/<rev>/` |
| `VieNeu backbone` | `pnnbao-ump/VieNeu-TTS-v3-Turbo` | `61b85e3d937fbbacb387714180e8182823512523` | `onnx_update/tokenizer.json` | 22320 | `6cc6bcbe380b8c37bd9f2514e37c5dfa3e00e122c6e3125dae5c4afe48e39158` | `hf/hub/models--pnnbao-ump--VieNeu-TTS-v3-Turbo/snapshots/<rev>/` |
| `VieNeu backbone` | `pnnbao-ump/VieNeu-TTS-v3-Turbo` | `61b85e3d937fbbacb387714180e8182823512523` | `onnx_update/vieneu_acoustic_cached.onnx` | 7207223 | `f631e3387c788c3d8b9a5ac5df94952af5bc4c4d1049ff8a751e76a246fff2d4` | `hf/hub/models--pnnbao-ump--VieNeu-TTS-v3-Turbo/snapshots/<rev>/` |
| `VieNeu backbone` | `pnnbao-ump/VieNeu-TTS-v3-Turbo` | `61b85e3d937fbbacb387714180e8182823512523` | `onnx_update/vieneu_backbone_shared.data` | 415319040 | `c7c072193db33d0542457e2612c7272c44c4279d1cafaf0aa4c379964911db2f` | `hf/hub/models--pnnbao-ump--VieNeu-TTS-v3-Turbo/snapshots/<rev>/` |
| `VieNeu backbone` | `pnnbao-ump/VieNeu-TTS-v3-Turbo` | `61b85e3d937fbbacb387714180e8182823512523` | `onnx_update/vieneu_decode_step.onnx` | 306134 | `bedc379cea61ea5d616312750d95ad3924e055856662d19187a889a5edc24ceb` | `hf/hub/models--pnnbao-ump--VieNeu-TTS-v3-Turbo/snapshots/<rev>/` |
| `VieNeu backbone` | `pnnbao-ump/VieNeu-TTS-v3-Turbo` | `61b85e3d937fbbacb387714180e8182823512523` | `onnx_update/vieneu_prefill.onnx` | 324499 | `27f8b064f6b57b5448e95d095f1959588c005d614678045c2b97ecccf3b7a0f7` | `hf/hub/models--pnnbao-ump--VieNeu-TTS-v3-Turbo/snapshots/<rev>/` |
| `VieNeu backbone` | `pnnbao-ump/VieNeu-TTS-v3-Turbo` | `61b85e3d937fbbacb387714180e8182823512523` | `onnx_update/vieneu_v3_heads.npz` | 52219622 | `fb22484baa424bbb775133a6e5f0d00d6299b2b256fbe3312a864b85b9aed01e` | `hf/hub/models--pnnbao-ump--VieNeu-TTS-v3-Turbo/snapshots/<rev>/` |
| name | repo | revision | file | bytes | sha256 | placed at (Mac) |
|---|---|---|---|---|---|---|
| `VieNeu codec (Mac ONNX path)` | `OpenMOSS-Team/MOSS-Audio-Tokenizer-Nano-ONNX` | `ceff0d0749bfb3fa2d61149794ec6feef0d1e1ae` | `codec_browser_onnx_meta.json` | 17036 | `3e291c883bb7d11ff2fe8e964e3e495519760358859f35c951254c7741592731` | `hf/hub/models--OpenMOSS-Team--MOSS-Audio-Tokenizer-Nano-ONNX/snapshots/<rev>/` |
| `VieNeu codec (Mac ONNX path)` | `OpenMOSS-Team/MOSS-Audio-Tokenizer-Nano-ONNX` | `ceff0d0749bfb3fa2d61149794ec6feef0d1e1ae` | `moss_audio_tokenizer_decode_full.onnx` | 681902 | `0fbbafe3fd4afa2a019af5c5ced204af6e2d1db044fa40f021525d2aee95b4ac` | `hf/hub/models--OpenMOSS-Team--MOSS-Audio-Tokenizer-Nano-ONNX/snapshots/<rev>/` |
| `VieNeu codec (Mac ONNX path)` | `OpenMOSS-Team/MOSS-Audio-Tokenizer-Nano-ONNX` | `ceff0d0749bfb3fa2d61149794ec6feef0d1e1ae` | `moss_audio_tokenizer_decode_shared.data` | 44198912 | `e69d52e0f4e84ca27850557ee54face46632d3a5a16c89bd246c7c408466dcad` | `hf/hub/models--OpenMOSS-Team--MOSS-Audio-Tokenizer-Nano-ONNX/snapshots/<rev>/` |
| `VieNeu codec (Mac ONNX path)` | `OpenMOSS-Team/MOSS-Audio-Tokenizer-Nano-ONNX` | `ceff0d0749bfb3fa2d61149794ec6feef0d1e1ae` | `moss_audio_tokenizer_decode_step.onnx` | 351400 | `9527c86a29e1837edec1f74db57d5eeaadb3a715af3382703566460afed25855` | `hf/hub/models--OpenMOSS-Team--MOSS-Audio-Tokenizer-Nano-ONNX/snapshots/<rev>/` |
| `VieNeu codec (Mac ONNX path)` | `OpenMOSS-Team/MOSS-Audio-Tokenizer-Nano-ONNX` | `ceff0d0749bfb3fa2d61149794ec6feef0d1e1ae` | `moss_audio_tokenizer_encode.data` | 44507136 | `aa751265b2bab2887eac224484546b194875aa7494b607115439b3dc6b228a2c` | `hf/hub/models--OpenMOSS-Team--MOSS-Audio-Tokenizer-Nano-ONNX/snapshots/<rev>/` |
| `VieNeu codec (Mac ONNX path)` | `OpenMOSS-Team/MOSS-Audio-Tokenizer-Nano-ONNX` | `ceff0d0749bfb3fa2d61149794ec6feef0d1e1ae` | `moss_audio_tokenizer_encode.onnx` | 815775 | `eadea4a645abdcf98714c7aead122ee2ce7da6e080f9f80b977cd1ca8e19473a` | `hf/hub/models--OpenMOSS-Team--MOSS-Audio-Tokenizer-Nano-ONNX/snapshots/<rev>/` |

⚠️ Node file list (01 CR-12): the node PyTorch path loads codec `OpenMOSS-Team/MOSS-Audio-Tokenizer-Nano`
(PT, not `-ONNX`) and PyTorch backbone weights, so the table above is the Mac ONNX set only; 11.3.1
pins the PyTorch set separately. Whisper (✅ M3, verified `shasum -a 256 -c` 2026-09-26, 7/7 OK):

| name | repo | revision | file | bytes | sha256 | placed at (Mac) |
|---|---|---|---|---|---|---|
| `whisper` | `mobiuslabsgmbh/faster-whisper-large-v3-turbo` | `0a363e9161cbc7ed1431c9597a8ceaf0c4f78fcf` | `.gitattributes` | 1519 | `11ad7efa24975ee4b0c3c3a38ed18737f0658a5f75a0a96787b576a78a023361` | `$NTS_HOME/src/mpt/models/whisper-large-v3-turbo/` |
| `whisper` | `mobiuslabsgmbh/faster-whisper-large-v3-turbo` | `0a363e9161cbc7ed1431c9597a8ceaf0c4f78fcf` | `README.md` | 1445 | `b3068692728faed23580cce5cd569fc47ff76c690c032b2641ffd5554ea64d8f` | `$NTS_HOME/src/mpt/models/whisper-large-v3-turbo/` |
| `whisper` | `mobiuslabsgmbh/faster-whisper-large-v3-turbo` | `0a363e9161cbc7ed1431c9597a8ceaf0c4f78fcf` | `config.json` | 2263 | `b0253ea6c0d3bea6b1e19e91a02acfd3b53f4467362efcb5a3e6b16c9b3a9b7e` | `$NTS_HOME/src/mpt/models/whisper-large-v3-turbo/` |
| `whisper` | `mobiuslabsgmbh/faster-whisper-large-v3-turbo` | `0a363e9161cbc7ed1431c9597a8ceaf0c4f78fcf` | `model.bin` | 1617884929 | `e76620f83d5f5b69efd3d87e3dc180c1bd21df9fbebacfd4335e5e1efcc018da` | `$NTS_HOME/src/mpt/models/whisper-large-v3-turbo/` |
| `whisper` | `mobiuslabsgmbh/faster-whisper-large-v3-turbo` | `0a363e9161cbc7ed1431c9597a8ceaf0c4f78fcf` | `preprocessor_config.json` | 340 | `7ccc62c6f2765af1f3b46c00c9b5894426835a05021c8b9c01eecb6dfb542711` | `$NTS_HOME/src/mpt/models/whisper-large-v3-turbo/` |
| `whisper` | `mobiuslabsgmbh/faster-whisper-large-v3-turbo` | `0a363e9161cbc7ed1431c9597a8ceaf0c4f78fcf` | `tokenizer.json` | 2710337 | `297b13372ac43916285644fb9687add3cc62ee2a1adb60da3dc25cc94c1871fd` | `$NTS_HOME/src/mpt/models/whisper-large-v3-turbo/` |
| `whisper` | `mobiuslabsgmbh/faster-whisper-large-v3-turbo` | `0a363e9161cbc7ed1431c9597a8ceaf0c4f78fcf` | `vocabulary.json` | 1068114 | `c69260f2ab26d659b7c398f9a2b2b48ed0df16c3b47d7326782fd9cba71690c1` | `$NTS_HOME/src/mpt/models/whisper-large-v3-turbo/` |

Sources: `worker-node/vieneu/weights.sha256` (01.2.1-T1, 15 files), `worker-node/mpt/whisper-large-v3-turbo.sha256`
(01.2.3-T1, 7 files). Bytes via `stat -Lf %z` (HF cache symlinks → blobs).

## 3. Python + locks

Node installs from these locks with hashes (11.5.3-T3 enforcement). Lock sha256 measured 2026-09-26;
`git -C $NTS_HOME/src/<x> status --porcelain uv.lock` was empty for both clones (frozen = pinned).

| service | Python | lock file | sha256 of lock | install command (Mac) | install command (node) |
|---|---|---|---|---|---|
| MPT | `3.11` ✅ M7 | `$NTS_HOME/src/mpt/uv.lock` | `165e1410430a26fe75aeeefab744c692f2f402bcfbee011fc6fe52469217e258` | `uv python install 3.11` then `cd $NTS_HOME/src/mpt && uv sync --frozen` (bootstrap.sh) | same, against the node checkout |
| VieNeu | `3.12` ✅ V3 | `$NTS_HOME/src/vieneu/uv.lock` | `0921052a578f63d82305eb4a19be2579751edfb579eab0c9d65d977652481e2e` | `uv python install 3.12` then `cd $NTS_HOME/src/vieneu && uv sync --frozen` | same **plus** `uv sync --frozen --extra cuda --extra watermark` (01 CR-13; extras exist at the pin ⚠️ re-verify) |
| proxy | `3.12` (04 CR-3; `requires-python >=3.12,<3.13`) | `worker-node/proxy/uv.lock` | `e2f34398243d1e498703f72a5cfc92ec4ed4fb898f9c775ba7e8cb084913d6ed` | `UV_PROJECT_ENVIRONMENT=$NTS_HOME/src/proxy-venv uv sync --frozen --project worker-node/proxy` (bootstrap.sh) | same shape into the node venv dir (11.3.1) |

## 4. System packages

Versions copied from the `env` object of `trial/runs/20260926-0818-en-landscape-01/run.json`
(`worker-node/bin/env-versions.sh`, 01.1.1-T1) — the only completed measured run; the §11 gate run
is still pending, so re-capture these from the 05.5.2 `run.json` env when it lands ⚠️ 05.5.2.

| item | value | node requirement |
|---|---|---|
| macOS / build / hw | `27.0` / `26A428` / `Mac16,10` (Apple M4, 16 GB) | n/a — node is Linux; CUDA driver + CUDA libs replace this row ⚠️ 11.3.1-T1 picks versions |
| git | `2.49.0` | any ≥ 2.x |
| uv | `0.12.18` | same version or newer; record the node version |
| ffmpeg | `9.0.2` | required encoders `aac`, `libmp3lame` (`ffmpeg -hide_banner -encoders`, both listed ✅ E2) |
| gitleaks | `8.30.1` | dev-side only, not on the node |
| uv pythons | `3.9.6 3.11.16 3.12.14` | node needs `3.11` + `3.12` (§3) |
| shellcheck | `0.11.0` (worker-node/README) | dev-side only |
| fonts | MPT `resource/fonts`: `BeVietnamPro-Bold.ttf`, `BeVietnamPro-Medium.ttf`, `Charm-Bold.ttf`, `Charm-Regular.ttf`, `MicrosoftYaHeiBold.ttc`, `MicrosoftYaHeiNormal.ttc`, `STHeitiLight.ttc`, `STHeitiMedium.ttc`, `UTM Kabel KT.ttf` | BeVietnamPro-* is the 05.2.2 candidate (CR-6); licence check ⚠️ 05; final font decided by 05.2.2 |

## 5. Config + env catalog

Template: `worker-node/mpt/config.template.toml` → rendered by `worker-node/bin/render-mpt-config.py`
to `$NTS_HOME/src/mpt/config.toml` (0600, atomic, D5). D5 guard list, verbatim:
`listen_host == "127.0.0.1"` (top-level), `app.api_key` non-empty, `video_source == "openai_image"`,
`subtitle_provider == "whisper"`, `whisper.model_size == "large-v3-turbo"`, `upload_post_enabled == false`,
`pexels_api_keys == []`, `pixabay_api_keys == []`, `max_concurrent_tasks == 1`,
`openai_model_name` and `openai_image_model` non-empty (full list in 01.2.2-T2).

| name | purpose | secret | Keychain service (Mac) | node secret-manager name | Mac value | node value |
|---|---|---|---|---|---|---|
| `NTS_MPT_API_KEY` | MPT `[app] api_key` | yes | `nts.mpt.api_key` | `nts/<env>/NTS_MPT_API_KEY` | Keychain-only | `nts/<env>/NTS_MPT_API_KEY` |
| `NTS_OPENAI_STORY_KEY` | `openai_api_key` (story LLM) | yes | `nts.openai.story` | `nts/<env>/NTS_OPENAI_STORY_KEY` | Keychain-only | `nts/<env>/NTS_OPENAI_STORY_KEY` |
| `NTS_OPENAI_IMAGE_KEY` | `openai_image_api_keys[0]` | yes | `nts.openai.image` | `nts/<env>/NTS_OPENAI_IMAGE_KEY` | Keychain-only | `nts/<env>/NTS_OPENAI_IMAGE_KEY` |
| `NTS_PROXY_API_KEY` | `[kokoro] api_key` = `PROXY_API_KEY` | yes | `nts.proxy.api_key` | `nts/<env>/NTS_PROXY_API_KEY` | Keychain-only | `nts/<env>/NTS_PROXY_API_KEY` |
| `VIENEU_API_KEY` | VieNeu bearer | yes | `nts.vieneu.api_key` | `nts/<env>/VIENEU_API_KEY` | Keychain-only | `nts/<env>/VIENEU_API_KEY` |
| `HOST` | VieNeu bind host | no | — | — | `127.0.0.1` | `127.0.0.1` |
| `PORT` | VieNeu bind port | no | — | — | `8000` | `8000` |
| `VIENEU_BACKEND` | engine backend | no | — | — | `onnx` | `⚠️ 11.3.1` (PyTorch path, CR-9) |
| `VIENEU_DEVICE` | compute device | no | — | — | `cpu` | `cuda` (CR-9) |
| `VIENEU_PRECISION` | precision | no | — | — | `fp32` | ⚠️ 11.3.1 (fp16/bf16 candidate) |
| `VIENEU_MAX_STREAMS` | concurrency cap | no | — | — | `1` | ≥ `max_concurrent_tasks` (04 D5 sizing) |
| `VIENEU_QUEUE_TIMEOUT` | upstream queue timeout | no | — | — | unset (upstream default) ⚠️ | `⚠️ 11.3.1` |
| `VIENEU_WATERMARK` | Perth watermark | no | — | — | `1` (`env.example`; silent-off on Mac, class C — DEC-7/CR-13) | `1` + `Audio watermarking initialized (Perth)` log REQUIRED |
| `HF_HOME` | HF cache root | no | — | — | `$NTS_HOME/hf` | `/opt/nts/...` per 11.3.1 layout ⚠️ |
| `HF_HUB_OFFLINE` | offline weights | no | — | — | `1` | `1` after initial fetch |
| `HF_HUB_DISABLE_TELEMETRY` | HF telemetry | no | — | — | `1` | `1` |
| `NTS_HOME` | runtime root | no | — | — | `<repo>/.nts` (CR-11) | node runtime root (11.3.1) |
| `MPT_WEBUI_HOST` / `MPT_WEBUI_PORT` | WebUI bind | no | — | — | `127.0.0.1` / `8501` (unused, D14) | not deployed |
| `TAP_IMAGE_QUALITY` | tap quality injection | no | — | — | unset (off until 03 CR-3) | n/a:Mac-trial-only |
| `PROXY_HOST` / `PROXY_PORT` | proxy bind | no | — | — | `127.0.0.1` / `8001` | `127.0.0.1` / `8001` |
| `VIENEU_BASE_URL` | proxy → VieNeu upstream | no | — | — | `http://127.0.0.1:8000` | `http://127.0.0.1:8000` |
| `VOICE_CATALOG_FILE` | voice catalog | no | — | — | `$NTS_REPO_ROOT/seed/voices.v1.json` | `/opt/nts/current/seed/voices.v1.json` |
| `MP3_BITRATE` | proxy encode | no | — | — | `192k` | `192k` |
| `UPSTREAM_CONNECT_TIMEOUT_S` | proxy connect timeout | no | — | — | `5` | `5` |
| `UPSTREAM_READ_GAP_S` | proxy read gap | no | — | — | `60` | `60` |
| `FIRST_AUDIO_DEADLINE_S` | first-audio deadline | no | — | — | `90` | `90` |
| `ADMISSION_WAIT_S` | admission wait | no | — | — | `80` | `80` |
| `REQUEST_MAX_S` | request ceiling | no | — | — | `1800` | `1800` |
| `PROXY_MAX_CONCURRENT` | proxy concurrency | no | — | — | `2` (Mac start script; `4` env.example default) | sized per 04 D5 |
| `CATALOG_CHECK_INTERVAL_S` | catalog re-check | no | — | — | `300` | `300` |
| `FFMPEG_PATH` | encoder binary | no | — | — | `ffmpeg` (PATH) | `/usr/bin/ffmpeg` |
| `OPENAI_BASE_URL` | relay upstream (§1) | no | — | — | `https://api.aixingialaire.shop/v1` | same pin |
| `OPENAI_IMAGE_API` / `OPENAI_IMAGE_HOST_MODEL` / `OPENAI_EFFORT_IMAGE_HOST` / `OPENAI_EFFORT_CHAT` | tap image translation + effort pins | no | — | — | `responses` / `gpt-6-luna` / `low` / `medium` | same pins (tap is §9; these apply wherever the tap runs — CR-4) |

## 6. Ports / binds

| service | Mac bind (D4) | node bind |
|---|---|---|
| VieNeu | `127.0.0.1:8000` | `127.0.0.1:8000` (11 FR-11-4) |
| proxy | `127.0.0.1:8001` | `127.0.0.1:8001` (04 D1, 11 FR-11-4) |
| usage tap | `127.0.0.1:8002` | not deployed — Mac trial only (D6; CR-4 decides a node metering forwarder) ⚠️ 03/11/12 |
| MPT API | `127.0.0.1:8080` | `127.0.0.1` on the node (11 FR-11-4 / 05 D15 S1); the Worker reaches it via `mpt-adapter` only |
| mpt-adapter | n/a (Mac trial has none) | private interface `:8090` HTTPS, cert pinned in `worker_nodes.tls_cert_sha256` (11 FR-11-4) ⚠️ 11.3.2 |
| MPT WebUI | `127.0.0.1:8501` unused; nothing may listen on 8501–8599 (D14, `check-binds.sh`) | not deployed |

Mac enforcement: `start-*` scripts exit 2 if the port is busy; `check-binds.sh` allowlists
`127.0.0.1:<port>`/`[::1]:<port>` only; LAN probes refused (SEC-05).

## 7. Start scripts → systemd

| Mac script | foreground command | node unit (11.3.1-T2) | restart policy |
|---|---|---|---|
| `worker-node/bin/start-vieneu.sh` | `uv run --no-sync python -m apps.openai_speech` ✅ V3 | `vieneu.service` | ⚠️ 11.3.1-T2 (on-failure expected) |
| `worker-node/bin/start-proxy.sh` | `$NTS_HOME/src/proxy-venv/bin/python -m nts_vieneu_proxy` (04 CR-6) | `vieneu-proxy.service` (`worker-node/proxy/systemd/`, 04 D13 hardening) | unit file exists; policy per 04 D13 |
| `worker-node/bin/start-mpt-api.sh` | `uv run python main.py` ✅ M2 | `mpt.service` | ⚠️ 11.3.1-T2 |
| `trial/tools/start-tap.sh` | `trial/tools/usage_tap.py` | not carried (§9) | — |
| n/a (render) | `worker-node/bin/render-mpt-config.py` | `ExecStartPre` on `mpt.service` [INFERENCE], secrets via node secret manager | — |
| `worker-node/bin/install-music.sh` | sync script | `nts-music-sync.service` (05.3.2 owner) | ⚠️ 11.3.1-T2 |
| `worker-node/bin/check-binds.sh`, `smoke*.sh` | preflight/smoke | folded into `gpu-smoke.sh` + `nts-cleanup.service` (11.3.1-T1/11.3.4-T1) | — |

## 8. Voices + music

- Voices: `worker-node/vieneu/voices.snapshot.json`, 25 voices (`data` list), SDK 3.8.3 ✅ V5.
  sha256 `8619cf96d935ec8ce32b798b14deb02cc8a1156b421e0aaa53e77b828ee6cdb2`. Node catalog file:
  `seed/voices.v1.json` (04 §5.2), synced into `/opt/nts/current/seed/`.
- Music: `worker-node/music/LICENSES.csv` = 1 data row (`nts-ambient-pad-cc0.mp3`, CC0-1.0,
  sha256 `575ea4c4f779bdf0f3e1c7ca333f1e2d73556c55e74ca6ccd112ce4bb7a3d66d`, 90.0 s, −37.4 LUFS).
  MPT bundled tracks are stripped by `strip-bundled-music.sh` (01.2.2-T3; 05.3.1-T1 reuses it as the
  build check, CR-7). Music sync to the node is owned by 05.3.2.

## 9. Not carried

| item | reason |
|---|---|
| usage tap `usage_tap.py` (`127.0.0.1:8002`) | D6 is a trial tool; CR-4 decides whether the node runs a metering forwarder ⚠️ 03/11/12 |
| MPT WebUI (Streamlit, 8501) | D14 — never deployed |
| `secrets.sh` / `lib.sh secret` / macOS Keychain | node uses its secret manager (`nts/<env>/<NAME>`, 11 CR-7) |
| trial OpenAI keys/projects `nts-trial-*` | D7: keys are revoked at M0 close; node gets production keys (11.2.3) |
| `trial/` tools (`finish_run.py`, `rss_sampler.sh`, baselines) | Mac measurement rig |
| `$NTS_HOME/current_run` + `trial/runs/` | Mac run-lock state |
| `.nts/src/*` clones, `.venv`, HF cache | node rebuilds from pins + locks (§1–3), never a copy |

## 10. Mac vs Linux GPU differences (CR-9)

- VieNeu `VIENEU_BACKEND=onnx`/`VIENEU_DEVICE=cpu` → PyTorch CUDA: sync with
  `uv sync --frozen --extra cuda --extra watermark` (01 CR-13); codec repo changes from
  `…-Nano-ONNX` to `…-Nano` (PyTorch, 01 CR-12 → separate pin in 11.3.1) ⚠️.
- Watermark: Mac ran without it (class C silent-off, DEC-7). The node MUST log
  `Audio watermarking initialized (Perth)` at startup; without the `--extra watermark` lock of
  `resemble-perth` 1.0.1, init fails "Watermarker init failed" ⚠️ verify at 11.3.1-T1.
- whisper `[whisper] device`/`compute_type`: `cpu`/`int8` → `cuda`/`float16` ⚠️ (config.template.toml
  comments, CR-9).
- `video_codec`: never `h264_videotoolbox` on the node (Mac-only encoder).
- ONNX thread count: fixed upstream at the pin (✅ V3) — Mac-only effect, nothing to carry.
- Streamlit WebUI: never deployed (D14).
- Every speed/memory number in §11 and `trial/baselines/` is a "Mac baseline" (R4) — it is **not**
  node capacity; 11.6.3 measures the node.

## 11. Baselines

⚠️ [OPEN] waits for the 05.5.2 trial rows — `trial/measurements.csv` currently holds only the
en wiring-calibration row `20260926-0818-en-landscape-01` (`run_status=calibration`, not a 05.5.2
production row; the vi row is pending). This section fills after 05.5.2-T2; until then T1 is not done.

Planned rows (metric | value | unit | source link):

| metric | value | unit | source link |
|---|---|---|---|
| VieNeu RTF + idle/peak RAM | ⚠️ 05.5.2 | RTF / MiB | `trial/baselines/vieneu-mac-20260925.json`, `trial/baselines/vieneu-mac-20260925-2.json` |
| whisper RTF + RAM | ⚠️ 05.5.2 | RTF / MiB | `trial/baselines/whisper-mac-20260925.json` |
| proxy overhead / direct baseline | ⚠️ review | ms | `trial/baselines/proxy-mac-20260926.json` |
| OpenAI access check | recorded | — | `trial/baselines/openai-access-mac-20260925.json` |
| 120 s gate result (01.2.1-T4) | ⚠️ 05.5.2 | pass/fail | `trial/baselines/vieneu-mac-20260925-2.json` |
| per-run rows (vi + en) | ⚠️ 05.5.2 | per `trial/schema.md` | `trial/measurements.csv` |
| peak RSS per stage | ⚠️ 05.5.2 | MiB | `trial/runs/<run_id>/rss.csv` (01.3.2-T1) |

## 12. Open issues

| ⚠️ | owner plan | ref |
|---|---|---|
| Node weight file list = PyTorch set (codec `…-Nano`, backbone `.safetensors`/`.pt`), not the ONNX set in §2 | 11 (11.3.1) | 01 CR-12, CR-9 |
| `--extra cuda --extra watermark` present at the pinned `uv.lock`; re-verify at pin | 11 (11.3.1) | 01 CR-13 |
| `VIENEU_PRECISION`/`VIENEU_QUEUE_TIMEOUT` values on node | 11 (11.3.1) | CR-9 |
| `VIENEU_WATERMARK` must log `Audio watermarking initialized (Perth)` | 11 (11.3.1) | 01 CR-13, DEC-7 |
| whisper `cuda/float16` config values | 11 (11.3.1) | CR-9 |
| BeVietnamPro font licence; final font choice | 05 (05.2.2) | CR-6 |
| mpt-adapter bind/cert pinning details | 11 (11.3.2) | 11 FR-11-4, 05 D15 S1 |
| Node metering forwarder (tap analogue) | 03 / 11 / 12 | CR-4 |
| systemd restart policies + unit contents | 11 (11.3.1-T2) | 04 D13 |
| §4 env values re-captured from the 05.5.2 `run.json` | 05 (05.5.2) | this file §4 |
| §11 baselines unfilled | 05 (05.5.2) | this file §11 |

## Checklist

- [x] §1: every `pins.env` value quoted verbatim (pins table + §5 OpenAI rows).
- [x] §2: per-file sha256 for VieNeu backbone (9) + codec (6) + whisper (7); Mac spot-check `shasum -a 256` OK 2026-09-26.
- [x] §3: lock sha256 for mpt / vieneu / proxy `uv.lock`; `git status --porcelain uv.lock` clean.
- [x] §4: versions copied from `run.json.env` (env-versions.sh), encoders `aac`+`libmp3lame`.
- [x] §5: D5 guard list verbatim; every secret row names a D3 Keychain service and carries no value.
- [x] §6: all D4 ports (8000/8001/8002/8080) + 8501 rule present.
- [x] §7: every Mac `start-*` script mapped to a unit name or marked not-carried.
- [x] §8: voices.snapshot.json sha256 + 25 voices; LICENSES.csv row count = 1.
- [x] §9: tap, WebUI, Keychain, trial keys, trial tools, `current_run` listed.
- [x] §10: every CR-9 item present.
- [ ] §11: filled — **blocked on 05.5.2** (`⚠️` markers in place).
- [x] §12: every `⚠️` from §1–11 listed with an owner.
- [x] No secrets: `grep -nE 'sk-[A-Za-z0-9]'` on this file returns nothing; gitleaks clean.

## Changelog

| date | author | change | question ref |
|---|---|---|---|
| 2026-09-26 | PythonSpikes (subagent) | initial version; §1–10 + §12 filled from repo facts; §11 stubbed pending 05.5.2 | 01.3.3-T1 |
| 2026-09-27 | ImplUpgradeDiff (subagent) | §1 gains the MPT upgrade runbook (six steps + the three pinned baselines + BLOCKING/advisory rules + the pin-run evidence); tool `worker-node/bin/mpt-upgrade-diff.py` | 05.1.1-T1 |
| 2026-09-27 | FixUpgradeDiff (subagent) | §1 runbook: candidate-code warning + cleanup/stderr-log/deadline notes, exit-2 matrix, verify_token per method+path, constraint/`required` BLOCKING rules, `## pins` block, 57 self-test cases, pin-run wall time | 05.1.1-T1 |
