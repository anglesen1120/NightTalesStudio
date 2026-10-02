#!/bin/bash
# tools/dev/down.sh - stop the native dev stack tools/dev/up.sh started (11.1.1-T1, D14).
# Only processes whose pid file names a live process of that service are touched; a pid that is not
# ours is never killed, and no dev data is discarded unless --volumes is given.
# Exit codes: 0 ok, 1 check failed, 2 usage / environment.
set -euo pipefail
umask 077

usage() {
  cat <<'EOF'
Usage: tools/dev/down.sh [--services] [--volumes] [--help]

Stops the processes tools/dev/up.sh started (their pid files are read from .nts/dev/pid):
the Api, the Worker, mock-mpt, the web app, and the seaweedfs and mailpit emulators.

Flags:
  --services   also stop the Homebrew postgresql@17 service (it is left running otherwise)
  --volumes    discard dev data: delete .nts/dev/seaweedfs and .nts/dev/mailpit, then drop and
               recreate the .env database through tools/dev/db-init.sh. The shared Homebrew data
               directory (and the NTS_TEST_PG databases in it) is never touched.
  -h, --help   this text

The optional Compose stack is stopped with `docker compose down` (or `down -v`); this script never
calls Docker. Nothing here discards data without --volumes.
EOF
}

stop_services=0
drop_volumes=0
while [ "$#" -gt 0 ]; do
  case "$1" in
    -h|--help) usage; exit 0 ;;
    --services) stop_services=1 ;;
    --volumes) drop_volumes=1 ;;
    *) echo "down.sh: unknown argument: $1 (see --help)" >&2; exit 2 ;;
  esac
  shift
done

log() { printf '%s down.sh: %s\n' "$(date -u +%FT%TZ)" "$*" >&2; }
die() { local code="$1"; shift; log "ERROR: $*"; exit "$code"; }

repo_root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$repo_root"

# Shared helpers: the service patterns, the instance-identity check (lib.sh's nts_pid_owned) and the
# Homebrew cluster check. The same file up.sh and db-init.sh source, so the three cannot drift.
# shellcheck source=tools/dev/lib.sh
. "$repo_root/tools/dev/lib.sh"

env_file="$repo_root/.env"
example_file="$repo_root/.env.example"
nts_dev="${NTS_HOME:-$repo_root/.nts}/dev"
pid_dir="$nts_dev/pid"

# Reads KEY=VALUE from a dotenv file without sourcing it (no code execution), the same rules
# tools/dev/db-init.sh uses.
env_value() {
  local file="$1" key="$2" line value
  [ -f "$file" ] || return 1
  line="$(grep -E "^[[:space:]]*${key}=" "$file" | tail -n 1)" || return 1
  value="${line#*=}"
  value="${value%$'\r'}"
  local dq='^"([^"]*)"[[:space:]]*(#.*)?$' sq="^'([^']*)'[[:space:]]*(#.*)?\$"
  case "$value" in
    \"*|\'*)
      if [[ "$value" =~ $dq ]] || [[ "$value" =~ $sq ]]; then
        value="${BASH_REMATCH[1]}"
      else
        return 2
      fi
      ;;
    *)
      value="${value%%[[:space:]]#*}"
      value="${value%"${value##*[![:space:]]}"}"
      ;;
  esac
  printf '%s' "$value"
}

# nts_tree_snapshot PID: "pid|identity" for PID and every descendant, deepest first (the order they
# are signalled in), capturing each instance identity now so the signal can re-check it. A process
# that is already gone is skipped.
nts_tree_snapshot() {
  local pid="$1" child identity
  for child in $(pgrep -P "$pid" 2>/dev/null || true); do
    nts_tree_snapshot "$child"
  done
  identity="$(nts_process_identity "$pid" || true)"
  [ -n "$identity" ] || return 0
  printf '%s|%s\n' "$pid" "$identity"
}

