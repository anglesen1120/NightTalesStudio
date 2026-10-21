#!/usr/bin/env bash
# worker-node/bin/check-music.sh — M0 music build check (05.3.1-T1, README §7 gate item).
# Proves a node cannot serve a render with audio it has no licence for, and is missing no
# active catalog track. Three groups, in order:
#   1  strip-bundled-music.sh --check      (allow-list/extension rules stay in 01.2.2-T3, CR-7)
#   2  every active catalog track exists in resource/songs with the catalog's sha256
#   3  check_catalog.py self-consistency   (05.3.2-T1)
# Read-only, always: never deletes, never creates files; the only side effect is
# 01.2.2-T3's `mkdir -p` of `resource/songs`/`storage/bgm` (empty media dirs) inside the
# strip script, which is only ever called with --check.
# Exit codes: 0 ok · 1 a check failed · 2 usage/environment (2 > 1).
set -euo pipefail
# shellcheck source=worker-node/bin/lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
STRIP_CHECK="$REPO_ROOT/worker-node/bin/strip-bundled-music.sh"
CATALOG_CHECK="$REPO_ROOT/worker-node/music/tools/check_catalog.py"

usage() {
  cat <<'EOF'
Usage: worker-node/bin/check-music.sh [--mpt-root DIR] [--catalog FILE] [--json] [--help]
  --mpt-root DIR   MPT checkout (default: $NTS_HOME/src/mpt)
  --catalog FILE   music catalog JSON (default: seed/music-tracks.v1.json)
  --json           print exactly one JSON line on stdout for the health adapter
                   (D4: ok -> assets.ok, catalog_revision -> assets.music_revision,
                    music_missing -> the music_missing reason); all human output
                   moves to stderr. On an exit-2 usage/environment failure the line
                   is best-effort (ok=false, catalog_revision=null): callers must
                   use the exit code to tell an env failure from a check failure.
  --help           this text
Exit codes: 0 all checks ok - 1 a check failed - 2 usage/environment.
EOF
}

JSON=0
MPT_ROOT=""
CATALOG=""
while [ $# -gt 0 ]; do
  case "$1" in
    --mpt-root) [ $# -ge 2 ] || { usage >&2; exit 2; }; MPT_ROOT="$2"; shift ;;
    --catalog)  [ $# -ge 2 ] || { usage >&2; exit 2; }; CATALOG="$2"; shift ;;
    --json)     JSON=1 ;;
    --help|-h)  usage; exit 0 ;;
    *)          usage >&2; exit 2 ;;
  esac
  shift
done

if [ -z "$MPT_ROOT" ]; then MPT_ROOT="$(nts_home)/src/mpt"; fi
if [ -z "$CATALOG" ]; then CATALOG="$REPO_ROOT/seed/music-tracks.v1.json"; fi

problems=0
missing_ids=""            # newline-separated track ids
n_unlicensed=0
unlicensed_lines=""       # newline-separated relative paths, capped at 20 + an ellipsis row
catalog_errors=0
catalog_revision="null"
n_active=0
env_failed=0

# human <text> — problem lines and the summary. stdout normally; stderr under --json so the
# single stdout line is machine-readable.
human() {
  if [ "$JSON" -eq 1 ]; then printf '%s\n' "$1" >&2; else printf '%s\n' "$1"; fi
}

