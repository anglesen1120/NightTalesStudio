#!/usr/bin/env bash
# worker-node/bin/fetch-vieneu-weights.sh — record, re-fetch and verify the VieNeu weights at the
# pinned HF revisions (01.2.1-T1). Public repos only: HF_TOKEN is never passed on.
set -euo pipefail
# shellcheck source=worker-node/bin/lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

usage() {
  cat <<'EOF'
Usage: worker-node/bin/fetch-vieneu-weights.sh [--fetch|--record|--verify]   (default: --fetch)

  --record  offline: check refs/main == pins, only the two pinned repos in $HF_HOME/hub, then write
            worker-node/vieneu/weights.sha256 (sha256 of every snapshot file).
  --fetch   online: download every file listed in weights.sha256 at its pinned revision, write
            refs/main = revision (VieNeu's revision-less lookup then resolves offline), then --verify.
  --verify  offline: refs/main == pins and every listed file matches its sha256; prints "OK <n> files".

Exit codes: 0 ok · 1 check failed (drift, extra repo, empty snapshot, mismatch, download error) ·
2 usage/environment (weights.sha256 missing, VieNeu not bootstrapped).
EOF
}

mode="${1:---fetch}"
[ $# -le 1 ] || { usage >&2; exit 2; }
case "$mode" in
  --help|-h) usage; exit 0 ;;
  --fetch|--record|--verify) ;;
  *) usage >&2; exit 2 ;;
esac

nts_home >/dev/null
load_pins
HF_HOME="$NTS_HOME/hf"
VENV_DIR="$NTS_HOME/src/vieneu"
SUMS="$NTS_REPO_ROOT/worker-node/vieneu/weights.sha256"
[ -e "$VENV_DIR/.git" ] || die 2 "VieNeu not bootstrapped: run worker-node/bin/bootstrap.sh vieneu"
[ "$mode" = --record ] || [ -f "$SUMS" ] || die 2 "missing $SUMS (run --record after the first online start)"

cache_dir() { printf '%s/hub/models--%s\n' "$HF_HOME" "${1//\//--}"; }

check_refs() {
  local repo rev d got rc=0
  for pair in "$VIENEU_MODEL_REPO=$VIENEU_MODEL_REVISION" "$VIENEU_CODEC_REPO=$VIENEU_CODEC_REVISION"; do
    repo="${pair%%=*}"; rev="${pair#*=}"; d="$(cache_dir "$repo")"
    got="$(cat "$d/refs/main" 2>/dev/null || echo missing)"
    if [ "$got" != "$rev" ]; then log "revision drift: $repo refs/main=$got pin=$rev"; rc=1; fi
  done
  return "$rc"
}

