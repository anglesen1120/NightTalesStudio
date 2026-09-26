#!/usr/bin/env bash
# worker-node/bin/tests/check-music.test.sh — 05.3.1-T1 named cases.
# Hermetic: builds a sandbox repo under $NTS_HOME/runs/check-music-$$/repo containing copies
# of check-music.sh / lib.sh / strip-bundled-music.sh, the real worker-node/music/tools/
# check_catalog.py (05.3.2-T1, byte copy; the sandbox is its REPO), and a sandbox
# LICENSES.csv with the appended track_id/platform_scope/evidence_url/retrieved_at columns,
# whose rows cover exactly the installed test tracks. No network, no mutation outside the
# temp tree.
set -euo pipefail

SELF="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"
BIN_DIR="$(dirname "$(dirname "$SELF")")"           # worker-node/bin
REPO_ROOT="$(cd "$BIN_DIR/../.." && pwd)"          # repo root
NTS_HOME_DIR="${NTS_HOME:-$REPO_ROOT/.nts}"
RUN_DIR="$NTS_HOME_DIR/runs/check-music-$$"
SB="$RUN_DIR/repo"                                 # sandbox repo root
CHECK="$SB/worker-node/bin/check-music.sh"         # sandbox copy under test
ERRF="$RUN_DIR/stderr.txt"

command -v uv >/dev/null 2>&1 || { echo "test: uv missing" >&2; exit 2; }
command -v shasum >/dev/null 2>&1 || { echo "test: shasum missing" >&2; exit 2; }

mkdir -p "$RUN_DIR"
trap 'rm -rf "$RUN_DIR"' EXIT

# ── Sandbox ────────────────────────────────────────────────────────────────────────────
mkdir -p "$SB/worker-node/bin" "$SB/worker-node/music/tools"
cp "$BIN_DIR/check-music.sh" "$BIN_DIR/lib.sh" "$BIN_DIR/strip-bundled-music.sh" \
   "$SB/worker-node/bin/"
printf 'file,title,artist,source_url,license,license_url,attribution,mood_tags,sha256,duration_s,loudness_lufs,added_at,track_id,platform_scope,evidence_url,retrieved_at\n' \
  > "$SB/worker-node/music/LICENSES.csv"
# The real 05.3.2-T1 checker (byte copy): group 3 is only exercised by the real tool. Its
# own REPO is parents[3] of this path, i.e. the sandbox, so --licenses defaults to the
# sandbox CSV and group 3 reads the curated row written by curated_row.
cp "$REPO_ROOT/worker-node/music/tools/check_catalog.py" "$SB/worker-node/music/tools/"

# ── Builders ───────────────────────────────────────────────────────────────────────────
CSV="$SB/worker-node/music/LICENSES.csv"
PY_RUN='uv run --no-project --python 3.12 python -c'

new_mpt() {  # <dir>
  mkdir -p "$1/resource/songs" "$1/storage/bgm"
  printf '#!/usr/bin/env python3\n' > "$1/main.py"
}

# install_track <mpt> <file> <bytes> — writes the audio file and echoes its sha256.
install_track() {
  printf '%s' "$3" > "$1/resource/songs/$2"
  shasum -a 256 "$1/resource/songs/$2" | cut -d' ' -f1
}

# curated_row <sha256> — (re)install the single curated LICENSES.csv row for the sandbox
# track, directly under the header: group 1 allow-lists its sha256, and group 3 (the real
# check_catalog.py) matches its track_id/file/license/platform_scope/mood_tags/evidence_url
# against the sandbox catalog. Any row an earlier case wrote for temple-01 is dropped, so the
# current case's row always wins.
curated_row() {
  local tmp="$CSV.tmp"
  {
    sed -n '1p' "$CSV"
    printf 'temple-01.mp3,Curated Title,Curated Artist,https://example.invalid/source,CC0-1.0,https://creativecommons.org/publicdomain/zero/1.0/,,temple,%s,120.0,-20.0,2026-01-01,temple-01,any,https://example.invalid/evidence/curated,2026-09-26T00:00:00Z\n' "$1"
    sed -n '2,$p' "$CSV" | grep -v ',temple-01,' || true
  } > "$tmp"
  mv "$tmp" "$CSV"
}

# allow_sha <sha256> — append an allow-list-only row (empty track_id, so the real
# check_catalog.py ignores it); only the sha256 column matters to group 1.
allow_sha() {
  printf 'x.mp3,t,a,http://e,CC0-1.0,http://l,,dark_ambient,%s,60,-30,2026-01-01,,,,\n' "$1" >> "$CSV"
}

