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
# D1: the base image's stock pages must not survive in the runtime stage.
for f in index.html 50x.html; do
    docker run --rm --entrypoint test "$IMAGE" ! -e "/usr/share/nginx/html/$f" \
        || fail "stock /usr/share/nginx/html/$f removed from image" "absent" "present"
done
pass "stock index.html and 50x.html removed from image"

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
SPA_TITLE='<title>NightTales Studio</title>'
for p in / /index.html; do
    OUT=$(curl -sS "http://127.0.0.1:$PORT$p")
    case "$OUT" in
        *"Welcome to nginx"*) fail "$p is not the nginx welcome page" "no stock page" "Welcome to nginx!" ;;
    esac
    printf '%s' "$OUT" | grep -qF "$SPA_TITLE" \
        || fail "$p serves the SPA title" "$SPA_TITLE" "$(printf '%s' "$OUT" | grep -oiE '<title>[^<]*</title>' | head -n1)"
    pass "$p serves the SPA title"
done
OUT=$(curl -sS -w '\n%{http_code}' "http://127.0.0.1:$PORT/50x.html")
case "$OUT" in
    *"Welcome to nginx"*|*"An error occurred"*) fail "/50x.html is not the stock error page" "no stock page" "stock page served" ;;
esac
pass "/50x.html is not the stock error page ($(printf '%s' "$OUT" | tail -n1))"
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
printf '%s\n' "$GZ" | grep -q '^vary:.*accept-encoding' \
    || fail "gzipped response carries Vary: Accept-Encoding (gzip_vary)" "vary: accept-encoding" "$(printf '%s' "$GZ" | tr '\n' '|')"
pass "gzipped hashed js carries Vary: Accept-Encoding"

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

# D5: bare and mixed-case API paths never fall through to the SPA; /apix, /healthzz,
# /hubsx are ordinary SPA routes, /healthz stays the container's own endpoint.
for p in /api /API/v1 /Api /hubs /HUBS/x /health /Health/ready; do
    OUT=$(curl -sS -o /dev/null -w '%{http_code} %{content_type}' "http://127.0.0.1:$PORT$p")
    [ "$OUT" = "404 text/plain" ] || fail "guard $p -> 404 text/plain" "404 text/plain" "$OUT"
    pass "guard $p -> $OUT"
done
for p in /apix /healthzz /hubsx; do
    OUT=$(curl -sS -o /dev/null -w '%{http_code} %{content_type}' "http://127.0.0.1:$PORT$p")
    case "$OUT" in "200 text/html"*) pass "$p stays on the SPA fallback" ;; *) fail "$p stays on the SPA fallback" "200 text/html" "$OUT" ;; esac
done

# Guard mode: a misrouted /hubs request (and a dotfile under /hubs) carries the single-use
# ticket in its query string; neither the access log nor the error log may contain it.
for p in '/hubs/episodes?ticket=SECRET' '/HUBS/x?ticket=SECRET' '/hubs?ticket=SECRET' '/hubs/.probe?ticket=SECRET' '/.env?ticket=SECRET'; do
    curl -sS -o /dev/null "http://127.0.0.1:$PORT$p" || true
done
sleep 1
LEAKED=$(docker logs "$CURRENT" 2>&1 | grep -c 'SECRET' || true)
[ "$LEAKED" = "0" ] || fail "guard mode: hub ticket absent from docker logs (stdout+stderr)" "0 occurrences of SECRET" "$LEAKED"
pass "guard mode: hub ticket absent from docker logs (stdout+stderr)"
LOGLINE=$(docker logs "$CURRENT" 2>/dev/null | grep '/hubs/episodes' || true)
[ -n "$LOGLINE" ] || fail "guard mode: /hubs request is access-logged" "a /hubs/episodes line" "none"
pass "guard mode: /hubs access log line is path-only"

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

# --- media origins: exact rendering (the value carries its own separator, 16-nts-media-origins.envsh) ----
# csp_directive NAME -> the directive from the served header, byte-exact (only the `; ` separator is cut).
csp_directive() {
    curl -sS -D - -o /dev/null "http://127.0.0.1:$PORT/" | tr -d '\r' \
        | sed -n 's/^[Cc]ontent-[Ss]ecurity-[Pp]olicy: //p' | tr ';' '\n' | sed 's/^ //' | grep -E "^$1( |\$)" || true
}
expect_directive() { # NAME EXPECTED LABEL
    local got
    got=$(csp_directive "$1")
    [ "$got" = "$2" ] || fail "$3" "[$2]" "[$got]"
    pass "$3"
}

start media-empty
expect_directive img-src "img-src 'self' data:" "empty NTS_CSP_MEDIA_ORIGINS: img-src is exactly 'self' data: (no trailing space)"
expect_directive media-src "media-src 'self'" "empty NTS_CSP_MEDIA_ORIGINS: media-src is exactly 'self' (no trailing space)"