do_record() {
  check_refs || die 1 "revision drift: re-run the 01.1.1-T2 procedure (never hand-edit pins.env)"
  local extra repo rev d n lic_m lic_c tmp
  extra="$(find "$HF_HOME/hub" -mindepth 1 -maxdepth 1 -type d -name 'models--*' \
    ! -name "$(basename "$(cache_dir "$VIENEU_MODEL_REPO")")" \
    ! -name "$(basename "$(cache_dir "$VIENEU_CODEC_REPO")")" -print)"
  [ -z "$extra" ] || die 1 "unexpected repos in $HF_HOME/hub: $(printf '%s\n' "$extra" | xargs -n1 basename | tr '\n' ' ')"
  # Licence: from the card in the snapshot if VieNeu downloaded it, else from the operator's
  # step-7 check (NTS_LICENCE_MODEL / NTS_LICENCE_CODEC, read from the card at the pinned revision).
  lic_m="$(sed -n 's/^license: *//p' "$(cache_dir "$VIENEU_MODEL_REPO")/snapshots/$VIENEU_MODEL_REVISION/README.md" 2>/dev/null | head -n 1 || true)"
  lic_c="$(sed -n 's/^license: *//p' "$(cache_dir "$VIENEU_CODEC_REPO")/snapshots/$VIENEU_CODEC_REVISION/README.md" 2>/dev/null | head -n 1 || true)"
  lic_m="${lic_m:-${NTS_LICENCE_MODEL:-unrecorded}}"
  lic_c="${lic_c:-${NTS_LICENCE_CODEC:-unrecorded}}"
  if [ "$lic_m" = unrecorded ] || [ "$lic_c" = unrecorded ]; then
    log "WARN licence unrecorded: run 01.2.1-T1 step 7 and pass NTS_LICENCE_MODEL / NTS_LICENCE_CODEC"
  fi
  tmp="$(mktemp "$SUMS.XXXXXX")"
  {
    printf '# generated %s by fetch-vieneu-weights.sh --record\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    printf '# vieneu_commit=%s\n' "$VIENEU_COMMIT"
    printf '# licence model=%s codec=%s (model card at the pinned revision, 01.2.1-T1 step 7)\n' "$lic_m" "$lic_c"
  } >"$tmp"
  for pair in "$VIENEU_MODEL_REPO=$VIENEU_MODEL_REVISION" "$VIENEU_CODEC_REPO=$VIENEU_CODEC_REVISION"; do
    repo="${pair%%=*}"; rev="${pair#*=}"; d="$(cache_dir "$repo")/snapshots/$rev"
    n="$(find -L "$d" -type f 2>/dev/null | wc -l | tr -d ' ' || true)"
    [ "$n" -gt 0 ] || { rm -f "$tmp"; die 1 "empty snapshot: $repo@$rev"; }
    (cd "$d" && find -L . -type f | sed 's|^\./||' | LC_ALL=C sort | while IFS= read -r f; do
      printf '%s  %s@%s/%s\n' "$(shasum -a 256 "$f" | awk '{print $1}')" "$repo" "$rev" "$f"
    done) >>"$tmp"
  done
  mv "$tmp" "$SUMS"
  log "wrote $SUMS ($(grep -vc '^#' "$SUMS") files)"
}

do_verify() {
  check_refs || die 1 "revision drift"
  local sum spec repo rest rev path f n=0 bad=0
  while read -r sum spec; do
    case "$sum" in ''|'#'*) continue ;; esac
    repo="${spec%%@*}"; rest="${spec#*@}"; rev="${rest%%/*}"; path="${rest#*/}"
    f="$(cache_dir "$repo")/snapshots/$rev/$path"
    if [ ! -e "$f" ]; then log "missing: $spec"; bad=1; continue; fi
    [ "$(shasum -a 256 "$f" | awk '{print $1}')" = "$sum" ] || { log "sha256 mismatch: $spec"; bad=1; }
    n=$((n + 1))
  done <"$SUMS"
  [ "$bad" -eq 0 ] || die 1 "verify failed"
  echo "OK $n files"
}

do_fetch() {
  grep -v '^#' "$SUMS" | awk '{print $2}' | env -u HF_TOKEN HF_HOME="$HF_HOME" HF_HUB_DISABLE_TELEMETRY=1 \
    uv run --quiet --directory "$VENV_DIR" python -c '
import sys
from huggingface_hub import hf_hub_download
for spec in sys.stdin.read().split():
    repo, rest = spec.split("@", 1)
    rev, path = rest.split("/", 1)
    sub, _, name = path.rpartition("/")
    hf_hub_download(repo_id=repo, filename=name, subfolder=sub or None, revision=rev, repo_type="model")
' || die 1 "download failed"
  local repo rev d
  for pair in "$VIENEU_MODEL_REPO=$VIENEU_MODEL_REVISION" "$VIENEU_CODEC_REPO=$VIENEU_CODEC_REVISION"; do
    repo="${pair%%=*}"; rev="${pair#*=}"; d="$(cache_dir "$repo")"
    mkdir -p "$d/refs"
    printf '%s' "$rev" >"$d/refs/main"
  done
  do_verify
}

case "$mode" in
  --record) do_record ;;
  --verify) do_verify ;;
  --fetch) do_fetch ;;
esac