# stop_captured SIGNAL SNAPSHOT: signal every pid in a snapshot, but only while it still carries the
# identity captured for it. A pid that is already gone is skipped, and a pid reused between the
# snapshot and the signal is left alone, so the escalation path can never kill a process this
# checkout did not start.
stop_captured() {
  local signal="$1" snapshot="$2" entry pid stored current
  printf '%s\n' "$snapshot" | while IFS= read -r entry; do
    [ -n "$entry" ] || continue
    pid="${entry%%|*}"
    stored="${entry#*|}"
    current="$(nts_process_identity "$pid" || true)"
    if [ -z "$current" ]; then
      continue
    fi
    if [ "$current" = "$stored" ]; then
      kill -s "$signal" "$pid" 2>/dev/null || true
    else
      log "pid $pid changed identity before SIG$signal, left alone"
    fi
  done
}

# stop_service NAME: 0 when a process of ours was stopped, 1 when there was nothing to stop. A pid
# file only counts as ours when the live process still matches both the service pattern and the
# instance identity up.sh stored (lib.sh's nts_pid_owned); the identity is checked right before the
# signal, again during the wait, and again before the SIGKILL escalation, so a pid recycled at any
# point - including during the 15 s wait - is never killed.
stop_service() {
  local name="$1" pid_file="$pid_dir/$1.pid" identity_file="$pid_dir/$1.identity" pid cmd expected snapshot
  if ! pid="$(nts_pid_owned "$name" "$pid_file" "$identity_file")"; then
    # Nothing to stop. When the pid file names a live process that is not the one up.sh started
    # (a recycled pid, or another checkout), say so before dropping the stale record.
    if [ -f "$pid_file" ]; then
      pid="$(cat "$pid_file" 2>/dev/null || true)"
      case "$pid" in
        ''|*[!0-9]*) ;;
        *)
          if kill -0 "$pid" 2>/dev/null; then
            cmd="$(ps -ww -p "$pid" -o command= 2>/dev/null || true)"
            log "$name: pid $pid is not the $name process this checkout started (${cmd:-no command line}), left alone"
          fi
          ;;
      esac
      rm -f "$pid_file" "$identity_file"
    fi
    return 1
  fi
  expected="$(cat "$identity_file")"
  # Re-check immediately before signalling: the identity must still match.
  if [ "$(nts_process_identity "$pid")" != "$expected" ]; then
    log "$name: pid $pid changed before the signal, left alone"
    rm -f "$pid_file" "$identity_file"
    return 1
  fi
  log "stopping $name (pid $pid)"
  snapshot="$(nts_tree_snapshot "$pid")"
  stop_captured TERM "$snapshot"
  # Wait up to 15 s for the process to exit, but stop the moment the pid no longer carries our
  # identity: it then belongs to another process and must never be escalated against.
  local waited=0
  while kill -0 "$pid" 2>/dev/null && [ "$waited" -lt 15 ]; do
    if [ "$(nts_process_identity "$pid")" != "$expected" ]; then
      log "$name: pid $pid no longer carries our identity, not escalating"
      break
    fi
    sleep 1
    waited=$((waited + 1))
  done
  # SeaweedFS is the one that needs the fallback: measured on seaweedfs 4.48 (2026-10-02) with
  # up.sh's command line, `weed server` takes 10-25 s to exit on SIGTERM, and the same on SIGINT or a
  # repeated SIGINT — the delay is its own graceful shutdown, not the signal — so the 15 s cap here
  # often ends in this SIGKILL. The dev store is disposable (docs/dev-setup.md), so interrupting it is
  # safe. The identity is checked once more first, and every captured pid is re-checked as it is
  # signalled, so a pid recycled during the wait is never killed.
  if kill -0 "$pid" 2>/dev/null && [ "$(nts_process_identity "$pid")" = "$expected" ]; then
    log "$name (pid $pid) did not exit in 15 s: SIGKILL"
    snapshot="$(nts_tree_snapshot "$pid")"
    stop_captured KILL "$snapshot"
  fi
  rm -f "$pid_file" "$identity_file"
  return 0
}

