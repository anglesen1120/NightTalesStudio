#!/usr/bin/env bash
# Executable acceptance checklist for the web container (10.1.1-T2): every point that
# does not need a browser. Prints expected vs observed per assertion and exits non-zero
# on the first mismatch.
#
#   npm run container:smoke                      # image nighttales-web:dev
#   NTS_SMOKE_IMAGE=<ref> npm run container:smoke
#   NTS_SMOKE_STABLE_SECONDS=5 npm run container:smoke   # shorten the stability wait
#
# Containers run with --read-only; the only writable paths are tmpfs mounts for /tmp
# (pid, proxy/cache temp) and /etc/nginx/conf.d (the entrypoint's envsubst output).
set -euo pipefail

IMAGE="${NTS_SMOKE_IMAGE:-nighttales-web:dev}"
STABLE_SECONDS="${NTS_SMOKE_STABLE_SECONDS:-60}"
PREFIX="nts-web-smoke-$$"
CONTAINERS=()

cleanup() {
    if [ "${#CONTAINERS[@]}" -gt 0 ]; then
        docker rm -f "${CONTAINERS[@]}" >/dev/null 2>&1 || true
    fi
}
trap cleanup EXIT

pass() { printf 'ok   %s\n' "$1"; }
fail() {
    printf 'FAIL %s\n     expected: %s\n     observed: %s\n' "$1" "$2" "$3" >&2
    exit 1
}

# start NAME [docker run args...] -> container up, $PORT set to the published host port.
start() {
    local name="$PREFIX-$1"; shift
    CONTAINERS+=("$name")
    docker run -d --name "$name" --read-only \
        --tmpfs /tmp \
        --tmpfs /etc/nginx/conf.d:uid=101,gid=101,mode=0755 \
        -p 127.0.0.1::8080 "$@" "$IMAGE" >/dev/null
    PORT=$(docker port "$name" 8080/tcp | head -n1 | sed 's/.*://')
    local i
    for i in $(seq 1 40); do
        if curl -fsS "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1; then
            CURRENT="$name"
            return 0
        fi
        sleep 0.5
    done
    docker logs "$name" 2>&1 | tail -n 20 >&2
    fail "container $name becomes ready" "GET /healthz succeeds within 20 s" "no response"
}

# hdrs PATH -> lower-cased response headers (GET) into $HDRS; status line first.
hdrs() { HDRS=$(curl -sS -D - -o /dev/null "http://127.0.0.1:$PORT$1" | tr -d '\r' | tr 'A-Z' 'a-z'); }

