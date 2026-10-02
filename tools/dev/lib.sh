#!/bin/bash
# tools/dev/lib.sh - helpers shared by tools/dev/{up,down,db-init}.sh (11.1.1-T1).
#
# Sourced, never executed: `set -euo pipefail`, `log` and `die` belong to the sourcing script, and
# `shellcheck -x` follows this file through every script that sources it.
#
# Two guarantees live here so up.sh and down.sh cannot drift apart:
#   1. a pid is only ever signalled when the live process is the very instance up.sh started
#      (pid + process start time + full command line), and
#   2. a destructive database action only runs against the Homebrew postgresql@17 cluster whose
#      data directory the caller expects.

# nts_service_pattern NAME: the substring that identifies a process tools/dev/up.sh started. up.sh
# stores it together with the instance identity (nts_record_identity) and down.sh re-checks both
# before signalling, so a pid file that names a recycled pid is never treated as ours.
nts_service_pattern() {
  case "$1" in
    api) printf 'NightTales.Api' ;;
    worker) printf 'NightTales.Worker' ;;
    mock) printf 'tools/mock-mpt' ;;
    web) printf 'node' ;;
    seaweedfs) printf 'weed' ;;
    mailpit) printf 'mailpit' ;;
    *) printf '%s' "$1" ;;
  esac
}

# nts_process_identity PID: a stable identity string for a live process - its start time and full
# command line - or nothing (return 1) when the pid is not running. It is captured at start and
# compared again immediately before any signal, so a pid reused by another process is never killed.
# The two fields are read with separate ps calls: a combined `-o lstart=,command=` pads the start
# column to a width that varies with how ps is invoked, which would break the comparison.
nts_process_identity() {
  local pid="$1" start cmd
  case "$pid" in ''|*[!0-9]*) return 1 ;; esac
  start="$(LC_ALL=C ps -ww -p "$pid" -o lstart= 2>/dev/null || true)"
  cmd="$(LC_ALL=C ps -ww -p "$pid" -o command= 2>/dev/null || true)"
  # ps pads the start time with leading and trailing blanks; keep the rest byte for byte.
  start="${start#"${start%%[![:space:]]*}"}"
  start="${start%"${start##*[![:space:]]}"}"
  [ -n "$start" ] || return 1
  [ -n "$cmd" ] || return 1
  printf '%s %s' "$start" "$cmd"
}

# nts_pid_owned NAME PID_FILE IDENTITY_FILE: prints the pid and returns 0 only when PID_FILE names a
# live process whose command line matches that service AND whose instance identity equals the stored
# one. Anything else (missing files, dead or recycled pid, another checkout) is not ours.
nts_pid_owned() {
  local name="$1" pid_file="$2" identity_file="$3" pid pattern stored current
  pattern="$(nts_service_pattern "$name")"
  [ -f "$pid_file" ] || return 1
  pid="$(cat "$pid_file" 2>/dev/null || true)"
  case "$pid" in ''|*[!0-9]*) return 1 ;; esac
  kill -0 "$pid" 2>/dev/null || return 1
  current="$(nts_process_identity "$pid")" || return 1
  case "$current" in *"$pattern"*) ;; *) return 1 ;; esac
  [ -f "$identity_file" ] || return 1
  stored="$(cat "$identity_file" 2>/dev/null || true)"
  [ -n "$stored" ] || return 1
  [ "$current" = "$stored" ] || return 1
  printf '%s' "$pid"
}

# nts_record_identity PID_FILE IDENTITY_FILE NAME PID [TENTHS]: write the pid and its instance
# identity. The just-forked subshell has not exec'ed its final command line yet, so the write
# retries (default 2 s) until the command line matches NAME's pattern; a process that never gets
# there is not recorded at all, so nothing is ever claimed that this run did not start.
nts_record_identity() {
  local pid_file="$1" identity_file="$2" name="$3" pid="$4" tenths="${5:-20}" pattern identity
  local i=0 pattern_ok=0
  pattern="$(nts_service_pattern "$name")"
  while [ "$i" -lt "$tenths" ]; do
    pattern_ok=0
    if identity="$(nts_process_identity "$pid")"; then
      case "$identity" in *"$pattern"*) pattern_ok=1 ;; esac
    fi
    if [ "$pattern_ok" -eq 1 ]; then
      printf '%s\n' "$pid" > "$pid_file"
      printf '%s\n' "$identity" > "$identity_file"
      return 0
    fi
    kill -0 "$pid" 2>/dev/null || return 1
    sleep 0.1
    i=$((i + 1))
  done
  return 1
}

# nts_pg_assert_homebrew_cluster PG_BIN EXPECTED_DATADIR: verify, through the local Homebrew
# superuser connection, that the server listening on 127.0.0.1:5432 is PostgreSQL 17 and that its
# data directory is EXPECTED_DATADIR. Refuses (die 1) any other cluster, so a destructive caller
# never touches a database this machine does not own. db-init.sh and down.sh --volumes share it.
nts_pg_assert_homebrew_cluster() {
  local pg_bin="$1" expected_datadir="$2" identity server_version server_datadir
  identity="$("$pg_bin/psql" -X -q -v ON_ERROR_STOP=1 -h 127.0.0.1 -p 5432 -d postgres -At -F ' ' \
    -c "SELECT current_setting('server_version_num'), current_setting('data_directory')")" \
    || die 1 "cannot connect as the Homebrew superuser to database postgres"
  server_version="${identity%% *}"
  server_datadir="${identity#* }"
  case "$server_version" in
    17[0-9][0-9][0-9][0-9]) ;;
    *) die 1 "the server on 127.0.0.1:5432 is not PostgreSQL 17 (server_version_num $server_version); stop the other cluster (brew services stop postgresql@<major>)" ;;
  esac
  [ "$server_datadir" = "$expected_datadir" ] \
    || die 1 "the server on 127.0.0.1:5432 is not Homebrew postgresql@17 (data_directory $server_datadir, expected $expected_datadir)"
}
