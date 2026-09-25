#!/usr/bin/env bash
# worker-node/bin/secrets.sh — create, list and rotate the D3 Keychain items. Never prints a value.
set -euo pipefail
umask 077
# shellcheck source=worker-node/bin/lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

usage() {
  cat <<'EOF'
Usage: worker-node/bin/secrets.sh <command> [args]

Commands:
  set <service>            Store an owner key at the interactive prompt (needs a TTY).
  init-local               Create the three local random keys if missing (created|unchanged).
  check [--local] [--acl]  Print "<service> present|missing"; --acl also checks the /usr/bin/security ACL.
  rotate-local [<service>] Regenerate one or all local keys (all services must be stopped).
  --help                   This text.

Services (D3, account $USER):
  nts.openai.story    OpenAI key, project nts-trial-story   (owner: set)
  nts.openai.image    OpenAI key, project nts-trial-image   (owner: set)
  nts.mpt.api_key     MPT app.api_key                       (init-local)
  nts.vieneu.api_key  VIENEU_API_KEY                        (init-local)
  nts.proxy.api_key   PROXY_API_KEY = MPT [kokoro] api_key  (init-local)

Exit codes: 0 ok · 1 check failed · 2 usage/environment error (D9).
Writers refuse (exit 2) until the D13 trusted location holds (worker-node/README.md).
EOF
}

_present() { security find-generic-password -a "$USER" -s "$1" >/dev/null 2>&1; }

_local_services() {
  if [ -n "${NTS_TEST_SERVICE:-}" ]; then printf '%s\n' "$NTS_TEST_SERVICE"; else printf '%s\n' "${NTS_LOCAL_SERVICES[@]}"; fi
}

_require_trusted() {
  _nts_trusted_location || die 2 "untrusted execution location (D13 mode $(_nts_d13_mode)): see worker-node/README.md § Trusted execution location"
}

# _store <service> <value-var-name> — write via `security -i` (value never in argv), then verify.
_store() {
  local svc="$1" v="$2"
  if ! printf 'add-generic-password -U -a %s -s %s -l %s -T /usr/bin/security -w %s\n' \
      "$USER" "$svc" "$svc" "$v" | security -i >/dev/null 2>&1; then
    die 2 "security -i failed for $svc; the argv fallback needs a TL decision in the runbook (SEC-06)"
  fi
  [ "$(secret "$svc")" = "$v" ] || die 1 "keychain verify failed: $svc"
}

_acl_ok() {
  # dump-keychain -a lists attributes and access lists, never secret data (no -d).
  security dump-keychain -a 2>/dev/null | awk -v svc="\"svce\"<blob>=\"$1\"" '
    /^keychain: / { if (hit && app) found=1; hit=0; app=0 }
    index($0, svc) { hit=1 }
    /\/usr\/bin\/security/ { app=1 }
    END { if (hit && app) found=1; exit found ? 0 : 1 }'
}

cmd_set() {
  local svc="${1:-}"
  [ -n "$svc" ] || die 2 "usage: secrets.sh set <service>"
  _nts_service_known "$svc" || die 2 "unknown service: $svc"
  [ -t 0 ] || die 2 "set needs an interactive terminal (the key is typed at the prompt)"
  _require_trusted
  case "$svc" in
    nts.openai.story) echo "Paste the OpenAI key of project nts-trial-story at the prompt." >&2 ;;
    nts.openai.image) echo "Paste the OpenAI key of project nts-trial-image at the prompt." >&2 ;;
    *) echo "Enter the value for $svc at the prompt." >&2 ;;
  esac
  security add-generic-password -U -a "$USER" -s "$svc" -l "$svc" -T /usr/bin/security -w
  _present "$svc" || die 1 "$svc not stored"
  echo "$svc present"
}

cmd_init_local() {
  _require_trusted
  local svc v
  while IFS= read -r svc; do
    if _present "$svc"; then echo "$svc unchanged"; continue; fi
    v="$(openssl rand -hex 32)"
    _store "$svc" "$v"
    unset v
    echo "$svc created"
  done < <(_local_services)
}

cmd_check() {
  local local_only=0 acl=0 svc rc=0
  local -a list
  for a in "$@"; do
    case "$a" in
      --local) local_only=1 ;;
      --acl) acl=1 ;;
      *) die 2 "unknown option: $a" ;;
    esac
  done
  if [ "$local_only" -eq 1 ]; then list=("${NTS_LOCAL_SERVICES[@]}"); else list=("${NTS_SERVICES[@]}"); fi
  [ -n "${NTS_TEST_SERVICE:-}" ] && list=("$NTS_TEST_SERVICE")
  for svc in "${list[@]}"; do
    if _present "$svc"; then
      echo "$svc present"
      if [ "$acl" -eq 1 ]; then
        if _acl_ok "$svc"; then echo "$svc acl ok"; else echo "$svc acl missing"; rc=1; fi
      fi
    else
      echo "$svc missing"; rc=1
    fi
  done
  return "$rc"
}

cmd_rotate_local() {
  local target="${1:-}" svc v p
  for p in 8000 8001 8002 8080 8501; do
    if lsof -nP -iTCP:"$p" -sTCP:LISTEN -t >/dev/null 2>&1; then die 2 "port $p is listening: stop all services first"; fi
  done
  _require_trusted
  local -a list
  if [ -n "$target" ]; then
    case "$target" in nts.openai.*) die 2 "OpenAI keys are rotated in the dashboard (01.2.4-T3), then 'set'" ;; esac
    _nts_in_list "$target" "${NTS_LOCAL_SERVICES[@]}" || [ "$target" = "${NTS_TEST_SERVICE:-}" ] || die 2 "not a local service: $target"
    list=("$target")
  else
    list=()
    while IFS= read -r svc; do list+=("$svc"); done < <(_local_services)
  fi
  for svc in "${list[@]}"; do
    v="$(openssl rand -hex 32)"
    _store "$svc" "$v"
    unset v
    echo "$svc rotated"
  done
  echo "Next: re-render the MPT config (start-mpt-api.sh renders on start), then restart VieNeu, the proxy and MPT." >&2
}

main() {
  local c="${1:-}"
  [ $# -gt 0 ] && shift
  case "$c" in
    set) cmd_set "$@" ;;
    init-local) cmd_init_local ;;
    check) cmd_check "$@" ;;
    rotate-local) cmd_rotate_local "$@" ;;
    --help|-h|help) usage ;;
    *) usage >&2; exit 2 ;;
  esac
}

main "$@"