# write_cat <path> <sha256> <active true|false> [<schema>] — a D8-valid 1-track catalog (the
# default schema passes the real checker; a wrong schema is env/usage for it → exit 2).
write_cat() {
  cat > "$1" <<EOF
{
  "schema": "${4:-music-tracks.v1}",
  "revision": 1,
  "tracks": [
    {
      "id": "temple-01",
      "file": "temple-01.mp3",
      "title": "Curated Title",
      "artist": "Curated Artist",
      "sourceUrl": "https://example.invalid/source",
      "license": "CC0-1.0",
      "licenseUrl": "https://creativecommons.org/publicdomain/zero/1.0/",
      "attribution": null,
      "platformScope": "any",
      "moods": ["temple"],
      "durationSec": 120.0,
      "loudnessLufs": -20.0,
      "bgmVolume": 0.2,
      "sha256": "$2",
      "active": $3
    }
  ]
}
EOF
}

# write_cat_ccby <path> <sha256> — a D8-valid catalog except license CC-BY-4.0 with no
# attribution (check_catalog.py's attribution_required rule error).
write_cat_ccby() {
  cat > "$1" <<EOF
{
  "schema": "music-tracks.v1",
  "revision": 1,
  "tracks": [
    {
      "id": "temple-01",
      "file": "temple-01.mp3",
      "title": "Curated Title",
      "artist": "Curated Artist",
      "sourceUrl": "https://example.invalid/source",
      "license": "CC-BY-4.0",
      "licenseUrl": "https://creativecommons.org/licenses/by/4.0/",
      "attribution": null,
      "platformScope": "any",
      "moods": ["temple"],
      "durationSec": 120.0,
      "loudnessLufs": -20.0,
      "bgmVolume": 0.2,
      "sha256": "$2",
      "active": true
    }
  ]
}
EOF
}

# run_check <mpt> <catalog> [extra args…] — sets RC, OUT (stdout), ERRF (stderr file).
run_check() {
  local mpt="$1" cat="$2"; shift 2
  RC=0
  OUT="$(bash "$CHECK" --mpt-root "$mpt" --catalog "$cat" "$@" 2>"$ERRF")" || RC=$?
}

fail() { printf '    FAIL: %s\n' "$1"; return 1; }
expect_rc() {
  if [ "$RC" -ne "$1" ]; then
    printf '    FAIL: expected rc=%s got %s\n    stdout: %s\n    stderr: %s\n' \
      "$1" "$RC" "$OUT" "$(cat "$ERRF")"
    return 1
  fi
  return 0
}
expect_line() {  # exact stdout line
  if ! printf '%s\n' "$OUT" | grep -xFq "$1"; then
    printf '    FAIL: stdout missing exact line: %s\n    stdout: %s\n' "$1" "$OUT"
    return 1
  fi
  return 0
}

# ── Cases ──────────────────────────────────────────────────────────────────────────────
t_healthy_tree_exit_0() {
  local d="$RUN_DIR/t_healthy" cat="$RUN_DIR/t_healthy.json" sha
  new_mpt "$d"
  sha="$(install_track "$d" temple-01.mp3 'track-bytes-01')" || return 1
  curated_row "$sha"
  write_cat "$cat" "$sha" true
  run_check "$d" "$cat"; expect_rc 0 || return 1
  expect_line 'music: ok (revision 1, 1 active tracks)' || return 1
  run_check "$d" "$cat" --json; expect_rc 0 || return 1
  $PY_RUN 'import json,sys; d=json.loads(sys.argv[1]); assert d["ok"] is True and d["music_missing"]==[] and d["catalog_revision"]==1 and d["catalog_errors"]==0, d' "$OUT" \
    || { printf '    FAIL: json wrong: %s\n' "$OUT"; return 1; }
  return 0
}

