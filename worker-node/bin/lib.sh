# shellcheck shell=bash
# worker-node/bin/lib.sh — shared helpers for every worker-node script (01 README D9).
# Sourced only. Callers set `set -euo pipefail` themselves. Never `set -x` here or in callers.
[[ "${BASH_SOURCE[0]}" != "$0" ]] || { echo "lib.sh: source me, do not run me" >&2; exit 2; }

NTS_REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
NTS_SERVICES=(nts.openai.story nts.openai.image nts.mpt.api_key nts.vieneu.api_key nts.proxy.api_key)
# shellcheck disable=SC2034  # used by callers (secrets.sh)
NTS_LOCAL_SERVICES=(nts.mpt.api_key nts.vieneu.api_key nts.proxy.api_key)
NTS_PIN_KEYS=(MPT_REPO MPT_COMMIT MPT_PYTHON VIENEU_REPO VIENEU_COMMIT VIENEU_PYTHON
  VIENEU_MODEL_REPO VIENEU_MODEL_REVISION VIENEU_CODEC_REPO VIENEU_CODEC_REVISION
  WHISPER_MODEL_REPO WHISPER_MODEL_REVISION PROXY_PYTHON OPENAI_BASE_URL)
export NTS_REPO_ROOT

# log <msg…> — timestamped line on stderr. Never pass secret values.
log() {
  printf '%s %s: %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$(basename "${0:-lib.sh}")" "$*" >&2
}

# die <code> <msg…> — log and exit with a D9 code (1 check failed, 2 usage/environment).
die() {
  local code="$1"; shift
  log "$*"
  exit "$code"
}