# emit_json — one line for render-node-health.v1.assets (D4).
emit_json() {
  {
    if [ "$problems" -eq 0 ] && [ "$env_failed" -eq 0 ]; then printf 'ok\ttrue\n'; else printf 'ok\tfalse\n'; fi
    printf 'revision\t%s\n' "$catalog_revision"
    if [ -n "$missing_ids" ]; then
      while IFS= read -r _m; do printf 'missing\t%s\n' "$_m"; done <<< "$missing_ids"
    fi
    if [ -n "$unlicensed_lines" ]; then
      while IFS= read -r _u; do printf 'unlicensed\t%s\n' "$_u"; done <<< "$unlicensed_lines"
    fi
    printf 'catalog_errors\t%s\n' "$catalog_errors"
  } | uv run --no-project --python 3.12 python -c '
import json, sys
ok, rev, missing, unlic, cerr = True, None, [], [], 0
for line in sys.stdin.read().splitlines():
    k, sep, v = line.partition("\t")
    if not sep:
        continue
    if k == "ok":
        ok = v == "true"
    elif k == "revision":
        try:
            rev = int(v)
        except ValueError:
            rev = None
    elif k == "missing":
        missing.append(v)
    elif k == "unlicensed":
        unlic.append(v)
    elif k == "catalog_errors":
        try:
            cerr = int(v)
        except ValueError:
            cerr = 0
print(json.dumps({"ok": ok, "catalog_revision": rev,
                  "music_missing": missing, "unlicensed": unlic,
                  "catalog_errors": cerr}))
'
}

# env_fail <msg> — usage/environment error: worst outcome, exit 2. The JSON line is still
# emitted under --json when uv is available, and always in the documented exit-2 shape
# (catalog_revision null, empty lists — 05 T53): a partial run must not leak the counters it
# happened to collect into the discriminator the adapter keys on.
env_fail() {
  env_failed=1
  printf 'check-music: %s\n' "$1" >&2
  if [ "$JSON" -eq 1 ]; then
    catalog_revision="null"
    missing_ids=""
    unlicensed_lines=""
    n_unlicensed=0
    catalog_errors=0
    emit_json 2>/dev/null || :
  fi
  exit 2
}

# finish — print the summary, emit the JSON line under --json, exit 0 or 1.
finish() {
  if [ "$problems" -eq 0 ]; then
    human "music: ok (revision $catalog_revision, $n_active active tracks)"
  else
    human "music: FAILED ($problems problems)"
  fi
  if [ "$JSON" -eq 1 ]; then emit_json; fi
  if [ "$problems" -eq 0 ]; then exit 0; else exit 1; fi
}

# add_missing <id> <annotation> — catalog track absent or hashed wrong (D8).
add_missing() {
  problems=$((problems + 1))
  if [ -n "$missing_ids" ]; then missing_ids="$missing_ids
$1"; else missing_ids="$1"; fi
  human "music_missing $1$2"
}

# add_unlicensed <rel-path> — offender reported by the strip check (group 1).
add_unlicensed() {
  problems=$((problems + 1))
  n_unlicensed=$((n_unlicensed + 1))
  if [ "$n_unlicensed" -le 20 ]; then
    if [ -n "$unlicensed_lines" ]; then unlicensed_lines="$unlicensed_lines
$1"; else unlicensed_lines="$1"; fi
    human "unlicensed $1"
  elif [ "$n_unlicensed" -eq 21 ]; then
    unlicensed_lines="$unlicensed_lines
…"
    human "unlicensed …"
  fi
}

# ── Preflight: every usage/environment exit-2 condition is checked before group 1 runs,
# so precedence is 2 > 1 no matter which check would have failed. ──────────────────────────
command -v uv >/dev/null 2>&1 \
  || env_fail "uv not found on PATH (the stdlib checks run 'uv run --no-project --python 3.12')"
uv run --no-project --python 3.12 python -c '' >/dev/null 2>&1 \
  || env_fail "python 3.12 via uv unavailable (no download is attempted: uv run --no-project only)"
[ -f "$CATALOG" ] \
  || env_fail "catalog file not found: $CATALOG (revision 1 is a 05-M0 gate item; see 05.3.2-T1)"
[ -f "$CATALOG_CHECK" ] \
  || env_fail "check_catalog.py not found: $CATALOG_CHECK (written by 05.3.2-T1)"
[ -f "$STRIP_CHECK" ] \
  || env_fail "strip-bundled-music.sh not found: $STRIP_CHECK (written by 01.2.2-T3; never inlined here)"