t_missing_installed_file_reports_music_missing() {
  local d="$RUN_DIR/t_missing" cat="$RUN_DIR/t_missing.json" sha
  new_mpt "$d"
  sha="$(install_track "$d" temple-01.mp3 'track-bytes-02')"
  curated_row "$sha"
  write_cat "$cat" "$sha" true
  rm -f "$d/resource/songs/temple-01.mp3"
  run_check "$d" "$cat"; expect_rc 1 || return 1
  expect_line 'music_missing temple-01' || return 1
  [ "$(printf '%s\n' "$OUT" | grep -c '^music_missing ')" -eq 1 ] || { printf '    FAIL: want exactly 1 music_missing line\n'; return 1; }
  run_check "$d" "$cat" --json; expect_rc 1 || return 1
  $PY_RUN 'import json,sys; d=json.loads(sys.argv[1]); assert d["ok"] is False and d["music_missing"]==["temple-01"], d' "$OUT" \
    || { printf '    FAIL: json wrong: %s\n' "$OUT"; return 1; }
  return 0
}

t_tampered_installed_file_reports_music_missing() {
  local d="$RUN_DIR/t_tampered" cat="$RUN_DIR/t_tampered.json" sha sha2
  new_mpt "$d"
  sha="$(install_track "$d" temple-01.mp3 'base-bytes')"
  printf 'x' >> "$d/resource/songs/temple-01.mp3"   # one byte appended (catalog pins $sha)
  sha2="$(shasum -a 256 "$d/resource/songs/temple-01.mp3" | cut -d' ' -f1)"
  # The installed bytes ARE allow-listed (a licensed v2), so group 1 is clean and group 2
  # reports the catalog-vs-disk sha256 mismatch — the only way to reach that line, since an
  # unlisted tamper is a group-1 offender instead.
  curated_row "$sha"
  allow_sha "$sha2"
  write_cat "$cat" "$sha" true
  run_check "$d" "$cat"; expect_rc 1 || return 1
  expect_line 'music_missing temple-01 (sha256)' || return 1
  run_check "$d" "$cat" --json; expect_rc 1 || return 1
  $PY_RUN 'import json,sys; d=json.loads(sys.argv[1]); assert d["ok"] is False and d["music_missing"]==["temple-01"], d' "$OUT" \
    || { printf '    FAIL: json wrong: %s\n' "$OUT"; return 1; }
  return 0
}

t_inactive_track_is_not_required() {
  local d="$RUN_DIR/t_inactive" cat="$RUN_DIR/t_inactive.json"
  new_mpt "$d"
  # group 3 needs the CSV row even for an inactive track (the real check_catalog.py has no
  # active filter); the file itself stays absent.
  curated_row "0000000000000000000000000000000000000000000000000000000000000000"
  write_cat "$cat" "0000000000000000000000000000000000000000000000000000000000000000" false
  run_check "$d" "$cat"; expect_rc 0 || return 1
  expect_line 'music: ok (revision 1, 0 active tracks)' || return 1
  return 0
}

t_planted_storage_bgm_file_exit_1() {
  local d="$RUN_DIR/t_planted" cat="$RUN_DIR/t_planted.json" sha
  new_mpt "$d"
  sha="$(install_track "$d" temple-01.mp3 'track-bytes-03')"
  allow_sha "$sha"
  write_cat "$cat" "$sha" true
  printf x > "$d/storage/bgm/planted.mp3"
  run_check "$d" "$cat"; expect_rc 1 || return 1
  expect_line 'unlicensed storage/bgm/planted.mp3' || return 1
  [ -f "$d/storage/bgm/planted.mp3" ] || fail 'planted file was deleted'
  return 0
}

t_tampered_resource_song_removed_by_strip_check() {
  local d="$RUN_DIR/t_song_offender" cat="$RUN_DIR/t_song_offender.json" sha
  new_mpt "$d"
  sha="$(install_track "$d" temple-01.mp3 'track-bytes-04')"
  allow_sha "$sha"
  write_cat "$cat" "$sha" true
  cp "$d/resource/songs/temple-01.mp3" "$d/resource/songs/side-copy.mp3"
  printf 'x' >> "$d/resource/songs/side-copy.mp3"   # unknown hash -> group-1 offender
  run_check "$d" "$cat"; expect_rc 1 || return 1
  expect_line 'unlicensed resource/songs/side-copy.mp3' || return 1
  [ -f "$d/resource/songs/side-copy.mp3" ] || fail 'offender was deleted (check must never remove)'
  return 0
}

t_missing_catalog_file_exit_2() {
  local d="$RUN_DIR/t_nocat"
  new_mpt "$d"
  run_check "$d" "$RUN_DIR/absent.json"; expect_rc 2 || return 1
  return 0
}