# _nts_abspath <path> — absolute path without requiring it to exist.
_nts_abspath() {
  case "$1" in
    /*) printf '%s\n' "$1" ;;
    *) printf '%s/%s\n' "$PWD" "$1" ;;
  esac
}

# nts_home — pure resolver: exports NTS_HOME and prints it; no side effects.
# CR-11 (DEC-5): NTS_HOME = <repo>/.nts on this Mac. Inside the repo only that exact
# path is accepted, and only while git ignores it. In D13 mode `volume` only: WARN on stderr when
# its volume fails the check (secret reads then refuse). Mode `accepted-risk` (DEC-6) never warns.
nts_home() {
  local h
  h="$(_nts_abspath "${NTS_HOME:-$NTS_REPO_ROOT/.nts}")"   # CR-11: default <repo>/.nts (was $HOME/.nts)
  h="${h%/}"
  case "$h/" in
    "$NTS_REPO_ROOT/.nts/")
      git -C "$NTS_REPO_ROOT" check-ignore -q .nts/ 2>/dev/null || die 2 "NTS_HOME $h is inside the repo but not ignored by .gitignore" ;;
    "$NTS_REPO_ROOT"/*) die 2 "NTS_HOME inside the repo must be exactly $NTS_REPO_ROOT/.nts: $h" ;;
  esac
  case "$h" in
    /Volumes/*)
      if [ "$(_nts_d13_mode)" = volume ]; then
        _nts_volume_trusted "$h" || log "WARN NTS_HOME is on a volume that fails the D13 volume check (Owners/Encrypted); secrets stay refused"
      fi ;;
  esac
  NTS_HOME="$h"
  export NTS_HOME
  printf '%s\n' "$h"
}

# nts_home_init — mutator, no stdout: create $NTS_HOME tree, mode 700, Time Machine exclusion.
nts_home_init() {
  nts_home >/dev/null
  if [ ! -d "$NTS_HOME" ]; then
    mkdir -p "$NTS_HOME"/src "$NTS_HOME"/hf "$NTS_HOME"/logs "$NTS_HOME"/runs
    chmod 700 "$NTS_HOME"
    tmutil addexclusion "$NTS_HOME" 2>/dev/null || log "WARN tmutil addexclusion failed for NTS_HOME (U7); exclude it manually"
    log "created NTS_HOME"
    return 0
  fi
  mkdir -p "$NTS_HOME"/src "$NTS_HOME"/hf "$NTS_HOME"/logs "$NTS_HOME"/runs
  if [ "$(stat -f %Lp "$NTS_HOME")" != "700" ]; then
    chmod 700 "$NTS_HOME"
    log "reset NTS_HOME mode to 700"
  fi
}

# load_pins — parse worker-node/pins.env line by line (never sourced) and export the 14 D2 keys (OPENAI_BASE_URL: 01 DEC-9).
load_pins() {
  local f="$NTS_REPO_ROOT/worker-node/pins.env" line key val k n=0
  local -a seen=()
  [ -f "$f" ] || die 2 "pins.env: missing file $f"
  while IFS= read -r line || [ -n "$line" ]; do
    case "$line" in ''|'#'*) continue ;; esac
    [[ "$line" =~ ^[A-Z_]+=[^[:space:]]*$ ]] || die 2 "pins.env: malformed line: ${line%%=*}"
    key="${line%%=*}"; val="${line#*=}"
    _nts_in_list "$key" "${NTS_PIN_KEYS[@]}" || die 2 "pins.env: $key: unknown key"
    _nts_in_list "$key" "${seen[@]+"${seen[@]}"}" && die 2 "pins.env: $key: duplicate key"
    case "$key" in
      *_COMMIT|*_REVISION) [[ "$val" =~ ^[0-9a-f]{40}$ ]] || die 2 "pins.env: $key: not a 40-char lowercase hex SHA" ;;
      *_PYTHON) [[ "$val" =~ ^3\.[0-9]+$ ]] || die 2 "pins.env: $key: not a major.minor Python version" ;;
      *_BASE_URL) [[ "$val" =~ ^https://[a-z0-9.-]+/v1$ ]] || die 2 "pins.env: $key: not https://<host>/v1" ;;
      *) [ -n "$val" ] || die 2 "pins.env: $key: empty value" ;;
    esac
    seen+=("$key")
    export "$key=$val"
    n=$((n + 1))
  done <"$f"
  for k in "${NTS_PIN_KEYS[@]}"; do
    _nts_in_list "$k" "${seen[@]+"${seen[@]}"}" || die 2 "pins.env: $k: missing key"
  done
  [ "$n" -eq "${#NTS_PIN_KEYS[@]}" ] || die 2 "pins.env: expected ${#NTS_PIN_KEYS[@]} keys, found $n"
}

_nts_in_list() {
  local needle="$1" x; shift
  for x in "$@"; do [ "$x" = "$needle" ] && return 0; done
  return 1
}

_nts_service_known() { _nts_in_list "$1" "${NTS_SERVICES[@]}"; }

# _nts_d13_mode — the mode recorded in worker-node/README.md ("D13 mode: <mode>").
_nts_d13_mode() {
  local m
  m="$(sed -n 's/^D13 mode: *\([a-z-]*\).*$/\1/p' "$NTS_REPO_ROOT/worker-node/README.md" 2>/dev/null | head -n 1)"
  printf '%s\n' "${m:-unrecorded}"
}

# _nts_volume_trusted <path> — 0 when <path> is not under /Volumes, or its volume has
# ownership enabled and is encrypted (diskutil labels verified on macOS 27, 2026-09-25).
_nts_volume_trusted() {
  local p="$1" dev info
  case "$(_nts_abspath "$p")" in /Volumes/*) ;; *) return 0 ;; esac
  while [ ! -e "$p" ] && [ "$p" != "/" ]; do p="$(dirname "$p")"; done
  dev="$(df -P "$p" | awk 'NR==2{print $1}')"
  info="$(diskutil info "$dev" 2>/dev/null)" || return 1
  grep -Eq '^ *Owners: +Enabled' <<<"$info" || return 1
  grep -Eq '^ *(Encrypted|FileVault): +Yes' <<<"$info" || return 1
  return 0
}

# _nts_trusted_location — D13 gate, called only by `secret` and secret writers.
_nts_trusted_location() {
  local mode caller sha
  mode="$(_nts_d13_mode)"
  case "$mode" in
    internal)
      fdesetup status 2>/dev/null | grep -q 'FileVault is On' || return 1
      case "$NTS_REPO_ROOT" in /Volumes/*) return 1 ;; esac
      return 0 ;;
    volume)
      nts_home >/dev/null 2>&1 || return 1
      _nts_volume_trusted "$NTS_REPO_ROOT" && _nts_volume_trusted "$NTS_HOME" ;;
    accepted-risk)
      # DEC-6 (owner, 2026-09-25): no volume encryption/ownership; the only hard rule is that keys
      # never leave this Mac (gitleaks pre-commit/pre-push, .nts/ and config.toml ignored).
      nts_home >/dev/null 2>&1 || return 1
      return 0 ;;
    export)
      nts_home >/dev/null 2>&1 || return 1
      caller="$(_nts_abspath "${BASH_SOURCE[2]:-$0}")"
      sha="$(git -C "$NTS_REPO_ROOT" rev-parse HEAD 2>/dev/null)" || return 1
      case "$caller" in "$NTS_HOME/exec/$sha/"*) return 0 ;; esac
      return 1 ;;
    *) return 1 ;;
  esac
}

# secret <service> — print only the Keychain value on stdout. Use as VAR="$(secret svc)".
secret() {
  local svc="$1" v err rc=0
  _nts_service_known "$svc" || [ "$svc" = "${NTS_TEST_SERVICE:-}" ] || die 2 "unknown service: $svc"
  _nts_trusted_location || die 2 "untrusted execution location (D13 mode $(_nts_d13_mode)): see worker-node/README.md § Trusted execution location"
  err="$(mktemp)"
  v="$(security find-generic-password -a "$USER" -s "$svc" -w 2>"$err")" || rc=$?
  if [ "$rc" -ne 0 ]; then
    local first
    first="$(head -n 1 "$err")"; rm -f "$err"
    [ "$rc" -eq 44 ] && die 2 "missing keychain item: $svc (run worker-node/bin/secrets.sh check)"
    die 2 "keychain read failed for $svc: $first"
  fi
  rm -f "$err"
  [ -n "$v" ] || die 2 "keychain read failed for $svc: empty value"
  printf '%s\n' "$v"
}

# require_port_free <port> — exit 2 if something listens on TCP <port> (D4).
require_port_free() {
  local port="$1"
  if lsof -nP -iTCP:"$port" -sTCP:LISTEN -t >/dev/null 2>&1; then
    die 2 "port $port already in use"
  fi
  return 0
}