[ -f "$MPT_ROOT/main.py" ] || env_fail "not an MPT root (no main.py): $MPT_ROOT"
[ -d "$MPT_ROOT/resource" ] || env_fail "no resource/ dir in MPT root: $MPT_ROOT"

# ── Group 1: no unlicensed audio. A failure exits 1 at once (foreign audio is the worse
# condition; groups 2/3 read a tree that must be trusted). ──────────────────────────────
strip_out=""
strip_rc=0
strip_out="$(bash "$STRIP_CHECK" --check --mpt-root "$MPT_ROOT" 2>&1)" || strip_rc=$?
case "$strip_rc" in
  0) ;;
  1)
    # Offender lines look like "<ts> strip-bundled-music.sh: unlicensed <relative-path>".
    printf '%s\n' "$strip_out" >&2
    while IFS= read -r line; do
      case "$line" in
        *": unlicensed "*) add_unlicensed "${line##*": unlicensed "}" ;;
      esac
    done <<< "$strip_out"
    [ "$n_unlicensed" -gt 0 ] || add_unlicensed "(strip check failed; see log output)"
    finish
    ;;
  *)
    printf '%s\n' "$strip_out" >&2
    env_fail "strip-bundled-music.sh --check failed (exit $strip_rc)"
    ;;
esac

# ── Group 2: every active catalog track present in resource/songs with the catalog sha256.
# A malformed catalog is catalog_unreadable (still exit 1); group 3 then reports the
# precise rule error. resource/songs missing = an empty tree: every active track missing.
catalog_dump=""
dump_rc=0
catalog_dump="$(uv run --no-project --python 3.12 python -c '
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
print(d["revision"])
for t in d["tracks"]:
    if t.get("active"):
        print(t["id"], t["file"], t["sha256"], sep="\t")
' "$CATALOG" 2>/dev/null)" || dump_rc=$?

if [ "$dump_rc" -ne 0 ]; then
  problems=$((problems + 1))
  human "catalog_unreadable $CATALOG"
else
  catalog_revision="${catalog_dump%%$'\n'*}"
  rows=""
  case "$catalog_dump" in *$'\n'*) rows="${catalog_dump#*$'\n'}" ;; esac
  while IFS=$'\t' read -r tid tfile tsha; do
    [ -n "$tid" ] || continue
    n_active=$((n_active + 1))
    f="$MPT_ROOT/resource/songs/$tfile"
    if [ -f "$f" ]; then
      have="$(shasum -a 256 "$f" | cut -d' ' -f1 | tr '[:upper:]' '[:lower:]')" || have="__unreadable__"
      want="$(printf '%s' "$tsha" | tr '[:upper:]' '[:lower:]')"
      [ "$have" = "$want" ] || add_missing "$tid" " (sha256)"
    else
      add_missing "$tid" ""
    fi
  done <<< "$rows"
fi

# ── Group 3: catalog self-consistency (05.3.2-T1's check_catalog.py). ───────────────────
cc_out=""
cc_rc=0
cc_out="$(uv run --no-project --python 3.12 "$CATALOG_CHECK" --catalog "$CATALOG" 2>&1)" || cc_rc=$?
case "$cc_rc" in
  0) ;;
  1)
    # catalog_errors = the check's error lines (ERROR <code> [<track>] <detail>), min 1.
    # 05.3.2-T1's verdict contract is the only format counted; an unknown-format failure
    # still counts as >= 1 via the fallback.
    n_err=0
    while IFS= read -r line; do
      case "$line" in
        "ERROR "*|"error "*) n_err=$((n_err + 1)) ;;
      esac
    done <<< "$cc_out"
    [ "$n_err" -gt 0 ] || n_err=1
    catalog_errors=$n_err
    printf '%s\n' "$cc_out" >&2
    problems=$((problems + n_err))
    ;;
  *)
    printf '%s\n' "$cc_out" >&2
    env_fail "check_catalog.py failed (exit $cc_rc)"
    ;;
esac

finish