status_of() { curl -sS -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT$1"; }

# header_has LABEL "name: value-substring"
header_has() {
    printf '%s\n' "$HDRS" | grep -qiF -- "$2" || fail "$1" "header '$2'" "$(printf '%s' "$HDRS" | tr '\n' '|')"
    pass "$1"
}
header_absent() {
    if printf '%s\n' "$HDRS" | grep -qiE -- "^$2:"; then
        fail "$1" "no '$2' header" "$(printf '%s' "$HDRS" | tr '\n' '|')"
    fi
    pass "$1"
}

D4_HEADERS=(
    "referrer-policy: no-referrer"
    "x-content-type-options: nosniff"
    "permissions-policy: camera=(), microphone=(), geolocation=(), payment=()"
    "cross-origin-opener-policy: same-origin"
)
d4_matrix() { # PATH  LABEL  (CSP expected)
    hdrs "$1"
    local h
    for h in "${D4_HEADERS[@]}"; do header_has "$2: $h" "$h"; done
    header_has "$2: Content-Security-Policy present" "content-security-policy:"
    header_has "$2: Cache-Control no-cache" "cache-control: no-cache"
    header_absent "$2: no HSTS when NTS_HSTS empty" "strict-transport-security"
}

echo "== image: $IMAGE"

# --- image contents -----------------------------------------------------------------
FALLBACK=$(docker run --rm --entrypoint printenv "$IMAGE" NTS_SPA_FALLBACK)
[ -n "$FALLBACK" ] || fail "NTS_SPA_FALLBACK default set" "non-empty" "empty"
docker run --rm --entrypoint test "$IMAGE" -f "/usr/share/nginx/html/$FALLBACK" \
    || fail "fallback file exists in image" "/usr/share/nginx/html/$FALLBACK" "missing"
pass "fallback file exists in image ($FALLBACK)"
LEAK=$(docker run --rm --entrypoint sh "$IMAGE" -c \
    'find / -xdev \( -name node_modules -o -name "*.map" -o -name package.json \) -not -path "/proc/*" -not -path "/usr/lib/node_modules/*" 2>/dev/null | head -n 5')
[ -z "$LEAK" ] || fail "no node_modules / .map / package.json in image" "none" "$LEAK"
pass "no node_modules / .map / package.json in image"

# Dockerfile static contract (QA): multi-stage, both bases digest-pinned, no ADD / remote URL.
DOCKERFILE="$(cd "$(dirname "$0")/../.." && pwd)/Dockerfile"
[ -f "$DOCKERFILE" ] || fail "Dockerfile found" "$DOCKERFILE" "missing"
[ "$(grep -cE '^FROM ' "$DOCKERFILE")" -ge 2 ] || fail "multi-stage Dockerfile" ">= 2 FROM" "fewer"
UNPINNED=$(grep -E '^FROM ' "$DOCKERFILE" | grep -v '@sha256:[0-9a-f]\{64\}' || true)
[ -z "$UNPINNED" ] || fail "every FROM is digest-pinned" "@sha256:<64 hex>" "$UNPINNED"
! grep -qE '^ADD |https?://[^ ]*\.(tar|tgz|zip|sh)' "$DOCKERFILE" || fail "no ADD / remote archive in Dockerfile" "none" "found"
pass "Dockerfile: multi-stage, FROM digest-pinned, no ADD / remote URL"

# --- default run: dev-shaped, no upstream, no HSTS ----------------------------------
start default
UID_IN=$(docker exec "$CURRENT" id -u)
[ "$UID_IN" = "101" ] || fail "runs as uid 101" "101" "$UID_IN"
pass "runs as uid 101 (read-only rootfs)"
BODY=$(curl -fsS "http://127.0.0.1:$PORT/healthz")
[ "$BODY" = "ok" ] || fail "/healthz body" "ok" "$BODY"
[ "$(status_of /healthz)" = "200" ] || fail "/healthz status" "200" "$(status_of /healthz)"
pass "/healthz 200 ok"

d4_matrix / "/"
d4_matrix /dashboard/episodes/42 "deep route /dashboard/episodes/42"
[ "$(status_of /khong-ton-tai)" = "200" ] || fail "unknown path falls back" "200" "$(status_of /khong-ton-tai)"
curl -fsS "http://127.0.0.1:$PORT/khong-ton-tai" | grep -qi '<app-root' \
    || fail "unknown path serves the fallback HTML" "<app-root> document" "other body"
pass "/khong-ton-tai -> 200 fallback HTML"

# Root document (QA): "/" must be the SPA, never a document left over from the base image.
ROOT_BODY=$(curl -fsS "http://127.0.0.1:$PORT/")
printf '%s' "$ROOT_BODY" | grep -qi '<app-root' \
    || fail "/ serves the SPA document" "<app-root> document" "$(printf '%s' "$ROOT_BODY" | grep -oiE '<title>[^<]*</title>' | head -n1)"
pass "/ serves the SPA document"
IDX_BODY=$(curl -sS "http://127.0.0.1:$PORT/index.html")
case "$IDX_BODY" in
    *"Welcome to nginx"*) fail "no base-image index.html is served" "no nginx welcome page at /index.html" "Welcome to nginx!" ;;
esac
pass "/index.html is not the nginx welcome page"
hdrs /index.csr.html
header_has "/index.csr.html: Cache-Control no-cache (never immutable)" "cache-control: no-cache"
if printf '%s\n' "$HDRS" | grep -qiE '^cache-control:.*immutable'; then
    fail "/index.csr.html: not immutable" "no immutable Cache-Control" "$(printf '%s' "$HDRS" | tr '\n' '|')"
fi
pass "/index.csr.html: not immutable"

# /healthz header set (QA): plain text, never cached, and no SPA/CSP headers.
hdrs /healthz
header_has "/healthz: Content-Type text/plain" "content-type: text/plain"
header_has "/healthz: Cache-Control no-store" "cache-control: no-store"
header_absent "/healthz: no Content-Security-Policy" "content-security-policy"

# Dotfiles, traversal, methods, long URI, gzip (QA).
for p in /.git /.git/config /.env; do
    [ "$(status_of "$p")" = "404" ] || fail "dotfile $p -> 404" "404" "$(status_of "$p")"
    pass "dotfile $p -> 404"
done
for p in '/../etc/passwd' '/%2e%2e/%2e%2e/etc/passwd' '/..%2f..%2fetc%2fpasswd' '/assets/../../etc/passwd'; do
    RESP=$(curl -sS --path-as-is -w '\n%{http_code}' "http://127.0.0.1:$PORT$p")
    printf '%s' "$RESP" | grep -q 'root:' && fail "traversal $p leaks no file" "no /etc/passwd content" "root: entry in body"
    pass "traversal $p -> $(printf '%s' "$RESP" | tail -n1), no file content"
done
for m in TRACE POST PUT DELETE; do
    OUT=$(curl -sS -o /dev/null -w '%{http_code}' -X "$m" "http://127.0.0.1:$PORT/")
    [ "$OUT" = "405" ] || fail "$m / -> 405" "405" "$OUT"
    pass "$m / -> 405"