t_missing_resource_dir_exit_2() {
  local d="$RUN_DIR/t_noresource" cat="$RUN_DIR/t_noresource.json"
  mkdir -p "$d"
  printf '#!/usr/bin/env python3\n' > "$d/main.py"
  write_cat "$cat" "0000000000000000000000000000000000000000000000000000000000000000" false
  run_check "$d" "$cat"; expect_rc 2 || return 1
  return 0
}

t_missing_check_catalog_exit_2() {
  local d="$RUN_DIR/t_nocheckcat" cat="$RUN_DIR/t_nocheckcat.json" sha tool="$SB/worker-node/music/tools/check_catalog.py"
  new_mpt "$d"
  sha="$(install_track "$d" temple-01.mp3 'track-bytes-05')"
  allow_sha "$sha"
  write_cat "$cat" "$sha" true
  mv "$tool" "$tool.off"
  run_check "$d" "$cat"; local saved_rc="$RC"
  mv "$tool.off" "$tool"
  RC="$saved_rc"; expect_rc 2 || return 1
  return 0
}

t_unreadable_catalog_exit_2() {
  local d="$RUN_DIR/t_broken" cat="$RUN_DIR/t_broken.json"
  new_mpt "$d"
  printf '{ "revision": 1, "tracks": [' > "$cat"   # truncated JSON
  # group 2 reports catalog_unreadable; group 3 (the real check_catalog.py) treats an
  # unreadable catalog as usage/env and exits 2 (05.3.2-T1), so the check exits 2 (2 > 1).
  run_check "$d" "$cat"; expect_rc 2 || return 1
  expect_line "catalog_unreadable $cat" || return 1
  run_check "$d" "$cat" --json; expect_rc 2 || return 1
  $PY_RUN 'import json,sys; d=json.loads(sys.argv[1]); assert d["ok"] is False and d["catalog_revision"] is None and d["music_missing"]==[] and d["unlicensed"]==[] and d["catalog_errors"]==0, d' "$OUT" \
    || { printf '    FAIL: json wrong: %s\n' "$OUT"; return 1; }
  return 0
}

t_catalog_rule_error_propagated() {
  local d="$RUN_DIR/t_rule" cat="$RUN_DIR/t_rule.json" sha rdir
  new_mpt "$d"
  sha="$(install_track "$d" temple-01.mp3 'track-bytes-06')"
  curated_row "$sha"
  write_cat_ccby "$cat" "$sha"      # CC-BY-4.0 without attribution -> real checker exits 1
  run_check "$d" "$cat"; expect_rc 1 || return 1
  if ! grep -q 'attribution_required' "$ERRF"; then
    printf '    FAIL: check_catalog.py error code not on stderr\n    stderr: %s\n' "$(cat "$ERRF")"
    return 1
  fi
  # 05.3.2-T3's fixture, once it exists: the same contract through the real tool on the real
  # repo (skipped while the fixture is absent). A media-free MPT root keeps group 1 clean, so
  # the code asserted on stderr is the group-3 one.
  fixture="$REPO_ROOT/seed/fixtures/music/invalid-attribution.json"
  if [ -f "$fixture" ]; then
    rdir="$RUN_DIR/t_rule_real"
    new_mpt "$rdir"
    RC=0
    OUT="$(bash "$BIN_DIR/check-music.sh" --mpt-root "$rdir" --catalog "$fixture" 2>"$ERRF")" || RC=$?
    expect_rc 1 || return 1
    grep -q 'attribution_required' "$ERRF" || {
      echo "    FAIL: real fixture code not on stderr: $(cat "$ERRF")"
      return 1
    }
  fi
  return 0
}

t_json_mode_is_single_line() {
  local d="$RUN_DIR/t_json" cat="$RUN_DIR/t_json.json" sha nlines
  new_mpt "$d"
  sha="$(install_track "$d" temple-01.mp3 'track-bytes-07')"
  curated_row "$sha"
  write_cat "$cat" "$sha" true
  run_check "$d" "$cat" --json; expect_rc 0 || return 1
  nlines="$(printf '%s\n' "$OUT" | grep -c '')"
  [ "$nlines" -eq 1 ] || fail "stdout had $nlines lines, want 1: $OUT"
  if printf '%s\n' "$OUT" | grep -q 'music:'; then
    fail 'human text leaked to stdout in --json mode'
  fi
  return 0
}

