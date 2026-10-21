#!/usr/bin/env bash
# worker-node/bin/smoke-mpt.sh — prove the running MPT API has auth on, binds loopback only, the
# rendered config passes the guards, and the renderer's negative cases fail (01.2.2-T5).
# Exits 0 only when every check passes. Read-only against the service (the renderer self-test
# uses a temp dir); sends no requests that create tasks — costs nothing.
set -euo pipefail
# shellcheck source=worker-node/bin/lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

if [ "${1:-}" = --help ] || [ "${1:-}" = -h ]; then
  cat <<'EOF'
Usage: worker-node/bin/smoke-mpt.sh
Checks: listening, auth 401/401/200/403, body text, static /tasks auth, auth-enabled log,
LAN refused, binds, config --check, no_value_leak (SEC-09), webui_absent (D14),
config mode, renderer self-test, music check.
Exit codes: 0 all pass · 1 a check failed · 2 service not started or keychain error.
EOF
  exit 0
fi
[ $# -eq 0 ] || { echo "smoke-mpt.sh: no arguments (see --help)" >&2; exit 2; }

H="$(nts_home)"
REPO="$NTS_REPO_ROOT"
B="$REPO/worker-node/bin"
BASE="http://127.0.0.1:8080/api/v1"
RENDERER="$B/render-mpt-config.py"
KEY=""
fails=0
trap 'unset KEY k1 k2 k3 k4 2>/dev/null || true' EXIT

pass() { echo "PASS $1"; }
fail() { echo "FAIL $1: $2"; fails=$((fails + 1)); }
warn() { echo "WARN $1: $2"; }
skip() { echo "SKIP $1: $2"; }

hdr() { printf 'x-api-key: %s\n' "$1"; }
code() { curl -sS -o /dev/null -w '%{http_code}' --max-time 10 "$@"; }

# 1. mpt.listening — exit 2 when the service is not started.
if lsof -nP -iTCP:8080 -sTCP:LISTEN -Fn 2>/dev/null | grep -qx 'n127.0.0.1:8080'; then
  pass mpt.listening
else
  fail mpt.listening "no listener on 127.0.0.1:8080 (service not started)"
  exit 2
fi

# Keychain key (needed by checks 4 and 6).
KEY="$(secret nts.mpt.api_key)" || { echo "FAIL keychain: nts.mpt.api_key"; exit 2; }

# 2. no key -> 401
got="$(code "$BASE/tasks")"
if [ "$got" = 401 ]; then pass mpt.auth.no_key; else fail mpt.auth.no_key "expected 401, got $got"; fi

# 3. wrong key -> 401
wrong="$(openssl rand -hex 32)"
got="$(code -H @<(hdr "$wrong") "$BASE/tasks")"
if [ "$got" = 401 ]; then pass mpt.auth.wrong_key; else fail mpt.auth.wrong_key "expected 401, got $got"; fi
unset wrong

# 4. right key -> 200
got="$(code -H @<(hdr "$KEY") "$BASE/tasks")"
if [ "$got" = 200 ]; then
  pass mpt.auth.right_key
elif [ "$got" = 404 ]; then
  warn mpt.auth.right_key "route not found at the pin (find it in /openapi.json; tell 05.4)"
else
  fail mpt.auth.right_key "expected 200, got $got"
fi

# 5. 401 body message
body="$(curl -sS --max-time 10 "$BASE/tasks" || true)"
msg="$(printf '%s' "$body" | uv run --no-project --python 3.12 python -c \
  'import json,sys;print(json.load(sys.stdin).get("message",""))' 2>/dev/null || true)"
if [ "$msg" = "invalid API key" ]; then
  pass mpt.auth.body
elif [ "$(code "$BASE/tasks")" = 401 ]; then
  warn mpt.auth.body "still 401 but message is '$msg' (M11 text differs at pin)"
else
  fail mpt.auth.body "no 401 body"
fi

# 6. foreign Origin -> 403
got="$(code -H @<(hdr "$KEY") -H 'Origin: https://evil.example.invalid' "$BASE/tasks")"
if [ "$got" = 403 ]; then
  pass mpt.origin.foreign
elif [ "$got" = 200 ]; then
  warn mpt.origin.foreign "no origin guard at the pin (recorded; tell 05.4)"
else
  fail mpt.origin.foreign "expected 403, got $got"
fi

# 7. static /tasks without key -> 401 (⚠️ 404 possible)
got="$(code "http://127.0.0.1:8080/tasks/")"
if [ "$got" = 401 ]; then
  pass mpt.tasks_static.no_key
elif [ "$got" = 404 ]; then
  warn mpt.tasks_static.no_key "static mount returns 404, not 401 (⚠️ at pin)"
else
  fail mpt.tasks_static.no_key "expected 401, got $got"
fi

# 8. newest mpt-api log has no auth-disabled warning
newest=""
for f in "$H"/logs/mpt-api-*.log; do
  [ -e "$f" ] || continue
  if [ -z "$newest" ] || [ "$f" -nt "$newest" ]; then newest="$f"; fi
done
if [ -n "$newest" ]; then
  if grep -q 'API key authentication is disabled' "$newest"; then
    fail mpt.log.auth_enabled "auth-disabled warning in $newest"
  else
    pass mpt.log.auth_enabled
  fi
else
  warn mpt.log.auth_enabled "no mpt-api-*.log found"
fi

# 9. LAN refused
ip="$(ipconfig getifaddr en0 2>/dev/null || ipconfig getifaddr en1 2>/dev/null || true)"
if [ -z "$ip" ]; then
  skip mpt.lan_refused "no en0/en1 address"
else
  crc=0
  curl -sS --max-time 3 "http://$ip:8080/" >/dev/null 2>&1 || crc=$?
  if [ "$crc" -eq 7 ]; then pass mpt.lan_refused; else fail mpt.lan_refused "curl to $ip:8080 exit $crc (want 7)"; fi
fi

# 10. binds
if "$B/check-binds.sh" >/dev/null 2>&1; then pass mpt.binds; else fail mpt.binds "check-binds.sh failed"; fi

# 11. config --check
if uv run --no-project --python 3.12 "$RENDERER" --check >/dev/null 2>&1; then
  pass mpt.config.check
else
  fail mpt.config.check "render-mpt-config.py --check failed"
fi

# 12. no_value_leak (SEC-09) — fail closed: resolve all four values first.
leak_fail=0
if k1="$(secret nts.mpt.api_key)" && k2="$(secret nts.openai.story)" \
   && k3="$(secret nts.openai.image)" && k4="$(secret nts.proxy.api_key)"; then
  for v in "$k1" "$k2" "$k3" "$k4"; do
    [ "${#v}" -ge 16 ] || { echo "FAIL mpt.no_value_leak: a keychain value is <16 chars"; exit 2; }
  done
  # The redacted state file must exist (the renderer writes it on every start); logs are optional.
  [ -f "$H/mpt-config.last.redacted.toml" ] \
    || { echo "FAIL mpt.no_value_leak: $H/mpt-config.last.redacted.toml missing (nothing checked)"; leak_fail=1; }
  for f in "$H/mpt-config.last.redacted.toml" "$H"/logs/mpt-*.log; do
    [ -f "$f" ] || continue
    n="$(grep -cFf <(printf '%s\n' "$k1" "$k2" "$k3" "$k4") "$f" || true)"
    [ "$n" = 0 ] || { echo "FAIL mpt.no_value_leak: $n secret value(s) in $f"; leak_fail=1; }
  done
  if [ "$leak_fail" -eq 0 ]; then pass mpt.no_value_leak; else fails=$((fails + 1)); fi
else
  echo "FAIL mpt.no_value_leak: could not resolve all four keychain values"
  exit 2
fi
unset k1 k2 k3 k4

# 13. webui_absent (D14)
web="$(lsof -nP -iTCP -sTCP:LISTEN 2>/dev/null | awk '$9 ~ /:85(0[1-9]|[1-9][0-9])$/' || true)"
if [ -z "$web" ] && [ ! -e "$B/start-mpt-webui.sh" ]; then
  pass mpt.webui_absent
else
  fail mpt.webui_absent "webui listener or script present"
fi

# 14. config mode
got="$(stat -f '%Lp %HT' "$H/src/mpt/config.toml" 2>/dev/null || echo missing)"
case "$(printf '%s' "$got" | tr '[:upper:]' '[:lower:]')" in
  "600 regular file") pass mpt.config.mode ;;
  *) fail mpt.config.mode "expected '600 Regular File', got '$got'" ;;
esac

# 15. renderer self-test (dummy values; temp dir)
if uv run --no-project --python 3.12 "$RENDERER" --self-test >/dev/null 2>&1; then
  pass mpt.renderer.selftest
else
  fail mpt.renderer.selftest "self-test failed"
fi

# 16. music check
if "$B/strip-bundled-music.sh" --check >/dev/null 2>&1; then
  pass mpt.music.check
else
  fail mpt.music.check "unlicensed audio present"
fi

[ "$fails" -eq 0 ] || exit 1
exit 0
