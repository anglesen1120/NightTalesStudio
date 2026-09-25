#!/usr/bin/env bash
# worker-node/bin/bootstrap.sh — put MPT and VieNeu at the pinned commits in $NTS_HOME/src and sync
# their uv environments from the upstream locks (01.1.2-T3). Idempotent. Never edits config.toml.
set -euo pipefail
# shellcheck source=worker-node/bin/lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

usage() {
  cat <<'EOF'
Usage: worker-node/bin/bootstrap.sh [mpt|vieneu|proxy|all]    (default: all)
       worker-node/bin/bootstrap.sh --export                  (D13 mode export only)

Clones into $NTS_HOME/src/<project> if absent, checks out the pin from worker-node/pins.env
(detached), syncs with `uv sync --frozen`, verifies the interpreter, and re-applies the MPT
bundled-music strip. One summary line per project on stdout:
  <project> <sha> python=<x.y> sync=<frozen|unlocked> strip=<ok|skipped|n/a>

Exit codes: 0 ok · 1 check failed (HEAD != pin, local changes, python mismatch, frozen sync
failed, strip left audio) · 2 usage/environment (bad argument, missing tool, invalid pins,
non-clone directory, wrong remote, fetch failure, pin not found).
Never edit files in $NTS_HOME/src/* by hand: an upstream change is a new pin.
EOF
}

bootstrap_project() {
  local p="$1" repo="$2" pin="$3" py="$4" dir sync strip changes other got fresh=0
  dir="$NTS_HOME/src/$p"

  # 1. Clone if absent. The $dir/.git test also stops git from walking up into this repo (.nts is inside it).
  if [ ! -e "$dir/.git" ]; then
    if [ -d "$dir" ] && [ -n "$(ls -A "$dir")" ]; then
      die 2 "$dir exists but is not a git clone"
    fi
    log "$p: clone $repo"
    git clone --quiet --no-checkout --filter=blob:none "$repo" "$dir" \
      || die 2 "$p: clone failed; if $dir holds only .git, delete it and retry"
    fresh=1
  fi

  # 2. Remote sanity.
  got="$(git -C "$dir" remote get-url origin)"
  [ "$got" = "$repo" ] || die 2 "$p: origin is $got, expected $repo"

  # 3. Fetch the pin only if it is missing.
  if ! git -C "$dir" cat-file -e "$pin^{commit}" 2>/dev/null; then
    log "$p: fetch origin"
    git -C "$dir" fetch --quiet origin || die 2 "$p: fetch failed"
    if ! git -C "$dir" cat-file -e "$pin^{commit}" 2>/dev/null; then
      git -C "$dir" fetch --quiet origin "$pin" 2>/dev/null || true
    fi
    git -C "$dir" cat-file -e "$pin^{commit}" 2>/dev/null || die 2 "pin $pin not found in $repo"
  fi

  # 4. Tracked-tree cleanliness. MPT may only have deletions under resource/songs/ (previous strip).
  # A fresh --no-checkout clone has a populated index and an empty worktree, so every path shows
  # as deleted; there is nothing local to protect yet, so the check is skipped (found 2026-09-25).
  changes=""
  [ "$fresh" -eq 1 ] || changes="$(git -C "$dir" status --porcelain --untracked-files=no)"
  if [ -n "$changes" ]; then
    if [ "$p" = mpt ]; then
      other="$(printf '%s\n' "$changes" | grep -vE '^ D resource/songs/' || true)"
      [ -z "$other" ] || die 1 "local changes in $dir: $(printf '%s' "$other" | awk '{print $2}' | tr '\n' ' ')"
      git -C "$dir" restore --worktree -- resource/songs
    else
      die 1 "local changes in $dir: $(printf '%s' "$changes" | awk '{print $2}' | tr '\n' ' ')"
    fi
  fi

  # 5–6. Checkout (untracked/ignored files survive) and verify.
  git -C "$dir" checkout --quiet --detach "$pin"
  [ "$(git -C "$dir" rev-parse HEAD)" = "$pin" ] || die 1 "$p: HEAD != pin $pin"

  # 7. Python + sync from the upstream lock. Never fall back silently to an unfrozen sync.
  uv python install --quiet "$py" || die 2 "$p: uv python install $py failed"
  if git -C "$dir" ls-files --error-unmatch uv.lock >/dev/null 2>&1; then
    (cd "$dir" && uv sync --quiet --frozen --python "$py") || die 1 "$p: uv sync --frozen failed (lock present): see the 01.1.2-T3 failure table"
    sync=frozen
  elif [ "$p" = vieneu ]; then
    log "WARN $p: no tracked uv.lock at the pin; plain uv sync (reproducibility ⚠️, record in CARRY-OVER)"
    (cd "$dir" && uv sync --quiet --python "$py") || die 1 "$p: uv sync failed"
    log "$p: generated uv.lock sha256 $(shasum -a 256 "$dir/uv.lock" | awk '{print $1}')"
    sync=unlocked
  else
    die 1 "$p: uv.lock is not tracked at the pin (M7 expects it)"
  fi
  got="$(cd "$dir" && uv run --quiet --python "$py" python -c 'import sys; print("%d.%d" % sys.version_info[:2])')"
  [ "$got" = "$py" ] || die 1 "$p: interpreter is $got, pins.env says $py"

  # 8. MPT only: re-apply the bundled-music strip (script owned by 01.2.2-T3).
  strip=n/a
  if [ "$p" = mpt ]; then
    local s="$NTS_REPO_ROOT/worker-node/bin/strip-bundled-music.sh"
    if [ -x "$s" ]; then
      local rc=0
      "$s" || rc=$?
      [ "$rc" -eq 0 ] || { log "$p: strip failed (exit $rc)"; exit "$rc"; }
      strip=ok
    else
      log "WARN $p: strip-bundled-music.sh not present yet (01.2.2-T3); do not start MPT"
      strip=skipped
    fi
  fi
  printf '%s %s python=%s sync=%s strip=%s\n' "$p" "$pin" "$py" "$sync" "$strip"
}

bootstrap_proxy() {
  local proj="$NTS_REPO_ROOT/worker-node/proxy"
  # Target reserved by 04 CR-4; the proxy code and lock come with 04.1.5-T1.
  if [ ! -f "$proj/pyproject.toml" ]; then
    [ "${1:-}" = optional ] && { log "WARN proxy: worker-node/proxy/pyproject.toml absent (04.1.5-T1 not landed); skipped"; return 0; }
    die 2 "proxy: worker-node/proxy/pyproject.toml absent (04.1.5-T1 not landed)"
  fi
  uv python install --quiet "$PROXY_PYTHON" || die 2 "proxy: uv python install failed"
  UV_PROJECT_ENVIRONMENT="$NTS_HOME/src/proxy-venv" uv sync --quiet --frozen --project "$proj" --python "$PROXY_PYTHON" \
    || die 1 "proxy: uv sync --frozen failed"
  printf 'proxy - python=%s sync=frozen strip=n/a\n' "$PROXY_PYTHON"
}

export_tree() {
  [ "$(_nts_d13_mode)" = export ] || die 2 "--export runs only in D13 mode export (recorded mode: $(_nts_d13_mode))"
  local dirty sha dst
  dirty="$(git -C "$NTS_REPO_ROOT" status --porcelain -- worker-node trial/tools .githooks)"
  [ -z "$dirty" ] || die 1 "uncommitted changes in executed paths: $(printf '%s' "$dirty" | awk '{print $2}' | tr '\n' ' ')"
  sha="$(git -C "$NTS_REPO_ROOT" rev-parse HEAD)"
  dst="$NTS_HOME/exec/$sha"
  if [ ! -d "$dst" ]; then
    rm -rf -- "$dst.tmp"
    mkdir -p "$dst.tmp"
    git -C "$NTS_REPO_ROOT" archive "$sha" worker-node trial/tools .githooks | tar -x -C "$dst.tmp"
    mv "$dst.tmp" "$dst"
  fi
  chmod -R go-rwx "$dst"
  printf '%s\n' "$sha" >"$dst/.nts-exec-sha"
  printf 'export %s %s\n' "$sha" "$dst"
}

main() {
  local target="${1:-all}"
  [ $# -le 1 ] || { usage >&2; exit 2; }
  case "$target" in
    --help|-h) usage; exit 0 ;;
    mpt|vieneu|proxy|all|--export) ;;
    *) usage >&2; exit 2 ;;
  esac
  command -v git >/dev/null 2>&1 || die 2 "git missing"
  command -v uv >/dev/null 2>&1 || die 2 "uv missing"
  nts_home_init
  load_pins
  case "$target" in
    mpt) bootstrap_project mpt "$MPT_REPO" "$MPT_COMMIT" "$MPT_PYTHON" ;;
    vieneu) bootstrap_project vieneu "$VIENEU_REPO" "$VIENEU_COMMIT" "$VIENEU_PYTHON" ;;
    proxy) bootstrap_proxy ;;
    all)
      bootstrap_project mpt "$MPT_REPO" "$MPT_COMMIT" "$MPT_PYTHON"
      bootstrap_project vieneu "$VIENEU_REPO" "$VIENEU_COMMIT" "$VIENEU_PYTHON"
      bootstrap_proxy optional ;;
    --export) export_tree ;;
  esac
}

main "$@"