t_never_deletes() {
  local d="$RUN_DIR/t_idem" cat="$RUN_DIR/t_idem.json" sha before1 before2 out1 out2
  new_mpt "$d"
  sha="$(install_track "$d" temple-01.mp3 'track-bytes-08')"
  curated_row "$sha"
  write_cat "$cat" "$sha" true
  printf 'x' > "$d/storage/bgm/keep-me.txt"          # non-media file must also survive
  before1="$(find "$d" -type f -exec stat -f '%N %m' {} + | sort)"
  run_check "$d" "$cat"; expect_rc 0 || return 1
  out1="$OUT"
  run_check "$d" "$cat"; expect_rc 0 || return 1
  out2="$OUT"
  before2="$(find "$d" -type f -exec stat -f '%N %m' {} + | sort)"
  [ "$before1" = "$before2" ] || { printf '    FAIL: file set/mtimes changed\nbefore: %s\nafter: %s\n' "$before1" "$before2"; return 1; }
  [ "$out1" = "$out2" ] || fail 'output differs between runs'
  return 0
}

t_exit_code_precedence() {
  local d="$RUN_DIR/t_prec"
  new_mpt "$d"
  printf 'x' > "$d/storage/bgm/planted.mp3"          # group-1 offender …
  run_check "$d" "$RUN_DIR/absent.json"            # … plus missing catalog -> 2, not 1
  expect_rc 2 || return 1
  return 0
}

t_json_env_failure_shape() {
  local d="$RUN_DIR/t_json_env" cat="$RUN_DIR/t_json_env.json" sha
  new_mpt "$d"
  sha="$(install_track "$d" temple-01.mp3 'track-bytes-09')"
  allow_sha "$sha"
  # A wrong `schema` passes group 2 (revision/tracks are readable) and reaches group 3,
  # which exits 2 — the path that used to leak the counters collected so far.
  write_cat "$cat" "$sha" true "music-tracks.v0"
  run_check "$d" "$cat" --json; expect_rc 2 || return 1
  [ "$(printf '%s\n' "$OUT" | grep -c '')" -eq 1 ] || { printf '    FAIL: want exactly 1 stdout line: %s\n' "$OUT"; return 1; }
  $PY_RUN 'import json,sys
d = json.loads(sys.argv[1])
assert set(d) == {"ok", "catalog_revision", "music_missing", "unlicensed", "catalog_errors"}, d
assert d["ok"] is False and d["catalog_revision"] is None and d["music_missing"] == [] and d["unlicensed"] == [] and d["catalog_errors"] == 0, d' "$OUT" \
    || { printf '    FAIL: exit-2 json shape wrong: %s\n' "$OUT"; return 1; }
  # the absent-catalog preflight must leave the same documented shape
  run_check "$d" "$RUN_DIR/absent.json" --json; expect_rc 2 || return 1
  $PY_RUN 'import json,sys
d = json.loads(sys.argv[1])
assert set(d) == {"ok", "catalog_revision", "music_missing", "unlicensed", "catalog_errors"}, d
assert d["ok"] is False and d["catalog_revision"] is None and d["music_missing"] == [] and d["unlicensed"] == [] and d["catalog_errors"] == 0, d' "$OUT" \
    || { printf '    FAIL: preflight exit-2 json shape wrong: %s\n' "$OUT"; return 1; }
  return 0
}

# ── Runner ─────────────────────────────────────────────────────────────────────────────
PASSED=0
FAILED=0
FAILED_NAMES=""
run_case() {
  local name="$1"
  if "$name"; then
    PASSED=$((PASSED + 1)); printf 'PASS %s\n' "$name"
  else
    FAILED=$((FAILED + 1)); FAILED_NAMES="$FAILED_NAMES $name"
    printf 'FAIL %s\n' "$name"
  fi
}

run_case t_healthy_tree_exit_0
run_case t_missing_installed_file_reports_music_missing
run_case t_tampered_installed_file_reports_music_missing
run_case t_inactive_track_is_not_required
run_case t_planted_storage_bgm_file_exit_1
run_case t_tampered_resource_song_removed_by_strip_check
run_case t_missing_catalog_file_exit_2
run_case t_missing_resource_dir_exit_2
run_case t_missing_check_catalog_exit_2
run_case t_unreadable_catalog_exit_2
run_case t_catalog_rule_error_propagated
run_case t_json_mode_is_single_line
run_case t_json_env_failure_shape
run_case t_never_deletes
run_case t_exit_code_precedence

printf '%s\n' "----------------------------------------"
printf 'check-music.test.sh: %d passed, %d failed\n' "$PASSED" "$FAILED"
[ "$FAILED" -eq 0 ]