done
OUT=$(curl -sS -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/$(head -c 9000 /dev/zero | tr '\0' a)")
[ "$OUT" = "414" ] || fail "9000-char URI -> 414" "414" "$OUT"
pass "9000-char URI -> 414"

CHUNK=$(docker exec "$CURRENT" sh -c 'ls /usr/share/nginx/html' | grep -E -- '-[A-Za-z0-9]{8}\.js$' | head -n1)
[ -n "$CHUNK" ] || fail "a hashed chunk with an 8-char hash exists" "name-XXXXXXXX.js" "none"
hdrs "/$CHUNK"
header_has "hashed chunk /$CHUNK: immutable" "cache-control: public, max-age=31536000, immutable"
for h in "${D4_HEADERS[@]}"; do header_has "hashed chunk: $h" "$h"; done
header_absent "hashed chunk: no Content-Security-Policy" "content-security-policy"
GZ=$(curl -sS -H 'Accept-Encoding: gzip' -D - -o /dev/null "http://127.0.0.1:$PORT/$CHUNK" | tr -d '\r' | tr 'A-Z' 'a-z')
printf '%s\n' "$GZ" | grep -q '^content-encoding: gzip' || fail "hashed js is gzip-compressed" "content-encoding: gzip" "$(printf '%s' "$GZ" | tr '\n' '|')"
pass "hashed js is gzip-compressed"

ASSET=$(docker exec "$CURRENT" sh -c 'cd /usr/share/nginx/html && find assets -type f | head -n1')
[ -n "$ASSET" ] || fail "an /assets file exists" "any file" "none"
hdrs "/$ASSET"
header_has "/$ASSET: 1h must-revalidate" "cache-control: public, max-age=3600, must-revalidate"
for h in "${D4_HEADERS[@]}"; do header_has "/$ASSET: $h" "$h"; done

# API guard (NTS_API_UPSTREAM unset): hard 404, never HTML.
for p in /api/v1/anything /hubs/episodes /health/ready; do
    OUT=$(curl -sS -o /dev/null -w '%{http_code} %{content_type}' "http://127.0.0.1:$PORT$p")
    case "$OUT" in
        "404 "*) ;;
        *) fail "guard $p -> 404" "404 <non-html>" "$OUT" ;;
    esac
    case "$OUT" in
        *text/html*) fail "guard $p content type" "not text/html" "$OUT" ;;
    esac
    pass "guard $p -> $OUT"
done

# API-guard prefix confusion: encoded slash and doubled slashes still hit the guard.
for p in '/api%2fv1' '//api/x' '/api//x'; do
    OUT=$(curl -sS --path-as-is -o /dev/null -w '%{http_code} %{content_type}' "http://127.0.0.1:$PORT$p")
    [ "$OUT" = "404 text/plain" ] || fail "guard $p -> 404 text/plain" "404 text/plain" "$OUT"
    pass "guard $p -> $OUT"
done

echo "== stability: ${STABLE_SECONDS}s"
sleep "$STABLE_SECONDS"
RUNNING=$(docker inspect -f '{{.State.Running}}' "$CURRENT")
HEALTH=$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{end}}' "$CURRENT")
[ "$RUNNING" = "true" ] || fail "container still running" "true" "$RUNNING"
[ "$HEALTH" = "healthy" ] || fail "container health" "healthy" "$HEALTH"
pass "still running and healthy after ${STABLE_SECONDS}s"

# --- HSTS present only when NTS_HSTS is set ------------------------------------------
HSTS_VALUE='max-age=31536000; includeSubDomains'
start hsts -e "NTS_HSTS=$HSTS_VALUE"
hdrs /
header_has "NTS_HSTS set -> exact HSTS value on /" "strict-transport-security: $HSTS_VALUE"

# --- media origins: accepted value reaches the served CSP -----------------------------
start media -e 'NTS_CSP_MEDIA_ORIGINS=https://media.example.com'
if docker exec "$CURRENT" grep -q 'img-src\|media-src' /etc/nginx/conf.d/includes/csp-headers.conf; then
    docker exec "$CURRENT" grep -q 'https://media.example.com' /etc/nginx/conf.d/includes/csp-headers.conf \
        || fail "media origin substituted into CSP" "https://media.example.com in csp-headers.conf" "absent"
    pass "NTS_CSP_MEDIA_ORIGINS reaches the rendered CSP include"
fi

