#!/usr/bin/env bash
# worker-node/bin/fetch-whisper-model.sh — fetch the pinned faster-whisper model
# into MPT's local lookup dir and sha256-verify it (01.2.3-T1, D9).
# Idempotent: a dir whose files match worker-node/mpt/whisper-large-v3-turbo.sha256
# is never re-downloaded. No secrets; public repo; HF_TOKEN is never set.
set -euo pipefail

if [ "${1:-}" = "--help" ] || [ "${1:-}" = "-h" ]; then
  cat <<'EOF'
Usage: worker-node/bin/fetch-whisper-model.sh [--verify]
Downloads mobiuslabsgmbh/faster-whisper-large-v3-turbo at WHISPER_MODEL_REVISION
(pins.env) into $NTS_HOME/src/mpt/models/whisper-large-v3-turbo/ so MPT loads it
without any hub call (HF_HUB_OFFLINE=1 at run time).
  (none)     verify existing dir; download + record/verify sha256 when needed
  --verify   checksum check only, no network; exit 1 on missing file/mismatch
Exit codes: 0 ok · 1 checksum/model check failed · 2 usage/environment (D9).
EOF
  exit 0
fi
VERIFY_ONLY=false
case "${1:-}" in
  "") ;;
  --verify) VERIFY_ONLY=true ;;
  *) echo "fetch-whisper-model.sh: unexpected argument '$1' (see --help)" >&2; exit 2 ;;
esac
[ $# -le 1 ] || { echo "fetch-whisper-model.sh: too many arguments (see --help)" >&2; exit 2; }

# shellcheck source=worker-node/bin/lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

VERIFY_ONLY="$VERIFY_ONLY" nts_home_init
NTS_HOME="$(nts_home)"
load_pins

MPT_ROOT="$NTS_HOME/src/mpt"
DEST="$MPT_ROOT/models/whisper-large-v3-turbo"
SUMS="$NTS_REPO_ROOT/worker-node/mpt/whisper-large-v3-turbo.sha256"

[ -d "$MPT_ROOT/.venv" ] || die 2 "MPT venv missing at $MPT_ROOT (run bootstrap.sh mpt first)"

verify_dir() {
  [ -f "$SUMS" ] || return 1
  [ -d "$DEST" ] || return 1
  (cd "$DEST" && shasum -a 256 -c "$SUMS") >/dev/null 2>&1
}

if verify_dir; then
  log "ok, verified: $DEST"
  exit 0
fi
if $VERIFY_ONLY; then
  die 1 "verify failed: $DEST missing or sha256 mismatch against $SUMS"
fi

# --- download path ---
command -v uv >/dev/null 2>&1 || die 2 "required tool missing: uv"
free_kb="$(df -k "$NTS_HOME" | awk 'NR==2{print $4}')"
[ "${free_kb:-0}" -ge 5242880 ] || die 2 "less than 5 GB free under $NTS_HOME"
[ -d "$DEST" ] && { log "existing $DEST failed verification; replacing"; rm -rf "$DEST"; }
rm -rf "$DEST.tmp"
mkdir -p "$DEST.tmp"

# hf CLI name depends on the huggingface_hub version (⚠️ at pin): try `hf`, then
# `huggingface-cli`, then a transient env via `uv run --with huggingface_hub`.
# This Mac's IPv6 is blackholed (connect to v6 addrs times out, curl -6 fails);
# HF DNS returns v6 first, so an unpatched client stalls for minutes. When v6 is
# unreachable we skip the CLI and call snapshot_download (the same call the `hf
# download` CLI makes) with getaddrinfo forced to AF_INET.
V6_PROBE_PY='import socket,sys
s=socket.socket(socket.AF_INET6,socket.SOCK_STREAM);s.settimeout(1.5)
try:
    s.connect(("2606:4700:4700::1111",443));s.close()
except OSError: sys.exit(1)'
HF_IPV4_SHIM='import os,socket,sys
_g=socket.getaddrinfo
def _v4(h,p,*a,**k):
    k.pop("family",None)          # callers may pass family= as kwarg
    return _g(h,p,socket.AF_INET,*a[1:],**k)
socket.getaddrinfo=_v4
from huggingface_hub import snapshot_download
snapshot_download(os.environ["WHISPER_MODEL_REPO"],
    revision=os.environ["WHISPER_MODEL_REVISION"],local_dir=sys.argv[1])'
# hf_xet resolves DNS in Rust, outside the Python patch: disable it so every
# socket honours the AF_INET shim.
export HF_HUB_DISABLE_XET=1
dl() {
  if "$MPT_ROOT/.venv/bin/python" -c "$V6_PROBE_PY" >/dev/null 2>&1; then
    if uv run --project "$MPT_ROOT" hf download "$WHISPER_MODEL_REPO" \
        --revision "$WHISPER_MODEL_REVISION" --local-dir "$DEST.tmp"; then
      return 0
    fi
    if uv run --project "$MPT_ROOT" huggingface-cli download "$WHISPER_MODEL_REPO" \
        --revision "$WHISPER_MODEL_REVISION" --local-dir "$DEST.tmp"; then
      return 0
    fi
    uv run --no-project --with huggingface_hub hf download "$WHISPER_MODEL_REPO" \
      --revision "$WHISPER_MODEL_REVISION" --local-dir "$DEST.tmp" && return 0
    return 1
  fi
  log "IPv6 unreachable; forcing AF_INET (snapshot_download, same call as 'hf download')"
  if uv run --project "$MPT_ROOT" python -c "$HF_IPV4_SHIM" "$DEST.tmp"; then
    return 0
  fi
  uv run --no-project --with huggingface_hub python -c "$HF_IPV4_SHIM" "$DEST.tmp"
}
log "downloading $WHISPER_MODEL_REPO @ $WHISPER_MODEL_REVISION"
dl || die 1 "download failed for $WHISPER_MODEL_REPO @ $WHISPER_MODEL_REVISION"
rm -rf "$DEST.tmp/.cache"   # hub metadata, not model files
if [ -n "$(find "$DEST.tmp" -type l -print -quit)" ]; then
  die 1 "unexpected symlink in $DEST.tmp (--local-dir must write real files)"
fi
[ -f "$DEST.tmp/model.bin" ] || die 1 "model.bin missing after download into $DEST.tmp"

if [ -f "$SUMS" ]; then
  # Manifest already committed: verify, do not overwrite (checksum drift → investigate).
  if (cd "$DEST.tmp" && shasum -a 256 -c "$SUMS") >/dev/null 2>&1; then
    mv "$DEST.tmp" "$DEST"
    log "ok, downloaded and verified against $SUMS"
    exit 0
  fi
  die 1 "fresh download fails sha256 check; kept $DEST.tmp for inspection"
fi

# First fetch on this machine: record the manifest, then verify it.
(cd "$DEST.tmp" && find . -type f ! -path './.cache/*' | sed 's|^\./||' | LC_ALL=C sort \
  | xargs shasum -a 256) > "$SUMS" \
  || die 1 "failed writing $SUMS"
mv "$DEST.tmp" "$DEST"
if verify_dir; then
  log "ok, downloaded; recorded $SUMS"
  exit 0
fi
die 1 "recorded manifest failed re-check: $SUMS"