# ---------------------------------------------------------------- stop the processes

stopped=0
for name in web mock worker api seaweedfs mailpit; do
  if stop_service "$name"; then stopped=$((stopped + 1)); fi
done
log "stopped $stopped service(s)"

# ---------------------------------------------------------------- dev data (only with --volumes)

if [ "$drop_volumes" -eq 1 ]; then
  db_name="$(env_value "$env_file" POSTGRES_DB)" || db_name="$(env_value "$example_file" POSTGRES_DB)" || db_name=""
  [ -n "$db_name" ] || die 2 "POSTGRES_DB is not set in .env or .env.example"
  ident_re='^[a-z_][a-z0-9_]{0,62}$'
  [[ "$db_name" =~ $ident_re ]] || die 2 "POSTGRES_DB must match $ident_re"
  case "$db_name" in
    postgres|template0|template1) die 2 "POSTGRES_DB must not be a system database ('$db_name')" ;;
  esac
  db_pass="$(env_value "$env_file" POSTGRES_PASSWORD)" || true
  [ -n "$db_pass" ] || die 2 "POSTGRES_PASSWORD is not set in .env (cp .env.example .env, then set your own)"

  pg_prefix="$(brew --prefix postgresql@17 2>/dev/null)" \
    || die 1 "postgresql@17 not installed: brew install postgresql@17"
  pg_bin="$pg_prefix/bin"
  [ -x "$pg_bin/psql" ] || die 1 "postgresql@17 not installed: brew install postgresql@17"
  brew_root="$(brew --prefix)" || die 1 "brew --prefix failed"
  expected_datadir="$brew_root/var/postgresql@17"
  "$pg_bin/pg_isready" -q -h 127.0.0.1 -p 5432 \
    || die 1 "PostgreSQL is not accepting connections on 127.0.0.1:5432; run: brew services start postgresql@17 (or tools/dev/up.sh)"

  # Nothing is deleted and no DROP is sent until the listener is the Homebrew postgresql@17 cluster
  # whose data directory this machine owns (the same check db-init.sh runs). A different cluster that
  # happens to hold port 5432 is refused before any data is touched.
  nts_pg_assert_homebrew_cluster "$pg_bin" "$expected_datadir"

  log "deleting $nts_dev/seaweedfs and $nts_dev/mailpit"
  rm -rf "$nts_dev/seaweedfs" "$nts_dev/mailpit"

  # The password reaches psql through its environment, never argv (01 D9). Only the .env database
  # is dropped; the Homebrew data directory holds the NTS_TEST_PG databases too.
  log "dropping and recreating the database '$db_name'"
  PGPASSWORD="${db_pass}" "$pg_bin/psql" -X -q -v ON_ERROR_STOP=1 -h 127.0.0.1 -p 5432 -d postgres \
    -c "DROP DATABASE IF EXISTS \"$db_name\" WITH (FORCE)" \
    || die 1 "dropping database '$db_name' failed"
  "$repo_root/tools/dev/db-init.sh" || { rc=$?; die "$rc" "tools/dev/db-init.sh failed (see above)"; }
fi

# ---------------------------------------------------------------- the brew service (only with --services)

if [ "$stop_services" -eq 1 ]; then
  command -v brew >/dev/null 2>&1 || die 1 "brew not found: install Homebrew (docs/dev-setup.md)"
  pg_state="$(brew services list | awk '$1 == "postgresql@17" { print $2 }')"
  case "$pg_state" in
    started)
      log "stopping the postgresql@17 service"
      brew services stop postgresql@17 >/dev/null || die 1 "brew services stop postgresql@17 failed"
      ;;
    *) log "postgresql@17 service is not started (was: ${pg_state:-absent}), left alone" ;;
  esac
fi