# --- env validation fails closed ------------------------------------------------------
for bad in \
    "NTS_CSP_MEDIA_ORIGINS=http://x; bad" \
    "NTS_CSP_MEDIA_ORIGINS=https://a.example.com'" \
    "NTS_CSP_MEDIA_ORIGINS=ftp://a.example.com" \
    "NTS_HSTS=max-age=abc" \
    "NTS_SPA_FALLBACK=../etc/passwd" \
    $'NTS_CSP_MEDIA_ORIGINS=https://a.example\nevil' \
    "NTS_CSP_MEDIA_ORIGINS=*" \
    "NTS_CSP_MEDIA_ORIGINS=http:" \
    "NTS_CSP_MEDIA_ORIGINS=https://a.example/" \
    "NTS_HSTS=max-age=1; preload" \
    "NTS_API_UPSTREAM=http://x/path"; do
    name="${bad%%=*}"
    set +e
    OUT=$(docker run --rm --read-only --tmpfs /tmp --tmpfs /etc/nginx/conf.d:uid=101,gid=101,mode=0755 \
        -e "$bad" "$IMAGE" 2>&1)
    RC=$?
    set -e
    [ "$RC" -ne 0 ] || fail "validation rejects '$bad'" "non-zero exit" "exit 0"
    printf '%s' "$OUT" | grep -q "$name" || fail "validation message names $name" "$name in output" "$OUT"
    printf '%s' "$OUT" | grep -q 'nginx.*start worker\|worker process' \
        && fail "nginx must not start for '$bad'" "no worker started" "$OUT"
    pass "rejected $name (exit $RC, nginx not started)"
done

# --- proxy mode: upstream set; hub ticket never logged --------------------------------
start proxy -e NTS_API_UPSTREAM=http://127.0.0.1:9
[ "$(status_of /api/v1/anything)" = "502" ] || fail "proxy mode: unreachable upstream -> 502" "502" "$(status_of /api/v1/anything)"
pass "proxy mode: /api/** reaches the proxy (unreachable upstream -> 502)"
curl -sS -o /dev/null "http://127.0.0.1:$PORT/hubs/episodes?ticket=abc" || true
sleep 1
# Access log = stdout. nginx's error log (stderr) is a separate stream and is not part of
# the WR13 access-log contract (it quotes the request line on upstream failures; see PR notes).
LOGLINE=$(docker logs "$CURRENT" 2>/dev/null | grep '/hubs/' || true)
[ -n "$LOGLINE" ] || fail "hub request is access-logged" "a /hubs/ line" "none"
case "$LOGLINE" in
    *ticket=*|*\?*) fail "hub log line has no query string" "path only" "$LOGLINE" ;;
esac
pass "hub access log line is path-only"

# --- proxy mode with a REACHABLE upstream (QA): a second container of this same image ----
# The upstream runs in guard mode, so a proxied /api/** answers its own 404 text/plain
# ("not found"); an unreachable upstream would answer nginx's 502 HTML instead.
UP="$PREFIX-upstream"
CONTAINERS+=("$UP")
docker run -d --name "$UP" --read-only --tmpfs /tmp \
    --tmpfs /etc/nginx/conf.d:uid=101,gid=101,mode=0755 "$IMAGE" >/dev/null
UPIP=""
for _ in $(seq 1 40); do
    UPIP=$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$UP" 2>/dev/null || true)
    if [ -n "$UPIP" ] && docker exec "$UP" wget -qO- http://127.0.0.1:8080/healthz >/dev/null 2>&1; then break; fi
    UPIP=""
    sleep 0.5
done
[ -n "$UPIP" ] || fail "reachable upstream container is ready" "an IP answering /healthz" "none"
start proxy-up -e "NTS_API_UPSTREAM=http://$UPIP:8080"
for p in /api/v1/anything /health/ready /hubs/episodes?ticket=abc; do
    OUT=$(curl -sS -o /dev/null -w '%{http_code} %{content_type}' "http://127.0.0.1:$PORT$p")
    [ "$OUT" = "404 text/plain" ] || fail "proxy mode: $p reaches the upstream" "404 text/plain (upstream answer, not 502)" "$OUT"
    pass "proxy mode: $p proxied to the upstream ($OUT)"
done
OUT=$(curl -sS -o /dev/null -w '%{http_code} %{content_type}' "http://127.0.0.1:$PORT/apix")
case "$OUT" in "200 text/html"*) pass "proxy mode: /apix is not proxied (SPA fallback)" ;; *) fail "proxy mode: /apix stays on the SPA" "200 text/html" "$OUT" ;; esac
sleep 1
LOGLINE=$(docker logs "$CURRENT" 2>/dev/null | grep '/hubs/' || true)
[ -n "$LOGLINE" ] || fail "proxy mode (reachable): hub request is access-logged" "a /hubs/ line" "none"
case "$LOGLINE" in
    *ticket=*|*\?*) fail "proxy mode (reachable): hub log line has no query string" "path only" "$LOGLINE" ;;
esac
pass "proxy mode (reachable): hub access log line is path-only"

echo "container smoke: ALL PASSED"