# Two origins, deliberately padded with extra spaces: joined by single spaces, nothing leading or trailing.
start media -e 'NTS_CSP_MEDIA_ORIGINS=  https://media.example.com   https://cdn.example.org '
expect_directive img-src "img-src 'self' data: https://media.example.com https://cdn.example.org" "two origins: img-src lists both, single-spaced"
expect_directive media-src "media-src 'self' https://media.example.com https://cdn.example.org" "two origins: media-src lists both, single-spaced"
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
    $'NTS_API_UPSTREAM=http://127.0.0.1:9\n; add_header X-Injected yes; #' \
    $'NTS_HSTS=max-age=1\n"; add_header X-Injected yes; #' \
    $'NTS_SPA_FALLBACK=index.csr.html\n; evil' \
    $'NTS_HSTS=max-age=1\revil' \
    $'NTS_API_UPSTREAM=http://127.0.0.1:9\revil' \
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
BODY=$(curl -fsS "http://127.0.0.1:$PORT/healthz")
[ "$BODY" = "ok" ] || fail "proxy mode: /healthz stays the container's own endpoint" "ok" "$BODY"
pass "proxy mode: /healthz is not proxied"
for p in /api /health; do
    [ "$(status_of "$p")" = "502" ] || fail "proxy mode: bare $p is proxied" "502 (unreachable upstream)" "$(status_of "$p")"
done
pass "proxy mode: bare /api and /health are proxied"
# Rendered proxy include: hubs overwrites the forwarded headers like /api, is unbuffered;
# /api|/health reuse upstream connections (Connection "").
INC=$(docker exec "$CURRENT" cat /etc/nginx/conf.d/nts-api-guard.inc)
[ "$(printf '%s\n' "$INC" | grep -c 'proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;')" = "2" ] \
    || fail "X-Forwarded-For set on /hubs and /api" "2 occurrences" "$(printf '%s\n' "$INC" | grep -c 'X-Forwarded-For')"
[ "$(printf '%s\n' "$INC" | grep -c 'proxy_set_header X-Forwarded-Proto \$scheme;')" = "2" ] \
    || fail "X-Forwarded-Proto set on /hubs and /api" "2 occurrences" "$(printf '%s\n' "$INC" | grep -c 'X-Forwarded-Proto')"
printf '%s\n' "$INC" | grep -q 'proxy_buffering off;' || fail "/hubs is unbuffered" "proxy_buffering off;" "missing"
printf '%s\n' "$INC" | grep -q 'proxy_set_header Connection "";' || fail "/api|/health clears Connection" 'proxy_set_header Connection "";' "missing"
pass "proxy include: hubs forwarded headers + unbuffered, api keepalive"
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
# D4: nginx's error log quotes the request line on upstream failures; the hubs location
# must not log the single-use ticket anywhere (stdout + stderr).
curl -sS -o /dev/null "http://127.0.0.1:$PORT/hubs/x?ticket=SECRET" || true
curl -sS -o /dev/null "http://127.0.0.1:$PORT/HUBS/y?ticket=SECRET" || true
curl -sS -o /dev/null "http://127.0.0.1:$PORT/hubs/.probe?ticket=SECRET" || true
curl -sS -o /dev/null "http://127.0.0.1:$PORT/.env?ticket=SECRET" || true
sleep 1
LEAKED=$(docker logs "$CURRENT" 2>&1 | grep -c 'SECRET' || true)
[ "$LEAKED" = "0" ] || fail "hub ticket absent from docker logs (stdout+stderr)" "0 occurrences of SECRET" "$LEAKED"
pass "hub ticket absent from docker logs (stdout+stderr)"
# Error logging for other locations is kept: the failed /api request is still logged.
curl -sS -o /dev/null "http://127.0.0.1:$PORT/api/v1/err-probe" || true
sleep 1
docker logs "$CURRENT" 2>&1 | grep -q 'err-probe' \
    || fail "non-hub upstream errors are still logged" "an err-probe line in docker logs" "none"
pass "non-hub upstream errors are still logged"

# NGINX_ENVSUBST_FILTER=^NTS_: unrelated environment variables named like nginx variables
# must not rewrite $uri / $host / $http_upgrade in the rendered config.
start envfilter -e host=EVIL -e uri=/EVIL -e http_upgrade=EVIL -e NTS_API_UPSTREAM=http://127.0.0.1:9
docker exec "$CURRENT" grep -q 'try_files \$uri \$uri/index.html' /etc/nginx/conf.d/default.conf \
    || fail "envsubst filter keeps \$uri literal" 'try_files $uri $uri/index.html' "$(docker exec "$CURRENT" grep -n try_files /etc/nginx/conf.d/default.conf | tr '\n' '|')"
docker exec "$CURRENT" grep -q 'map \$http_upgrade \$connection_upgrade' /etc/nginx/conf.d/00-nts-map.conf \
    || fail "envsubst filter keeps \$http_upgrade literal" 'map $http_upgrade $connection_upgrade' "rewritten"
docker exec "$CURRENT" grep -rq 'EVIL' /etc/nginx/conf.d && fail "no unrelated env value rendered into conf.d" "no EVIL" "found"
pass "NGINX_ENVSUBST_FILTER=^NTS_: unrelated env vars are not substituted"

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
